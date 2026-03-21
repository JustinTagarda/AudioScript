using NAudio.Wave;

namespace VoxTranscribe.Abstractions;

public interface IPlaybackTranscriptionService {
    Task<string> TranscribePcmChunkAsync(
        byte[] pcmAudio,
        WaveFormat sourceFormat,
        string model,
        CancellationToken cancellationToken);
}



