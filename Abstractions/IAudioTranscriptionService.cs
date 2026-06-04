namespace AudioScript.Abstractions;

public interface IAudioTranscriptionService
{
    Task<TranscriptionResult> TranscribeAudioFileAsync(
        string audioFilePath,
        string model,
        CancellationToken cancellationToken,
        IProgress<TranscriptionProgressSnapshot>? progress = null,
        string? diagnosticRoute = null);
}
