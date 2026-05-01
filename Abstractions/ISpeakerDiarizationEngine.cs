namespace AudioScript.Abstractions;

public interface ISpeakerDiarizationEngine {
    Task<IReadOnlyList<SpeakerDiarizationTurn>> DiarizeAudioFileAsync(
        string audioFilePath,
        CancellationToken cancellationToken,
        IProgress<SpeakerDiarizationProgress>? progress = null);
}
