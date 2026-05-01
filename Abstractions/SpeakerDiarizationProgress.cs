namespace AudioScript.Abstractions;

public sealed record SpeakerDiarizationProgress(
    long ProcessedSamples,
    long TotalSamples)
{
    public double Percent => TotalSamples <= 0
        ? 0
        : Math.Clamp(ProcessedSamples * 100d / TotalSamples, 0, 100);
}
