using AudioScript.Services;
using AudioScript.ViewModels;
using Whisper.net.Ggml;
using Xunit;

namespace AudioScript.Tests;

public sealed class AppFeatureAccessTests
{
    [Theory]
    [InlineData(TranscriptionModelCatalog.WhisperSmall, false)]
    [InlineData(TranscriptionModelCatalog.WhisperMedium, true)]
    [InlineData(TranscriptionModelCatalog.WhisperLargeV3, true)]
    [InlineData(TranscriptionModelCatalog.WhisperLargeV3Turbo, true)]
    public void IsPremiumOnlyModel_RecognizesExpectedModels(string modelId, bool expected)
    {
        Assert.Equal(expected, AppFeatureAccess.IsPremiumOnlyModel(modelId));
    }

    [Fact]
    public void CanAccessFeature_RequiresPremiumForLiveAndSpeakerFeatures()
    {
        Assert.False(AppFeatureAccess.CanAccessFeature(AppFeature.LiveTranscription, hasPremium: false));
        Assert.False(AppFeatureAccess.CanAccessFeature(AppFeature.SpeakerDiarization, hasPremium: false));
        Assert.True(AppFeatureAccess.CanAccessFeature(AppFeature.LiveTranscription, hasPremium: true));
        Assert.True(AppFeatureAccess.CanAccessFeature(AppFeature.SpeakerDiarization, hasPremium: true));
    }

    [Fact]
    public void SettingsItemViewModel_BlocksPremiumEngineInstallWithoutPremium()
    {
        var definition = new WhisperEngineModelDefinition(
            Id: TranscriptionModelCatalog.WhisperMedium,
            DisplayName: "Whisper medium",
            FileName: "ggml-medium.bin",
            SizeText: "about 1.5 GB",
            Description: "Higher accuracy.",
            Benefits: "Premium model.",
            Notes: "Requires Premium.",
            GgmlType: GgmlType.Medium,
            ExpectedBytes: 10,
            IsBundled: false,
            IsFixedInstalled: false);
        var item = new SettingsItemViewModel(definition, isInstalled: false, hasPremiumAccess: false);

        Assert.True(item.RequiresPremiumToInstall);
        Assert.True(item.ShowPremiumUpsell);
        Assert.True(item.ShowInstallButton);
        Assert.False(item.CanInstall);

        item.SetPremiumAccess(true);

        Assert.False(item.RequiresPremiumToInstall);
        Assert.True(item.CanInstall);
    }
}
