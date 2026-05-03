using NAudio.Wave;

namespace AudioScript.Audio;

public sealed class LoopbackAudioFrameEventArgs : EventArgs {
    public LoopbackAudioFrameEventArgs(
        byte[] buffer,
        WaveFormat waveFormat,
        string sourceName = "",
        double appliedGain = 1,
        bool automaticGainApplied = false) {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(waveFormat);

        Buffer = buffer;
        WaveFormat = waveFormat;
        SourceName = sourceName?.Trim() ?? string.Empty;
        AppliedGain = double.IsFinite(appliedGain) && appliedGain > 0
            ? appliedGain
            : 1;
        AutomaticGainApplied = automaticGainApplied;
    }

    public byte[] Buffer { get; }

    public int BytesRecorded => Buffer.Length;

    public WaveFormat WaveFormat { get; }

    public string SourceName { get; }

    public double AppliedGain { get; }

    public bool AutomaticGainApplied { get; }
}



