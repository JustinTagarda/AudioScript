namespace AudioScript.Audio;

public sealed record AudioGainProcessingResult(
    byte[] Buffer,
    double InputPeak,
    double OutputPeak,
    double GainMultiplier,
    bool IsAutomaticGainEnabled);
