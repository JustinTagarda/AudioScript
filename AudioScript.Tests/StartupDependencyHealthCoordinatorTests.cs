using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class StartupDependencyHealthCoordinatorTests
{
    [Fact]
    public async Task RunAsync_WhenAllDependenciesHealthy_ReturnsSucceeded()
    {
        var assetService = new FakeAssetProvisioningService([
            RequiredAsset("a", "Asset A"),
        ]);
        using var logs = new ProcessLogService();
        var assetCoordinator = new StartupAssetProvisioningCoordinator(assetService, logs);

        var coordinator = new StartupDependencyHealthCoordinator(assetCoordinator, logs);
        StartupDependencyHealthResult result = await coordinator.RunAsync(progress: null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.Degraded);
        Assert.Empty(result.FailedItems);
    }

    [Fact]
    public async Task RunAsync_WhenPythonRepairFails_SkipsDiarizationPythonAtStartup()
    {
        var assetService = new FakeAssetProvisioningService([
            RequiredAsset("a", "Asset A"),
        ]);
        using var logs = new ProcessLogService();
        var assetCoordinator = new StartupAssetProvisioningCoordinator(assetService, logs);
        var coordinator = new StartupDependencyHealthCoordinator(assetCoordinator, logs);
        StartupDependencyHealthResult result = await coordinator.RunAsync(progress: null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.Degraded);
        Assert.Empty(result.FailedItems);
        Assert.Empty(result.AttemptedRepairs);
    }

    private static ProvisionedAssetDescriptor RequiredAsset(string id, string displayName)
    {
        return new ProvisionedAssetDescriptor
        {
            Id = id,
            DisplayName = displayName,
            Version = "1.0.0",
            Required = true,
            InstallKind = ProvisioningInstallKind.File,
            InstallRoot = ProvisioningInstallRoot.Models,
            InstallRelativePath = $"{id}.bin",
        };
    }

    private sealed class FakeAssetProvisioningService : IAssetProvisioningService
    {
        private readonly IReadOnlyList<ProvisionedAssetDescriptor> _manifestAssets;
        private readonly HashSet<string> _installed;

        public FakeAssetProvisioningService(IReadOnlyList<ProvisionedAssetDescriptor> manifestAssets)
        {
            _manifestAssets = manifestAssets;
            _installed = new HashSet<string>(manifestAssets.Select(item => item.Id), StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<ProvisionedAssetDescriptor> GetManifestAssets() => _manifestAssets;

        public AssetProvisioningStatus GetStatus(string assetId)
        {
            ProvisionedAssetDescriptor descriptor = _manifestAssets.First(item =>
                string.Equals(item.Id, assetId, StringComparison.OrdinalIgnoreCase));
            return new AssetProvisioningStatus(
                assetId,
                descriptor.DisplayName,
                _installed.Contains(assetId) ? AssetProvisioningState.Ready : AssetProvisioningState.Missing,
                ResolveInstallPath(assetId));
        }

        public string ResolveInstallPath(string assetId) => Path.Combine("C:\\", "fake", assetId);

        public bool IsInstalled(string assetId) => _installed.Contains(assetId);

        public Task InstallAssetAsync(string assetId, IProgress<AssetProvisioningProgress>? progress, CancellationToken cancellationToken)
        {
            _installed.Add(assetId);
            return Task.CompletedTask;
        }

        public Task RemoveAssetAsync(string assetId, CancellationToken cancellationToken)
        {
            _installed.Remove(assetId);
            return Task.CompletedTask;
        }
    }
}
