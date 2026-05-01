using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class WhisperModelManagerTests {
    [Fact]
    public void GetSelectableTranscriptionModels_IncludesBundledSmallAndInstalledOptionalModels() {
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
    public void UninstallModel_RemovesOptionalModelButRejectsBundledSmall() {
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

            Assert.True(result.WasDeleted);
            Assert.Equal(1, result.DeletedBytes);
            Assert.Equal(TranscriptionModelCatalog.WhisperMedium, result.ModelId);
            Assert.Equal(mediumPath, result.ModelPath);
            Assert.True(File.Exists(smallPath));
            Assert.False(File.Exists(mediumPath));
            Assert.Throws<InvalidOperationException>(() =>
                manager.UninstallModel(TranscriptionModelCatalog.WhisperSmall));
        }
        finally {
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
}
