using AudioScript.Abstractions;

namespace AudioScript.Audio;

public sealed record AudioChunkingOptions(
    long DirectUploadLimitBytes,
    long ChunkUploadSafetyBytes,
    TimeSpan DirectRequestMaxDuration
) {
    public static AudioChunkingOptions Default { get; } = new(
        DirectUploadLimitBytes: 25_000_000,
        ChunkUploadSafetyBytes: 24_000_000,
        DirectRequestMaxDuration: TimeSpan.FromSeconds(1400));

    public SilenceAwareChunkPlannerOptions BuildRecommendedChunkPlannerOptions() {
        double maxSecondsByUploadSize =
            Math.Floor((ChunkUploadSafetyBytes - 44d) / AudioFormatConstants.EngineWaveFormat.AverageBytesPerSecond);
        double boundedMaxSeconds = Math.Min(maxSecondsByUploadSize, DirectRequestMaxDuration.TotalSeconds);
        TimeSpan maximumChunkDuration = TimeSpan.FromSeconds(Math.Max(420, boundedMaxSeconds));
        TimeSpan targetChunkDuration = maximumChunkDuration - TimeSpan.FromSeconds(90);
        if (targetChunkDuration < TimeSpan.FromMinutes(6)) {
            targetChunkDuration = maximumChunkDuration;
        }

        return new SilenceAwareChunkPlannerOptions(
            TargetChunkDuration: targetChunkDuration,
            MinimumChunkDuration: TimeSpan.FromMinutes(5),
            MaximumChunkDuration: maximumChunkDuration,
            OverlapDuration: TimeSpan.FromSeconds(10),
            SearchBeforePreferredSplit: TimeSpan.FromSeconds(90),
            SearchAfterPreferredSplit: TimeSpan.FromSeconds(30),
            MinimumSilenceDuration: TimeSpan.FromMilliseconds(450));
    }

    public bool RequiresChunking(long fileSizeBytes, TimeSpan duration) {
        return fileSizeBytes > DirectUploadLimitBytes || duration > DirectRequestMaxDuration;
    }
}
