using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Interop;
using AudioScript.Audio;
using AudioScript.Services;
using AudioScript.Services.Store;
using AudioScript.ViewModels;

namespace AudioScript;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\AudioScript_SingleInstance";
    private const string ActivateEventName = @"Local\AudioScript_Activate";
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private CancellationTokenSource? _activationListenerCts;
    private Task? _activationListenerTask;
    private ProcessLogService? _processLogService;
    private AssetProvisioningService? _assetProvisioningService;
    private AppUpdateService? _appUpdateService;

    private MainViewModel? _mainViewModel;
    private WindowPlacementService? _windowPlacementService;
    private AppPreferencesStore? _appPreferencesStore;
    private AppThemeService? _appThemeService;
    private DateTimeOffset _lastPremiumActivationRefreshUtc = DateTimeOffset.MinValue;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        bool createdNew;
        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, createdNew: out createdNew);

        if (!createdNew)
        {
            NotifyRunningInstance();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activationListenerCts = new CancellationTokenSource();
        _activationListenerTask = Task.Run(
            () => ListenForActivationRequests(_activationListenerCts.Token),
            _activationListenerCts.Token);

        var appDataPathProvider = AppDataPathProvider.Create();
        var processLogService = new ProcessLogService(appDataPathProvider.LogsPath);
        _processLogService = processLogService;
        RegisterGlobalExceptionLogging(processLogService);
        processLogService.UpdateCrashContext("app.startup.bootstrap");
        processLogService.Log("App", $"Application startup initiated. Log file: {processLogService.LogFilePath}");
        processLogService.Log(
            "AppData",
            $"App data root resolved. root='{appDataPathProvider.RootPath}', packaged={appDataPathProvider.IsPackaged}.");
        processLogService.LogEnvironmentSnapshot("Environment", appDataPathProvider.RootPath, appDataPathProvider.IsPackaged);

        var assetProvisioningService = new AssetProvisioningService(processLogService, appDataPathProvider);
        _assetProvisioningService = assetProvisioningService;
        var startupProvisioningCoordinator = new StartupAssetProvisioningCoordinator(
            assetProvisioningService,
            processLogService);
        var audioStandardizer = new AudioStandardizer();
        var pyannoteCommunityModelManager = new PyannoteCommunityModelManager(
            assetProvisioningService,
            appDataPathProvider);
        var pyannoteCommunityDiarizationEngine = new PyannoteCommunityDiarizationEngine(
            audioStandardizer,
            pyannoteCommunityModelManager,
            processLogService);
        var officialSourceBootstrapService = new OfficialSourceBootstrapService(
            appDataPathProvider,
            processLogService);
        var pythonDependencyRepairService = new PythonDependencyRepairService(
            pyannoteCommunityModelManager,
            processLogService);
        var speakerDiarizationDependencyCoordinator = new SpeakerDiarizationDependencyCoordinator(
            assetProvisioningService,
            pyannoteCommunityModelManager,
            pythonDependencyRepairService,
            pyannoteCommunityDiarizationEngine,
            processLogService,
            officialSourceBootstrapService);
        var startupDependencyHealthCoordinator = new StartupDependencyHealthCoordinator(
            startupProvisioningCoordinator,
            processLogService);
        bool isSpeakerDiarizationSupported = pyannoteCommunityModelManager.IsSupportedOnCurrentArchitecture;

        var appDataMigrationService = new AppDataMigrationService(appDataPathProvider, processLogService);
        var appVersionProvider = new AppVersionProvider();
        StorePremiumAddonDefinition premiumAddon = StorePremiumAddonCatalog.AudioScriptPremiumLifetime;
        premiumAddon.Validate();
        var storeContextProvider = new StoreContextProvider(processLogService, ResolveStoreOwnerWindowHandle);
        var premiumEntitlementCache = new PremiumEntitlementCache(
            Path.Combine(appDataPathProvider.SettingsPath, "premium-entitlement-cache.json"),
            processLogService);
        var entitlementService = new StoreEntitlementService(
            appVersionProvider,
            processLogService,
            ownerWindowHandleProvider: ResolveStoreOwnerWindowHandle,
            storeContextProvider: storeContextProvider,
            premiumEntitlementCache: premiumEntitlementCache,
            options: new StoreEntitlementServiceOptions
            {
                TreatUnpackagedBuildsAsPremium = false,
                PremiumProductDisplayName = premiumAddon.DisplayName,
                PremiumStoreIds = [premiumAddon.StoreId],
                PremiumProductIds = [premiumAddon.ProductId],
                PremiumKeyword = "premium",
            });
        var deferredUpdateStateStore = new DeferredUpdateStateStore(
            Path.Combine(appDataPathProvider.SettingsPath, "store-update-state.json"),
            processLogService);
        var storeUpdateProvider = new MicrosoftStoreUpdateProvider(
            appVersionProvider,
            processLogService,
            storeContextProvider);
        _appUpdateService = new AppUpdateService(
            appVersionProvider,
            storeUpdateProvider,
            deferredUpdateStateStore,
            processLogService);
        var whisperModelManager = new WhisperModelManager(
            processLogService,
            appDataPathProvider.ModelsPath,
            assetProvisioningService: assetProvisioningService);
        appDataMigrationService.MigrateLegacyData(whisperModelManager.Models);
        _appPreferencesStore = new AppPreferencesStore(appDataPathProvider.SettingsFilePath);
        _appThemeService = new AppThemeService();

        AppPreferencesSnapshot appPreferencesSnapshot = _appPreferencesStore.Load();
        _appThemeService.Apply(appPreferencesSnapshot.ThemePreference);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        StartupDependencyHealthResult startupDependencyHealthResult = RunStartupDependencyHealthCheck(
            startupProvisioningCoordinator,
            startupDependencyHealthCoordinator,
            processLogService);
        if (!startupDependencyHealthResult.Succeeded)
        {
            processLogService.UpdateCrashContext("app.startup.dependency_validation.aborted");
            Shutdown();
            return;
        }

        var transcriptionOptions = new TranscriptionOptions();
        var silenceIntervalDetector = new SilenceIntervalDetector();
        var audioChunkingOptions = AudioChunkingOptions.Default;
        var chunkPlanner = new SilenceAwareChunkPlanner(
            audioChunkingOptions.BuildRecommendedChunkPlannerOptions());
        var waveClipExtractor = new WaveClipExtractor();
        var audioChunkingService = new AudioChunkingService(
            audioStandardizer,
            silenceIntervalDetector,
            chunkPlanner,
            waveClipExtractor,
            audioChunkingOptions);
        var audioPlaybackService = new NaudioAudioPlaybackService(processLogService);
        var sessionStore = new TranscriptSessionStore(appDataPathProvider.SessionsPath, processLogService);
        var whisperTranscriptionService = new WhisperAudioTranscriptionService(
            audioStandardizer,
            transcriptionOptions,
            processLogService,
            whisperModelManager,
            assetProvisioningService,
            appDataPathProvider);
        var chunkedAudioTranscriptionService = new ChunkedAudioTranscriptionService(
            audioChunkingService,
            whisperTranscriptionService,
            processLogService);
        var offlineSpeakerDiarizationService = new OfflineSpeakerDiarizationService(
            pyannoteCommunityDiarizationEngine,
            processLogService);
        var chunkedSpeakerDiarizationService = new ChunkedSpeakerDiarizationService(
            audioChunkingService,
            offlineSpeakerDiarizationService,
            processLogService);
        var playbackEditTranscriptionOptions = new PlaybackTranscriptionSessionOptions(
            MinimumSegmentDuration: TimeSpan.FromSeconds(1.5),
            InterimWindowDuration: TimeSpan.FromSeconds(10),
            InterimCadence: TimeSpan.FromSeconds(10),
            FinalWindowDuration: TimeSpan.FromSeconds(10),
            PollInterval: TimeSpan.FromMilliseconds(100),
            MinimumPeakLevel: 0);
        _windowPlacementService = new WindowPlacementService(
            Path.Combine(appDataPathProvider.SettingsPath, "window-placement.json"));
        _mainViewModel = new MainViewModel(
            whisperModelManager.GetSelectableTranscriptionModels(),
            chunkedAudioTranscriptionService,
            chunkedSpeakerDiarizationService,
            audioPlaybackService,
            processLogService,
            sessionStore,
            _appPreferencesStore,
            _appThemeService,
            appPreferencesSnapshot,
            appUpdateService: _appUpdateService,
            entitlementService: entitlementService,
            () => whisperModelManager.GetSelectableTranscriptionModels(),
            isSpeakerDiarizationRuntimeAvailable: isSpeakerDiarizationSupported,
            speakerDiarizationRuntimeStatusMessage: isSpeakerDiarizationSupported
                ? "Speaker diarization dependencies install when Detect Speaker is used."
                : "Speaker diarization requires an x64 AudioScript build.");

        var mainWindow = new MainWindow(
            playbackTranscriptionSessionFactory: () => new PlaybackTranscriptionSession(
                new PlaybackAudioCaptureService(audioPlaybackService),
                whisperTranscriptionService,
                processLogService,
                playbackEditTranscriptionOptions),
            liveRecordingCaptureSessionFactory: (source, gainOptions, recordingSession) => new LiveRecordingCaptureSession(
                CreateLiveCaptureService(source, gainOptions),
                recordingSession,
                processLogService),
            rowAudioTranscriptionService: whisperTranscriptionService,
            rowAudioStandardizer: audioStandardizer,
            rowWaveClipExtractor: waveClipExtractor,
            processLogService: processLogService,
            whisperModelManager: whisperModelManager,
            pyannoteCommunityModelManager: pyannoteCommunityModelManager,
            speakerDiarizationDependencyCoordinator: speakerDiarizationDependencyCoordinator)
        {
            DataContext = _mainViewModel,
        };
        _windowPlacementService.Apply(mainWindow);
        _windowPlacementService.Attach(mainWindow);
        MainWindow = mainWindow;
        mainWindow.Show();
        mainWindow.Activated += OnMainWindowActivatedRefreshPremium;
        mainWindow.ContentRendered += async (_, _) =>
        {
            try
            {
                await _mainViewModel.RefreshPremiumEntitlementAsync();
                if (_appUpdateService is not null)
                {
                    await _appUpdateService.StartAsync();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                processLogService.LogException("Premium", "premium_startup_refresh_failed", ex);
            }
        };
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        processLogService.Log("App", "Application startup completed.");
        processLogService.UpdateCrashContext("app.idle.ready");
    }

    private StartupDependencyHealthResult RunStartupDependencyHealthCheck(
        StartupAssetProvisioningCoordinator startupProvisioningCoordinator,
        IStartupDependencyHealthCoordinator startupDependencyHealthCoordinator,
        ProcessLogService processLogService)
    {
        processLogService.Log("StartupProvisioning", "Running startup dependency validation before opening the main window.");

        StartupDependencyHealthResult healthResult;
        try
        {
            healthResult = startupDependencyHealthCoordinator
                .RunAsync(progress: null, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            processLogService.LogException("StartupDependency", "Startup dependency check failed.", ex);
            healthResult = new StartupDependencyHealthResult(
                Succeeded: false,
                Degraded: true,
                FailedItems:
                [
                    new DependencyHealthItem(
                        "startup-dependency-check",
                        "Startup dependency health check",
                        DependencyHealthCategory.Asset,
                        DependencyHealthStatus.Failed,
                        ex.Message,
                        "Some features may be unavailable.",
                        [])
                ],
                AttemptedRepairs: []);
        }

        if (healthResult.FailedItems.Count > 0)
        {
            ShowStartupDependencyFailureSummary(healthResult.FailedItems);
        }

        return healthResult;
    }

    private static void ShowStartupDependencyFailureSummary(
        IReadOnlyList<DependencyHealthItem> failures)
    {
        var builder = new StringBuilder();
        builder.AppendLine("One or more required bundled runtime components are missing or corrupted:");
        builder.AppendLine();

        foreach (DependencyHealthItem failure in failures)
        {
            builder.AppendLine($"- {failure.DisplayName} ({failure.Id})");
            builder.AppendLine($"  Reason: {failure.Message}");
            if (!string.IsNullOrWhiteSpace(failure.Impact))
            {
                builder.AppendLine($"  Impact: {failure.Impact}");
            }

            builder.AppendLine();
        }

        var dialog = new ErrorDialogWindow(builder.ToString().TrimEnd());
        dialog.Title = "AudioScript installation issue";
        _ = dialog.ShowDialog();
    }

    private static IAudioLoopbackCaptureService CreateLiveCaptureService(
        AudioInputDeviceOption source,
        LiveAudioGainOptions gainOptions)
    {
        return new AutomaticGainAudioCaptureService(
            CreateStandardizedLiveCaptureService(source),
            gainOptions);
    }

    private static IAudioLoopbackCaptureService CreateStandardizedLiveCaptureService(
        AudioInputDeviceOption source)
    {
        return source.Kind switch
        {
            LiveAudioSourceKind.DefaultPlayback => new StandardizingAudioCaptureService(
                new WasapiLoopbackCaptureService()),
            LiveAudioSourceKind.MicrophoneAndDefaultPlayback => new CompositeAudioCaptureService(
                new StandardizingAudioCaptureService(new MicrophoneAudioCaptureService(source.DeviceNumber)),
                new StandardizingAudioCaptureService(new WasapiLoopbackCaptureService())),
            _ => new StandardizingAudioCaptureService(
                new MicrophoneAudioCaptureService(source.DeviceNumber)),
        };
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _processLogService?.Log("App", "Application exit initiated.");
        _processLogService?.UpdateCrashContext("app.shutdown.started");
        if (MainWindow is not null)
        {
            MainWindow.Activated -= OnMainWindowActivatedRefreshPremium;
        }
        try
        {
            _mainViewModel?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            _activationListenerCts?.Cancel();

            try
            {
                _activateEvent?.Set();
            }
            catch
            {
                // Ignore wake failures during shutdown.
            }

            if (_activationListenerTask is not null)
            {
                try
                {
                    _activationListenerTask.Wait(TimeSpan.FromMilliseconds(500));
                }
                catch
                {
                    // Ignore listener shutdown failures.
                }
            }

            _activationListenerTask = null;
            _activationListenerCts?.Dispose();
            _activationListenerCts = null;
            _activateEvent?.Dispose();
            _activateEvent = null;

            if (_singleInstanceMutex is not null)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Ignore release errors when mutex is not owned.
                }

                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }
        }

        base.OnExit(e);
        if (_appUpdateService is not null)
        {
            try
            {
                _appUpdateService.StopAsync().GetAwaiter().GetResult();
                _appUpdateService.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _processLogService?.LogException("AppUpdate", "app_update_shutdown_failed", ex);
            }
            finally
            {
                _appUpdateService = null;
            }
        }
        _assetProvisioningService?.Dispose();
        _assetProvisioningService = null;
        _processLogService?.Log("App", "Application exit completed.");
        _processLogService?.UpdateCrashContext("app.shutdown.completed");
        _processLogService?.Dispose();
    }

    private void RegisterGlobalExceptionLogging(ProcessLogService processLogService)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            processLogService.UpdateCrashContext("app.crash.dispatcher_unhandled", e.Exception.GetType().FullName);
            processLogService.LogException("App", "Unhandled dispatcher exception.", e.Exception);
        }

        void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception exception)
            {
                processLogService.UpdateCrashContext("app.crash.appdomain_unhandled", exception.GetType().FullName);
                processLogService.LogException("App", $"Unhandled AppDomain exception. IsTerminating={e.IsTerminating}.", exception);
                return;
            }

            processLogService.UpdateCrashContext("app.crash.appdomain_unhandled_non_exception");
            processLogService.Log(
                "App",
                $"Unhandled AppDomain exception object of type '{e.ExceptionObject?.GetType().FullName ?? "unknown"}'. IsTerminating={e.IsTerminating}.",
                ProcessLogLevel.Error);
        }

        void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            processLogService.UpdateCrashContext("app.crash.unobserved_task", e.Exception.GetType().FullName);
            processLogService.LogException("App", "Unobserved task exception.", e.Exception);
        }
    }

    private void ListenForActivationRequests(CancellationToken cancellationToken)
    {
        if (_activateEvent is null)
        {
            return;
        }

        WaitHandle[] handles = {
            _activateEvent,
            cancellationToken.WaitHandle,
        };

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int signalIndex = WaitHandle.WaitAny(handles);

                if (signalIndex == 1 || cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                Dispatcher.BeginInvoke(new Action(ActivateMainWindow));
            }
        }
        catch (ObjectDisposedException)
        {
            // Ignore shutdown races.
        }
    }

    private void ActivateMainWindow()
    {
        if (MainWindow is null)
        {
            return;
        }

        if (MainWindow.WindowState == WindowState.Minimized)
        {
            MainWindow.WindowState = WindowState.Normal;
        }

        if (!MainWindow.IsVisible)
        {
            MainWindow.Show();
        }

        MainWindow.Activate();
        MainWindow.Topmost = true;
        MainWindow.Topmost = false;
        MainWindow.Focus();
    }

    private async void OnMainWindowActivatedRefreshPremium(object? sender, EventArgs e)
    {
        if (_mainViewModel is null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _lastPremiumActivationRefreshUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _lastPremiumActivationRefreshUtc = now;
        try
        {
            await _mainViewModel.RefreshPremiumEntitlementAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _processLogService?.LogException("Premium", "premium_activation_refresh_failed", ex);
        }
    }

    private static void NotifyRunningInstance()
    {
        try
        {
            using EventWaitHandle activateEvent = EventWaitHandle.OpenExisting(ActivateEventName);
            activateEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Existing instance is not yet ready to receive activation signal.
        }
        catch
        {
            // Ignore activation signal failures for secondary instances.
        }
    }

    private IntPtr ResolveStoreOwnerWindowHandle()
    {
        IntPtr scopedHandle = StorePurchaseOwnerWindowBinding.GetCurrentOrDefault();
        if (scopedHandle != IntPtr.Zero)
        {
            return scopedHandle;
        }

        Window? owner = Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
            ?? MainWindow
            ?? Windows.OfType<Window>().FirstOrDefault(window => window.IsVisible);
        if (owner is null)
        {
            return IntPtr.Zero;
        }

        try
        {
            return new WindowInteropHelper(owner).Handle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
}

