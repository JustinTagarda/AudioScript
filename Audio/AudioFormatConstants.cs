using NAudio.Wave;

namespace AudioTranscript.Audio;

public static class AudioFormatConstants {
    public static readonly WaveFormat EngineWaveFormat = new(16000, 16, 1);

    public const int BytesPerSecond = 16000 * 2;
}