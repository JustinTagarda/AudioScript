using System.Windows.Threading;
using AudioScript.Services.Store;
using Windows.Services.Store;

namespace AudioScript.Services;

public sealed class MicrosoftStoreUpdateProvider : IMicrosoftStoreUpdateProvider
{
    private readonly IAppVersionProvider _versionProvider;
    private readonly IStoreContextProvider _storeContextProvider;
    private readonly ProcessLogService _processLogService;
    private string _lastInstalledVersion = "unknown";
    private string _lastAvailableVersion = "unknown";
    private int _lastUpdateCount;
    private bool _lastMandatory;

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
            progress?.Invoke(new StoreUpdateOperationProgress(ClampProgress(status.PackageDownloadProgress)));
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
