using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class TranscriptionModelCatalogTests
{
    [Fact]
    public void Models_IncludeManualAndLocalEntriesOnly()
    {
        string[] modelIds = TranscriptionModelCatalog.Models.Select(model => model.Id).ToArray();

        Assert.Equal(
            new[] {
                TranscriptionModelCatalog.WhisperSmall,
                TranscriptionModelCatalog.WhisperMedium,
                TranscriptionModelCatalog.WhisperLargeV3,
                TranscriptionModelCatalog.WhisperLargeV3Turbo,
                TranscriptionModelCatalog.ManualTranscription,
            },
            modelIds);
    }

    [Fact]
    public void ManualEngine_IsNotTreatedAsAiAssistModel()
    {
        Assert.True(TranscriptionModelCatalog.IsManualOnly(TranscriptionModelCatalog.ManualTranscription));
        Assert.False(TranscriptionModelCatalog.UsesAiAssist(TranscriptionModelCatalog.ManualTranscription));
    }

    [Fact]
    public void WhisperSmallEngine_IsDefaultLocalAiAssist()
    {
        Assert.True(TranscriptionModelCatalog.IsLocalWhisper(TranscriptionModelCatalog.WhisperSmall));
        Assert.True(TranscriptionModelCatalog.UsesAiAssist(TranscriptionModelCatalog.WhisperSmall));
        Assert.True(TranscriptionModelCatalog.SupportsFileTranscription(TranscriptionModelCatalog.WhisperSmall));
        Assert.True(TranscriptionModelCatalog.SupportsPlaybackTranscription(TranscriptionModelCatalog.WhisperSmall));
        Assert.True(TranscriptionModelCatalog.SupportsSpeakerDiarization(TranscriptionModelCatalog.WhisperSmall));
        string legacyMinimumModelId = string.Concat("whisper", "-", "base");
        Assert.False(TranscriptionModelCatalog.IsRecognizedTranscriptionEngine(legacyMinimumModelId));
        Assert.False(TranscriptionModelCatalog.IsLocalWhisper(legacyMinimumModelId));
    }

    [Theory]
    [InlineData(TranscriptionModelCatalog.WhisperMedium)]
    [InlineData(TranscriptionModelCatalog.WhisperLargeV3)]
    [InlineData(TranscriptionModelCatalog.WhisperLargeV3Turbo)]
    public void OptionalWhisperEngines_AreRecognizedLocalEngines(string modelId)
    {
        Assert.True(TranscriptionModelCatalog.IsLocalWhisper(modelId));
        Assert.True(TranscriptionModelCatalog.SupportsFileTranscription(modelId));
        Assert.True(TranscriptionModelCatalog.SupportsPlaybackTranscription(modelId));
        Assert.True(TranscriptionModelCatalog.SupportsSpeakerDiarization(modelId));
    }
}
