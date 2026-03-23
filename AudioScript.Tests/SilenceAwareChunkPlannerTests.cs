using AudioScript.Abstractions;
using AudioScript.Audio;
using Xunit;

namespace AudioScript.Tests;

public sealed class SilenceAwareChunkPlannerTests {
    [Fact]
    public void PlanChunks_UsesNearbySilenceMidpoints_AndAddsOverlap() {
        var planner = new SilenceAwareChunkPlanner(new SilenceAwareChunkPlannerOptions(
            TargetChunkDuration: TimeSpan.FromMinutes(10),
            MinimumChunkDuration: TimeSpan.FromMinutes(4),
            MaximumChunkDuration: TimeSpan.FromMinutes(12),
            OverlapDuration: TimeSpan.FromSeconds(10),
            SearchBeforePreferredSplit: TimeSpan.FromSeconds(90),
            SearchAfterPreferredSplit: TimeSpan.FromSeconds(30),
            MinimumSilenceDuration: TimeSpan.FromMilliseconds(450)));

        IReadOnlyList<DiarizationChunkPlan> chunks = planner.PlanChunks(
            TimeSpan.FromMinutes(32),
            new[] {
                new TimeSpanRange(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(6)),
                new TimeSpanRange(TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(20) + TimeSpan.FromSeconds(4)),
            });

        Assert.Equal(3, chunks.Count);

        Assert.Equal(TimeSpan.Zero, chunks[0].RequestStart);
        Assert.Equal(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(3), chunks[0].KeepEnd);
        Assert.Equal(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(13), chunks[0].RequestEnd);

        Assert.Equal(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(3), chunks[1].KeepStart);
        Assert.Equal(TimeSpan.FromMinutes(20) + TimeSpan.FromSeconds(2), chunks[1].KeepEnd);
        Assert.Equal(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(53), chunks[1].RequestStart);
        Assert.Equal(TimeSpan.FromMinutes(20) + TimeSpan.FromSeconds(12), chunks[1].RequestEnd);

        Assert.Equal(TimeSpan.FromMinutes(20) + TimeSpan.FromSeconds(2), chunks[2].KeepStart);
        Assert.Equal(TimeSpan.FromMinutes(19) + TimeSpan.FromSeconds(52), chunks[2].RequestStart);
        Assert.Equal(TimeSpan.FromMinutes(32), chunks[2].KeepEnd);
        Assert.Equal(TimeSpan.FromMinutes(32), chunks[2].RequestEnd);
    }

    [Fact]
    public void PlanChunks_FallsBackToMaximumDuration_WhenNoSilenceExists() {
        var planner = new SilenceAwareChunkPlanner(new SilenceAwareChunkPlannerOptions(
            TargetChunkDuration: TimeSpan.FromMinutes(10),
            MinimumChunkDuration: TimeSpan.FromMinutes(4),
            MaximumChunkDuration: TimeSpan.FromMinutes(12),
            OverlapDuration: TimeSpan.FromSeconds(10),
            SearchBeforePreferredSplit: TimeSpan.FromSeconds(90),
            SearchAfterPreferredSplit: TimeSpan.FromSeconds(30),
            MinimumSilenceDuration: TimeSpan.FromMilliseconds(450)));

        IReadOnlyList<DiarizationChunkPlan> chunks = planner.PlanChunks(
            TimeSpan.FromMinutes(25),
            Array.Empty<TimeSpanRange>());

        Assert.Equal(3, chunks.Count);
        Assert.Equal(TimeSpan.FromMinutes(12), chunks[0].KeepEnd);
        Assert.Equal(TimeSpan.FromMinutes(11) + TimeSpan.FromSeconds(50), chunks[1].RequestStart);
        Assert.Equal(TimeSpan.FromMinutes(24), chunks[1].KeepEnd);
        Assert.Equal(TimeSpan.FromMinutes(23) + TimeSpan.FromSeconds(50), chunks[2].RequestStart);
        Assert.Equal(TimeSpan.FromMinutes(25), chunks[2].KeepEnd);
    }
}

