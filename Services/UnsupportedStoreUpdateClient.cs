namespace AudioScript.Services;

public sealed class UnsupportedStoreUpdateClient : IStoreUpdateClient
{
    public Task<StoreUpdateQueryResult> QueryUpdatesAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new StoreUpdateQueryResult(
            new StorePackageUpdateSet(Array.Empty<StorePackageUpdateInfo>()),
            CanSilentlyDownload: false));
    }

    public Task<StoreUpdateOperationResult> DownloadUpdatesAsync(
        StorePackageUpdateSet updateSet,
        Action<StoreUpdateOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new StoreUpdateOperationResult(StoreUpdateOperationState.OtherError));
    }

    public Task<StoreUpdateOperationResult> InstallUpdatesAsync(
        StorePackageUpdateSet updateSet,
        Action<StoreUpdateOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new StoreUpdateOperationResult(StoreUpdateOperationState.OtherError));
    }
}
