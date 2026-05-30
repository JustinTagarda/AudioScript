namespace AudioScript.Services;

public sealed class AppUpdateService : IAppUpdateService, IAppUpdateCoordinator
{
    private readonly IAppVersionProvider _versionProvider;
    private readonly IMicrosoftStoreUpdateProvider _storeUpdateProvider;
    private readonly IDeferredUpdateStateStore _deferredStateStore;
    private readonly ProcessLogService _processLogService;
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
    private System.Threading.Timer? _retryTimer;
    private StorePackageUpdateSet? _lastKnownUpdateSet;
    private bool _lastKnownCanSilentlyDownload;
    private string? _lastKnownAvailableVersion;
    private bool _lastKnownMandatory;

    public AppUpdateService(
        IAppVersionProvider versionProvider,
        IMicrosoftStoreUpdateProvider storeUpdateProvider,
        IDeferredUpdateStateStore deferredStateStore,
        ProcessLogService processLogService,
        StoreUpdateOptions? options = null)
    {
        _versionProvider = versionProvider ?? throw new ArgumentNullException(nameof(versionProvider));
        _storeUpdateProvider = storeUpdateProvider ?? throw new ArgumentNullException(nameof(storeUpdateProvider));
        _deferredStateStore = deferredStateStore ?? throw new ArgumentNullException(nameof(deferredStateStore));
        _processLogService = processLogService ?? throw new ArgumentNullException(nameof(processLogService));
        _options = options ?? new StoreUpdateOptions();
        ValidateProductionPolicy(_options);
        _currentSnapshot = AppUpdateSnapshot.Idle(_versionProvider.InstalledVersion);
    }

    public event EventHandler<AppUpdateSnapshot>? SnapshotChanged;

    public bool IsStoreUpdateSupported => _storeUpdateProvider.IsStoreUpdateSupported();

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

    public Task RunUserInitiatedUpdateFlowAsync(CancellationToken cancellationToken = default) =>
        RunUpdateFlowAsync(
            isStartupFlow: false,
            hideCheckingSnapshot: false,
            cancellationToken);

    public Task<StoreUpdateOperationResult?> RunExitTimeInstallAsync(CancellationToken cancellationToken = default) =>
        RunExitTimeInstallAsyncCore(cancellationToken);

    public Task<bool> HasDeferredInstallOnExitAsync(CancellationToken cancellationToken = default) =>
        HasDeferredInstallOnExitAsyncCore(cancellationToken);

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
            _retryTimer?.Dispose();
            _retryTimer = null;
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

            await RunUpdateFlowAsync(
                isStartupFlow: true,
                hideCheckingSnapshot: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log("startup_update_check_canceled", "startup", extra: "message=Application update startup check canceled.");
        }
        catch (Exception ex)
        {
            LogException("startup_update_flow_failed", "startup", ex);
            Publish(AppUpdateSnapshot.Idle(_versionProvider.InstalledVersion));
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await RunUserInitiatedUpdateFlowAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunUpdateFlowAsync(
        bool isStartupFlow,
        bool hideCheckingSnapshot,
        CancellationToken cancellationToken)
    {
        bool entered = false;
        try
        {
            await _workflowSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            await RunWorkflowCoreAsync(
                isStartupFlow,
                hideCheckingSnapshot,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogException("update_workflow_failed", isStartupFlow ? "startup" : "manual", ex);
            Publish(isStartupFlow
                ? AppUpdateSnapshot.Idle(_versionProvider.InstalledVersion)
                : Failed("Update check failed", "Application will continue normally."));
        }
        finally
        {
            if (entered)
            {
                _workflowSemaphore.Release();
            }
        }
    }

    private async Task RunWorkflowCoreAsync(
        bool isStartupFlow,
        bool hideCheckingSnapshot,
        CancellationToken cancellationToken)
    {
        string installedVersion = _versionProvider.InstalledVersion;
        _lastInstalledVersion = installedVersion;
        CancellationToken lifetimeToken = _lifetimeCts?.Token ?? cancellationToken;

        if (isStartupFlow && !_options.EnableStartupUpdateCheck)
        {
            Log("startup_update_check_disabled", "check");
            Publish(AppUpdateSnapshot.Idle(installedVersion));
            return;
        }

        if (!_storeUpdateProvider.IsStoreUpdateSupported())
        {
            Log("update_check_skipped_not_supported", "check", extra: $"packaged={_versionProvider.IsPackaged}");
            await _deferredStateStore.ClearAsync(cancellationToken).ConfigureAwait(false);
            Publish(AppUpdateSnapshot.Idle(installedVersion));
            return;
        }

        StoreQueueRecoveryState queueState = await _storeUpdateProvider.TryGetActiveQueueStateAsync(cancellationToken).ConfigureAwait(false);
        if (queueState.HasActiveQueueItem)
        {
            Publish(new AppUpdateSnapshot(
                AppUpdateState.Installing,
                queueState.PhaseText ?? "Installing",
                queueState.PhaseText ?? "Installing",
                IsMandatoryUpdateAvailable: false,
                IsProgressVisible: true,
                ProgressValue: queueState.ProgressValue,
                InstalledVersion: installedVersion,
                AvailableVersion: null,
                HasActiveQueueItem: true,
                PackageDetailText: queueState.PackageDetailText));
            return;
        }

        DeferredUpdateState? existingState = await LoadDeferredStateAsync(cancellationToken).ConfigureAwait(false);
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

        if (ShouldSkipStartupUpdateCheckDueToInterval(existingState))
        {
            string lastCheckUtcText = existingState?.LastCheckUtc?.ToString("O") ?? "n/a";
            Log(
                "startup_update_check_throttled",
                "check",
                extra: $"minimumInterval={_options.MinimumCheckInterval}; lastCheckUtc={lastCheckUtcText}");
            Publish(AppUpdateSnapshot.Idle(installedVersion));
            return;
        }

        if (!hideCheckingSnapshot)
        {
            Publish(new AppUpdateSnapshot(
                AppUpdateState.Checking,
                "Checking for updates",
                string.Empty,
                IsMandatoryUpdateAvailable: false,
                IsProgressVisible: false,
                ProgressValue: 0,
                InstalledVersion: installedVersion,
                AvailableVersion: null));
        }
        Log("update_check_started", "check");

        StoreUpdateQueryResult queryResult = await _storeUpdateProvider
            .GetAvailableUpdatesAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!queryResult.UpdateSet.HasUpdates)
        {
            _lastUpdateCount = 0;
            _lastAvailableVersion = "unknown";
            _lastMandatory = false;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await _deferredStateStore.SaveAsync(new DeferredUpdateState
            {
                LastCheckUtc = now,
                LastUpdateDetectedUtc = existingState?.LastUpdateDetectedUtc,
                LastDownloadCompletedUtc = existingState?.LastDownloadCompletedUtc,
                LastInstallDeferredUtc = existingState?.LastInstallDeferredUtc,
                LastFailureUtc = existingState?.LastFailureUtc,
                LastSuccessfulOperationUtc = existingState?.LastSuccessfulOperationUtc,
                LastAttemptUtc = existingState?.LastAttemptUtc,
                InstallDeferred = false,
                RetryCount = 0,
                LastFailureCategory = null,
                LastFailureMessage = null,
                PackageIdentitySnapshot = null,
                PackageFamilyNames = Array.Empty<string>(),
                RecentCheckHistoryUtc = AppendCheckHistory(existingState?.RecentCheckHistoryUtc, now),
            }, cancellationToken).ConfigureAwait(false);
            _lastKnownUpdateSet = null;
            Log("no_updates_available", "check");
            if (isStartupFlow)
            {
                ScheduleOneHourRetry(lifetimeToken);
            }
            Publish(AppUpdateSnapshot.Idle(installedVersion));
            return;
        }

        bool mandatory = queryResult.UpdateSet.Updates.Any(update => update.IsMandatory);
        string? availableVersion = ResolveHighestVersion(queryResult.UpdateSet.Updates);
        _lastUpdateCount = queryResult.UpdateSet.Updates.Count;
        _lastAvailableVersion = availableVersion ?? "unknown";
        _lastMandatory = mandatory;
        PackageIdentitySnapshot? identitySnapshot = ResolvePrimaryPackageIdentitySnapshot(queryResult.UpdateSet.Updates);
        bool isThrottled = IsDeferredInstallThrottled(existingState, identitySnapshot);
        Publish(new AppUpdateSnapshot(
            AppUpdateState.UpdateAvailable,
            "Update available",
            "Microsoft Store update is available.",
            mandatory,
            IsProgressVisible: false,
            ProgressValue: 0,
            installedVersion,
            availableVersion));
        if (!isThrottled)
        {
            await SaveDetectedStateAsync(queryResult.UpdateSet, identitySnapshot, existingState, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Log("deferred_install_suppressed_by_retry_policy", "check");
        }
        Log("updates_available", "check");

        _lastKnownUpdateSet = queryResult.UpdateSet;
        _lastKnownCanSilentlyDownload = queryResult.CanSilentlyDownload;
        _lastKnownAvailableVersion = availableVersion;
        _lastKnownMandatory = mandatory;

        if (isStartupFlow)
        {
            return;
        }

        CancelRetryTimer();

        StorePackageUpdateSet installSet = _lastKnownUpdateSet ?? queryResult.UpdateSet;
        Publish(new AppUpdateSnapshot(
            AppUpdateState.Checking,
            "Preparing",
            "Waiting for permission",
            mandatory,
            IsProgressVisible: true,
            ProgressValue: 0,
            installedVersion,
            availableVersion));
        StoreUpdateOperationResult result = await _storeUpdateProvider
            .RequestDownloadAndInstallWithStoreUiAsync(
                installSet,
                progress => PublishProgress(
                    AppUpdateState.Installing,
                    "Installing",
                    installedVersion,
                    availableVersion,
                    mandatory,
                    progress),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Succeeded)
        {
            await _deferredStateStore.ClearAsync(cancellationToken).ConfigureAwait(false);
            _lastKnownUpdateSet = null;
            Publish(new AppUpdateSnapshot(
                AppUpdateState.Completed,
                "Completed",
                "Update completed.",
                mandatory,
                IsProgressVisible: true,
                ProgressValue: 1,
                installedVersion,
                availableVersion,
                ResultGuidanceText: null));
            Publish(AppUpdateSnapshot.Idle(installedVersion));
            return;
        }

        await SaveRetryStateAsync(
            installSet,
            identitySnapshot,
            result,
            cancellationToken).ConfigureAwait(false);
        Publish(Failed("Failed", BuildFailureGuidance(result.State)));
    }

    private void ScheduleOneHourRetry(CancellationToken lifetimeToken)
    {
        lock (_sync)
        {
            _retryTimer?.Dispose();
            _retryTimer = new System.Threading.Timer(async _ =>
            {
                try
                {
                    await RunUpdateFlowAsync(
                        isStartupFlow: true,
                        hideCheckingSnapshot: true,
                        lifetimeToken).ConfigureAwait(false);
                }
                catch
                {
                }
            }, null, TimeSpan.FromHours(1), Timeout.InfiniteTimeSpan);
        }
    }

    private void CancelRetryTimer()
    {
        lock (_sync)
        {
            _retryTimer?.Dispose();
            _retryTimer = null;
        }
    }

    private async Task RunFallbackStoreUiAsync(
        StorePackageUpdateSet updateSet,
        string installedVersion,
        string? availableVersion,
        bool mandatory,
        bool isStartupFlow,
        CancellationToken cancellationToken)
    {
        try
        {
            Log("fallback_ui_starting", "fallback_ui");
            StoreUpdateOperationResult result = await _storeUpdateProvider
                .RequestDownloadAndInstallWithStoreUiAsync(
                    updateSet,
                    _options.ShowProgressDuringFallbackUi && !isStartupFlow
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
                await SaveRetryStateAsync(
                    updateSet,
                    ResolvePrimaryPackageIdentitySnapshot(updateSet.Updates),
                    result,
                    cancellationToken).ConfigureAwait(false);
                Publish(AppUpdateSnapshot.Idle(installedVersion));
                return;
            }

            Log(
                "fallback_ui_failed",
                "fallback_ui",
                state: result.State.ToString(),
                failedPackageCount: result.FailedPackageCount);
            await SaveRetryStateAsync(updateSet, ResolvePrimaryPackageIdentitySnapshot(updateSet.Updates), result, cancellationToken)
                .ConfigureAwait(false);
            Publish(AppUpdateSnapshot.Idle(installedVersion));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogException("fallback_ui_failed", "fallback_ui", ex);
            await SaveRetryStateAsync(
                updateSet,
                ResolvePrimaryPackageIdentitySnapshot(updateSet.Updates),
                new StoreUpdateOperationResult(StoreUpdateOperationState.OtherError, ErrorMessage: ex.Message),
                cancellationToken).ConfigureAwait(false);
            Publish(AppUpdateSnapshot.Idle(installedVersion));
        }
    }

    private async Task<bool> HasDeferredInstallOnExitAsyncCore(CancellationToken cancellationToken)
    {
        if (!_storeUpdateProvider.IsStoreUpdateSupported())
        {
            await _deferredStateStore.ClearAsync(cancellationToken).ConfigureAwait(false);
            Log("deferred_state_skipped_not_supported", "exit_check", extra: $"packaged={_versionProvider.IsPackaged}");
            return false;
        }

        DeferredUpdateState? state = await LoadDeferredStateAsync(cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return false;
        }

        if (IsDeferredStateStale(state))
        {
            await _deferredStateStore.ClearAsync(cancellationToken).ConfigureAwait(false);
            Log("deferred_state_cleared_stale", "exit_check");
            return false;
        }

        return state.InstallDeferred && state.PackageIdentitySnapshot is not null;
    }

    private async Task<StoreUpdateOperationResult?> RunExitTimeInstallAsyncCore(CancellationToken cancellationToken)
    {
        CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.ExitInstallTimeout > TimeSpan.Zero)
        {
            timeoutCts.CancelAfter(_options.ExitInstallTimeout);
        }

        CancellationToken linkedToken = timeoutCts.Token;
        DeferredUpdateState? deferredState = null;
        bool entered = false;
        try
        {
            if (!_storeUpdateProvider.IsStoreUpdateSupported())
            {
                Log("exit_install_skipped_not_supported", "exit_install", extra: $"packaged={_versionProvider.IsPackaged}");
                await _deferredStateStore.ClearAsync(CancellationToken.None).ConfigureAwait(false);
                Publish(AppUpdateSnapshot.Idle(_versionProvider.InstalledVersion));
                return null;
            }

            await _workflowSemaphore.WaitAsync(linkedToken).ConfigureAwait(false);
            entered = true;

            deferredState = await LoadDeferredStateAsync(linkedToken).ConfigureAwait(false);
            if (deferredState is null || !deferredState.InstallDeferred || deferredState.PackageIdentitySnapshot is null)
            {
                return null;
            }

            if (IsDeferredStateStale(deferredState))
            {
                await _deferredStateStore.ClearAsync(CancellationToken.None).ConfigureAwait(false);
                Log("deferred_state_cleared_stale", "exit_install");
                return null;
            }

            Log("exit_install_started", "exit_install", extra: $"retryCount={deferredState.RetryCount}");
            Publish(new AppUpdateSnapshot(
                AppUpdateState.Checking,
                "Preparing update",
                "Revalidating update before closing",
                IsMandatoryUpdateAvailable: false,
                IsProgressVisible: false,
                ProgressValue: 0,
                InstalledVersion: _versionProvider.InstalledVersion,
                AvailableVersion: null));

            StoreUpdateQueryResult queryResult;
            try
            {
                queryResult = await _storeUpdateProvider.GetAvailableUpdatesAsync(linkedToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogException("exit_install_query_failed", "exit_install", ex);
                await SaveRetryStateAsync(
                    new StorePackageUpdateSet(Array.Empty<StorePackageUpdateInfo>()),
                    deferredState.PackageIdentitySnapshot,
                    new StoreUpdateOperationResult(StoreUpdateOperationState.OtherError, ErrorMessage: ex.Message),
                    CancellationToken.None).ConfigureAwait(false);
                Publish(Failed("Update installation failed", "The app will close normally."));
                return new StoreUpdateOperationResult(StoreUpdateOperationState.OtherError, ErrorMessage: ex.Message);
            }

            if (!queryResult.UpdateSet.HasUpdates)
            {
                Log("exit_install_skipped_no_updates", "exit_install");
                await _deferredStateStore.ClearAsync(CancellationToken.None).ConfigureAwait(false);
                Publish(AppUpdateSnapshot.Idle(_versionProvider.InstalledVersion));
                return null;
            }

            PackageIdentitySnapshot? queryIdentity = ResolveMatchingPackageIdentity(
                queryResult.UpdateSet.Updates,
                deferredState.PackageIdentitySnapshot);
            if (!IsSamePackageIdentity(deferredState.PackageIdentitySnapshot, queryIdentity))
            {
                Log("exit_install_skipped_identity_mismatch", "exit_install");
                await _deferredStateStore.ClearAsync(CancellationToken.None).ConfigureAwait(false);
                Publish(AppUpdateSnapshot.Idle(_versionProvider.InstalledVersion));
                return null;
            }

            bool mandatory = queryResult.UpdateSet.Updates.Any(update => update.IsMandatory);
            string? availableVersion = ResolveHighestVersion(queryResult.UpdateSet.Updates);

            StoreUpdateOperationResult installResult;
            if (_storeUpdateProvider.CanSilentlyDownloadUpdates(queryResult))
            {
                Log("exit_install_silent_starting", "exit_install");
                installResult = await _storeUpdateProvider
                    .TrySilentDownloadAndInstallAsync(
                        queryResult.UpdateSet,
                        progress => PublishProgress(
                            AppUpdateState.Installing,
                            "Installing update",
                            _versionProvider.InstalledVersion,
                            availableVersion,
                            mandatory,
                            progress),
                        linkedToken)
                    .ConfigureAwait(false);

                if (!installResult.Succeeded)
                {
                    Log(
                        "exit_install_silent_failed",
                        "exit_install",
                        state: installResult.State.ToString(),
                        failedPackageCount: installResult.FailedPackageCount);
                }
            }
            else
            {
                Log("exit_install_silent_unavailable", "exit_install");
                installResult = new StoreUpdateOperationResult(StoreUpdateOperationState.Unknown);
            }

            if (!installResult.Succeeded)
            {
                Log("exit_install_fallback_ui_starting", "exit_install");
                installResult = await _storeUpdateProvider
                    .RequestDownloadAndInstallWithStoreUiAsync(
                        queryResult.UpdateSet,
                        progress => PublishProgress(
                            AppUpdateState.Installing,
                            "Installing update",
                            _versionProvider.InstalledVersion,
                            availableVersion,
                            mandatory,
                            progress),
                        linkedToken)
                    .ConfigureAwait(false);
            }

            if (installResult.Succeeded)
            {
                await _deferredStateStore.ClearAsync(CancellationToken.None).ConfigureAwait(false);
                Log("exit_install_completed", "exit_install", state: installResult.State.ToString());
                Publish(AppUpdateSnapshot.Idle(_versionProvider.InstalledVersion));
                return installResult;
            }

            Log(
                "exit_install_failed",
                "exit_install",
                state: installResult.State.ToString(),
                failedPackageCount: installResult.FailedPackageCount,
                extra: deferredState.PackageIdentitySnapshot.PackageFullName);
            await SaveRetryStateAsync(
                queryResult.UpdateSet,
                deferredState.PackageIdentitySnapshot,
                installResult,
                CancellationToken.None).ConfigureAwait(false);
            Publish(Failed("Update installation failed", "The app will close normally."));
            return installResult;
        }
        catch (OperationCanceledException) when (linkedToken.IsCancellationRequested)
        {
            Log("exit_install_canceled", "exit_install");
            if (deferredState?.PackageIdentitySnapshot is not null)
            {
                await SaveRetryStateAsync(
                    new StorePackageUpdateSet(Array.Empty<StorePackageUpdateInfo>()),
                    deferredState.PackageIdentitySnapshot,
                    new StoreUpdateOperationResult(StoreUpdateOperationState.Canceled),
                    CancellationToken.None).ConfigureAwait(false);
            }
            Publish(Failed("Update installation canceled", "The app will close normally."));
            return new StoreUpdateOperationResult(StoreUpdateOperationState.Canceled);
        }
        catch (Exception ex)
        {
            LogException("exit_install_failed", "exit_install", ex);
            if (deferredState?.PackageIdentitySnapshot is not null)
            {
                await SaveRetryStateAsync(
                    new StorePackageUpdateSet(Array.Empty<StorePackageUpdateInfo>()),
                    deferredState.PackageIdentitySnapshot,
                    new StoreUpdateOperationResult(StoreUpdateOperationState.OtherError, ErrorMessage: ex.Message),
                    CancellationToken.None).ConfigureAwait(false);
            }
            Publish(Failed("Update installation failed", "The app will close normally."));
            return new StoreUpdateOperationResult(StoreUpdateOperationState.OtherError, ErrorMessage: ex.Message);
        }
        finally
        {
            timeoutCts.Dispose();
            if (entered)
            {
                _workflowSemaphore.Release();
            }
        }
    }

    private async Task<DeferredUpdateState?> LoadDeferredStateAsync(CancellationToken cancellationToken)
    {
        DeferredUpdateState? state = await _deferredStateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return null;
        }

        if (state.PackageIdentitySnapshot is null
            && state.PackageFamilyNames.Count == 0
            && !state.InstallDeferred
            && state.RetryCount <= 0)
        {
            return null;
        }

        return state;
    }

    private bool IsDeferredInstallThrottled(
        DeferredUpdateState? existingState,
        PackageIdentitySnapshot? currentIdentitySnapshot)
    {
        if (existingState is null
            || currentIdentitySnapshot is null
            || existingState.PackageIdentitySnapshot is null
            || !IsSamePackageIdentity(existingState.PackageIdentitySnapshot, currentIdentitySnapshot)
            || existingState.RetryCount <= 0)
        {
            return false;
        }

        if (existingState.RetryCount >= _options.ExitInstallRetryCountLimit)
        {
            return true;
        }

        if (existingState.LastFailureUtc is not DateTimeOffset lastFailureUtc)
        {
            return false;
        }

        return _options.ExitInstallRetryCooldown > TimeSpan.Zero
            && DateTimeOffset.UtcNow - lastFailureUtc < _options.ExitInstallRetryCooldown;
    }

    private bool ShouldSkipStartupUpdateCheckDueToInterval(DeferredUpdateState? existingState)
    {
        if (existingState is null || existingState.InstallDeferred)
        {
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<DateTimeOffset> history = TrimCheckHistory(existingState.RecentCheckHistoryUtc, now);
        bool tooFrequent = _options.MinimumCheckInterval > TimeSpan.Zero
            && existingState.LastCheckUtc.HasValue
            && now - existingState.LastCheckUtc.Value < _options.MinimumCheckInterval;
        bool overRollingLimit = history.Count >= 10;
        return tooFrequent || overRollingLimit;
    }

    private static IReadOnlyList<DateTimeOffset> TrimCheckHistory(IEnumerable<DateTimeOffset>? history, DateTimeOffset nowUtc)
    {
        if (history is null)
        {
            return Array.Empty<DateTimeOffset>();
        }

        return history
            .Where(x => nowUtc - x <= TimeSpan.FromHours(24))
            .OrderBy(x => x)
            .ToArray();
    }

    private static IReadOnlyList<DateTimeOffset> AppendCheckHistory(IEnumerable<DateTimeOffset>? history, DateTimeOffset nowUtc)
    {
        List<DateTimeOffset> list = TrimCheckHistory(history, nowUtc).ToList();
        list.Add(nowUtc);
        return list;
    }

    private static bool IsSamePackageIdentity(
        PackageIdentitySnapshot? left,
        PackageIdentitySnapshot? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        return string.Equals(left.PackageFamilyName, right.PackageFamilyName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.PackageFullName, right.PackageFullName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.PackageVersion, right.PackageVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static PackageIdentitySnapshot? ResolvePrimaryPackageIdentitySnapshot(
        IEnumerable<StorePackageUpdateInfo> updates)
    {
        StorePackageUpdateInfo? update = updates.FirstOrDefault();
        if (update is null)
        {
            return null;
        }

        return new PackageIdentitySnapshot(
            update.PackageFamilyName,
            update.PackageFullName,
            update.Version);
    }

    private static PackageIdentitySnapshot? ResolveMatchingPackageIdentity(
        IEnumerable<StorePackageUpdateInfo> updates,
        PackageIdentitySnapshot targetIdentity)
    {
        StorePackageUpdateInfo? exactMatch = updates.FirstOrDefault(update =>
            string.Equals(update.PackageFamilyName, targetIdentity.PackageFamilyName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(update.PackageFullName, targetIdentity.PackageFullName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(update.Version, targetIdentity.PackageVersion, StringComparison.OrdinalIgnoreCase));

        return exactMatch is not null
            ? new PackageIdentitySnapshot(
                exactMatch.PackageFamilyName,
                exactMatch.PackageFullName,
                exactMatch.Version)
            : ResolvePrimaryPackageIdentitySnapshot(updates);
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
            availableVersion,
            HasActiveQueueItem: true,
            PackageDetailText: BuildPackageDetailText(progress)));
    }

    private static string? BuildPackageDetailText(StoreUpdateOperationProgress progress)
    {
        if (progress.BytesDownloaded is not ulong downloaded || progress.TotalBytesToDownload is not ulong total || total == 0)
        {
            return progress.PackageFamilyName;
        }

        return $"{progress.PackageFamilyName ?? "Package"} ({downloaded / (1024d * 1024d):0.0} MB / {total / (1024d * 1024d):0.0} MB)";
    }

    private static string BuildFailureGuidance(StoreUpdateOperationState state) =>
        state switch
        {
            StoreUpdateOperationState.Canceled => "Update was canceled. Retry when ready.",
            StoreUpdateOperationState.ErrorLowBattery => "Charge the device, then retry.",
            StoreUpdateOperationState.ErrorWiFiRecommended => "Connect to Wi-Fi for a reliable update, then retry.",
            StoreUpdateOperationState.ErrorWiFiRequired => "Connect to Wi-Fi, then retry.",
            _ => "Retry again later from the Update button.",
        };

    private async Task SaveDetectedStateAsync(
        StorePackageUpdateSet updateSet,
        PackageIdentitySnapshot? identitySnapshot,
        DeferredUpdateState? existingState,
        CancellationToken cancellationToken)
    {
        if (identitySnapshot is null)
        {
            return;
        }

        bool sameIdentity = IsSamePackageIdentity(existingState?.PackageIdentitySnapshot, identitySnapshot);
        await _deferredStateStore.SaveAsync(new DeferredUpdateState
        {
            LastCheckUtc = DateTimeOffset.UtcNow,
            LastUpdateDetectedUtc = DateTimeOffset.UtcNow,
            LastDownloadCompletedUtc = sameIdentity ? existingState?.LastDownloadCompletedUtc : null,
            LastInstallDeferredUtc = sameIdentity ? existingState?.LastInstallDeferredUtc : null,
            LastAttemptUtc = sameIdentity ? existingState?.LastAttemptUtc : null,
            LastFailureUtc = sameIdentity ? existingState?.LastFailureUtc : null,
            LastSuccessfulOperationUtc = sameIdentity ? existingState?.LastSuccessfulOperationUtc : null,
            InstallDeferred = sameIdentity && existingState?.InstallDeferred == true,
            RetryCount = sameIdentity ? existingState?.RetryCount ?? 0 : 0,
            LastFailureCategory = sameIdentity ? existingState?.LastFailureCategory : null,
            LastFailureMessage = sameIdentity ? existingState?.LastFailureMessage : null,
            PackageIdentitySnapshot = identitySnapshot,
            PackageFamilyNames = updateSet.Updates
                .Select(update => update.PackageFamilyName)
                .Where(packageFamilyName => !string.IsNullOrWhiteSpace(packageFamilyName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            RecentCheckHistoryUtc = AppendCheckHistory(existingState?.RecentCheckHistoryUtc, DateTimeOffset.UtcNow),
        }, cancellationToken).ConfigureAwait(false);
        Log("deferred_state_update_detected_saved", "check");
    }

    private Task SaveDeferredInstallStateAsync(
        StorePackageUpdateSet updateSet,
        PackageIdentitySnapshot? identitySnapshot,
        StoreUpdateOperationResult result,
        CancellationToken cancellationToken)
    {
        Log("deferred_install_state_saved", "install", state: result.State.ToString());
        return SaveStateAsync(updateSet, identitySnapshot, result, installDeferred: true, cancellationToken);
    }

    private Task SaveThrottledDeferredInstallSuppressedStateAsync(
        StorePackageUpdateSet updateSet,
        PackageIdentitySnapshot? identitySnapshot,
        StoreUpdateOperationResult result,
        CancellationToken cancellationToken)
    {
        Log("deferred_install_suppressed_by_retry_policy_saved", "install", state: result.State.ToString());
        return SaveStateAsync(updateSet, identitySnapshot, result, installDeferred: false, cancellationToken);
    }

    private Task SaveRetryStateAsync(
        StorePackageUpdateSet updateSet,
        PackageIdentitySnapshot? identitySnapshot,
        StoreUpdateOperationResult result,
        CancellationToken cancellationToken)
    {
        Log("retry_state_saved", "retry", state: result.State.ToString());
        return SaveStateAsync(updateSet, identitySnapshot, result, installDeferred: false, cancellationToken);
    }

    private async Task SaveStateAsync(
        StorePackageUpdateSet updateSet,
        PackageIdentitySnapshot? identitySnapshot,
        StoreUpdateOperationResult result,
        bool installDeferred,
        CancellationToken cancellationToken)
    {
        DeferredUpdateState? existingState = await _deferredStateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        bool sameIdentity = IsSamePackageIdentity(existingState?.PackageIdentitySnapshot, identitySnapshot);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int retryCount = result.Succeeded
            ? sameIdentity
                ? existingState?.RetryCount ?? 0
                : 0
            : (sameIdentity ? existingState?.RetryCount ?? 0 : 0) + 1;
        await _deferredStateStore.SaveAsync(new DeferredUpdateState
        {
            LastCheckUtc = now,
            LastUpdateDetectedUtc = sameIdentity ? existingState?.LastUpdateDetectedUtc : now,
            LastDownloadCompletedUtc = installDeferred && result.Succeeded
                ? now
                : sameIdentity
                    ? existingState?.LastDownloadCompletedUtc
                    : null,
            LastInstallDeferredUtc = installDeferred
                ? now
                : sameIdentity
                    ? existingState?.LastInstallDeferredUtc
                    : null,
            LastAttemptUtc = now,
            LastFailureUtc = result.Succeeded
                ? sameIdentity
                    ? existingState?.LastFailureUtc
                    : null
                : now,
            LastSuccessfulOperationUtc = result.Succeeded
                ? now
                : sameIdentity
                    ? existingState?.LastSuccessfulOperationUtc
                    : null,
            InstallDeferred = installDeferred && result.Succeeded,
            RetryCount = retryCount,
            LastFailureCategory = result.Succeeded
                ? sameIdentity
                    ? existingState?.LastFailureCategory
                    : null
                : result.State.ToString(),
            LastFailureMessage = result.Succeeded
                ? sameIdentity
                    ? existingState?.LastFailureMessage
                    : null
                : result.ErrorMessage,
            PackageIdentitySnapshot = identitySnapshot ?? existingState?.PackageIdentitySnapshot,
            PackageFamilyNames = updateSet.Updates
                .Select(update => update.PackageFamilyName)
                .Where(packageFamilyName => !string.IsNullOrWhiteSpace(packageFamilyName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            RecentCheckHistoryUtc = AppendCheckHistory(existingState?.RecentCheckHistoryUtc, now),
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

    private static void ValidateProductionPolicy(StoreUpdateOptions options)
    {
        if (!options.EnableStartupUpdateCheck)
        {
            throw new InvalidOperationException("Store update policy violation: startup update check must remain enabled.");
        }

        if (!options.UseFallbackStoreUiWhenSilentUnavailable)
        {
            throw new InvalidOperationException("Store update policy violation: Store/OS fallback UI must remain enabled.");
        }
    }
}
