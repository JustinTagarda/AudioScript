using Windows.Services.Store;

namespace AudioScript.Services;

public sealed class StoreUpdateClient : IStoreUpdateClient
{
    private readonly ProcessLogService _processLogService;
    private readonly Func<IntPtr>? _ownerWindowHandleProvider;

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

        Log(
            "store_update_query_completed",
            $"count={updateInfos.Length}; canSilent={context.CanSilentlyDownloadStorePackageUpdates}");

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
            $"state={state}; failedPackageCount={failedPackageFamilyNames.Length}; failedPackages={string.Join(",", failedPackageFamilyNames)}");
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
                $"store_context_window_initialize_failed; type={ex.GetType().Name}; message={ex.Message}",
                ProcessLogLevel.Warning);
        }

        return context;
    }

    private void Log(string eventName, string metadata)
    {
        _processLogService.Log("StoreUpdate", $"{eventName}; {metadata}");
    }
}
