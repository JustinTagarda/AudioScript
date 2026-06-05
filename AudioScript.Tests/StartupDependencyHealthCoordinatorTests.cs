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
        var python = new StubPythonRepairService(new PythonDependencyRepairResult(true,
        [
            new DependencyHealthItem("torch", "torch", DependencyHealthCategory.PythonModule, DependencyHealthStatus.Completed, "Ready", string.Empty, [])
        ], []));

        var coordinator = new StartupDependencyHealthCoordinator(assetCoordinator, python, logs);
        StartupDependencyHealthResult result = await coordinator.RunAsync(progress: null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.Degraded);
        Assert.Empty(result.FailedItems);
    }

    [Fact]
    public async Task RunAsync_WhenPythonRepairFails_ReturnsDegradedWithFailedItem()
    {
        var assetService = new FakeAssetProvisioningService([
            RequiredAsset("a", "Asset A"),
        ]);
        using var logs = new ProcessLogService();
        var assetCoordinator = new StartupAssetProvisioningCoordinator(assetService, logs);
        var python = new StubPythonRepairService(new PythonDependencyRepairResult(false,
        [
            new DependencyHealthItem("torch", "torch", DependencyHealthCategory.PythonModule, DependencyHealthStatus.Failed, "missing", "impact", [])
        ],
        [
            new DependencyRepairAttempt("tier2", 1, false, 1, "failed")
        ]));

        var coordinator = new StartupDependencyHealthCoordinator(assetCoordinator, python, logs);
        StartupDependencyHealthResult result = await coordinator.RunAsync(progress: null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.Degraded);
        Assert.Single(result.FailedItems);
        Assert.Equal("torch", result.FailedItems[0].Id);
        Assert.Single(result.AttemptedRepairs);
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

    private sealed class StubPythonRepairService : IPythonDependencyRepairService
    {
        private readonly PythonDependencyRepairResult _result;

        public StubPythonRepairService(PythonDependencyRepairResult result)
        {
            _result = result;
        }

        public Task<PythonDependencyRepairResult> ValidateAndRepairAsync(IProgress<StartupDependencyHealthProgress>? progress, CancellationToken cancellationToken)
        {
            return Task.FromResult(_result);
        }
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
