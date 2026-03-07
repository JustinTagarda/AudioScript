using NAudio.Wave;

namespace AudioTranscript.Abstractions;

public interface IPlaybackAudioTranscriptionService {
    Task<string> TranscribePcmChunkAsync(
        byte[] pcmAudio,
        WaveFormat sourceFormat,
        string model,
        CancellationToken cancellationToken);
}
