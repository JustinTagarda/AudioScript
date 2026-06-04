using NAudio.Wave;

namespace AudioScript.Abstractions;

public interface IPlaybackTranscriptionService {
    Task<string> TranscribePcmChunkAsync(
        byte[] pcmAudio,
        WaveFormat sourceFormat,
        string model,
        CancellationToken cancellationToken,
        string? diagnosticRoute = null);
}




