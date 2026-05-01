namespace AudioScript.Abstractions;

public sealed record TranscriptionProgressSnapshot(
    TranscriptionProgressPhase Phase,
    double Percent,
    double OverallPercent,
    int? CurrentChunk,
    int? TotalChunks,
    TimeSpan ProcessedAudio,
    TimeSpan TotalAudio,
    TimeSpan Elapsed,
    TimeSpan? EstimatedRemaining,
    string DetailMessage)
{
    public static TranscriptionProgressSnapshot Create(
        TranscriptionProgressPhase phase,
        double percent,
        double? overallPercent,
        int? currentChunk,
        int? totalChunks,
        TimeSpan processedAudio,
        TimeSpan totalAudio,
        TimeSpan elapsed,
        string detailMessage)
    {
        double clampedPercent = double.IsFinite(percent)
            ? Math.Clamp(percent, 0, 100)
            : 0;
        double clampedOverallPercent = overallPercent is double resolvedOverallPercent && double.IsFinite(resolvedOverallPercent)
            ? Math.Clamp(resolvedOverallPercent, 0, 100)
            : clampedPercent;

        TimeSpan? estimatedRemaining = null;
        if (clampedOverallPercent >= 5
            && clampedOverallPercent < 100
            && elapsed >= TimeSpan.FromSeconds(5))
        {
            double remainingRatio = (100d - clampedOverallPercent) / clampedOverallPercent;
            estimatedRemaining = TimeSpan.FromSeconds(Math.Max(0, elapsed.TotalSeconds * remainingRatio));
        }

        return new TranscriptionProgressSnapshot(
            phase,
            clampedPercent,
            clampedOverallPercent,
            currentChunk,
            totalChunks,
            processedAudio < TimeSpan.Zero ? TimeSpan.Zero : processedAudio,
            totalAudio < TimeSpan.Zero ? TimeSpan.Zero : totalAudio,
            elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed,
            estimatedRemaining,
            detailMessage);
    }
}
