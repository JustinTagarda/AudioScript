namespace AudioScript.Audio;

public sealed record LiveAudioGainOptions(
    bool IsAutomaticGainEnabled = true,
    double ManualGainLevel = LiveAudioGainOptions.DefaultManualGainLevel)
{
    public const double DefaultManualGainLevel = 0.5;

    public static LiveAudioGainOptions Default { get; } = new();

    public LiveAudioGainOptions Validate()
    {
        if (!double.IsFinite(ManualGainLevel) || ManualGainLevel < 0 || ManualGainLevel > 1)
        {
            throw new InvalidOperationException("Live audio gain level must be between 0 and 1.");
        }

        return this;
    }

    public static double ManualGainLevelToMultiplier(double level)
    {
        double normalized = Math.Clamp(level, 0, 1);
        double exponent = normalized <= DefaultManualGainLevel
            ? -3 * (1 - (normalized / DefaultManualGainLevel))
            : 6 * ((normalized - DefaultManualGainLevel) / DefaultManualGainLevel);

        return Math.Pow(2, exponent);
    }
}
