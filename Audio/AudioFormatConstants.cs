using NAudio.Wave;

namespace VoxTranscribe.Audio;

public static class AudioFormatConstants {
    public static readonly WaveFormat EngineWaveFormat = new(16000, 16, 1);
}


