namespace AudioTranscript.Abstractions;

public interface ITranscriptionService {
    Task<TranscriptionResult> TranscribeFileAsync(
        string audioFilePath,
        string model,
        CancellationToken cancellationToken);
}
