using Windows.Services.Store;

namespace AudioScript.Services;

public sealed class StoreUpdateClient : IStoreUpdateClient
{
    private readonly ProcessLogService _processLogService;
    private readonly Func<IntPtr>? _ownerWindowHandleProvider;
    private string _lastInstalledVersion = "unknown";
    private string _lastAvailableVersion = "unknown";
    private int _lastUpdateCount;
    private bool _lastMandatory;

    public StoreUpdateClient(ProcessLogService processLogService, Func<IntPtr>? ownerWindowHandleProvider = null)
    {
        _processLogService = processLogService ?? throw new ArgumentNullException(nameof(processLogService));
        _ownerWindowHandleProvider = ownerWindowHandleProvider;
    }

    public async Task<StoreUpdateQueryResult> QueryUpdatesAsync(CancellationToken cancellationToken)
    {
        StoreContext context = CreateStoreContext();
        IReadOnlyList<StorePackageUpdate> updates =
            await context.GetAppAndOptionalStorePackageUpdatesAsync().AsTask(cancellationToken);
        StorePackageUpdateInfo[] updateInfos = updates
            .Select(update => new StorePackageUpdateInfo(
                PackageFamilyName: update.Package.Id.FamilyName,
                Version: FormatPackageVersion(update.Package.Id.Version),
                IsMandatory: update.Mandatory))
            .ToArray();
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

    public Task<StoreUpdateOperationResult> DownloadUpdatesAsync(
        StorePackageUpdateSet updateSet,
        Action<StoreUpdateOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        StoreContext context = CreateStoreContext();
        IReadOnlyList<StorePackageUpdate> updates = ResolveNativeUpdates(updateSet);
        return RunOperationAsync(
            operationName: "download",
            operationFactory: () => context.TrySilentDownloadStorePackageUpdatesAsync(updates),
            progress,
            cancellationToken);
    }

    public Task<StoreUpdateOperationResult> InstallUpdatesAsync(
        StorePackageUpdateSet updateSet,
        Action<StoreUpdateOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        StoreContext context = CreateStoreContext();
        IReadOnlyList<StorePackageUpdate> updates = ResolveNativeUpdates(updateSet);
        return RunOperationAsync(
            operationName: "install",
            operationFactory: () => context.TrySilentDownloadAndInstallStorePackageUpdatesAsync(updates),
            progress,
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

    private StoreContext CreateStoreContext()
    {
        StoreContext context = StoreContext.GetDefault();
        IntPtr ownerWindowHandle = _ownerWindowHandleProvider?.Invoke() ?? IntPtr.Zero;
        if (ownerWindowHandle == IntPtr.Zero)
        {
            return context;
        }

        try
        {
            WinRT.Interop.InitializeWithWindow.Initialize(context, ownerWindowHandle);
        }
        catch (Exception ex)
        {
            _processLogService.Log(
                "StoreUpdate",
                $"store_context_window_initialize_failed; {UpdateLogMetadata.Build("check", "Error", _lastInstalledVersion, _lastAvailableVersion, _lastUpdateCount, _lastMandatory, 0, ex)}",
                ProcessLogLevel.Warning);
        }

        return context;
    }

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
