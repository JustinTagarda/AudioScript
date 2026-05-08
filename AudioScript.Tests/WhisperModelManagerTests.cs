using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class WhisperModelManagerTests {
    [Fact]
    public void GetSelectableTranscriptionModels_IncludesInstalledSmallAndOptionalModels() {
        string rootPath = CreateTempDirectory();
        string bundledPath = Path.Combine(rootPath, "bundled");
        string optionalPath = Path.Combine(rootPath, "optional");

        try {
            Directory.CreateDirectory(bundledPath);
            Directory.CreateDirectory(optionalPath);

            using var logs = new ProcessLogService(Path.Combine(rootPath, "logs"));
            var manager = new WhisperModelManager(logs, optionalPath, bundledPath);
            File.WriteAllBytes(manager.ResolveModelPath(TranscriptionModelCatalog.WhisperSmall), [1]);
            File.WriteAllBytes(manager.ResolveModelPath(TranscriptionModelCatalog.WhisperMedium), [1]);

            string[] selectableIds = manager
                .GetSelectableTranscriptionModels()
                .Select(model => model.Id)
                .ToArray();

            Assert.Equal(
                new[] {
                    TranscriptionModelCatalog.WhisperSmall,
                    TranscriptionModelCatalog.WhisperMedium,
                },
                selectableIds);
        }
        finally {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void UninstallModel_RemovesInstalledModels() {
        string rootPath = CreateTempDirectory();
        string bundledPath = Path.Combine(rootPath, "bundled");
        string optionalPath = Path.Combine(rootPath, "optional");

        try {
            Directory.CreateDirectory(bundledPath);
            Directory.CreateDirectory(optionalPath);

            using var logs = new ProcessLogService(Path.Combine(rootPath, "logs"));
            var manager = new WhisperModelManager(logs, optionalPath, bundledPath);
            string smallPath = manager.ResolveModelPath(TranscriptionModelCatalog.WhisperSmall);
            string mediumPath = manager.ResolveModelPath(TranscriptionModelCatalog.WhisperMedium);
            File.WriteAllBytes(smallPath, [1]);
            File.WriteAllBytes(mediumPath, [1]);

            WhisperModelUninstallResult result = manager.UninstallModel(TranscriptionModelCatalog.WhisperMedium);
            WhisperModelUninstallResult smallResult = manager.UninstallModel(TranscriptionModelCatalog.WhisperSmall);

            Assert.True(result.WasDeleted);
            Assert.Equal(1, result.DeletedBytes);
            Assert.Equal(TranscriptionModelCatalog.WhisperMedium, result.ModelId);
            Assert.Equal(mediumPath, result.ModelPath);
            Assert.True(smallResult.WasDeleted);
            Assert.False(File.Exists(smallPath));
            Assert.False(File.Exists(mediumPath));
        }
        finally {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task InstallModelAsync_InstallsWhisperSmallFromProvisioningService()
    {
        string rootPath = CreateTempDirectory();
        string sourcePath = Path.Combine(rootPath, "source");
        string optionalPath = Path.Combine(rootPath, "optional");
        string logsPath = Path.Combine(rootPath, "logs");

        try
        {
            Directory.CreateDirectory(sourcePath);
            Directory.CreateDirectory(optionalPath);
            File.WriteAllBytes(Path.Combine(sourcePath, "ggml-small.bin"), [1, 2, 3]);
            using var logs = new ProcessLogService(logsPath);
            var service = new StubAssetProvisioningService(sourcePath, optionalPath);
            var manager = new WhisperModelManager(logs, optionalPath, assetProvisioningService: service);

            await manager.InstallModelAsync(TranscriptionModelCatalog.WhisperSmall, progress: null, CancellationToken.None);

            Assert.True(manager.IsModelInstalled(TranscriptionModelCatalog.WhisperSmall));
            Assert.True(service.WasInstallCalled);
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task InstallModelAsync_InstallsWhisperMediumFromProvisioningService()
    {
        string rootPath = CreateTempDirectory();
        string sourcePath = Path.Combine(rootPath, "source");
        string optionalPath = Path.Combine(rootPath, "optional");
        string logsPath = Path.Combine(rootPath, "logs");

        try
        {
            Directory.CreateDirectory(sourcePath);
            Directory.CreateDirectory(optionalPath);
            File.WriteAllBytes(Path.Combine(sourcePath, "ggml-medium.bin"), [7, 8, 9]);
            using var logs = new ProcessLogService(logsPath);
            var service = new StubAssetProvisioningService(sourcePath, optionalPath);
            var manager = new WhisperModelManager(logs, optionalPath, assetProvisioningService: service);

            await manager.InstallModelAsync(TranscriptionModelCatalog.WhisperMedium, progress: null, CancellationToken.None);

            Assert.True(manager.IsModelInstalled(TranscriptionModelCatalog.WhisperMedium));
            Assert.True(service.WasInstallCalled);
            Assert.Equal("whisper-medium", service.LastInstalledAssetId);
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void UninstallModel_ReturnsNotDeleted_WhenOptionalModelFileIsAlreadyMissing() {
        string rootPath = CreateTempDirectory();
        string bundledPath = Path.Combine(rootPath, "bundled");
        string optionalPath = Path.Combine(rootPath, "optional");

        try {
            Directory.CreateDirectory(bundledPath);
            Directory.CreateDirectory(optionalPath);

            using var logs = new ProcessLogService(Path.Combine(rootPath, "logs"));
            var manager = new WhisperModelManager(logs, optionalPath, bundledPath);

            WhisperModelUninstallResult result = manager.UninstallModel(TranscriptionModelCatalog.WhisperMedium);

            Assert.False(result.WasDeleted);
            Assert.Equal(0, result.DeletedBytes);
            Assert.Equal(TranscriptionModelCatalog.WhisperMedium, result.ModelId);
            Assert.EndsWith("ggml-medium.bin", result.ModelPath);
        }
        finally {
            DeleteDirectory(rootPath);
        }
    }

    private static string CreateTempDirectory() {
        string path = Path.Combine(Path.GetTempPath(), $"AudioScript-whisper-model-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path) {
        if (!Directory.Exists(path)) {
            return;
        }

        Directory.Delete(path, recursive: true);
    }

    private sealed class StubAssetProvisioningService : IAssetProvisioningService
    {
        private readonly string _sourceDirectoryPath;
        private readonly string _installDirectoryPath;

        public StubAssetProvisioningService(string sourcePath, string installDirectoryPath)
        {
            _sourceDirectoryPath = sourcePath;
            _installDirectoryPath = installDirectoryPath;
        }

        public bool WasInstallCalled { get; private set; }
        public string? LastInstalledAssetId { get; private set; }

        public IReadOnlyList<ProvisionedAssetDescriptor> GetManifestAssets()
        {
            return Array.Empty<ProvisionedAssetDescriptor>();
        }

        public AssetProvisioningStatus GetStatus(string assetId)
        {
            return new AssetProvisioningStatus(assetId, assetId, AssetProvisioningState.Missing, ResolveInstallPath(assetId));
        }

        public string ResolveInstallPath(string assetId)
        {
            return Path.Combine(_installDirectoryPath, ResolveFileName(assetId));
        }

        public bool IsInstalled(string assetId)
        {
            return File.Exists(ResolveInstallPath(assetId));
        }

        public Task InstallAssetAsync(string assetId, IProgress<AssetProvisioningProgress>? progress, CancellationToken cancellationToken)
        {
            WasInstallCalled = true;
            LastInstalledAssetId = assetId;
            string targetPath = Path.GetFullPath(ResolveInstallPath(assetId));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            string sourcePath = Path.Combine(_sourceDirectoryPath, ResolveFileName(assetId));
            File.Copy(sourcePath, targetPath, overwrite: true);
            return Task.CompletedTask;
        }

        public Task RemoveAssetAsync(string assetId, CancellationToken cancellationToken)
        {
            string targetPath = Path.GetFullPath(ResolveInstallPath(assetId));
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            return Task.CompletedTask;
        }

        private static string ResolveFileName(string assetId)
        {
            return assetId switch
            {
                "whisper-small" => "ggml-small.bin",
                "whisper-medium" => "ggml-medium.bin",
                "whisper-large-v3" => "ggml-large-v3.bin",
                "whisper-large-v3-turbo" => "ggml-large-v3-turbo.bin",
                _ => throw new InvalidOperationException($"Unknown test asset id '{assetId}'."),
            };
        }
    }
}
