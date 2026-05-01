using System.IO;

namespace AudioScript.Services;

public sealed class SherpaDiarizationModelManager {
    private const string SegmentationModelRelativePath =
        "sherpa-diarization/pyannote-segmentation-3.0/model.onnx";
    private const string EmbeddingModelRelativePath =
        "sherpa-diarization/nemo-speakernet/nemo_en_speakerverification_speakernet.onnx";

    private readonly string _bundledModelsDirectoryPath;

    public SherpaDiarizationModelManager(string? bundledModelsDirectoryPath = null) {
        _bundledModelsDirectoryPath = string.IsNullOrWhiteSpace(bundledModelsDirectoryPath)
            ? Path.Combine(AppContext.BaseDirectory, "assets", "models")
            : Path.GetFullPath(bundledModelsDirectoryPath);
    }

    public string SegmentationModelPath =>
        Path.Combine(_bundledModelsDirectoryPath, SegmentationModelRelativePath);

    public string EmbeddingModelPath =>
        Path.Combine(_bundledModelsDirectoryPath, EmbeddingModelRelativePath);

    public void EnsureInstalled() {
        if (!File.Exists(SegmentationModelPath)) {
            throw new FileNotFoundException(
                "Bundled sherpa-onnx segmentation model was not found. Reinstall or repair AudioScript.",
                SegmentationModelPath);
        }

        if (!File.Exists(EmbeddingModelPath)) {
            throw new FileNotFoundException(
                "Bundled sherpa-onnx speaker embedding model was not found. Reinstall or repair AudioScript.",
                EmbeddingModelPath);
        }
    }
}

