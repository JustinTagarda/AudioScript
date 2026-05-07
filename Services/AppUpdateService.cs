namespace AudioScript.Services;

public sealed class AppUpdateServiceOptions
{
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan DiscoveryRetryDelay { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan InstallRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan InstallQuietPeriod { get; init; } = TimeSpan.FromSeconds(5);

    public int? MaxDiscoveryRetryCount { get; init; }

    public int? MaxInstallRetryCount { get; init; }
}

public sealed class AppUpdateService : IAppUpdateService
{
    private readonly IAppVersionProvider _versionProvider;
    private readonly IStoreUpdateClient _storeUpdateClient;
    private readonly ProcessLogService _processLogService;
    private readonly Func<bool> _isAppBusy;
    private readonly AppUpdateServiceOptions _options;
    private readonly SemaphoreSlim _workflowSemaphore = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _lifetimeCts;
    private Task? _startupTask;
    private Task? _discoveryRetryTask;
    private Task? _installRetryTask;
    private bool _started;
    private bool _discoveryRetryScheduled;
    private bool _installRetryScheduled;
    private int _discoveryRetryCount;
    private int _installRetryCount;
    private AppUpdateSnapshot _currentSnapshot;

    public AppUpdateService(
        IAppVersionProvider versionProvider,
        IStoreUpdateClient storeUpdateClient,
        ProcessLogService processLogService,
        Func<bool> isAppBusy,
        AppUpdateServiceOptions? options = null)
    {
        _versionProvider = versionProvider ?? throw new ArgumentNullException(nameof(versionProvider));
        _storeUpdateClient = storeUpdateClient ?? throw new ArgumentNullException(nameof(storeUpdateClient));
        _processLogService = processLogService ?? throw new ArgumentNullException(nameof(processLogService));
        _isAppBusy = isAppBusy ?? throw new ArgumentNullException(nameof(isAppBusy));
        _options = options ?? new AppUpdateServiceOptions();
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
            _startupTask = RunStartupWorkflowAsync(_lifetimeCts.Token);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Task? startupTask;
        Task? discoveryRetryTask;
        Task? installRetryTask;
        lock (_sync)
        {
            _lifetimeCts?.Cancel();
            startupTask = _startupTask;
            discoveryRetryTask = _discoveryRetryTask;
            installRetryTask = _installRetryTask;
        }

        Task[] tasks = new[] { startupTask, discoveryRetryTask, installRetryTask }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal application shutdown.
            }
            catch (Exception ex)
            {
                LogException("update_stop_failed", ex);
            }
        }

        lock (_sync)
        {
            _startupTask = null;
            _discoveryRetryTask = null;
            _installRetryTask = null;
            _lifetimeCts?.Dispose();
            _lifetimeCts = null;
            _started = false;
            _discoveryRetryScheduled = false;
            _installRetryScheduled = false;
            _discoveryRetryCount = 0;
            _installRetryCount = 0;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _workflowSemaphore.Dispose();
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await _workflowSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunWorkflowCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _workflowSemaphore.Release();
        }
    }

    private async Task RunStartupWorkflowAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_options.StartupDelay, cancellationToken).ConfigureAwait(false);
            await RunOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log("startup_update_check_canceled", "Application update startup check canceled.");
        }
        catch (Exception ex)
        {
            LogException("startup_update_check_failed", ex);
            Publish(Failed("Update check failed", "AudioScript will retry update detection later."));
            ScheduleDiscoveryRetry();
        }
    }

    private async Task RunWorkflowCoreAsync(CancellationToken cancellationToken)
    {
        string installedVersion = _versionProvider.InstalledVersion;
        if (!_versionProvider.IsPackaged)
        {
            Log("update_check_skipped_unpacked", $"installedVersion={installedVersion}");
            Publish(AppUpdateSnapshot.Idle(installedVersion));
            return;
        }

        Publish(new AppUpdateSnapshot(
            AppUpdateState.Checking,
            "Checking for updates",
            "Looking for Microsoft Store updates.",
            IsMandatoryUpdateAvailable: false,
            IsProgressVisible: false,
            ProgressValue: 0,
            InstalledVersion: installedVersion,
            AvailableVersion: null));
        Log("update_check_started", $"installedVersion={installedVersion}");

        StoreUpdateQueryResult queryResult;
        try
        {
            queryResult = await _storeUpdateClient.QueryUpdatesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogException("update_check_failed", ex);
            Publish(Failed("Update check failed", "AudioScript will retry update detection later."));
            ScheduleDiscoveryRetry();
            return;
        }

        if (!queryResult.UpdateSet.HasUpdates)
        {
            Log("no_updates_available", $"installedVersion={installedVersion}");
            ResetRetryCounts();
            Publish(AppUpdateSnapshot.Idle(installedVersion));
            return;
        }

        bool mandatory = queryResult.UpdateSet.Updates.Any(update => update.IsMandatory);
        string? availableVersion = ResolveHighestVersion(queryResult.UpdateSet.Updates);
        Publish(UpdateAvailable(installedVersion, availableVersion, mandatory));
        Log(
            "updates_available",
            $"count={queryResult.UpdateSet.Updates.Count}; mandatory={mandatory}; availableVersion={availableVersion ?? "unknown"}");

        if (!queryResult.CanSilentlyDownload)
        {
            Log("silent_download_unavailable", "Microsoft Store silent download is unavailable.");
            Publish(Deferred(
                "Update deferred",
                "Microsoft Store automatic updates are unavailable right now. AudioScript will retry later.",
                installedVersion,
                availableVersion,
                mandatory));
            ScheduleDiscoveryRetry();
            return;
        }

        StoreUpdateOperationResult downloadResult = await DownloadAsync(
            queryResult.UpdateSet,
            installedVersion,
            availableVersion,
            mandatory,
            cancellationToken).ConfigureAwait(false);

        if (downloadResult.State != StoreUpdateOperationState.Completed)
        {
            Log(
                "download_failed",
                $"state={downloadResult.State}; failedPackageCount={downloadResult.FailedPackageCount}");
            Publish(Deferred(
                "Update deferred",
                "AudioScript could not download the Store update. It will retry later.",
                installedVersion,
                availableVersion,
                mandatory));
            ScheduleDiscoveryRetry();
            return;
        }

        await WaitForIdleAndInstallAsync(
            queryResult.UpdateSet,
            installedVersion,
            availableVersion,
            mandatory,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<StoreUpdateOperationResult> DownloadAsync(
        StorePackageUpdateSet updateSet,
        string installedVersion,
        string? availableVersion,
        bool mandatory,
        CancellationToken cancellationToken)
    {
        Publish(new AppUpdateSnapshot(
            AppUpdateState.Downloading,
            "Downloading update",
            "AudioScript is downloading the latest Store package in the background.",
            mandatory,
            IsProgressVisible: true,
            ProgressValue: 0,
            installedVersion,
            availableVersion));

        try
        {
            StoreUpdateOperationResult result = await _storeUpdateClient.DownloadUpdatesAsync(
                updateSet,
                progress => PublishProgress(AppUpdateState.Downloading, "Downloading update", installedVersion, availableVersion, mandatory, progress),
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogException("download_failed", ex);
            return new StoreUpdateOperationResult(StoreUpdateOperationState.OtherError, ErrorMessage: ex.Message);
        }
    }

    private async Task WaitForIdleAndInstallAsync(
        StorePackageUpdateSet updateSet,
        string installedVersion,
        string? availableVersion,
        bool mandatory,
        CancellationToken cancellationToken)
    {
        while (IsAppBusy())
        {
            Publish(Deferred(
                "Update ready",
                "AudioScript will install the update when current audio work is idle.",
                installedVersion,
                availableVersion,
                mandatory));
            await Task.Delay(_options.InstallRetryDelay, cancellationToken).ConfigureAwait(false);
        }

        while (true)
        {
            await Task.Delay(_options.InstallQuietPeriod, cancellationToken).ConfigureAwait(false);
            if (!IsAppBusy())
            {
                break;
            }

            Publish(Deferred(
                "Update ready",
                "AudioScript will install the update when current audio work is idle.",
                installedVersion,
                availableVersion,
                mandatory));
            await Task.Delay(_options.InstallRetryDelay, cancellationToken).ConfigureAwait(false);
        }

        Publish(new AppUpdateSnapshot(
            AppUpdateState.Installing,
            "Installing update",
            "AudioScript is installing the latest Store package.",
            mandatory,
            IsProgressVisible: true,
            ProgressValue: 0,
            installedVersion,
            availableVersion));

        StoreUpdateOperationResult installResult;
        try
        {
            installResult = await _storeUpdateClient.InstallUpdatesAsync(
                updateSet,
                progress => PublishProgress(AppUpdateState.Installing, "Installing update", installedVersion, availableVersion, mandatory, progress),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogException("install_failed", ex);
            installResult = new StoreUpdateOperationResult(StoreUpdateOperationState.OtherError, ErrorMessage: ex.Message);
        }

        if (installResult.State == StoreUpdateOperationState.Completed)
        {
            ResetRetryCounts();
            Log("install_completed", $"availableVersion={availableVersion ?? "unknown"}");
            Publish(new AppUpdateSnapshot(
                AppUpdateState.Completed,
                "Update installed",
                "Latest package installed. Restart AudioScript to run the new version.",
                mandatory,
                IsProgressVisible: false,
                ProgressValue: 1,
                installedVersion,
                availableVersion));
            return;
        }

        Log(
            "install_failed",
            $"state={installResult.State}; failedPackageCount={installResult.FailedPackageCount}");
        Publish(Deferred(
            "Update deferred",
            "AudioScript could not install the Store update. It will retry when idle.",
            installedVersion,
            availableVersion,
            mandatory));
        ScheduleInstallRetry(updateSet, installedVersion, availableVersion, mandatory);
    }

    private void PublishProgress(
        AppUpdateState state,
        string stageText,
        string installedVersion,
        string? availableVersion,
        bool mandatory,
        StoreUpdateOperationProgress progress)
    {
        string status = state == AppUpdateState.Installing
            ? "AudioScript is installing the latest Store package."
            : "AudioScript is downloading the latest Store package in the background.";
        Publish(new AppUpdateSnapshot(
            state,
            stageText,
            status,
            mandatory,
            IsProgressVisible: true,
            ProgressValue: ClampProgress(progress.ProgressValue),
            installedVersion,
            availableVersion));
    }

    private void ScheduleDiscoveryRetry()
    {
        if (!TryScheduleRetry(isInstallRetry: false))
        {
            return;
        }

        CancellationToken token = LifetimeToken;
        Task retryTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_options.DiscoveryRetryDelay, token).ConfigureAwait(false);
                lock (_sync)
                {
                    _discoveryRetryScheduled = false;
                }

                await RunOnceAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                LogException("discovery_retry_failed", ex);
            }
            finally
            {
                lock (_sync)
                {
                    _discoveryRetryScheduled = false;
                }
            }
        }, token);

        lock (_sync)
        {
            _discoveryRetryTask = retryTask;
        }
    }

    private void ScheduleInstallRetry(
        StorePackageUpdateSet updateSet,
        string installedVersion,
        string? availableVersion,
        bool mandatory)
    {
        if (!TryScheduleRetry(isInstallRetry: true))
        {
            return;
        }

        CancellationToken token = LifetimeToken;
        Task retryTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_options.InstallRetryDelay, token).ConfigureAwait(false);
                lock (_sync)
                {
                    _installRetryScheduled = false;
                }

                await WaitForIdleAndInstallAsync(updateSet, installedVersion, availableVersion, mandatory, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                LogException("install_retry_failed", ex);
            }
            finally
            {
                lock (_sync)
                {
                    _installRetryScheduled = false;
                }
            }
        }, token);

        lock (_sync)
        {
            _installRetryTask = retryTask;
        }
    }

    private bool TryScheduleRetry(bool isInstallRetry)
    {
        lock (_sync)
        {
            if (isInstallRetry)
            {
                if (_installRetryScheduled || HasReachedLimit(_installRetryCount, _options.MaxInstallRetryCount))
                {
                    return false;
                }

                _installRetryScheduled = true;
                _installRetryCount++;
                Log("install_retry_scheduled", $"retryCount={_installRetryCount}");
                return true;
            }

            if (_discoveryRetryScheduled || HasReachedLimit(_discoveryRetryCount, _options.MaxDiscoveryRetryCount))
            {
                return false;
            }

            _discoveryRetryScheduled = true;
            _discoveryRetryCount++;
            Log("discovery_retry_scheduled", $"retryCount={_discoveryRetryCount}");
            return true;
        }
    }

    private CancellationToken LifetimeToken
    {
        get
        {
            lock (_sync)
            {
                if (_lifetimeCts is null)
                {
                    _lifetimeCts = new CancellationTokenSource();
                }

                return _lifetimeCts.Token;
            }
        }
    }

    private bool IsAppBusy()
    {
        try
        {
            return _isAppBusy();
        }
        catch (Exception ex)
        {
            LogException("busy_check_failed", ex);
            return true;
        }
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
            LogException("snapshot_subscriber_failed", ex);
        }
    }

    private AppUpdateSnapshot UpdateAvailable(string installedVersion, string? availableVersion, bool mandatory) =>
        new(
            AppUpdateState.UpdateAvailable,
            mandatory ? "Required update available" : "Update available",
            "AudioScript found a Microsoft Store update.",
            mandatory,
            IsProgressVisible: false,
            ProgressValue: 0,
            installedVersion,
            availableVersion);

    private static AppUpdateSnapshot Deferred(
        string stageText,
        string message,
        string installedVersion,
        string? availableVersion,
        bool mandatory) =>
        new(
            AppUpdateState.Deferred,
            stageText,
            message,
            mandatory,
            IsProgressVisible: false,
            ProgressValue: 0,
            installedVersion,
            availableVersion);

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

    private static bool HasReachedLimit(int currentCount, int? maxCount) =>
        maxCount.HasValue && currentCount >= maxCount.Value;

    private static double ClampProgress(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        return Math.Clamp(value, 0, 1);
    }

    private void ResetRetryCounts()
    {
        lock (_sync)
        {
            _discoveryRetryCount = 0;
            _installRetryCount = 0;
        }
    }

    private void Log(string eventName, string metadata)
    {
        _processLogService.Log("AppUpdate", $"{eventName}; {metadata}");
    }

    private void LogException(string eventName, Exception ex)
    {
        _processLogService.LogException("AppUpdate", eventName, ex);
    }
}
