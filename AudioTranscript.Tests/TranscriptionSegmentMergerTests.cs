using AudioTranscript.Abstractions;
using AudioTranscript.Services;
using Xunit;

namespace AudioTranscript.Tests;

public sealed class TranscriptionSegmentMergerTests {
    [Fact]
    public void Merge_RemapsAbsoluteOffsets_AndRemovesOverlapDuplicate() {
        var merger = new TranscriptionSegmentMerger();
        var segments = new[] {
            new ChunkTranscriptionSegment(
                ChunkIndex: 0,
                ChunkStartOffset: TimeSpan.Zero,
                LocalStartOffset: TimeSpan.FromSeconds(0),
                LocalEndOffset: TimeSpan.FromSeconds(4),
                Text: "hello world"),
            new ChunkTranscriptionSegment(
                ChunkIndex: 0,
                ChunkStartOffset: TimeSpan.Zero,
                LocalStartOffset: TimeSpan.FromSeconds(4),
                LocalEndOffset: TimeSpan.FromSeconds(8),
                Text: "second line"),
            new ChunkTranscriptionSegment(
                ChunkIndex: 1,
                ChunkStartOffset: TimeSpan.FromSeconds(6),
                LocalStartOffset: TimeSpan.Zero,
                LocalEndOffset: TimeSpan.FromSeconds(2),
                Text: "second line"),
            new ChunkTranscriptionSegment(
                ChunkIndex: 1,
                ChunkStartOffset: TimeSpan.FromSeconds(6),
                LocalStartOffset: TimeSpan.FromSeconds(2),
                LocalEndOffset: TimeSpan.FromSeconds(5),
                Text: "third line"),
        };

        IReadOnlyList<TranscriptionTimedLine> merged = merger.Merge(segments);

        Assert.Equal(3, merged.Count);
        Assert.Equal(TimeSpan.Zero, merged[0].StartOffset);
        Assert.Equal(TimeSpan.FromSeconds(4), merged[1].StartOffset);
        Assert.Equal(TimeSpan.FromSeconds(8), merged[2].StartOffset);
        Assert.Equal("second line", merged[1].Text);
        Assert.False(merged[1].IsTimestampEstimated);
    }
}
