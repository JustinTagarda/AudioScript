using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class SherpaDiarizationModelManagerTests {
    [Fact]
    public void EnsureInstalled_Succeeds_WhenBundledAssetsExist() {
        string rootPath = CreateTempDirectory();

        try {
            string segmentationPath = Path.Combine(
                rootPath,
                "sherpa-diarization",
                "pyannote-segmentation-3.0",
                "model.onnx");
            string embeddingPath = Path.Combine(
                rootPath,
                "sherpa-diarization",
                "nemo-speakernet",
                "nemo_en_speakerverification_speakernet.onnx");
            Directory.CreateDirectory(Path.GetDirectoryName(segmentationPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(embeddingPath)!);
            File.WriteAllBytes(segmentationPath, [1]);
            File.WriteAllBytes(embeddingPath, [1]);

            var manager = new SherpaDiarizationModelManager(rootPath);

            manager.EnsureInstalled();
        }
        finally {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void EnsureInstalled_Throws_WhenBundledAssetIsMissing() {
        string rootPath = CreateTempDirectory();

        try {
            var manager = new SherpaDiarizationModelManager(rootPath);

            Assert.Throws<FileNotFoundException>(manager.EnsureInstalled);
        }
        finally {
            DeleteDirectory(rootPath);
        }
    }

    private static string CreateTempDirectory() {
        string path = Path.Combine(Path.GetTempPath(), $"AudioScript-sherpa-model-tests-{Guid.NewGuid():N}");
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

