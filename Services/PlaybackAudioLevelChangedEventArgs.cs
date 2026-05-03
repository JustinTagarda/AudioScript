namespace AudioScript.Services;

public sealed class PlaybackAudioLevelChangedEventArgs : EventArgs
{
    public PlaybackAudioLevelChangedEventArgs(
        string sessionId,
        double peakLevel,
        string sourceName,
        double gainMultiplier = 1,
        bool automaticGainApplied = false)
    {
        SessionId = sessionId;
        PeakLevel = Math.Max(0, Math.Min(1, peakLevel));
        SourceName = sourceName?.Trim() ?? string.Empty;
        GainMultiplier = double.IsFinite(gainMultiplier) && gainMultiplier > 0
            ? gainMultiplier
            : 1;
        AutomaticGainApplied = automaticGainApplied;
    }

    public string SessionId { get; }

    public double PeakLevel { get; }

    public string SourceName { get; }

    public double GainMultiplier { get; }

    public bool AutomaticGainApplied { get; }
}
