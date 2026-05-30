using AudioScript.Services;
using AudioScript.ViewModels;
using Xunit;

namespace AudioScript.Tests;

public sealed class BasicPremiumGatingPolicyTests
{
    [Theory]
    [InlineData(TranscriptionModelCatalog.ManualTranscription, false)]
    [InlineData(TranscriptionModelCatalog.WhisperSmall, false)]
    [InlineData(TranscriptionModelCatalog.WhisperMedium, true)]
    [InlineData(TranscriptionModelCatalog.WhisperLargeV3, true)]
    [InlineData(TranscriptionModelCatalog.WhisperLargeV3Turbo, true)]
    public void Policy_PremiumOnlyModelSet_IsStable(string modelId, bool expectedPremiumOnly)
    {
        Assert.Equal(expectedPremiumOnly, AppFeatureAccess.IsPremiumOnlyModel(modelId));
    }

    [Fact]
    public void Policy_Basic_AccessMatrix_IsStable()
    {
        const bool hasPremium = false;

        Assert.True(AppFeatureAccess.CanUseModel(TranscriptionModelCatalog.ManualTranscription, hasPremium));
        Assert.True(AppFeatureAccess.CanUseModel(TranscriptionModelCatalog.WhisperSmall, hasPremium));
        Assert.False(AppFeatureAccess.CanUseModel(TranscriptionModelCatalog.WhisperMedium, hasPremium));
        Assert.False(AppFeatureAccess.CanUseModel(TranscriptionModelCatalog.WhisperLargeV3, hasPremium));
        Assert.False(AppFeatureAccess.CanUseModel(TranscriptionModelCatalog.WhisperLargeV3Turbo, hasPremium));

        Assert.False(AppFeatureAccess.CanInstallModel(TranscriptionModelCatalog.WhisperMedium, hasPremium));
        Assert.False(AppFeatureAccess.CanInstallModel(TranscriptionModelCatalog.WhisperLargeV3, hasPremium));
        Assert.False(AppFeatureAccess.CanInstallModel(TranscriptionModelCatalog.WhisperLargeV3Turbo, hasPremium));

        Assert.False(AppFeatureAccess.CanAccessFeature(AppFeature.LiveTranscription, hasPremium));
        Assert.False(AppFeatureAccess.CanAccessFeature(AppFeature.SpeakerDiarization, hasPremium));
        Assert.False(AppFeatureAccess.CanAccessFeature(AppFeature.PremiumModelInstall, hasPremium));
    }

    [Fact]
    public void Policy_Premium_AccessMatrix_IsStable()
    {
        const bool hasPremium = true;

        Assert.True(AppFeatureAccess.CanUseModel(TranscriptionModelCatalog.ManualTranscription, hasPremium));
        Assert.True(AppFeatureAccess.CanUseModel(TranscriptionModelCatalog.WhisperSmall, hasPremium));
        Assert.True(AppFeatureAccess.CanUseModel(TranscriptionModelCatalog.WhisperMedium, hasPremium));
        Assert.True(AppFeatureAccess.CanUseModel(TranscriptionModelCatalog.WhisperLargeV3, hasPremium));
        Assert.True(AppFeatureAccess.CanUseModel(TranscriptionModelCatalog.WhisperLargeV3Turbo, hasPremium));

        Assert.True(AppFeatureAccess.CanInstallModel(TranscriptionModelCatalog.WhisperMedium, hasPremium));
        Assert.True(AppFeatureAccess.CanInstallModel(TranscriptionModelCatalog.WhisperLargeV3, hasPremium));
        Assert.True(AppFeatureAccess.CanInstallModel(TranscriptionModelCatalog.WhisperLargeV3Turbo, hasPremium));

        Assert.True(AppFeatureAccess.CanAccessFeature(AppFeature.LiveTranscription, hasPremium));
        Assert.True(AppFeatureAccess.CanAccessFeature(AppFeature.SpeakerDiarization, hasPremium));
        Assert.True(AppFeatureAccess.CanAccessFeature(AppFeature.PremiumModelInstall, hasPremium));
    }

    [Fact]
    public void Policy_StartupFallbackWithoutEntitlementService_DefaultsToDevelopmentPremium()
    {
        AppEntitlementSnapshot fallback = AppEntitlementSnapshot.Development("AudioScript Premium");

        Assert.True(fallback.HasPremium);
        Assert.Equal(PremiumEntitlementState.VerifiedPremium, fallback.State);
        Assert.False(fallback.IsPackaged);
    }
}

