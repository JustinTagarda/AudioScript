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
}
