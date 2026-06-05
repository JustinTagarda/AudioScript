using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using AudioScript.Services;
using AudioScript.Services.Store;
using AudioScript.ViewModels;
using Xunit;

namespace AudioScript.Tests;

public sealed class BasicPremiumGatingPolicyTests
{
    [Fact]
    public void Policy_UnpackagedDevelopmentRun_HidesStatusAndUpgradeAffordance()
    {
        MainViewModel viewModel = CreateVisibilityContractViewModel(
            new AppEntitlementSnapshot(
                IsPackaged: false,
                HasPremium: false,
                IsPremiumProductAvailable: false,
                PremiumProductDisplayName: "AudioScript Premium",
                StatusMessage: "Premium entitlement is unavailable outside the Microsoft Store package."));

        Assert.False(viewModel.IsApplicationAccessTierVisible);
        Assert.False(viewModel.IsUpgradeButtonVisible);
        Assert.Equal(string.Empty, viewModel.ApplicationAccessTierText);
    }

    [Fact]
    public void Policy_UnpackagedDevelopmentRun_DoesNotEnforcePremiumGates()
    {
        MainViewModel viewModel = CreateVisibilityContractViewModel(
            new AppEntitlementSnapshot(
                IsPackaged: false,
                HasPremium: false,
                IsPremiumProductAvailable: false,
                PremiumProductDisplayName: "AudioScript Premium",
                StatusMessage: "Premium entitlement is unavailable outside the Microsoft Store package."));

        Assert.True(viewModel.IsDevelopmentUnpackagedMode);
        Assert.True(viewModel.CanUseLiveTranscription);
        Assert.True(viewModel.HasUnlimitedLiveTranscription);
        Assert.Null(viewModel.LiveTranscriptionLimit);
        Assert.True(viewModel.CanUseSpeakerDiarization);
        Assert.True(viewModel.HasUnlimitedSpeakerDiarization);
        Assert.Null(viewModel.SpeakerDiarizationLimit);
        Assert.True(viewModel.CanInstallModel(TranscriptionModelCatalog.WhisperLargeV3Turbo));
    }

    [Fact]
    public void Policy_PackagedOwned_ShowsPremiumAndHidesUpgrade()
    {
        MainViewModel viewModel = CreateVisibilityContractViewModel(
            new AppEntitlementSnapshot(
                IsPackaged: true,
                HasPremium: true,
                IsPremiumProductAvailable: true,
                PremiumProductDisplayName: "AudioScript Premium",
                StatusMessage: "AudioScript Premium is unlocked.",
                State: PremiumEntitlementState.VerifiedPremium));

        Assert.True(viewModel.IsApplicationAccessTierVisible);
        Assert.False(viewModel.IsUpgradeButtonVisible);
        Assert.True(viewModel.CanUseLiveTranscription);
        Assert.True(viewModel.HasUnlimitedLiveTranscription);
        Assert.Null(viewModel.LiveTranscriptionLimit);
        Assert.True(viewModel.CanUseSpeakerDiarization);
        Assert.True(viewModel.HasUnlimitedSpeakerDiarization);
        Assert.Null(viewModel.SpeakerDiarizationLimit);
        Assert.Equal("Premium", viewModel.ApplicationAccessTierText);
    }

    [Fact]
    public void Policy_PackagedNotOwned_ShowsBasicAndUpgrade()
    {
        MainViewModel viewModel = CreateVisibilityContractViewModel(
            new AppEntitlementSnapshot(
                IsPackaged: true,
                HasPremium: false,
                IsPremiumProductAvailable: true,
                PremiumProductDisplayName: "AudioScript Premium",
                StatusMessage: "AudioScript Premium is available for purchase in Microsoft Store.",
                State: PremiumEntitlementState.VerifiedBasic));

        Assert.True(viewModel.IsApplicationAccessTierVisible);
        Assert.True(viewModel.IsUpgradeButtonVisible);
        Assert.True(viewModel.CanUseLiveTranscription);
        Assert.False(viewModel.HasUnlimitedLiveTranscription);
        Assert.Equal(AppFeatureAccess.BasicLiveTranscriptionLimit, viewModel.LiveTranscriptionLimit);
        Assert.True(viewModel.CanUseSpeakerDiarization);
        Assert.False(viewModel.HasUnlimitedSpeakerDiarization);
        Assert.Equal(AppFeatureAccess.BasicSpeakerDiarizationLimit, viewModel.SpeakerDiarizationLimit);
        Assert.Equal("Basic", viewModel.ApplicationAccessTierText);
    }

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

        Assert.True(AppFeatureAccess.CanAccessFeature(AppFeature.LiveTranscription, hasPremium));
        Assert.True(AppFeatureAccess.CanAccessFeature(AppFeature.SpeakerDiarization, hasPremium));
        Assert.False(AppFeatureAccess.CanAccessFeature(AppFeature.PremiumModelInstall, hasPremium));
        Assert.False(AppFeatureAccess.HasUnlimitedLiveTranscription(hasPremium));
        Assert.Equal(AppFeatureAccess.BasicLiveTranscriptionLimit, AppFeatureAccess.GetLiveTranscriptionLimit(hasPremium));
        Assert.Equal(AppFeatureAccess.BasicSpeakerDiarizationLimit, AppFeatureAccess.GetSpeakerDiarizationLimit(hasPremium));
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
        Assert.True(AppFeatureAccess.HasUnlimitedLiveTranscription(hasPremium));
        Assert.Null(AppFeatureAccess.GetLiveTranscriptionLimit(hasPremium));
        Assert.Null(AppFeatureAccess.GetSpeakerDiarizationLimit(hasPremium));
    }

    [Fact]
    public void Policy_UnpackagedStoreEntitlementService_DefaultsToBasicWhenConfigured()
    {
        string logsPath = Path.Combine(Path.GetTempPath(), "audioscript-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logsPath);
        var processLogService = new ProcessLogService(logsPath);

        try
        {
            var service = new StoreEntitlementService(
                new FakeAppVersionProvider(isPackaged: false),
                processLogService,
                options: new StoreEntitlementServiceOptions
                {
                    TreatUnpackagedBuildsAsPremium = false,
                    PremiumStoreIds = [StorePremiumAddonCatalog.AudioScriptPremiumLifetime.StoreId],
                    PremiumProductIds = [StorePremiumAddonCatalog.AudioScriptPremiumLifetime.ProductId],
                });

            AppEntitlementSnapshot snapshot = service.CurrentSnapshot;
            Assert.False(snapshot.HasPremium);
            Assert.Equal(PremiumEntitlementState.Checking, snapshot.State);
            Assert.False(snapshot.IsPackaged);
        }
        finally
        {
            processLogService.Dispose();
        }
    }

    private sealed class FakeAppVersionProvider : IAppVersionProvider
    {
        public FakeAppVersionProvider(bool isPackaged)
        {
            IsPackaged = isPackaged;
        }

        public bool IsPackaged { get; }

        public string InstalledVersion => "0.0.0.0";
    }

    private static MainViewModel CreateVisibilityContractViewModel(AppEntitlementSnapshot snapshot)
    {
        var viewModel = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        FieldInfo? entitlementField = typeof(MainViewModel).GetField("_entitlementSnapshot", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(entitlementField);
        entitlementField.SetValue(viewModel, snapshot);

        FieldInfo? runtimeField = typeof(MainViewModel).GetField("_isSpeakerDiarizationRuntimeAvailable", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(runtimeField);
        runtimeField.SetValue(viewModel, true);
        return viewModel;
    }
}
