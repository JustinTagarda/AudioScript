using AudioScript.Services;
using System.Runtime.InteropServices;
using Xunit;

namespace AudioScript.Tests;

public sealed class SpeakerDiarizationDependencyCoordinatorTests
{
    [Fact]
    public async Task CheckStatusAsync_WhenAssetsMissing_ReturnsMissing()
    {
        string rootPath = CreateTempDirectory();
        try
        {
            AppDataPathProvider paths = new(localAppDataPath: rootPath);
            var assets = new FakeAssetProvisioningService(paths);
            using var logs = new ProcessLogService(Path.Combine(rootPath, "logs"));
            var manager = new PyannoteCommunityModelManager(assets, paths, architectureResolver: () => Architecture.X64);
            var coordinator = new SpeakerDiarizationDependencyCoordinator(
                assets,
                manager,
                new StubPythonRepairService(PythonSuccess()),
                logs);

            SpeakerDiarizationDependencyStatus status = await coordinator.CheckStatusAsync(CancellationToken.None);

            Assert.Equal(SpeakerDiarizationDependencyState.Missing, status.State);
            Assert.True(status.CanInstall);
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task InstallOrRepairAsync_WhenAssetsMissing_InstallsAndVerifies()
    {
        string rootPath = CreateTempDirectory();
        try
        {
            AppDataPathProvider paths = new(localAppDataPath: rootPath);
            var assets = new FakeAssetProvisioningService(paths);
            using var logs = new ProcessLogService(Path.Combine(rootPath, "logs"));
            var manager = new PyannoteCommunityModelManager(assets, paths, architectureResolver: () => Architecture.X64);
            var coordinator = new SpeakerDiarizationDependencyCoordinator(
                assets,
                manager,
                new StubPythonRepairService(PythonSuccess()),
                logs);
            SpeakerDiarizationDependencyStatus status = await coordinator.CheckStatusAsync(CancellationToken.None);

            SpeakerDiarizationDependencyResult result = await coordinator.InstallOrRepairAsync(
                status,
                progress: null,
                CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.True(assets.IsInstalled(PyannoteCommunityModelManager.PyannotePythonX64AssetId));
            Assert.True(assets.IsInstalled(PyannoteCommunityModelManager.PyannoteModelAssetId));
            Assert.Equal(2, assets.InstalledAssetIds.Count);
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task InstallOrRepairAsync_WhenPythonProbeFails_ReplacesAssetsAndVerifies()
    {
        string rootPath = CreateTempDirectory();
        try
        {
            AppDataPathProvider paths = new(localAppDataPath: rootPath);
            var assets = new FakeAssetProvisioningService(paths);
            assets.InstallTestAsset(PyannoteCommunityModelManager.PyannotePythonX64AssetId);
            assets.InstallTestAsset(PyannoteCommunityModelManager.PyannoteModelAssetId);
            using var logs = new ProcessLogService(Path.Combine(rootPath, "logs"));
            var manager = new PyannoteCommunityModelManager(assets, paths, architectureResolver: () => Architecture.X64);
            var python = new StubPythonRepairService(
                PythonFailure(),
                PythonSuccess());
            var coordinator = new SpeakerDiarizationDependencyCoordinator(
                assets,
                manager,
                python,
                logs);
            SpeakerDiarizationDependencyStatus status = await coordinator.CheckStatusAsync(CancellationToken.None);

            SpeakerDiarizationDependencyResult result = await coordinator.InstallOrRepairAsync(
                status,
                progress: null,
                CancellationToken.None);

            Assert.Equal(SpeakerDiarizationDependencyState.Corrupted, status.State);
            Assert.True(result.Succeeded);
            Assert.Equal(2, assets.RemovedAssetIds.Count);
            Assert.Equal(2, assets.InstalledAssetIds.Count);
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    private static PythonDependencyRepairResult PythonSuccess()
    {
        return new PythonDependencyRepairResult(
            true,
            [
                new DependencyHealthItem("torch", "torch", DependencyHealthCategory.PythonModule, DependencyHealthStatus.Completed, "Ready", string.Empty, []),
            ],
            []);
    }

    private static PythonDependencyRepairResult PythonFailure()
    {
        return new PythonDependencyRepairResult(
            false,
            [
                new DependencyHealthItem("torch", "torch", DependencyHealthCategory.PythonModule, DependencyHealthStatus.Failed, "missing", "impact", []),
            ],
            []);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "AudioScript-diarization-deps-tests", Guid.NewGuid().ToString("N"));
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

    private sealed class StubPythonRepairService : IPythonDependencyRepairService
    {
        private readonly Queue<PythonDependencyRepairResult> _results;

        public StubPythonRepairService(params PythonDependencyRepairResult[] results)
        {
            _results = new Queue<PythonDependencyRepairResult>(results);
        }

        public Task<PythonDependencyRepairResult> ValidateAndRepairAsync(
            IProgress<StartupDependencyHealthProgress>? progress,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_results.Count > 1 ? _results.Dequeue() : _results.Peek());
        }
    }

    private sealed class FakeAssetProvisioningService : IAssetProvisioningService
    {
        private readonly AppDataPathProvider _paths;
        private readonly HashSet<string> _installed = new(StringComparer.OrdinalIgnoreCase);

        public FakeAssetProvisioningService(AppDataPathProvider paths)
        {
            _paths = paths;
        }

        public List<string> InstalledAssetIds { get; } = new();

        public List<string> RemovedAssetIds { get; } = new();

        public IReadOnlyList<ProvisionedAssetDescriptor> GetManifestAssets()
        {
            return Array.Empty<ProvisionedAssetDescriptor>();
        }

        public AssetProvisioningStatus GetStatus(string assetId)
        {
            return new AssetProvisioningStatus(
                assetId,
                assetId,
                _installed.Contains(assetId) ? AssetProvisioningState.Ready : AssetProvisioningState.Missing,
                ResolveInstallPath(assetId));
        }

        public string ResolveInstallPath(string assetId)
        {
            return assetId switch
            {
                PyannoteCommunityModelManager.PyannotePythonX64AssetId => Path.Combine(_paths.PythonRuntimesPath, "win-x64"),
                PyannoteCommunityModelManager.PyannoteModelAssetId => Path.Combine(_paths.PyannoteAssetsPath, "speaker-diarization-community-1"),
                _ => Path.Combine(_paths.RootPath, assetId),
            };
        }

        public bool IsInstalled(string assetId)
        {
            return _installed.Contains(assetId);
        }

        public Task InstallAssetAsync(
            string assetId,
            IProgress<AssetProvisioningProgress>? progress,
            CancellationToken cancellationToken)
        {
            InstallTestAsset(assetId);
            InstalledAssetIds.Add(assetId);
            progress?.Report(new AssetProvisioningProgress(assetId, assetId, "Ready", 1, 1, 100));
            return Task.CompletedTask;
        }

        public Task RemoveAssetAsync(string assetId, CancellationToken cancellationToken)
        {
            RemovedAssetIds.Add(assetId);
            _installed.Remove(assetId);
            string installPath = ResolveInstallPath(assetId);
            if (Directory.Exists(installPath))
            {
                Directory.Delete(installPath, recursive: true);
            }

            return Task.CompletedTask;
        }

        public void InstallTestAsset(string assetId)
        {
            _installed.Add(assetId);
            string installPath = ResolveInstallPath(assetId);
            Directory.CreateDirectory(installPath);
            if (string.Equals(assetId, PyannoteCommunityModelManager.PyannotePythonX64AssetId, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.Combine(installPath, "Lib", "site-packages", "torch"));
                Directory.CreateDirectory(Path.Combine(installPath, "Lib", "site-packages", "torchaudio"));
                Directory.CreateDirectory(Path.Combine(installPath, "Lib", "site-packages", "pyannote", "audio"));
                File.WriteAllText(Path.Combine(installPath, "python.exe"), string.Empty);
            }
            else if (string.Equals(assetId, PyannoteCommunityModelManager.PyannoteModelAssetId, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.Combine(installPath, "segmentation"));
                Directory.CreateDirectory(Path.Combine(installPath, "embedding"));
                Directory.CreateDirectory(Path.Combine(installPath, "plda"));
                File.WriteAllText(Path.Combine(installPath, "config.yaml"), "pipeline: community-1");
                File.WriteAllText(Path.Combine(installPath, "segmentation", "pytorch_model.bin"), string.Empty);
                File.WriteAllText(Path.Combine(installPath, "embedding", "pytorch_model.bin"), string.Empty);
                File.WriteAllText(Path.Combine(installPath, "plda", "plda.npz"), string.Empty);
                File.WriteAllText(Path.Combine(installPath, "plda", "xvec_transform.npz"), string.Empty);
            }
        }
    }
}
