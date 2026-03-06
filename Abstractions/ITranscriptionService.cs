namespace AudioTranscript.Abstractions;

public interface ITranscriptionService {
    Task<TranscriptionResult> TranscribeFileAsync(
        string audioFilePath,
        string model,
        CancellationToken cancellationToken);

    Task<TranscriptionResult> TranscribePcmChunkAsync(
        ReadOnlyMemory<byte> pcm16KhzMono,
        string model,
        CancellationToken cancellationToken);
}
