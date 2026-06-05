using System.Windows.Threading;
using System.Reflection;
using AudioScript.Services.Store;
using Windows.Foundation;
using Windows.Services.Store;

namespace AudioScript.Services;

public sealed class MicrosoftStoreUpdateProvider : IMicrosoftStoreUpdateProvider
{
    private readonly IAppVersionProvider _versionProvider;
    private readonly IStoreContextProvider _storeContextProvider;
    private readonly ProcessLogService _processLogService;
    private readonly Dictionary<object, Delegate> _trackedQueueItems = new(ReferenceEqualityComparer.Instance);
    private readonly object _queueSync = new();
    private string _lastInstalledVersion = "unknown";
    private string _lastAvailableVersion = "unknown";
    private int _lastUpdateCount;
    private bool _lastMandatory;

    public event EventHandler<StoreQueueRecoveryState>? QueueStateChanged;

    public MicrosoftStoreUpdateProvider(
        IAppVersionProvider versionProvider,
        ProcessLogService processLogService,
        IStoreContextProvider storeContextProvider)
    {
        _versionProvider = versionProvider ?? throw new ArgumentNullException(nameof(versionProvider));
        _processLogService = processLogService ?? throw new ArgumentNullException(nameof(processLogService));
        _storeContextProvider = storeContextProvider ?? throw new ArgumentNullException(nameof(storeContextProvider));
    }

    public bool IsStoreUpdateSupported() =>
        _versionProvider.IsPackaged && _storeContextProvider.IsStoreApiAvailable;

    public async Task<StoreUpdateQueryResult> GetAvailableUpdatesAsync(CancellationToken cancellationToken = default)
    {
        StoreContext context = _storeContextProvider.GetContext();
        IReadOnlyList<StorePackageUpdate> updates =
            await context.GetAppAndOptionalStorePackageUpdatesAsync().AsTask(cancellationToken);
        StorePackageUpdateInfo[] updateInfos = updates
            .Select(update => new StorePackageUpdateInfo(
                PackageFamilyName: update.Package.Id.FamilyName,
                PackageFullName: update.Package.Id.FullName,
                Version: FormatPackageVersion(update.Package.Id.Version),
                IsMandatory: update.Mandatory))
            .ToArray();
        _lastInstalledVersion = _versionProvider.InstalledVersion;
        _lastUpdateCount = updateInfos.Length;
        _lastAvailableVersion = updateInfos
            .Select(update => update.Version)
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .OrderByDescending(version => version, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? "unknown";
        _lastMandatory = updateInfos.Any(update => update.IsMandatory);

        Log(
            "store_update_query_completed",
            operation: "check",
            state: "Completed",
            failedPackageCount: 0,
            extra: $"canSilent={context.CanSilentlyDownloadStorePackageUpdates}");

        return new StoreUpdateQueryResult(
            new StorePackageUpdateSet(updateInfos, updates),
            context.CanSilentlyDownloadStorePackageUpdates);
    }

    public bool CanSilentlyDownloadUpdates(StoreUpdateQueryResult queryResult) =>
        queryResult.CanSilentlyDownload;

    public Task<StoreUpdateOperationResult> TrySilentDownloadAsync(
        StorePackageUpdateSet updateSet,
        Action<StoreUpdateOperationProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        StoreContext context = _storeContextProvider.GetContext();
        IReadOnlyList<StorePackageUpdate> updates = ResolveNativeUpdates(updateSet);
        return RunOperationAsync(
            operationName: "download",
            operationFactory: () => context.TrySilentDownloadStorePackageUpdatesAsync(updates),
            progress,
            cancellationToken);
    }

    public Task<StoreUpdateOperationResult> TrySilentDownloadAndInstallAsync(
        StorePackageUpdateSet updateSet,
        Action<StoreUpdateOperationProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        StoreContext context = _storeContextProvider.GetContext();
        IReadOnlyList<StorePackageUpdate> updates = ResolveNativeUpdates(updateSet);
        return RunOperationAsync(
            operationName: "install",
            operationFactory: () => context.TrySilentDownloadAndInstallStorePackageUpdatesAsync(updates),
            progress,
            cancellationToken);
    }

    public Task<StoreUpdateOperationResult> RequestDownloadAndInstallWithStoreUiAsync(
        StorePackageUpdateSet updateSet,
        Action<StoreUpdateOperationProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<StorePackageUpdate> updates = ResolveNativeUpdates(updateSet);
        return RunStoreUiOperationAsync(
            () =>
            {
                // Store UI flows for desktop apps must initialize StoreContext and invoke
                // RequestDownloadAndInstall on the UI thread with owner window association.
                StoreContext context = _storeContextProvider.GetContext();
                return RunOperationAsync(
                    operationName: "fallback_ui",
                    operationFactory: () => context.RequestDownloadAndInstallStorePackageUpdatesAsync(updates),
                    progress,
                    cancellationToken);
            },
            cancellationToken);
    }

    public async Task<StoreQueueRecoveryState> TryGetActiveQueueStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            StoreQueueRecoveryState queueState = await ReadQueueStateAsync(cancellationToken).ConfigureAwait(false);
            if (!queueState.HasActiveQueueItem)
            {
                ClearTrackedQueueItems();
            }

            return queueState;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _processLogService.LogException("StoreUpdate", "store_queue_recovery_failed", ex);
            return new StoreQueueRecoveryState(false);
        }
    }

    private async Task<StoreQueueRecoveryState> ReadQueueStateAsync(CancellationToken cancellationToken)
    {
        StoreContext context = _storeContextProvider.GetContext();
        MethodInfo? method = context.GetType().GetMethod("GetAssociatedStoreQueueItemsAsync");
        if (method is null)
        {
            return new StoreQueueRecoveryState(false);
        }

        object? operation = method.Invoke(context, null);
        if (operation is null)
        {
            return new StoreQueueRecoveryState(false);
        }

        dynamic dynamicOperation = operation;
        object items = await dynamicOperation.AsTask().ConfigureAwait(false);
        List<StoreQueueRecoveryState> activeStates = new();

        foreach (object item in (System.Collections.IEnumerable)items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StoreQueueRecoveryState? state = TryBuildQueueState(item);
            if (state is null)
            {
                continue;
            }

            TrackQueueItem(item);
            activeStates.Add(state);
        }

        return activeStates.Count > 0
            ? MergeQueueStates(activeStates)
            : new StoreQueueRecoveryState(false);
    }

    private static StoreQueueRecoveryState? TryBuildQueueState(object queueItem)
    {
        Type queueItemType = queueItem.GetType();
        object? status = queueItemType.GetProperty("Status")?.GetValue(queueItem)
            ?? queueItemType.GetMethod("GetCurrentStatus")?.Invoke(queueItem, null);
        if (status is null)
        {
            return null;
        }

        Type statusType = status.GetType();
        string stateText = ReadStateText(statusType, status);
        if (!IsActiveQueueState(stateText))
        {
            return null;
        }

        object? updateStatus = statusType.GetProperty("UpdateStatus")?.GetValue(status);
        string? packageDetailText = ReadPackageDetailText(queueItemType, queueItem, updateStatus);
        double progressValue = ReadProgressValue(updateStatus);
        string? phaseText = ResolvePhaseText(updateStatus, stateText);

        return new StoreQueueRecoveryState(
            HasActiveQueueItem: true,
            PhaseText: phaseText,
            ProgressValue: progressValue,
            PackageDetailText: packageDetailText);
    }

    private static bool IsActiveQueueState(string stateText) =>
        string.Equals(stateText, "Active", StringComparison.OrdinalIgnoreCase)
        || string.Equals(stateText, "Downloading", StringComparison.OrdinalIgnoreCase)
        || string.Equals(stateText, "Deploying", StringComparison.OrdinalIgnoreCase)
        || string.Equals(stateText, "Pending", StringComparison.OrdinalIgnoreCase)
        || string.Equals(stateText, "ActivePending", StringComparison.OrdinalIgnoreCase)
        || string.Equals(stateText, "Paused", StringComparison.OrdinalIgnoreCase);

    private static string ReadStateText(Type statusType, object status)
    {
        object? stateValue = statusType.GetProperty("PackageInstallState")?.GetValue(status)
            ?? statusType.GetProperty("StoreQueueItemState")?.GetValue(status)
            ?? statusType.GetProperty("State")?.GetValue(status);
        return stateValue?.ToString() ?? "Unknown";
    }

    private static string ResolvePhaseText(object? updateStatus, string fallbackStateText)
    {
        if (updateStatus is null)
        {
            return fallbackStateText switch
            {
                "Downloading" => "Downloading",
                "Deploying" => "Installing",
                "Paused" => "Waiting",
                _ => "Preparing",
            };
        }

        Type updateStatusType = updateStatus.GetType();
        object? updateState = updateStatusType.GetField("PackageUpdateState")?.GetValue(updateStatus)
            ?? updateStatusType.GetProperty("PackageUpdateState")?.GetValue(updateStatus);
        string stateText = updateState?.ToString() ?? "Pending";
        return stateText switch
        {
            "Pending" => "Preparing",
            "Downloading" => "Downloading",
            "Deploying" => "Installing",
            "Completed" => "Completed",
            "Canceled" => "Canceled",
            "ErrorLowBattery" => "Failed",
            "ErrorWiFiRecommended" => "Failed",
            "ErrorWiFiRequired" => "Failed",
            "OtherError" => "Failed",
            _ => "Preparing",
        };
    }

    private static double ReadProgressValue(object? updateStatus)
    {
        if (updateStatus is null)
        {
            return 0;
        }

        Type updateStatusType = updateStatus.GetType();
        object? totalProgress = updateStatusType.GetField("TotalDownloadProgress")?.GetValue(updateStatus)
            ?? updateStatusType.GetProperty("TotalDownloadProgress")?.GetValue(updateStatus);
        if (totalProgress is double totalValue && !double.IsNaN(totalValue) && !double.IsInfinity(totalValue))
        {
            return Math.Clamp(totalValue, 0, 1);
        }

        object? packageProgress = updateStatusType.GetField("PackageDownloadProgress")?.GetValue(updateStatus)
            ?? updateStatusType.GetProperty("PackageDownloadProgress")?.GetValue(updateStatus);
        if (packageProgress is double packageValue && !double.IsNaN(packageValue) && !double.IsInfinity(packageValue))
        {
            return Math.Clamp(packageValue, 0, 1);
        }

        return 0;
    }

    private static string? ReadPackageDetailText(Type queueItemType, object queueItem, object? updateStatus)
    {
        string? packageFamilyName = null;
        if (updateStatus is not null)
        {
            Type packageStatusType = updateStatus.GetType();
            packageFamilyName = packageStatusType.GetField("PackageFamilyName")?.GetValue(updateStatus)?.ToString()
                ?? packageStatusType.GetProperty("PackageFamilyName")?.GetValue(updateStatus)?.ToString();
        }

        packageFamilyName ??= queueItemType.GetProperty("PackageFamilyName")?.GetValue(queueItem)?.ToString();
        if (updateStatus is null)
        {
            return packageFamilyName;
        }

        Type packageStatusType2 = updateStatus.GetType();
        object? downloadedValue = packageStatusType2.GetField("PackageBytesDownloaded")?.GetValue(updateStatus)
            ?? packageStatusType2.GetProperty("PackageBytesDownloaded")?.GetValue(updateStatus);
        object? totalValue = packageStatusType2.GetField("PackageDownloadSizeInBytes")?.GetValue(updateStatus)
            ?? packageStatusType2.GetProperty("PackageDownloadSizeInBytes")?.GetValue(updateStatus);
        if (downloadedValue is ulong downloaded && totalValue is ulong total && total > 0)
        {
            return $"{packageFamilyName ?? "Package"} ({downloaded / (1024d * 1024d):0.0} MB / {total / (1024d * 1024d):0.0} MB)";
        }

        return packageFamilyName;
    }

    private static StoreQueueRecoveryState MergeQueueStates(IReadOnlyList<StoreQueueRecoveryState> states)
    {
        StoreQueueRecoveryState first = states
            .OrderByDescending(state => state.ProgressValue)
            .First();
        return new StoreQueueRecoveryState(
            HasActiveQueueItem: true,
            PhaseText: first.PhaseText ?? "Preparing",
            ProgressValue: first.ProgressValue,
            PackageDetailText: first.PackageDetailText);
    }

    private void TrackQueueItem(object queueItem)
    {
        TypedEventHandler<StoreQueueItem, object> handler = OnQueueItemStatusChanged;
        lock (_queueSync)
        {
            if (_trackedQueueItems.ContainsKey(queueItem))
            {
                return;
            }

            _trackedQueueItems[queueItem] = handler;
        }

        try
        {
            EventInfo? statusChangedEvent = queueItem.GetType().GetEvent("StatusChanged");
            if (statusChangedEvent is null)
            {
                lock (_queueSync)
                {
                    _trackedQueueItems.Remove(queueItem);
                }
                return;
            }

            statusChangedEvent.AddEventHandler(queueItem, handler);
        }
        catch (Exception ex)
        {
            lock (_queueSync)
            {
                _trackedQueueItems.Remove(queueItem);
            }
            _processLogService.LogException("StoreUpdate", "store_queue_item_tracking_failed", ex);
        }
    }

    private void OnQueueItemStatusChanged(StoreQueueItem sender, object args)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                StoreQueueRecoveryState state = await ReadQueueStateAsync(CancellationToken.None).ConfigureAwait(false);
                if (state.HasActiveQueueItem)
                {
                    QueueStateChanged?.Invoke(this, state);
                    return;
                }

                ClearTrackedQueueItems();
            }
            catch (Exception ex)
            {
                _processLogService.LogException("StoreUpdate", "store_queue_item_status_changed_failed", ex);
            }
        });
    }

    private void ClearTrackedQueueItems()
    {
        lock (_queueSync)
        {
            foreach (KeyValuePair<object, Delegate> trackedItem in _trackedQueueItems)
            {
                try
                {
                    EventInfo? statusChangedEvent = trackedItem.Key.GetType().GetEvent("StatusChanged");
                    statusChangedEvent?.RemoveEventHandler(trackedItem.Key, trackedItem.Value);
                }
                catch (Exception ex)
                {
                    _processLogService.LogException("StoreUpdate", "store_queue_item_tracking_clear_failed", ex);
                }
            }

            _trackedQueueItems.Clear();
        }
    }

    private async Task<StoreUpdateOperationResult> RunOperationAsync(
        string operationName,
        Func<Windows.Foundation.IAsyncOperationWithProgress<StorePackageUpdateResult, StorePackageUpdateStatus>> operationFactory,
        Action<StoreUpdateOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Windows.Foundation.IAsyncOperationWithProgress<StorePackageUpdateResult, StorePackageUpdateStatus> operation =
            operationFactory();
        operation.Progress = (_, status) =>
        {
            string phaseText = status.PackageUpdateState switch
            {
                StorePackageUpdateState.Pending => "Preparing",
                StorePackageUpdateState.Downloading => "Downloading",
                StorePackageUpdateState.Deploying => "Installing",
                StorePackageUpdateState.Completed => "Completed",
                StorePackageUpdateState.Canceled => "Canceled",
                StorePackageUpdateState.ErrorLowBattery => "Failed",
                StorePackageUpdateState.ErrorWiFiRecommended => "Failed",
                StorePackageUpdateState.ErrorWiFiRequired => "Failed",
                StorePackageUpdateState.OtherError => "Failed",
                _ => "Preparing",
            };
            progress?.Invoke(new StoreUpdateOperationProgress(
                ClampProgress(status.PackageDownloadProgress),
                PhaseText: phaseText,
                PackageFamilyName: status.PackageFamilyName,
                BytesDownloaded: status.PackageBytesDownloaded,
                TotalBytesToDownload: status.PackageDownloadSizeInBytes));
        };

        StorePackageUpdateResult result = await operation.AsTask(cancellationToken);
        StoreUpdateOperationState state = MapOperationState(result.OverallState);
        string[] failedPackageFamilyNames = result.StorePackageUpdateStatuses?
            .Where(status => MapOperationState(status.PackageUpdateState) != StoreUpdateOperationState.Completed)
            .Select(status => status.PackageFamilyName)
            .Where(packageFamilyName => !string.IsNullOrWhiteSpace(packageFamilyName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

        Log(
            $"store_update_{operationName}_completed",
            operation: operationName,
            state: state.ToString(),
            failedPackageCount: failedPackageFamilyNames.Length,
            extra: $"failedPackages={string.Join(",", failedPackageFamilyNames)}");
        return new StoreUpdateOperationResult(state, failedPackageFamilyNames.Length, FailedPackageFamilyNames: failedPackageFamilyNames);
    }

    private static async Task<StoreUpdateOperationResult> RunStoreUiOperationAsync(
        Func<Task<StoreUpdateOperationResult>> operation,
        CancellationToken cancellationToken)
    {
        Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return await operation().ConfigureAwait(false);
        }

        DispatcherOperation<Task<StoreUpdateOperationResult>> dispatcherOperation = dispatcher.InvokeAsync(operation);
        using (cancellationToken.Register(() => dispatcherOperation.Abort()))
        {
            Task<StoreUpdateOperationResult> operationTask = await dispatcherOperation.Task.ConfigureAwait(false);
            return await operationTask.ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<StorePackageUpdate> ResolveNativeUpdates(StorePackageUpdateSet updateSet)
    {
        ArgumentNullException.ThrowIfNull(updateSet);

        return updateSet.NativeUpdates as IReadOnlyList<StorePackageUpdate>
            ?? throw new InvalidOperationException("Store update set does not contain native Store updates.");
    }

    private static StoreUpdateOperationState MapOperationState(StorePackageUpdateState state) =>
        state switch
        {
            StorePackageUpdateState.Completed => StoreUpdateOperationState.Completed,
            StorePackageUpdateState.Canceled => StoreUpdateOperationState.Canceled,
            StorePackageUpdateState.ErrorLowBattery => StoreUpdateOperationState.ErrorLowBattery,
            StorePackageUpdateState.ErrorWiFiRecommended => StoreUpdateOperationState.ErrorWiFiRecommended,
            StorePackageUpdateState.ErrorWiFiRequired => StoreUpdateOperationState.ErrorWiFiRequired,
            StorePackageUpdateState.OtherError => StoreUpdateOperationState.OtherError,
            _ => StoreUpdateOperationState.Unknown,
        };

    private static double ClampProgress(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        return Math.Clamp(value, 0, 1);
    }

    private static string FormatPackageVersion(Windows.ApplicationModel.PackageVersion version) =>
        $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";

    private void Log(string eventName, string operation, string state, int failedPackageCount, string? extra = null)
    {
        string metadata = UpdateLogMetadata.Build(
            operation,
            state,
            _lastInstalledVersion,
            _lastAvailableVersion,
            _lastUpdateCount,
            _lastMandatory,
            failedPackageCount,
            extra: extra);
        _processLogService.Log("StoreUpdate", $"{eventName}; {metadata}");
    }
}
