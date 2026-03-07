using AudioTranscript.Services;
using Xunit;

namespace AudioTranscript.Tests;

public sealed class AudioChunkPlannerTests {
    [Fact]
    public void PlanWaveChunks_StaysWithinSafeUploadBudget_AndUsesOverlap() {
        var planner = new AudioChunkPlanner();
        long waveDataBytes = 60L * 16000L * 2L * 30L;

        IReadOnlyList<AudioChunkPlan> plans = planner.PlanWaveChunks(
            waveDataBytes: waveDataBytes,
            averageBytesPerSecond: 16000 * 2,
            blockAlign: 2);

        Assert.True(plans.Count >= 2);
        Assert.All(plans, plan => Assert.True(plan.EstimatedFileSizeBytes < AudioChunkPlanner.MaxUploadBytes));
        Assert.Equal(TimeSpan.Zero, plans[0].OverlapDuration);
        Assert.True(plans.Skip(1).All(plan => plan.OverlapDuration == AudioChunkPlanner.DefaultOverlap));
    }
}
