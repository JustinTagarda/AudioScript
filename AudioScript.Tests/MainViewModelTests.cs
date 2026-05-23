using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Text;
using System.Reflection;
using AudioScript.Abstractions;
using AudioScript.Audio;
using AudioScript.Services;
using AudioScript.ViewModels;
using Xunit;

namespace AudioScript.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public async Task UpdateFooterMode_CheckingHiddenAndInstallingUsesCompactFooterAndUpdateAction()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var appUpdateService = new StubAppUpdateService(AppUpdateSnapshot.Idle("1.2.3.4"));
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService),
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall),
                    appUpdateService: appUpdateService);

                try
                {
                    Assert.False(viewModel.IsApplicationFooterCompactMode);
                    Assert.True(viewModel.IsApplicationFooterDefaultVisible);
                    Assert.True(viewModel.CanCheckForUpdates);
                    Assert.True(viewModel.CheckForUpdatesCommand.CanExecute(null));
                    Assert.Equal(string.Empty, viewModel.ApplicationUpdateStatusText);

                    appUpdateService.Publish(new AppUpdateSnapshot(
                        AppUpdateState.Checking,
                        "Checking for updates",
                        "Looking for Microsoft Store updates.",
                        IsMandatoryUpdateAvailable: false,
                        IsProgressVisible: false,
                        ProgressValue: 0,
                        InstalledVersion: "1.2.3.4",
                        AvailableVersion: null));
                    queuedContext.Drain();

                    Assert.False(viewModel.IsApplicationFooterCompactMode);
                    Assert.True(viewModel.IsApplicationFooterDefaultVisible);
                    Assert.False(viewModel.CanCheckForUpdates);
                    Assert.False(viewModel.CheckForUpdatesCommand.CanExecute(null));
                    Assert.Equal("Checking for updates", viewModel.ApplicationUpdateStatusText);

                    appUpdateService.Publish(new AppUpdateSnapshot(
                        AppUpdateState.UpdateAvailable,
                        "Update available",
                        "Microsoft Store update is available.",
                        IsMandatoryUpdateAvailable: false,
                        IsProgressVisible: false,
                        ProgressValue: 0,
                        InstalledVersion: "1.2.3.4",
                        AvailableVersion: "1.2.3.5"));
                    queuedContext.Drain();

                    Assert.False(viewModel.IsApplicationFooterCompactMode);
                    Assert.True(viewModel.IsApplicationFooterDefaultVisible);
                    Assert.False(viewModel.CanCheckForUpdates);
                    Assert.False(viewModel.CheckForUpdatesCommand.CanExecute(null));
                    Assert.Equal("Update available", viewModel.ApplicationUpdateStatusText);

                    appUpdateService.Publish(new AppUpdateSnapshot(
                        AppUpdateState.Installing,
                        "Installing update",
                        "Installing update",
                        IsMandatoryUpdateAvailable: false,
                        IsProgressVisible: true,
                        ProgressValue: 0.5,
                        InstalledVersion: "1.2.3.4",
                        AvailableVersion: "1.2.3.5"));
                    queuedContext.Drain();

                    Assert.True(viewModel.IsApplicationFooterCompactMode);
                    Assert.False(viewModel.IsApplicationFooterDefaultVisible);
                    Assert.False(viewModel.CanCheckForUpdates);
                    Assert.False(viewModel.CheckForUpdatesCommand.CanExecute(null));
                    Assert.Equal("Installing update", viewModel.ApplicationUpdateStatusText);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
            }
        });
    }

    [Fact]
    public async Task CheckForUpdatesCommand_UsesUpdateCoordinator()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var appUpdateService = new StubAppUpdateService(AppUpdateSnapshot.Idle("1.2.3.4"));
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService),
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall),
                    appUpdateService: appUpdateService);

                try
                {
                    viewModel.CheckForUpdatesCommand.Execute(null);
                    queuedContext.Drain();

                    Assert.True(viewModel.CheckForUpdatesCommand.CanExecute(null));
                    Assert.Equal(1, appUpdateService.UserInitiatedUpdateFlowCallCount);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
            }
        });
    }

    [Fact]
    public async Task TryImportAudioFileFromPath_NewAudio_LoadsPreviewAndKeepsTranscriptGenerationEnabled()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            string audioPath = CreateSilentWaveFile(16000);
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([
                    new TranscriptionTimedLine("ok", TimeSpan.Zero, TimeSpan.FromSeconds(1), false),
                ]);
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService),
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    bool imported = viewModel.TryImportAudioFileFromPath(audioPath);

                    Assert.True(imported);
                    Assert.Equal(1, playbackService.LoadFileCallCount);
                    Assert.Equal(Path.GetFullPath(audioPath), viewModel.LoadedAudioFilePath);
                    Assert.True(viewModel.IsAudioFileLoaded);
                    Assert.True(viewModel.IsTranscriptGenerationEnabled);

                    queuedContext.Drain();

                    Assert.Equal(Path.GetFullPath(audioPath), viewModel.LoadedAudioFilePath);
                    Assert.True(viewModel.IsAudioFileLoaded);
                    Assert.True(viewModel.IsTranscriptGenerationEnabled);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
                File.Delete(audioPath);
            }
        });
    }

    [Fact]
    public async Task TryImportAudioFileFromPath_NewAudio_RaisesTranscribeAudioStagedEvent()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            string audioPath = CreateSilentWaveFile(160000);
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService),
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    int eventCount = 0;
                    viewModel.NewAudioFileStagedForTranscribeAudio += (_, _) => eventCount++;

                    Assert.True(viewModel.TryImportAudioFileFromPath(audioPath));
                    queuedContext.Drain();

                    Assert.Equal(1, eventCount);
                    Assert.True(viewModel.IsAudioFileLoaded);
                    Assert.False(viewModel.HasCurrentSession);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
                File.Delete(audioPath);
            }
        });
    }

    [Fact]
    public async Task RefreshEngines_WithPreferredInstalledEngine_SelectsAndPersistsPreferredEngine()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            string settingsPath = Path.Combine(rootPath, "app-preferences.json");
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var preferencesStore = new AppPreferencesStore(settingsPath);
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var smallModel = TranscriptionModelCatalog.Find(TranscriptionModelCatalog.WhisperSmall)!;
                var mediumModel = TranscriptionModelCatalog.Find(TranscriptionModelCatalog.WhisperMedium)!;
                var viewModel = new MainViewModel(
                    [smallModel],
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService),
                    preferencesStore,
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    viewModel.RefreshEngines(
                        [smallModel, mediumModel],
                        TranscriptionModelCatalog.WhisperMedium);

                    Assert.Equal(TranscriptionModelCatalog.WhisperMedium, viewModel.SelectedEngineId);
                    Assert.Equal(TranscriptionModelCatalog.WhisperMedium, preferencesStore.Load().SelectedEngineId);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
            }
        });
    }

    [Fact]
    public async Task TryImportAudioFileFromPath_ExistingSession_DoesNotRaiseTranscribeAudioStagedEvent()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            string audioPath = CreateSilentWaveFile(160000);
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var sessionStore = new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService);
                sessionStore.ImportAudioFile(audioPath);
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    sessionStore,
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    int eventCount = 0;
                    viewModel.NewAudioFileStagedForTranscribeAudio += (_, _) => eventCount++;

                    Assert.True(viewModel.TryImportAudioFileFromPath(audioPath));
                    queuedContext.Drain();

                    Assert.Equal(0, eventCount);
                    Assert.True(viewModel.HasCurrentSession);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
                File.Delete(audioPath);
            }
        });
    }

    [Fact]
    public async Task TryImportAudioFileFromPath_NewAudio_WhenBasicSessionLimitReached_RaisesPremiumUpsell()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);
            string? newAudioPath = null;
            IReadOnlyList<string> seededAudioPaths = Array.Empty<string>();

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var sessionStore = new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService);
                seededAudioPaths = SeedSessionStore(sessionStore, sessionCount: 10);
                newAudioPath = CreateSilentWaveFile(260000);
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    sessionStore,
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall),
                    entitlementService: new StubEntitlementService(hasPremium: false));

                try
                {
                    PremiumUpsellRequest? upsellRequest = null;
                    viewModel.PremiumUpsellRequested += (_, request) => upsellRequest = request;

                    bool imported = viewModel.TryImportAudioFileFromPath(newAudioPath);
                    queuedContext.Drain();

                    Assert.False(imported);
                    Assert.NotNull(upsellRequest);
                    Assert.Equal("Session limit reached", upsellRequest!.FeatureName);
                    Assert.Equal(0, playbackService.LoadFileCallCount);
                    Assert.False(viewModel.IsAudioFileLoaded);
                    Assert.False(viewModel.HasCurrentSession);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
                if (!string.IsNullOrWhiteSpace(newAudioPath) && File.Exists(newAudioPath))
                {
                    File.Delete(newAudioPath);
                }

                DeleteFiles(seededAudioPaths);
            }
        });
    }

    [Fact]
    public async Task TryImportAudioFileFromPath_ExistingSession_WhenBasicSessionLimitReached_LoadsSessionWithoutUpsell()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);
            string existingAudioPath = string.Empty;
            IReadOnlyList<string> seededAudioPaths = Array.Empty<string>();

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var sessionStore = new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService);
                seededAudioPaths = SeedSessionStore(sessionStore, sessionCount: 10);
                existingAudioPath = seededAudioPaths.First();
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    sessionStore,
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall),
                    entitlementService: new StubEntitlementService(hasPremium: false));

                try
                {
                    int upsellCount = 0;
                    viewModel.PremiumUpsellRequested += (_, _) => upsellCount++;

                    bool imported = viewModel.TryImportAudioFileFromPath(existingAudioPath);
                    queuedContext.Drain();

                    Assert.True(imported);
                    Assert.Equal(0, upsellCount);
                    Assert.True(viewModel.HasCurrentSession);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
                DeleteFiles(seededAudioPaths);
            }
        });
    }

    [Fact]
    public async Task EnsureLiveTranscriptSession_WhenBasicSessionLimitReached_RaisesPremiumUpsellAndReturnsFalse()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            IReadOnlyList<string> seededAudioPaths = Array.Empty<string>();

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var sessionStore = new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService);
                seededAudioPaths = SeedSessionStore(sessionStore, sessionCount: 10);
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    sessionStore,
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall),
                    entitlementService: new StubEntitlementService(hasPremium: false));

                try
                {
                    int upsellCount = 0;
                    viewModel.PremiumUpsellRequested += (_, _) => upsellCount++;

                    bool created = viewModel.EnsureLiveTranscriptSession("test input");

                    Assert.False(created);
                    Assert.Equal(1, upsellCount);
                    Assert.False(viewModel.HasCurrentSession);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
                DeleteFiles(seededAudioPaths);
            }
        });
    }

    [Fact]
    public async Task EnsureCurrentSessionForAudioFile_WhenPremium_IgnoresBasicSessionLimit()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);
            string? audioPath = null;
            IReadOnlyList<string> seededAudioPaths = Array.Empty<string>();

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var sessionStore = new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService);
                seededAudioPaths = SeedSessionStore(sessionStore, sessionCount: 10);
                audioPath = CreateSilentWaveFile(280000);
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    sessionStore,
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall),
                    entitlementService: new StubEntitlementService(hasPremium: true));

                try
                {
                    MethodInfo method = typeof(MainViewModel).GetMethod(
                        "EnsureCurrentSessionForAudioFile",
                        BindingFlags.Instance | BindingFlags.NonPublic)!;
                    bool created = (bool)method.Invoke(viewModel, [audioPath])!;

                    Assert.True(created);
                    Assert.True(viewModel.HasCurrentSession);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
                if (!string.IsNullOrWhiteSpace(audioPath) && File.Exists(audioPath))
                {
                    File.Delete(audioPath);
                }

                DeleteFiles(seededAudioPaths);
            }
        });
    }

    [Fact]
    public async Task EmptyTranscriptState_WithLoadedAudio_ShowsTranscribeAction()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            string audioPath = CreateSilentWaveFile(16000);
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService),
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    Assert.True(viewModel.TryImportAudioFileFromPath(audioPath));

                    Assert.True(viewModel.IsAudioFileLoaded);
                    Assert.False(viewModel.HasCurrentSession);
                    Assert.True(viewModel.IsTranscriptEmptyStateVisible);
                    Assert.Equal("Ready to transcribe", viewModel.TranscriptEmptyStateTitle);
                    Assert.Equal("Click the button below to transcribe this audio file.", viewModel.TranscriptEmptyStateMessage);
                    Assert.False(viewModel.ShouldShowTranscriptChooseFileAction);
                    Assert.True(viewModel.ShouldShowTranscriptTranscribeAudioAction);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
                File.Delete(audioPath);
            }
        });
    }

    [Fact]
    public async Task EmptyTranscriptState_WithoutLoadedAudio_ShowsSelectAudioAction()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService),
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    Assert.False(viewModel.IsAudioFileLoaded);
                    Assert.False(viewModel.HasCurrentSession);
                    Assert.True(viewModel.IsTranscriptEmptyStateVisible);
                    Assert.Equal("No transcript", viewModel.TranscriptEmptyStateTitle);
                    Assert.Equal("Drop audio here, choose a file, or open a session.", viewModel.TranscriptEmptyStateMessage);
                    Assert.True(viewModel.ShouldShowTranscriptChooseFileAction);
                    Assert.False(viewModel.ShouldShowTranscriptTranscribeAudioAction);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
            }
        });
    }

    [Fact]
    public async Task GenerateTranscribeAudioTranscriptAsync_CreatesTimedTranscriptRows()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            string audioPath = CreateSilentWaveFile(16000);
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([
                    new TranscriptionTimedLine("hello", TimeSpan.Zero, TimeSpan.FromSeconds(1), false),
                    new TranscriptionTimedLine("world", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), false),
                ]);
                var diarizationEngine = new TestSpeakerDiarizationEngine();
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService, diarizationEngine),
                    playbackService,
                    processLogService,
                    new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService),
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    bool imported = viewModel.TryImportAudioFileFromPath(audioPath);
                    Assert.True(imported);

                    bool created = await viewModel.GenerateTranscribeAudioTranscriptAsync(CancellationToken.None);

                    Assert.True(created);
                    Assert.False(viewModel.HasSpeakerLabels);
                    Assert.Equal(0, diarizationEngine.RequestCount);
                    Assert.Equal(2, viewModel.FinalizedTranscriptLines.Count);
                    Assert.Equal("hello", viewModel.FinalizedTranscriptLines[0].Text);
                    Assert.Equal(string.Empty, viewModel.FinalizedTranscriptLines[0].SpeakerLabel);
                    Assert.Equal(string.Empty, viewModel.FinalizedTranscriptLines[1].SpeakerLabel);
                    Assert.Equal(TimeSpan.Zero, viewModel.FinalizedTranscriptLines[0].StartOffset);
                    Assert.Equal(TimeSpan.FromSeconds(2), viewModel.FinalizedTranscriptLines[1].StartOffset);
                    Assert.Contains("hello", viewModel.BuildClipboardTranscriptText());
                    Assert.False(viewModel.IsTranscriptEmptyStateVisible);
                    Assert.False(viewModel.ShouldShowTranscriptTranscribeAudioAction);
                    Assert.Equal("Transcript rows are available.", viewModel.TranscriptEmptyStateMessage);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
                File.Delete(audioPath);
            }
        });
    }

    [Fact]
    public async Task GenerateTranscribeAudioTranscriptAsync_UsesDirectTranscription_WhenFileExceedsOldUploadThreshold()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            string audioPath = CreateSilentWaveFile(25_100_000);
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([
                    new TranscriptionTimedLine("large file", TimeSpan.Zero, TimeSpan.FromSeconds(1), false),
                ]);
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService),
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    Assert.True(viewModel.TryImportAudioFileFromPath(audioPath));

                    bool created = await viewModel.GenerateTranscribeAudioTranscriptAsync(CancellationToken.None);

                    Assert.True(created);
                    Assert.Equal(1, transcriptionService.RequestCount);
                    Assert.DoesNotContain("audio-chunk-", transcriptionService.LastAudioFilePath, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal("large file", viewModel.FinalizedTranscriptLines.Single().Text);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
                File.Delete(audioPath);
            }
        });
    }

    [Fact]
    public async Task CanRunDetectSpeakersPrimaryAction_RequiresLoadedSessionWithTranscriptRows()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            string audioPath = CreateSilentWaveFile(16000);
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var sessionStore = new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService);
                TranscriptSessionLoadResult imported = sessionStore.ImportAudioFile(audioPath);
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    sessionStore,
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    Assert.False(viewModel.CanRunDetectSpeakersPrimaryAction);

                    await viewModel.LoadRecentSessionAsync(Assert.Single(sessionStore.ListRecentSessions()));
                    queuedContext.Drain();

                    Assert.False(viewModel.CanRunDetectSpeakersPrimaryAction);

                    viewModel.FinalizedTranscriptLines.Add(new FinalizedTranscriptLineViewModel(
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(1),
                        false,
                        "hello"));

                    Assert.True(viewModel.CanRunDetectSpeakersPrimaryAction);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
                File.Delete(audioPath);
            }
        });
    }

    [Fact]
    public async Task RunSpeakerDetectionAsync_UpdatesSpeakerLabelsAndSavesSession()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            string audioPath = CreateSilentWaveFile(160000);
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var diarizationEngine = new TestSpeakerDiarizationEngine();
                var sessionStore = new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService);
                TranscriptSessionLoadResult imported = sessionStore.ImportAudioFile(audioPath);
                imported.Document.Transcript.Lines.Add(new TranscriptSessionLineDocument
                {
                    Text = "hello",
                    StartSeconds = 0,
                    EndSeconds = 1,
                });
                imported.Document.Transcript.Lines.Add(new TranscriptSessionLineDocument
                {
                    Text = "reply",
                    StartSeconds = 1.3,
                    EndSeconds = 2.4,
                });
                sessionStore.Save(imported.Document);

                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService, diarizationEngine),
                    playbackService,
                    processLogService,
                    sessionStore,
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    await viewModel.LoadRecentSessionAsync(Assert.Single(sessionStore.ListRecentSessions()));
                    queuedContext.Drain();

                    Task<bool> runTask = viewModel.RunSpeakerDetectionAsync(CancellationToken.None);
                    while (!runTask.IsCompleted)
                    {
                        queuedContext.Drain();
                        await Task.Delay(10).ConfigureAwait(false);
                    }

                    bool completed = await runTask.ConfigureAwait(false);

                    Assert.True(completed);
                    Assert.Equal(1, diarizationEngine.RequestCount);
                    Assert.Equal("Speaker 1", viewModel.FinalizedTranscriptLines[0].SpeakerLabel);
                    Assert.Equal("Speaker 2", viewModel.FinalizedTranscriptLines[1].SpeakerLabel);

                    TranscriptSessionLoadResult saved = sessionStore.LoadSession(imported.Document.SessionId);
                    Assert.Equal("Speaker 1", saved.Document.Transcript.Lines[0].SpeakerLabel);
                    Assert.Equal("Speaker 2", saved.Document.Transcript.Lines[1].SpeakerLabel);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
                File.Delete(audioPath);
            }
        });
    }

    [Fact]
    public async Task RenameSpeakerAcrossTranscript_RenamesMatchingRowsAndMarksManual()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            string audioPath = CreateSilentWaveFile(16000);
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var sessionStore = new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService);
                TranscriptSessionLoadResult imported = sessionStore.ImportAudioFile(audioPath);
                imported.Document.Transcript.Lines.Add(new TranscriptSessionLineDocument
                {
                    Text = "one",
                    SpeakerLabel = "Speaker 1",
                    SpeakerLabelSource = SpeakerLabelSources.DiarizationFinal,
                    StartSeconds = 0,
                    EndSeconds = 1,
                });
                imported.Document.Transcript.Lines.Add(new TranscriptSessionLineDocument
                {
                    Text = "two",
                    SpeakerLabel = "Speaker 2",
                    SpeakerLabelSource = SpeakerLabelSources.DiarizationFinal,
                    StartSeconds = 1,
                    EndSeconds = 2,
                });
                imported.Document.Transcript.Lines.Add(new TranscriptSessionLineDocument
                {
                    Text = "three",
                    SpeakerLabel = "Speaker 1",
                    SpeakerLabelSource = SpeakerLabelSources.DiarizationFinal,
                    StartSeconds = 2,
                    EndSeconds = 3,
                });
                sessionStore.Save(imported.Document);

                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    sessionStore,
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    await viewModel.LoadRecentSessionAsync(Assert.Single(sessionStore.ListRecentSessions()));
                    queuedContext.Drain();

                    int changed = viewModel.RenameSpeakerAcrossTranscript("Speaker 1", " Clinician ");

                    Assert.Equal(2, changed);
                    Assert.Equal("Clinician", viewModel.FinalizedTranscriptLines[0].SpeakerLabel);
                    Assert.Equal("Speaker 2", viewModel.FinalizedTranscriptLines[1].SpeakerLabel);
                    Assert.Equal("Clinician", viewModel.FinalizedTranscriptLines[2].SpeakerLabel);
                    Assert.Equal(SpeakerLabelSources.Manual, viewModel.FinalizedTranscriptLines[0].SpeakerLabelSource);
                    Assert.Equal(SpeakerLabelSources.Manual, viewModel.FinalizedTranscriptLines[2].SpeakerLabelSource);

                    TranscriptSessionLoadResult saved = sessionStore.LoadSession(imported.Document.SessionId);
                    Assert.Equal("Clinician", saved.Document.Transcript.Lines[0].SpeakerLabel);
                    Assert.Equal("Speaker 2", saved.Document.Transcript.Lines[1].SpeakerLabel);
                    Assert.Equal("Clinician", saved.Document.Transcript.Lines[2].SpeakerLabel);
                    Assert.Equal(SpeakerLabelSources.Manual, saved.Document.Transcript.Lines[0].SpeakerLabelSource);
                    Assert.Contains("Clinician: one", saved.Document.Transcript.FinalText);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
                File.Delete(audioPath);
            }
        });
    }

    [Fact]
    public async Task RenameSpeakerAcrossTranscript_RejectsBlankOrUnchangedTarget()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService),
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    Assert.Throws<ArgumentException>(() =>
                        viewModel.RenameSpeakerAcrossTranscript("Speaker 1", " "));
                    Assert.Throws<ArgumentException>(() =>
                        viewModel.RenameSpeakerAcrossTranscript("Speaker 1", "Speaker 1"));
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
            }
        });
    }

    [Fact]
    public async Task ConfirmSpeakerLabelOverwrite_ReturnsFalseWhenUserCancels()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            string audioPath = CreateSilentWaveFile(16000);
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var sessionStore = new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService);
                TranscriptSessionLoadResult imported = sessionStore.ImportAudioFile(audioPath);
                imported.Document.Transcript.Lines.Add(new TranscriptSessionLineDocument
                {
                    Text = "hello",
                    SpeakerLabel = "Speaker 9",
                    StartSeconds = 0,
                    EndSeconds = 1,
                });
                sessionStore.Save(imported.Document);

                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    sessionStore,
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    await viewModel.LoadRecentSessionAsync(Assert.Single(sessionStore.ListRecentSessions()));
                    queuedContext.Drain();
                    viewModel.ConfirmationRequested += (_, request) => request.IsConfirmed = false;

                    Assert.False(viewModel.ConfirmSpeakerLabelOverwrite());
                    Assert.Equal("Speaker 9", viewModel.FinalizedTranscriptLines.Single().SpeakerLabel);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
                File.Delete(audioPath);
            }
        });
    }

    [Fact]
    public async Task RunSpeakerDetectionAsync_ErrorKeepsExistingSpeakerLabels()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            string audioPath = CreateSilentWaveFile(16000);
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var sessionStore = new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService);
                TranscriptSessionLoadResult imported = sessionStore.ImportAudioFile(audioPath);
                imported.Document.Transcript.Lines.Add(new TranscriptSessionLineDocument
                {
                    Text = "hello",
                    SpeakerLabel = "Existing",
                    StartSeconds = 0,
                    EndSeconds = 1,
                });
                sessionStore.Save(imported.Document);

                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(
                        transcriptionService,
                        processLogService,
                        new TestSpeakerDiarizationEngine(new ApplicationException("Synthetic diarization failure."))),
                    playbackService,
                    processLogService,
                    sessionStore,
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    await viewModel.LoadRecentSessionAsync(Assert.Single(sessionStore.ListRecentSessions()));
                    queuedContext.Drain();

                    Task<ApplicationException> failureTask = Assert.ThrowsAsync<ApplicationException>(async () =>
                        await viewModel.RunSpeakerDetectionAsync(CancellationToken.None));
                    while (!failureTask.IsCompleted)
                    {
                        queuedContext.Drain();
                        await Task.Delay(10).ConfigureAwait(false);
                    }

                    await failureTask.ConfigureAwait(false);

                    Assert.Equal("Existing", viewModel.FinalizedTranscriptLines.Single().SpeakerLabel);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
                File.Delete(audioPath);
            }
        });
    }

    [Fact]
    public async Task SaveLiveTranscriptSession_PreservesLiveRecordingManifestPath()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var sessionStore = new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService);
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    sessionStore,
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    Assert.True(viewModel.InitializeNewLiveTranscriptSession("Test Source"));
                    await using LiveRecordingSession recording = viewModel.CreateLiveRecordingSession("Test Source");

                    viewModel.SaveLiveTranscriptSession();

                    string sessionId = Assert.Single(sessionStore.ListRecentSessions()).SessionId;
                    TranscriptSessionLoadResult loaded = sessionStore.LoadSession(sessionId);
                    Assert.Equal(AudioStorageKinds.LiveRecordingManifest, loaded.Document.Audio.StorageKind);
                    Assert.Equal(
                        TranscriptSessionStore.LiveRecordingManifestRelativePath,
                        loaded.Document.Audio.StoredRelativePath);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
            }
        });
    }

    [Fact]
    public async Task GenerateTranscribeAudioTranscriptAsync_LiveRecording_UsesPreparedWaveFile()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([
                    new TranscriptionTimedLine(
                        "Recorded segment.",
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(1),
                        false),
                ]);
                var sessionStore = new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService);
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    sessionStore,
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    Assert.True(viewModel.InitializeNewLiveTranscriptSession("Test Source"));
                    await using (LiveRecordingSession recording = viewModel.CreateLiveRecordingSession(
                                     "Test Source",
                                     TimeSpan.FromMilliseconds(100)))
                    {
                        recording.Start();
                        recording.WriteFrame(new LoopbackAudioFrameEventArgs(
                            new byte[3200],
                            StandardizingAudioCaptureService.StandardFormat));
                        recording.CompleteAsync().GetAwaiter().GetResult();
                    }

                    viewModel.RefreshLiveRecordingMetadata();
                    Assert.True(viewModel.LoadCurrentSessionAudioPreview());

                    bool completed = viewModel.GenerateTranscribeAudioTranscriptAsync(CancellationToken.None).GetAwaiter().GetResult();

                    Assert.True(completed);
                    Assert.Equal(1, transcriptionService.RequestCount);
                    Assert.EndsWith(".wav", transcriptionService.LastAudioFilePath, StringComparison.OrdinalIgnoreCase);
                    Assert.False(transcriptionService.LastAudioFilePath.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase));
                    Assert.Single(viewModel.FinalizedTranscriptLines);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
            }
        });
    }

    [Fact]
    public async Task LoadedAudioFileName_ForLiveRecordingManifest_UsesSessionLabel()
    {
        string rootPath = CreateTempDirectory();

        try
        {
            var playbackService = new FakeAudioPlaybackService();
            var processLogService = new ProcessLogService();
            var transcriptionService = new StubAudioTranscriptionService([]);
            var viewModel = new MainViewModel(
                TranscriptionModelCatalog.Models,
                transcriptionService,
                CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                playbackService,
                processLogService,
                new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService),
                new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                new AppThemeService(),
                new AppPreferencesSnapshot(
                    CopyFinalizedWithTimeline: false,
                    AutoTranscribeWithAi: false,
                    ThemePreference: AppThemePreference.System,
                    AutoPlayTimelineSelection: true,
                    LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                    LiveAudioDeviceNumber: -1,
                    SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

            try
            {
                TranscriptSessionDocument liveSession = new()
                {
                    DisplayName = "Live Transcription 2026-05-02 10-00",
                    Audio = new TranscriptSessionAudioDocument
                    {
                        StorageKind = AudioStorageKinds.LiveRecordingManifest,
                        StoredRelativePath = TranscriptSessionStore.LiveRecordingManifestRelativePath,
                        OriginalFileName = TranscriptSessionStore.LiveSessionAudioName,
                    },
                };

                typeof(MainViewModel)
                    .GetField("_currentSessionDocument", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(viewModel, liveSession);
                typeof(MainViewModel)
                    .GetField("_loadedAudioFilePath", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(viewModel, Path.Combine(rootPath, "sessions", "live-1", "audio", "live", "manifest.json"));

                Assert.Equal(TranscriptSessionStore.LiveSessionAudioName, viewModel.LoadedAudioFileName);
                Assert.DoesNotContain("manifest.json", viewModel.LoadedAudioFileName, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task DeleteSelectedSessionCommand_IsDisabled_WhenOnlyRecentSessionIsSelected()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            string audioPath = CreateSilentWaveFile(16000);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var sessionStore = new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService);
                TranscriptSessionLoadResult imported = sessionStore.ImportAudioFile(audioPath);
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    sessionStore,
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    Assert.False(viewModel.HasCurrentSession);

                    viewModel.SelectedRecentSession = Assert.Single(sessionStore.ListRecentSessions());

                    Assert.False(viewModel.DeleteSelectedSessionCommand.CanExecute(null));
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                DeleteDirectory(rootPath);
                File.Delete(audioPath);
            }
        });
    }

    [Fact]
    public async Task DeleteSelectedSessionAsync_RemovesLoadedSession()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            string audioPath = CreateSilentWaveFile(16000);
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var sessionStore = new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService);
                TranscriptSessionLoadResult imported = sessionStore.ImportAudioFile(audioPath);
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    sessionStore,
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    await viewModel.LoadRecentSessionAsync(Assert.Single(sessionStore.ListRecentSessions()));

                    queuedContext.Drain();

                    Assert.True(viewModel.HasCurrentSession);
                    Assert.True(viewModel.DeleteSelectedSessionCommand.CanExecute(null));

                    EventHandler<ConfirmationRequest>? handler = null;
                    handler = (_, request) => request.IsConfirmed = true;
                    viewModel.ConfirmationRequested += handler;

                    try
                    {
                        MethodInfo deleteMethod = typeof(MainViewModel)
                            .GetMethod("DeleteSelectedSessionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        await (Task)deleteMethod.Invoke(viewModel, null)!;
                        queuedContext.Drain();
                    }
                    finally
                    {
                        viewModel.ConfirmationRequested -= handler;
                    }

                    Assert.False(viewModel.HasCurrentSession);
                    Assert.Empty(sessionStore.ListRecentSessions());
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
                File.Delete(audioPath);
            }
        });
    }

    [Fact]
    public async Task ClosePendingTranscribeAudioWorkflow_ExistingSession_RestoresClearedTranscript()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            string audioPath = CreateSilentWaveFile(16000);
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var sessionStore = new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService);
                TranscriptSessionLoadResult imported = sessionStore.ImportAudioFile(audioPath);
                imported.Document.Transcript.Lines.Add(new TranscriptSessionLineDocument
                {
                    Text = "original transcript",
                    SpeakerLabel = "Speaker 1",
                    StartSeconds = 0,
                    EndSeconds = 1,
                });
                imported.Document.Transcript.FinalText = "Speaker 1: original transcript";
                sessionStore.Save(imported.Document);

                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    sessionStore,
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    await viewModel.LoadRecentSessionAsync(Assert.Single(sessionStore.ListRecentSessions()));
                    queuedContext.Drain();
                    viewModel.ConfirmationRequested += ConfirmRequest;

                    try
                    {
                        Assert.True(viewModel.TryPrepareTranscribeAudioWorkflow());
                        Assert.Empty(viewModel.FinalizedTranscriptLines);

                        viewModel.ClosePendingTranscribeAudioWorkflow();
                        queuedContext.Drain();
                    }
                    finally
                    {
                        viewModel.ConfirmationRequested -= ConfirmRequest;
                    }

                    FinalizedTranscriptLineViewModel line = Assert.Single(viewModel.FinalizedTranscriptLines);
                    Assert.Equal("original transcript", line.Text);
                    TranscriptSessionLoadResult restored = sessionStore.LoadSession(imported.Document.SessionId);
                    Assert.Equal("original transcript", Assert.Single(restored.Document.Transcript.Lines).Text);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
                File.Delete(audioPath);
            }
        });

        static void ConfirmRequest(object? sender, ConfirmationRequest request)
        {
            request.IsConfirmed = true;
        }
    }

    [Fact]
    public async Task ClosePendingTranscribeAudioWorkflow_NewFile_ClearsPreviewWithoutCreatingSession()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            string audioPath = CreateSilentWaveFile(16000);
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var sessionStore = new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService);
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    sessionStore,
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    Assert.True(viewModel.TryImportAudioFileFromPath(audioPath));
                    Assert.True(viewModel.IsAudioFileLoaded);
                    Assert.Empty(sessionStore.ListRecentSessions());

                    Assert.True(viewModel.TryPrepareTranscribeAudioWorkflow());
                    viewModel.ClosePendingTranscribeAudioWorkflow();
                    queuedContext.Drain();

                    Assert.False(viewModel.IsAudioFileLoaded);
                    Assert.Empty(sessionStore.ListRecentSessions());
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
                File.Delete(audioPath);
            }
        });
    }

    [Fact]
    public async Task FailPreparedTranscribeAudioWorkflow_NewFile_DeletesTransientSessionAndClearsPreview()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            string audioPath = CreateSilentWaveFile(16000);
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService(
                    [],
                    new InvalidOperationException("Synthetic transcription failure."));
                var sessionStore = new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService);
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    sessionStore,
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    Assert.True(viewModel.TryImportAudioFileFromPath(audioPath));
                    Assert.True(viewModel.TryPrepareTranscribeAudioWorkflow());

                    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                        await viewModel.RunPreparedTranscribeAudioWorkflowAsync(CancellationToken.None));
                    viewModel.FailPreparedTranscribeAudioWorkflow();
                    queuedContext.Drain();

                    Assert.False(viewModel.IsAudioFileLoaded);
                    Assert.Empty(sessionStore.ListRecentSessions());
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
                File.Delete(audioPath);
            }
        });
    }

    [Fact]
    public async Task AppendLiveTranscriptionResult_SkipsNearBoundaryDuplicateRows()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService),
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    int firstAdded = viewModel.AppendLiveTranscriptionResult(new TranscriptionResult(
                        Text: "Boundary phrase",
                        Model: TranscriptionModelCatalog.WhisperSmall,
                        CreatedAt: DateTimeOffset.UtcNow,
                        Duration: TimeSpan.FromMilliseconds(100),
                        TokenLogprobs: Array.Empty<TranscriptionTokenLogprob>(),
                        LowConfidenceTokens: Array.Empty<LowConfidenceToken>(),
                        TimedLines: new[]
                        {
                            new TranscriptionTimedLine(
                                "Boundary phrase.",
                                TimeSpan.FromMilliseconds(80),
                                TimeSpan.FromMilliseconds(100),
                                false),
                        }));
                    int secondAdded = viewModel.AppendLiveTranscriptionResult(new TranscriptionResult(
                        Text: "Boundary phrase. Next sentence.",
                        Model: TranscriptionModelCatalog.WhisperSmall,
                        CreatedAt: DateTimeOffset.UtcNow,
                        Duration: TimeSpan.FromMilliseconds(200),
                        TokenLogprobs: Array.Empty<TranscriptionTokenLogprob>(),
                        LowConfidenceTokens: Array.Empty<LowConfidenceToken>(),
                        TimedLines: new[]
                        {
                            new TranscriptionTimedLine(
                                "Boundary phrase",
                                TimeSpan.FromMilliseconds(100),
                                TimeSpan.FromMilliseconds(140),
                                false),
                            new TranscriptionTimedLine(
                                "Next sentence",
                                TimeSpan.FromMilliseconds(150),
                                TimeSpan.FromMilliseconds(190),
                                false),
                        }));

                    Assert.Equal(1, firstAdded);
                    Assert.Equal(1, secondAdded);
                    Assert.Equal(new[] { "Boundary phrase.", "Next sentence" }, viewModel.FinalizedTranscriptLines.Select(line => line.Text).ToArray());
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
            }
        });
    }

    [Fact]
    public async Task AppendLiveTranscriptionResult_KeepsRepeatedRowsOutsideBoundaryOverlap()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService),
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    int firstAdded = viewModel.AppendLiveTranscriptionResult(new TranscriptionResult(
                        Text: "yes",
                        Model: TranscriptionModelCatalog.WhisperSmall,
                        CreatedAt: DateTimeOffset.UtcNow,
                        Duration: TimeSpan.FromMilliseconds(100),
                        TokenLogprobs: Array.Empty<TranscriptionTokenLogprob>(),
                        LowConfidenceTokens: Array.Empty<LowConfidenceToken>(),
                        TimedLines: new[]
                        {
                            new TranscriptionTimedLine(
                                "yes",
                                TimeSpan.FromMilliseconds(80),
                                TimeSpan.FromMilliseconds(100),
                                false),
                        }));
                    int secondAdded = viewModel.AppendLiveTranscriptionResult(new TranscriptionResult(
                        Text: "yes",
                        Model: TranscriptionModelCatalog.WhisperSmall,
                        CreatedAt: DateTimeOffset.UtcNow,
                        Duration: TimeSpan.FromMilliseconds(200),
                        TokenLogprobs: Array.Empty<TranscriptionTokenLogprob>(),
                        LowConfidenceTokens: Array.Empty<LowConfidenceToken>(),
                        TimedLines: new[]
                        {
                            new TranscriptionTimedLine(
                                "yes",
                                TimeSpan.FromMilliseconds(120),
                                TimeSpan.FromMilliseconds(140),
                                false),
                        }));

                    Assert.Equal(1, firstAdded);
                    Assert.Equal(1, secondAdded);
                    Assert.Equal(new[] { "yes", "yes" }, viewModel.FinalizedTranscriptLines.Select(line => line.Text).ToArray());
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
            }
        });
    }

    private static Task RunInStaAsync(Func<Task> action)
    {
        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action().GetAwaiter().GetResult();
                completionSource.SetResult();
            }
            catch (Exception ex)
            {
                completionSource.SetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completionSource.Task;
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"AudioScript-mainvm-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static ChunkedSpeakerDiarizationService CreateChunkedSpeakerDiarizationService(
        IAudioTranscriptionService audioTranscriptionService,
        ProcessLogService processLogService,
        TestSpeakerDiarizationEngine? diarizationEngine = null)
    {
        var waveClipExtractor = new WaveClipExtractor();
        var audioChunkingService = new AudioChunkingService(
            new AudioStandardizer(),
            new SilenceIntervalDetector(),
            new SilenceAwareChunkPlanner(),
            waveClipExtractor);
        var offlineDiarizationService = new OfflineSpeakerDiarizationService(
            diarizationEngine ?? new TestSpeakerDiarizationEngine(),
            processLogService);

        return new ChunkedSpeakerDiarizationService(
            audioChunkingService,
            offlineDiarizationService,
            processLogService);
    }

    private static IReadOnlyList<string> SeedSessionStore(TranscriptSessionStore sessionStore, int sessionCount)
    {
        var audioPaths = new List<string>(sessionCount);
        for (int index = 0; index < sessionCount; index++)
        {
            string audioPath = CreateSilentWaveFile(200000 + (index * 1024));
            audioPaths.Add(audioPath);
            sessionStore.ImportAudioFile(audioPath);
        }

        return audioPaths;
    }

    private sealed class StubEntitlementService : IEntitlementService
    {
        public StubEntitlementService(bool hasPremium)
        {
            CurrentSnapshot = new AppEntitlementSnapshot(
                IsPackaged: true,
                HasPremium: hasPremium,
                IsPremiumProductAvailable: true,
                PremiumProductDisplayName: "AudioScript Premium",
                StatusMessage: hasPremium
                    ? "Premium unlocked."
                    : "AudioScript Premium is available for purchase in Microsoft Store.");
        }

        public AppEntitlementSnapshot CurrentSnapshot { get; private set; }

        public event EventHandler<AppEntitlementSnapshot>? SnapshotChanged;

        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            SnapshotChanged?.Invoke(this, CurrentSnapshot);
            return Task.CompletedTask;
        }

        public Task<PremiumPurchaseResult> RequestPremiumPurchaseAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PremiumPurchaseResult(
                PremiumPurchaseStatus.NotSupported,
                "Stub entitlement service does not support purchases."));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubAppUpdateService : IAppUpdateService
    {
        public StubAppUpdateService(AppUpdateSnapshot snapshot)
        {
            CurrentSnapshot = snapshot;
        }

        public bool IsStoreUpdateSupported { get; set; } = true;

        public AppUpdateSnapshot CurrentSnapshot { get; private set; }

        public event EventHandler<AppUpdateSnapshot>? SnapshotChanged;

        public int UserInitiatedUpdateFlowCallCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task RunUserInitiatedUpdateFlowAsync(CancellationToken cancellationToken = default)
        {
            UserInitiatedUpdateFlowCallCount++;
            return Task.CompletedTask;
        }

        public Task<StoreUpdateOperationResult?> RunExitTimeInstallAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<StoreUpdateOperationResult?>(null);
        }

        public Task<bool> HasDeferredInstallOnExitAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task StopAsync()
        {
            return Task.CompletedTask;
        }

        public void Publish(AppUpdateSnapshot snapshot)
        {
            CurrentSnapshot = snapshot;
            SnapshotChanged?.Invoke(this, snapshot);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubAudioTranscriptionService : IAudioTranscriptionService
    {
        private readonly IReadOnlyList<TranscriptionTimedLine> _timedLines;
        private readonly Exception? _exception;

        public StubAudioTranscriptionService(IReadOnlyList<TranscriptionTimedLine> timedLines, Exception? exception = null)
        {
            _timedLines = timedLines;
            _exception = exception;
        }

        public int RequestCount { get; private set; }

        public string LastAudioFilePath { get; private set; } = string.Empty;

        public Task<TranscriptionResult> TranscribeAudioFileAsync(
            string audioFilePath,
            string model,
            CancellationToken cancellationToken,
            IProgress<TranscriptionProgressSnapshot>? progress = null)
        {
            RequestCount++;
            LastAudioFilePath = audioFilePath;
            if (_exception is not null)
            {
                return Task.FromException<TranscriptionResult>(_exception);
            }

            return Task.FromResult(new TranscriptionResult(
                Text: string.Join(Environment.NewLine, _timedLines.Select(line => line.Text)),
                Model: model,
                CreatedAt: DateTimeOffset.UtcNow,
                Duration: TimeSpan.FromSeconds(10),
                TokenLogprobs: Array.Empty<TranscriptionTokenLogprob>(),
                LowConfidenceTokens: Array.Empty<LowConfidenceToken>(),
                TimedLines: _timedLines));
        }
    }

    private sealed class TestSpeakerDiarizationEngine : ISpeakerDiarizationEngine
    {
        private readonly Exception? _exception;

        public TestSpeakerDiarizationEngine(Exception? exception = null)
        {
            _exception = exception;
        }

        public int RequestCount { get; private set; }

        public Task<IReadOnlyList<SpeakerDiarizationTurn>> DiarizeAudioFileAsync(
            string audioFilePath,
            CancellationToken cancellationToken,
            IProgress<SpeakerDiarizationProgress>? progress = null)
        {
            RequestCount++;
            if (_exception is not null)
            {
                return Task.FromException<IReadOnlyList<SpeakerDiarizationTurn>>(_exception);
            }

            progress?.Report(new SpeakerDiarizationProgress(1, 2));
            progress?.Report(new SpeakerDiarizationProgress(2, 2));
            IReadOnlyList<SpeakerDiarizationTurn> turns = [
                new SpeakerDiarizationTurn("speaker_1", TimeSpan.Zero, TimeSpan.FromSeconds(1.2)),
                new SpeakerDiarizationTurn("speaker_2", TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(4)),
            ];
            return Task.FromResult(turns);
        }
    }

    private static string CreateSilentWaveFile(long dataBytes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"AudioScript-mainvm-audio-{Guid.NewGuid():N}.wav");
        int sampleRate = 16000;
        short channels = 1;
        short bitsPerSample = 16;
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        int byteRate = sampleRate * blockAlign;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write((int)(36 + dataBytes));
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write((int)dataBytes);

        stream.SetLength(44 + dataBytes);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        Directory.Delete(path, recursive: true);
    }

    private static void DeleteFiles(IEnumerable<string> filePaths)
    {
        foreach (string path in filePaths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            File.Delete(path);
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            _callbacks.Enqueue((d, state));
        }

        public void Drain()
        {
            while (_callbacks.TryDequeue(out var callback))
            {
                callback.Callback(callback.State);
            }
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public StubHttpMessageHandler(string responseBody = "{}")
        {
            _responseBody = responseBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FakeAudioPlaybackService : IAudioPlaybackService
    {
        private string? _loadedFilePath;
        private bool _isPlaying;
        private bool _isMuted;
        private TimeSpan _position;

        public event EventHandler? PlaybackStateChanged;

        public int LoadFileCallCount { get; private set; }

        public string? LoadedFilePath => _loadedFilePath;

        public bool IsLoaded => !string.IsNullOrWhiteSpace(_loadedFilePath);

        public bool IsPlaying => _isPlaying;

        public bool IsMuted
        {
            get => _isMuted;
            set => _isMuted = value;
        }

        public TimeSpan Duration => IsLoaded ? TimeSpan.FromSeconds(10) : TimeSpan.Zero;

        public TimeSpan Position => _position;

        public void LoadFile(string filePath)
        {
            _loadedFilePath = Path.GetFullPath(filePath);
            _position = TimeSpan.Zero;
            _isPlaying = false;
            LoadFileCallCount++;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void LoadLiveRecordingManifest(string manifestPath)
        {
            LoadFile(manifestPath);
        }

        public void UnloadFile()
        {
            _loadedFilePath = null;
            _position = TimeSpan.Zero;
            _isPlaying = false;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Play()
        {
            if (!IsLoaded)
            {
                throw new InvalidOperationException("No audio file is loaded.");
            }

            _isPlaying = true;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Pause()
        {
            _isPlaying = false;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Stop()
        {
            _isPlaying = false;
            _position = TimeSpan.Zero;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Seek(TimeSpan position)
        {
            _position = position < TimeSpan.Zero
                ? TimeSpan.Zero
                : position > Duration
                    ? Duration
                    : position;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            UnloadFile();
        }
    }
}

