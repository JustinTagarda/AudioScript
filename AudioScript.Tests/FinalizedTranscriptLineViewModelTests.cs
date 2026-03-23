using AudioScript.ViewModels;
using Xunit;

namespace AudioScript.Tests;

public sealed class FinalizedTranscriptLineViewModelTests {
    [Theory]
    [InlineData("00:00", 0)]
    [InlineData("01:04", 64)]
    [InlineData("99:59", 5999)]
    public void TryParseTimeline_AcceptsStrictMinuteSecondFormat(string value, int expectedSeconds) {
        bool parsed = FinalizedTranscriptLineViewModel.TryParseTimeline(value, out TimeSpan offset);

        Assert.True(parsed);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1:04")]
    [InlineData("001:04")]
    [InlineData("AA:BB")]
    [InlineData("00-00")]
    [InlineData("00:60")]
    public void TryParseTimeline_RejectsInvalidFormats(string value) {
        bool parsed = FinalizedTranscriptLineViewModel.TryParseTimeline(value, out _);

        Assert.False(parsed);
    }

    [Fact]
    public void TimelineSetter_ShiftsStartAndPreservesDuration() {
        var line = new FinalizedTranscriptLineViewModel(
            startOffset: TimeSpan.FromSeconds(53),
            endOffset: TimeSpan.FromSeconds(64),
            isTimestampEstimated: false,
            text: "Sample");

        line.Timeline = "01:20";

        Assert.Equal(TimeSpan.FromSeconds(80), line.StartOffset);
        Assert.Equal(TimeSpan.FromSeconds(91), line.EndOffset);
        Assert.Equal("01:20", line.Timeline);
    }

    [Fact]
    public void TimelineSetter_InvalidValue_RevertsToExistingTimeline() {
        var line = new FinalizedTranscriptLineViewModel(
            startOffset: TimeSpan.FromSeconds(53),
            endOffset: TimeSpan.FromSeconds(64),
            isTimestampEstimated: false,
            text: "Sample");

        line.Timeline = "01:99";

        Assert.Equal(TimeSpan.FromSeconds(53), line.StartOffset);
        Assert.Equal(TimeSpan.FromSeconds(64), line.EndOffset);
        Assert.Equal("00:53", line.Timeline);
    }
}



