using NAudio.Wave;

namespace VoxTranscriber.Abstractions;

public interface IPlaybackTranscriptionService {
    Task<string> TranscribePcmChunkAsync(
        byte[] pcmAudio,
        WaveFormat sourceFormat,
        string model,
        CancellationToken cancellationToken);
}



