using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class OpenAiTranscriptionModelCatalogTests {
    [Fact]
    public void Models_IncludeManualAndOnlineEntries() {
        Assert.Contains(
            OpenAiTranscriptionModelCatalog.Models,
            model => string.Equals(
                model.Id,
                OpenAiTranscriptionModelCatalog.ManualTranscription,
                StringComparison.OrdinalIgnoreCase));

        Assert.Contains(
            OpenAiTranscriptionModelCatalog.Models,
            model => string.Equals(
                model.Id,
                OpenAiTranscriptionModelCatalog.Gpt4oTranscribe,
                StringComparison.OrdinalIgnoreCase));

        Assert.Contains(
            OpenAiTranscriptionModelCatalog.Models,
            model => string.Equals(
                model.Id,
                OpenAiTranscriptionModelCatalog.Gpt4oMiniTranscribe,
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ManualEngine_IsNotTreatedAsOnlineAiAssistModel() {
        Assert.True(OpenAiTranscriptionModelCatalog.IsManualOnly(OpenAiTranscriptionModelCatalog.ManualTranscription));
        Assert.False(OpenAiTranscriptionModelCatalog.UsesAiAssist(OpenAiTranscriptionModelCatalog.ManualTranscription));
        Assert.False(OpenAiTranscriptionModelCatalog.IsSupported(OpenAiTranscriptionModelCatalog.ManualTranscription));
    }
}



