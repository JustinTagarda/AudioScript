using NAudio.Wave;

namespace AudioScript.Audio;

public sealed class PlaybackAudioFrameEventArgs : EventArgs {
    public PlaybackAudioFrameEventArgs(byte[] buffer, WaveFormat waveFormat) {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(waveFormat);

        Buffer = buffer;
        WaveFormat = waveFormat;
    }

    public byte[] Buffer { get; }

    public int BytesRecorded => Buffer.Length;

    public WaveFormat WaveFormat { get; }
}



