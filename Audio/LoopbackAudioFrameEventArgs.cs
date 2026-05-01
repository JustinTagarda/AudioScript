using NAudio.Wave;

namespace AudioScript.Audio;

public sealed class LoopbackAudioFrameEventArgs : EventArgs {
    public LoopbackAudioFrameEventArgs(byte[] buffer, WaveFormat waveFormat, string sourceName = "") {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(waveFormat);

        Buffer = buffer;
        WaveFormat = waveFormat;
        SourceName = sourceName?.Trim() ?? string.Empty;
    }

    public byte[] Buffer { get; }

    public int BytesRecorded => Buffer.Length;

    public WaveFormat WaveFormat { get; }

    public string SourceName { get; }
}



