namespace AudioScript.Services;

public interface IAssetProvisioningService
{
    AssetProvisioningStatus GetStatus(string assetId);

    string ResolveInstallPath(string assetId);

    bool IsInstalled(string assetId);

    Task InstallAssetAsync(
        string assetId,
        IProgress<AssetProvisioningProgress>? progress,
        CancellationToken cancellationToken);

    Task RemoveAssetAsync(string assetId, CancellationToken cancellationToken);
}
