namespace AudioScript.Abstractions;

public interface IConfigurableAudioTranscriptionService : IAudioTranscriptionService
{
    Task<TranscriptionResult> TranscribeAudioFileAsync(
        string audioFilePath,
        string model,
        AudioTranscriptionRequestOptions options,
        CancellationToken cancellationToken,
        IProgress<TranscriptionProgressSnapshot>? progress = null,
        string? diagnosticRoute = null);
}
