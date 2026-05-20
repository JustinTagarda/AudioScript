namespace AudioScript.Services;

public sealed class AppUpdateService : IAppUpdateService, IAppUpdateCoordinator
{
    private readonly IAppVersionProvider _versionProvider;
    private readonly IMicrosoftStoreUpdateProvider _storeUpdateProvider;
    private readonly IDeferredUpdateStateStore _deferredStateStore;
    private readonly ProcessLogService _processLogService;
    private readonly Func<bool> _isAppBusy;
    private readonly StoreUpdateOptions _options;
    private readonly SemaphoreSlim _workflowSemaphore = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _lifetimeCts;
    private Task? _startupTask;
    private bool _started;
    private AppUpdateSnapshot _currentSnapshot;
    private string _lastInstalledVersion = "unknown";
    private string _lastAvailableVersion = "unknown";
    private int _lastUpdateCount;
    private bool _lastMandatory;

    public AppUpdateService(
        IAppVersionProvider versionProvider,
        IMicrosoftStoreUpdateProvider storeUpdateProvider,
        IDeferredUpdateStateStore deferredStateStore,
        ProcessLogService processLogService,
        Func<bool> isAppBusy,
        StoreUpdateOptions? options = null)
    {
        _versionProvider = versionProvider ?? throw new ArgumentNullException(nameof(versionProvider));
        _storeUpdateProvider = storeUpdateProvider ?? throw new ArgumentNullException(nameof(storeUpdateProvider));
        _deferredStateStore = deferredStateStore ?? throw new ArgumentNullException(nameof(deferredStateStore));
        _processLogService = processLogService ?? throw new ArgumentNullException(nameof(processLogService));
        _isAppBusy = isAppBusy ?? throw new ArgumentNullException(nameof(isAppBusy));
        _options = options ?? new StoreUpdateOptions();
        _currentSnapshot = AppUpdateSnapshot.Idle(_versionProvider.InstalledVersion);
    }

    public event EventHandler<AppUpdateSnapshot>? SnapshotChanged;

    public AppUpdateSnapshot CurrentSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _currentSnapshot;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_started)
            {
                return Task.CompletedTask;
            }

            _started = true;
            _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _startupTask = Task.Run(
                () => RunStartupUpdateFlowAsync(_lifetimeCts.Token),
                _lifetimeCts.Token);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Task? startupTask;
        lock (_sync)
        {
            _lifetimeCts?.Cancel();
            startupTask = _startupTask;
        }

        if (startupTask is not null)
        {
            try
            {
                await startupTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogException("update_stop_failed", "shutdown", ex);
            }
        }

        lock (_sync)
        {
            _startupTask = null;
            _lifetimeCts?.Dispose();
            _lifetimeCts = null;
            _started = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _workflowSemaphore.Dispose();
    }

    public async Task RunStartupUpdateFlowAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_options.StartupDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.StartupDelay, cancellationToken).ConfigureAwait(false);
            }

            await RunOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log("startup_update_check_canceled", "startup", extra: "message=Application update startup check canceled.");
        }
        catch (Exception ex)
        {
            LogException("startup_update_flow_failed", "startup", ex);
            Publish(Failed("Update check failed", "Application will continue normally."));
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await _workflowSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunWorkflowCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogException("update_workflow_failed", "startup", ex);
            Publish(Failed("Update check failed", "Application will continue normally."));
        }
        finally
        {
            _workflowSemaphore.Release();
        }
    }

    private async Task RunWorkflowCoreAsync(CancellationToken cancellationToken)
    {
        string installedVersion = _versionProvider.InstalledVersion;
        _lastInstalledVersion = installedVersion;

        if (!_options.EnableStartupUpdateCheck)
        {
            Log("startup_update_check_disabled", "check");
            Publish(AppUpdateSnapshot.Idle(installedVersion));
            return;
        }

        if (!_storeUpdateProvider.IsStoreUpdateSupported())
        {
            Log("update_check_skipped_not_supported", "check", extra: $"packaged={_versionProvider.IsPackaged}");
            Publish(AppUpdateSnapshot.Idle(installedVersion));
            return;
        }

        DeferredUpdateState? existingState = await _deferredStateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (IsDeferredStateStale(existingState))
        {
            await _deferredStateStore.ClearAsync(cancellationToken).ConfigureAwait(false);
            Log("deferred_state_cleared_stale", "check");
            existingState = null;
        }

        if (existingState?.InstallDeferred == true)
        {
            Log("deferred_state_loaded", "check", extra: $"retryCount={existingState.RetryCount}");
        }

        Publish(new AppUpdateSnapshot(
            AppUpdateState.Checking,
            "Checking for updates",
            string.Empty,
            IsMandatoryUpdateAvailable: false,
            IsProgressVisible: false,
            ProgressValue: 0,
            InstalledVersion: installedVersion,
            AvailableVersion: null));
        Log("update_check_started", "check");

        StoreUpdateQueryResult queryResult = await _storeUpdateProvider
            .GetAvailableUpdatesAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!queryResult.UpdateSet.HasUpdates)
        {
            _lastUpdateCount = 0;
            _lastAvailableVersion = "unknown";
            _lastMandatory = false;
            await _deferredStateStore.ClearAsync(cancellationToken).ConfigureAwait(false);
            Log("no_updates_available", "check");
            Publish(AppUpdateSnapshot.Idle(installedVersion));
            return;
        }

        bool mandatory = queryResult.UpdateSet.Updates.Any(update => update.IsMandatory);
        string? availableVersion = ResolveHighestVersion(queryResult.UpdateSet.Updates);
        _lastUpdateCount = queryResult.UpdateSet.Updates.Count;
        _lastAvailableVersion = availableVersion ?? "unknown";
        _lastMandatory = mandatory;
        await SaveDetectedStateAsync(queryResult.UpdateSet, existingState, cancellationToken).ConfigureAwait(false);
        Log("updates_available", "check");

        if (_options.PreferSilentUpdateWhenAvailable
            && _storeUpdateProvider.CanSilentlyDownloadUpdates(queryResult))
        {
            Log("silent_download_starting", "download", extra: "canSilent=true");
            StoreUpdateOperationResult silentDownloadResult = await _storeUpdateProvider
                .TrySilentDownloadAsync(
                    queryResult.UpdateSet,
                    progress => PublishProgress(AppUpdateState.Downloading, "Downloading update", installedVersion, availableVersion, mandatory, progress),
                    cancellationToken)
                .ConfigureAwait(false);

            if (silentDownloadResult.Succeeded)
            {
                Log("silent_download_completed", "download");
                if (IsSafeToInstallNow())
                {
                    Log("silent_install_starting", "install");
                    StoreUpdateOperationResult silentInstallResult = await _storeUpdateProvider
                        .TrySilentDownloadAndInstallAsync(
                            queryResult.UpdateSet,
                            progress => PublishProgress(AppUpdateState.Installing, "Installing update", installedVersion, availableVersion, mandatory, progress),
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (silentInstallResult.Succeeded)
                    {
                        await _deferredStateStore.ClearAsync(cancellationToken).ConfigureAwait(false);
                        Log("silent_install_completed", "install");
                        Publish(AppUpdateSnapshot.Idle(installedVersion));
                        return;
                    }

                    Log(
                        "silent_install_failed",
                        "install",
                        state: silentInstallResult.State.ToString(),
                        failedPackageCount: silentInstallResult.FailedPackageCount);
                    await SaveRetryStateAsync(queryResult.UpdateSet, silentInstallResult, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    Log("silent_install_deferred_busy", "install");
                    await SaveDeferredInstallStateAsync(queryResult.UpdateSet, silentDownloadResult, cancellationToken).ConfigureAwait(false);
                    Publish(AppUpdateSnapshot.Idle(installedVersion));
                    return;
                }
            }
            else
            {
                Log(
                    "silent_download_failed",
                    "download",
                    state: silentDownloadResult.State.ToString(),
                    failedPackageCount: silentDownloadResult.FailedPackageCount);
                await SaveRetryStateAsync(queryResult.UpdateSet, silentDownloadResult, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            Log("silent_update_unavailable", "check", extra: $"canSilent={queryResult.CanSilentlyDownload}");
        }

        if (_options.UseFallbackStoreUiWhenSilentUnavailable)
        {
            await RunFallbackStoreUiAsync(queryResult.UpdateSet, installedVersion, availableVersion, mandatory, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await SaveDeferredInstallStateAsync(
                queryResult.UpdateSet,
                new StoreUpdateOperationResult(StoreUpdateOperationState.Unknown),
                cancellationToken).ConfigureAwait(false);
            Publish(AppUpdateSnapshot.Idle(installedVersion));
        }
    }

    private async Task RunFallbackStoreUiAsync(
        StorePackageUpdateSet updateSet,
        string installedVersion,
        string? availableVersion,
        bool mandatory,
        CancellationToken cancellationToken)
    {
        try
        {
            Log("fallback_ui_starting", "fallback_ui");
            StoreUpdateOperationResult result = await _storeUpdateProvider
                .RequestDownloadAndInstallWithStoreUiAsync(
                    updateSet,
                    _options.ShowProgressDuringFallbackUi
                        ? progress => PublishProgress(AppUpdateState.Installing, "Installing update", installedVersion, availableVersion, mandatory, progress)
                        : null,
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.Succeeded)
            {
                await _deferredStateStore.ClearAsync(cancellationToken).ConfigureAwait(false);
                Log("fallback_ui_completed", "fallback_ui");
                Publish(AppUpdateSnapshot.Idle(installedVersion));
                return;
            }

            if (result.Cancelled)
            {
                Log("fallback_ui_cancelled", "fallback_ui", state: result.State.ToString());
                await SaveRetryStateAsync(updateSet, result, cancellationToken).ConfigureAwait(false);
                Publish(AppUpdateSnapshot.Idle(installedVersion));
                return;
            }

            Log(
                "fallback_ui_failed",
                "fallback_ui",
                state: result.State.ToString(),
                failedPackageCount: result.FailedPackageCount);
            await SaveRetryStateAsync(updateSet, result, cancellationToken).ConfigureAwait(false);
            Publish(AppUpdateSnapshot.Idle(installedVersion));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogException("fallback_ui_failed", "fallback_ui", ex);
            await SaveRetryStateAsync(
                updateSet,
                new StoreUpdateOperationResult(StoreUpdateOperationState.OtherError, ErrorMessage: ex.Message),
                cancellationToken).ConfigureAwait(false);
            Publish(AppUpdateSnapshot.Idle(installedVersion));
        }
    }

    private bool IsSafeToInstallNow()
    {
        try
        {
            return !_isAppBusy();
        }
        catch (Exception ex)
        {
            LogException("busy_check_failed", "check", ex);
            return false;
        }
    }

    private void PublishProgress(
        AppUpdateState state,
        string stageText,
        string installedVersion,
        string? availableVersion,
        bool mandatory,
        StoreUpdateOperationProgress progress)
    {
        Publish(new AppUpdateSnapshot(
            state,
            stageText,
            stageText,
            mandatory,
            IsProgressVisible: true,
            ProgressValue: ClampProgress(progress.ProgressValue),
            installedVersion,
            availableVersion));
    }

    private async Task SaveDetectedStateAsync(
        StorePackageUpdateSet updateSet,
        DeferredUpdateState? existingState,
        CancellationToken cancellationToken)
    {
        await _deferredStateStore.SaveAsync(new DeferredUpdateState
        {
            LastCheckUtc = DateTimeOffset.UtcNow,
            LastUpdateDetectedUtc = DateTimeOffset.UtcNow,
            LastFailureUtc = existingState?.LastFailureUtc,
            LastSuccessfulOperationUtc = existingState?.LastSuccessfulOperationUtc,
            InstallDeferred = existingState?.InstallDeferred ?? false,
            RetryCount = existingState?.RetryCount ?? 0,
            LastFailureCategory = existingState?.LastFailureCategory,
            PackageFamilyNames = updateSet.Updates
                .Select(update => update.PackageFamilyName)
                .Where(packageFamilyName => !string.IsNullOrWhiteSpace(packageFamilyName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        }, cancellationToken).ConfigureAwait(false);
        Log("deferred_state_update_detected_saved", "check");
    }

    private Task SaveDeferredInstallStateAsync(
        StorePackageUpdateSet updateSet,
        StoreUpdateOperationResult result,
        CancellationToken cancellationToken)
    {
        Log("deferred_install_state_saved", "install", state: result.State.ToString());
        return SaveStateAsync(updateSet, result, installDeferred: true, cancellationToken);
    }

    private Task SaveRetryStateAsync(
        StorePackageUpdateSet updateSet,
        StoreUpdateOperationResult result,
        CancellationToken cancellationToken)
    {
        Log("retry_state_saved", "retry", state: result.State.ToString());
        return SaveStateAsync(updateSet, result, installDeferred: false, cancellationToken);
    }

    private async Task SaveStateAsync(
        StorePackageUpdateSet updateSet,
        StoreUpdateOperationResult result,
        bool installDeferred,
        CancellationToken cancellationToken)
    {
        DeferredUpdateState? existingState = await _deferredStateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        await _deferredStateStore.SaveAsync(new DeferredUpdateState
        {
            LastCheckUtc = DateTimeOffset.UtcNow,
            LastUpdateDetectedUtc = existingState?.LastUpdateDetectedUtc ?? DateTimeOffset.UtcNow,
            LastFailureUtc = result.Succeeded ? existingState?.LastFailureUtc : DateTimeOffset.UtcNow,
            LastSuccessfulOperationUtc = result.Succeeded ? DateTimeOffset.UtcNow : existingState?.LastSuccessfulOperationUtc,
            InstallDeferred = installDeferred,
            RetryCount = result.Succeeded ? 0 : (existingState?.RetryCount ?? 0) + 1,
            LastFailureCategory = result.Succeeded ? null : result.State.ToString(),
            PackageFamilyNames = updateSet.Updates
                .Select(update => update.PackageFamilyName)
                .Where(packageFamilyName => !string.IsNullOrWhiteSpace(packageFamilyName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        }, cancellationToken).ConfigureAwait(false);
    }

    private void Publish(AppUpdateSnapshot snapshot)
    {
        AppUpdateSnapshot normalized = snapshot with
        {
            ProgressValue = ClampProgress(snapshot.ProgressValue),
            InstalledVersion = string.IsNullOrWhiteSpace(snapshot.InstalledVersion)
                ? _versionProvider.InstalledVersion
                : snapshot.InstalledVersion,
        };

        EventHandler<AppUpdateSnapshot>? handler;
        lock (_sync)
        {
            _currentSnapshot = normalized;
            handler = SnapshotChanged;
        }

        try
        {
            handler?.Invoke(this, normalized);
        }
        catch (Exception ex)
        {
            LogException("snapshot_subscriber_failed", "shutdown", ex);
        }
    }

    private AppUpdateSnapshot Failed(string stageText, string message) =>
        new(
            AppUpdateState.Failed,
            stageText,
            message,
            IsMandatoryUpdateAvailable: false,
            IsProgressVisible: false,
            ProgressValue: 0,
            _versionProvider.InstalledVersion,
            AvailableVersion: null);

    private static string? ResolveHighestVersion(IEnumerable<StorePackageUpdateInfo> updates)
    {
        Version? highest = null;
        foreach (StorePackageUpdateInfo update in updates)
        {
            if (!Version.TryParse(update.Version, out Version? version))
            {
                continue;
            }

            if (highest is null || version > highest)
            {
                highest = version;
            }
        }

        return highest?.ToString(4);
    }

    private bool IsDeferredStateStale(DeferredUpdateState? state)
    {
        if (state is null || _options.DeferredStateMaxAge <= TimeSpan.Zero)
        {
            return false;
        }

        DateTimeOffset? referenceUtc = state.LastUpdateDetectedUtc
            ?? state.LastCheckUtc
            ?? state.LastFailureUtc;
        return referenceUtc.HasValue
            && DateTimeOffset.UtcNow - referenceUtc.Value > _options.DeferredStateMaxAge;
    }

    private static double ClampProgress(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        return Math.Clamp(value, 0, 1);
    }

    private void Log(
        string eventName,
        string operation,
        string? state = null,
        int failedPackageCount = 0,
        Exception? exception = null,
        string? extra = null)
    {
        string metadata = UpdateLogMetadata.Build(
            operation,
            state ?? CurrentSnapshot.State.ToString(),
            _lastInstalledVersion,
            _lastAvailableVersion,
            _lastUpdateCount,
            _lastMandatory,
            failedPackageCount,
            exception,
            extra);
        _processLogService.Log("AppUpdate", $"{eventName}; {metadata}");
    }

    private void LogException(string eventName, string operation, Exception ex)
    {
        string metadata = UpdateLogMetadata.Build(
            operation,
            CurrentSnapshot.State.ToString(),
            _lastInstalledVersion,
            _lastAvailableVersion,
            _lastUpdateCount,
            _lastMandatory,
            failedPackageCount: 0,
            exception: ex);
        _processLogService.Log("AppUpdate", $"{eventName}; {metadata}", ProcessLogLevel.Error);
    }
}
