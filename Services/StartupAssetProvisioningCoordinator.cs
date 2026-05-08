using System.Runtime.InteropServices;

namespace AudioScript.Services;

public sealed record StartupProvisioningResult(
    int RequiredAssetCount,
    int InstalledAssetCount,
    int FailedAssetCount,
    bool WasCanceled)
{
    public bool Succeeded => !WasCanceled && FailedAssetCount == 0;
}

public sealed class StartupAssetProvisioningCoordinator
{
    private readonly IAssetProvisioningService _assetProvisioningService;
    private readonly ProcessLogService _processLogService;

    public StartupAssetProvisioningCoordinator(
        IAssetProvisioningService assetProvisioningService,
        ProcessLogService processLogService)
    {
        _assetProvisioningService = assetProvisioningService;
        _processLogService = processLogService;
    }

    public IReadOnlyList<ProvisionedAssetDescriptor> GetRequiredAssetsNeedingInstall()
    {
        return _assetProvisioningService
            .GetManifestAssets()
            .Where(asset => asset.Required)
            .Where(asset => IsSupportedOnCurrentArchitecture(asset))
            .Where(asset => !_assetProvisioningService.IsInstalled(asset.Id))
            .ToArray();
    }

    public async Task<StartupProvisioningResult> ProvisionRequiredAssetsAsync(
        IProgress<AssetProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ProvisionedAssetDescriptor> requiredAssets = GetRequiredAssetsNeedingInstall();
        DateTimeOffset startUtc = DateTimeOffset.UtcNow;
        _processLogService.Log(
            nameof(StartupAssetProvisioningCoordinator),
            $"startup_provisioning begin utc='{startUtc:O}' assetCount={requiredAssets.Count}.");

        int installedCount = 0;
        int failedCount = 0;

        try
        {
            foreach (ProvisionedAssetDescriptor asset in requiredAssets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    AssetProvisioningStatus beforeStatus = _assetProvisioningService.GetStatus(asset.Id);
                    if (beforeStatus.State == AssetProvisioningState.Ready)
                    {
                        _processLogService.Log(
                            nameof(StartupAssetProvisioningCoordinator),
                            $"startup_provisioning asset='{asset.Id}' display='{asset.DisplayName}' status='ready' reason='Already installed.'.");
                        continue;
                    }

                    await _assetProvisioningService.InstallAssetAsync(asset.Id, progress, cancellationToken);
                    installedCount++;

                    AssetProvisioningStatus afterStatus = _assetProvisioningService.GetStatus(asset.Id);
                    _processLogService.Log(
                        nameof(StartupAssetProvisioningCoordinator),
                        $"startup_provisioning asset='{asset.Id}' display='{asset.DisplayName}' status='{afterStatus.State.ToString().ToLowerInvariant()}' reason='Installed successfully.'.");
                }
                catch (OperationCanceledException)
                {
                    _processLogService.Log(
                        nameof(StartupAssetProvisioningCoordinator),
                        $"startup_provisioning asset='{asset.Id}' display='{asset.DisplayName}' status='canceled' reason='Startup provisioning was canceled.'.",
                        ProcessLogLevel.Warning);
                    throw;
                }
                catch (Exception ex)
                {
                    failedCount++;
                    _processLogService.LogException(
                        nameof(StartupAssetProvisioningCoordinator),
                        $"Startup provisioning failed for asset '{asset.DisplayName}'.",
                        ex);
                }
            }
        }
        finally
        {
            DateTimeOffset endUtc = DateTimeOffset.UtcNow;
            _processLogService.Log(
                nameof(StartupAssetProvisioningCoordinator),
                $"startup_provisioning end utc='{endUtc:O}' durationMs={(long)(endUtc - startUtc).TotalMilliseconds} failureCount={failedCount}.");
        }

        return new StartupProvisioningResult(requiredAssets.Count, installedCount, failedCount, WasCanceled: false);
    }

    private static bool IsSupportedOnCurrentArchitecture(ProvisionedAssetDescriptor descriptor)
    {
        if (descriptor.SupportedArchitectures.Length == 0)
        {
            return true;
        }

        string currentArchitecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        };

        return descriptor.SupportedArchitectures.Any(architecture =>
            string.Equals(architecture, currentArchitecture, StringComparison.OrdinalIgnoreCase));
    }
}
