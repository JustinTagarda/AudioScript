using NAudio.Wave;

namespace VoxTranscribe.Audio;

public sealed class LoopbackAudioFrameEventArgs : EventArgs {
    public LoopbackAudioFrameEventArgs(byte[] buffer, WaveFormat waveFormat) {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(waveFormat);

        Buffer = buffer;
        WaveFormat = waveFormat;
    }

    public byte[] Buffer { get; }

    public int BytesRecorded => Buffer.Length;

    public WaveFormat WaveFormat { get; }
}


