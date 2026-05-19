using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class StartupAssetProvisioningCoordinatorTests
{
    [Fact]
    public async Task ProvisionRequiredAssetsAsync_ContinuesWhenOneAssetFails_AndCapturesFailureDetails()
    {
        string rootPath = CreateTempDirectory();

        try
        {
            var service = new FakeAssetProvisioningService(
                new[]
                {
                    CreateRequiredAsset("asset-a", "Asset A"),
                    CreateRequiredAsset("asset-b", "Asset B"),
                    CreateRequiredAsset("asset-c", "Asset C"),
                },
                failingAssetId: "asset-b");
            using var logs = new ProcessLogService(rootPath);
            var coordinator = new StartupAssetProvisioningCoordinator(service, logs);

            StartupProvisioningResult result = await coordinator.ProvisionRequiredAssetsAsync(
                progress: null,
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(3, result.RequiredAssetCount);
            Assert.Equal(2, result.InstalledAssetCount);
            Assert.Equal(1, result.FailedAssetCount);
            Assert.False(result.WasCanceled);
            Assert.Single(result.Failures);
            Assert.Equal("asset-b", result.Failures[0].AssetId);
            Assert.Equal(3, service.InstallAttempts.Count);
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task ProvisionRequiredAssetsAsync_AllSuccessful_ReturnsSucceededWithNoFailures()
    {
        string rootPath = CreateTempDirectory();

        try
        {
            var service = new FakeAssetProvisioningService(
                new[]
                {
                    CreateRequiredAsset("asset-a", "Asset A"),
                    CreateRequiredAsset("asset-b", "Asset B"),
                });
            using var logs = new ProcessLogService(rootPath);
            var coordinator = new StartupAssetProvisioningCoordinator(service, logs);

            StartupProvisioningResult result = await coordinator.ProvisionRequiredAssetsAsync(
                progress: null,
                CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal(2, result.RequiredAssetCount);
            Assert.Equal(2, result.InstalledAssetCount);
            Assert.Equal(0, result.FailedAssetCount);
            Assert.Empty(result.Failures);
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task ProvisionRequiredAssetsAsync_WhenCanceled_ThrowsOperationCanceledException()
    {
        string rootPath = CreateTempDirectory();

        try
        {
            var service = new FakeAssetProvisioningService(
                new[]
                {
                    CreateRequiredAsset("asset-a", "Asset A"),
                    CreateRequiredAsset("asset-b", "Asset B"),
                },
                cancelOnInstallAttempt: 2);
            using var logs = new ProcessLogService(rootPath);
            var coordinator = new StartupAssetProvisioningCoordinator(service, logs);

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                coordinator.ProvisionRequiredAssetsAsync(progress: null, CancellationToken.None));
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    private static ProvisionedAssetDescriptor CreateRequiredAsset(string id, string displayName)
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

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "AudioScript-StartupCoordinatorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class FakeAssetProvisioningService : IAssetProvisioningService
    {
        private readonly IReadOnlyList<ProvisionedAssetDescriptor> _manifestAssets;
        private readonly HashSet<string> _installedAssetIds;
        private readonly string? _failingAssetId;
        private readonly int? _cancelOnInstallAttempt;

        public FakeAssetProvisioningService(
            IReadOnlyList<ProvisionedAssetDescriptor> manifestAssets,
            string? failingAssetId = null,
            int? cancelOnInstallAttempt = null)
        {
            _manifestAssets = manifestAssets;
            _installedAssetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _failingAssetId = failingAssetId;
            _cancelOnInstallAttempt = cancelOnInstallAttempt;
        }

        public List<string> InstallAttempts { get; } = new();

        public IReadOnlyList<ProvisionedAssetDescriptor> GetManifestAssets() => _manifestAssets;

        public AssetProvisioningStatus GetStatus(string assetId)
        {
            ProvisionedAssetDescriptor descriptor = _manifestAssets.First(asset =>
                string.Equals(asset.Id, assetId, StringComparison.OrdinalIgnoreCase));
            bool isInstalled = _installedAssetIds.Contains(assetId);
            return new AssetProvisioningStatus(
                assetId,
                descriptor.DisplayName,
                isInstalled ? AssetProvisioningState.Ready : AssetProvisioningState.Missing,
                ResolveInstallPath(assetId));
        }

        public string ResolveInstallPath(string assetId) => Path.Combine("C:\\", "fake", assetId);

        public bool IsInstalled(string assetId) => _installedAssetIds.Contains(assetId);

        public Task InstallAssetAsync(string assetId, IProgress<AssetProvisioningProgress>? progress, CancellationToken cancellationToken)
        {
            InstallAttempts.Add(assetId);

            if (_cancelOnInstallAttempt.HasValue && InstallAttempts.Count == _cancelOnInstallAttempt.Value)
            {
                throw new OperationCanceledException("Canceled by test.");
            }

            if (string.Equals(_failingAssetId, assetId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Simulated install failure.");
            }

            _installedAssetIds.Add(assetId);
            return Task.CompletedTask;
        }

        public Task RemoveAssetAsync(string assetId, CancellationToken cancellationToken)
        {
            _installedAssetIds.Remove(assetId);
            return Task.CompletedTask;
        }
    }
}
