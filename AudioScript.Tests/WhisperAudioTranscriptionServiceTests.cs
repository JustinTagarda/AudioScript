using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class WhisperAudioTranscriptionServiceTests
{
    [Fact]
    public void ShouldApplyPrompt_ReturnsFalse_ForShortAudio()
    {
        Assert.False(WhisperAudioTranscriptionService.ShouldApplyPrompt(TimeSpan.FromSeconds(8)));
    }

    [Fact]
    public void ShouldApplyPrompt_ReturnsTrue_ForLongAudioAndUnknownDuration()
    {
        Assert.True(WhisperAudioTranscriptionService.ShouldApplyPrompt(TimeSpan.FromSeconds(30)));
        Assert.True(WhisperAudioTranscriptionService.ShouldApplyPrompt(null));
    }

    [Fact]
    public void ParseSrtTimedLines_ParsesSegmentsAndText()
    {
        string srt = """
            1
            00:00:00,000 --> 00:00:01,250
            Hello

            2
            00:00:01,300 --> 00:00:02,900
            world
            again

            """;

        IReadOnlyList<AudioScript.Abstractions.TranscriptionTimedLine> lines =
            WhisperAudioTranscriptionService.ParseSrtTimedLines(srt);

        Assert.Equal(2, lines.Count);
        Assert.Equal("Hello", lines[0].Text);
        Assert.Equal(TimeSpan.Zero, lines[0].StartOffset);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), lines[0].EndOffset);
        Assert.Equal("world again", lines[1].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(1300), lines[1].StartOffset);
        Assert.Equal(TimeSpan.FromMilliseconds(2900), lines[1].EndOffset);
    }
}
