using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioScript.Audio;
using AudioScript.Services;
using AudioScript.Services.Store;
using AudioScript.ViewModels;

namespace AudioScript;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\AudioScript_SingleInstance";
    private const string ActivateEventName = @"Local\AudioScript_Activate";
    private static readonly StorePremiumAddonDefinition PremiumAddon = StorePremiumAddonCatalog.AudioScriptPremiumLifetime;

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private CancellationTokenSource? _activationListenerCts;
    private Task? _activationListenerTask;
    private ProcessLogService? _processLogService;
    private AssetProvisioningService? _assetProvisioningService;

    private MainViewModel? _mainViewModel;
    private IAppUpdateService? _appUpdateService;
    private IEntitlementService? _entitlementService;
    private WindowPlacementService? _windowPlacementService;
    private AppPreferencesStore? _appPreferencesStore;
    private AppThemeService? _appThemeService;

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
        PremiumAddon.Validate();
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

        var appDataMigrationService = new AppDataMigrationService(appDataPathProvider, processLogService);
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
        if (!RunStartupProvisioningIfRequired(startupProvisioningCoordinator, processLogService))
        {
            processLogService.UpdateCrashContext("app.startup.provisioning.aborted");
            Shutdown();
            return;
        }

        var transcriptionOptions = new TranscriptionOptions();
        var audioStandardizer = new AudioStandardizer();
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
        var pyannoteCommunityModelManager = new PyannoteCommunityModelManager(
            assetProvisioningService,
            appDataPathProvider);
        var pyannoteCommunityDiarizationEngine = new PyannoteCommunityDiarizationEngine(
            audioStandardizer,
            pyannoteCommunityModelManager,
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

        IntPtr updateOwnerWindowHandle = IntPtr.Zero;
        var appVersionProvider = new AppVersionProvider();
        var storeContextProvider = new StoreContextProvider(processLogService, () => updateOwnerWindowHandle);
        var storeNavigationService = new StoreNavigationService(processLogService);
        var premiumEntitlementCache = new PremiumEntitlementCache(
            Path.Combine(appDataPathProvider.SettingsPath, "premium-entitlement.cache"),
            processLogService);
        _entitlementService = new StoreEntitlementService(
            appVersionProvider,
            processLogService,
            () => updateOwnerWindowHandle,
            storeContextProvider,
            premiumEntitlementCache,
            new StoreEntitlementServiceOptions
            {
                PremiumProductDisplayName = PremiumAddon.DisplayName,
                PremiumStoreIds = [PremiumAddon.StoreId],
                PremiumProductIds = [PremiumAddon.ProductId],
                PremiumKeyword = "premium",
            });
        var storeUpdateProvider = new MicrosoftStoreUpdateProvider(
            appVersionProvider,
            processLogService,
            storeContextProvider);
        processLogService.Log(
            "StoreUpdate",
            $"store_update_provider_selected; packaged={appVersionProvider.IsPackaged}; storeApiSupported={storeContextProvider.IsStoreApiAvailable}; provider={storeUpdateProvider.GetType().Name}");
        var deferredUpdateStateStore = new DeferredUpdateStateStore(
            Path.Combine(appDataPathProvider.SettingsPath, "update-state.json"),
            processLogService);
        _appUpdateService = new AppUpdateService(
            appVersionProvider,
            storeUpdateProvider,
            deferredUpdateStateStore,
            processLogService,
            new StoreUpdateOptions
            {
                EnableStartupUpdateCheck = true,
                PreferSilentUpdateWhenAvailable = true,
                UseFallbackStoreUiWhenSilentUnavailable = true,
                ShowProgressDuringFallbackUi = true,
                StartupDelay = TimeSpan.Zero,
            });
        var appStatusViewModel = new AppStatusViewModel(
            new StoreLicenseService(_entitlementService),
            new StorePurchaseService(_entitlementService),
            storeNavigationService,
            new AppVersionService(appVersionProvider),
            _appUpdateService);

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
            _appUpdateService,
            _entitlementService,
            appStatusViewModel,
            () => whisperModelManager.GetSelectableTranscriptionModels());

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
            pyannoteCommunityModelManager: pyannoteCommunityModelManager)
        {
            DataContext = _mainViewModel,
        };
        _windowPlacementService.Apply(mainWindow);
        _windowPlacementService.Attach(mainWindow);

        _ = _entitlementService.RefreshAsync();
        MainWindow = mainWindow;
        mainWindow.ContentRendered += (_, _) =>
        {
            updateOwnerWindowHandle = new WindowInteropHelper(mainWindow).Handle;
            _ = _appUpdateService.StartAsync();
        };
        mainWindow.Show();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        updateOwnerWindowHandle = new WindowInteropHelper(mainWindow).Handle;
        processLogService.Log("App", "Application startup completed.");
        processLogService.UpdateCrashContext("app.idle.ready");
    }

    private bool RunStartupProvisioningIfRequired(
        StartupAssetProvisioningCoordinator startupProvisioningCoordinator,
        ProcessLogService processLogService)
    {
        IReadOnlyList<ProvisionedAssetDescriptor> requiredAssetsForDisplay = startupProvisioningCoordinator.GetRequiredAssetsForStartupDisplay();
        IReadOnlyList<ProvisionedAssetDescriptor> requiredAssetsNeedingInstall = startupProvisioningCoordinator.GetRequiredAssetsNeedingInstall();
        if (requiredAssetsNeedingInstall.Count == 0)
        {
            processLogService.Log(
                nameof(StartupAssetProvisioningCoordinator),
                "startup_provisioning skipped; all required assets are already installed.");
            return true;
        }

        var startupWindow = new StartupProvisioningWindow();
        var viewModel = new StartupProvisioningWindowViewModel(requiredAssetsForDisplay);
        foreach (ProvisionedAssetDescriptor asset in requiredAssetsForDisplay)
        {
            AssetProvisioningStatus status = startupProvisioningCoordinator.GetAssetStatus(asset.Id);
            (string statusLabel, double percent) = status.State switch
            {
                AssetProvisioningState.Ready => ("Ready", 100),
                AssetProvisioningState.Unsupported => ("Unsupported", 0),
                AssetProvisioningState.Unconfigured => ("Unconfigured", 0),
                _ => ("Waiting...", 0),
            };
            viewModel.SetAssetStatus(asset.Id, statusLabel, percent);
        }

        startupWindow.DataContext = viewModel;
        processLogService.Log("StartupProvisioning", "Startup provisioning dialog opened modally.");

        StartupProvisioningResult? provisioningResult = null;
        var cancellationTokenSource = new CancellationTokenSource();
        startupWindow.CancellationTokenSource = cancellationTokenSource;
        startupWindow.ContentRendered += async (_, _) =>
        {
            try
            {
                var progress = new Progress<AssetProvisioningProgress>(assetProgress =>
                {
                    viewModel.UpdateProgress(assetProgress);
                });

                StartupProvisioningResult result = await startupProvisioningCoordinator
                    .ProvisionRequiredAssetsAsync(progress, cancellationTokenSource.Token)
                    .ConfigureAwait(true);
                provisioningResult = result;

                if (result.Succeeded)
                {
                    viewModel.MarkCompleted();
                }
                else
                {
                    viewModel.MarkFailed("One or more startup assets failed to install.");
                }

                startupWindow.CloseWithResult(result.Succeeded);
            }
            catch (OperationCanceledException)
            {
                viewModel.MarkCanceled();
                startupWindow.CloseWithResult(false);
            }
            catch (Exception ex)
            {
                processLogService.LogException(
                    nameof(StartupAssetProvisioningCoordinator),
                    "Startup asset provisioning failed.",
                    ex);
                viewModel.MarkFailed("Startup asset installation failed.");
                provisioningResult = new StartupProvisioningResult(
                    RequiredAssetCount: requiredAssetsNeedingInstall.Count,
                    InstalledAssetCount: 0,
                    FailedAssetCount: requiredAssetsNeedingInstall.Count,
                    WasCanceled: false,
                    Failures: requiredAssetsNeedingInstall.Select(asset => new StartupProvisioningAssetFailure(
                        asset.Id,
                        asset.DisplayName,
                        "Startup asset installation failed.",
                        ex.Message)).ToArray());
                startupWindow.CloseWithResult(false);
            }
        };

        bool? dialogResult = startupWindow.ShowDialog();
        cancellationTokenSource.Dispose();

        if (provisioningResult is not null && provisioningResult.FailedAssetCount > 0)
        {
            ShowStartupProvisioningFailureSummary(provisioningResult.Failures);
            ShowStartupProvisioningFailureExitReason(provisioningResult.Failures.Count);
            return false;
        }

        return dialogResult == true;
    }

    private static void ShowStartupProvisioningFailureSummary(
        IReadOnlyList<StartupProvisioningAssetFailure> failures)
    {
        var builder = new StringBuilder();
        builder.AppendLine("One or more required startup dependencies could not be installed:");
        builder.AppendLine();

        foreach (StartupProvisioningAssetFailure failure in failures)
        {
            builder.AppendLine($"- {failure.DisplayName} ({failure.AssetId})");
            builder.AppendLine($"  Reason: {failure.Reason}");
            if (!string.IsNullOrWhiteSpace(failure.LimitationOrBlocker))
            {
                builder.AppendLine($"  Limitation/Blocker: {failure.LimitationOrBlocker}");
            }

            builder.AppendLine();
        }

        var dialog = new ErrorDialogWindow(builder.ToString().TrimEnd());
        dialog.Title = "Startup dependency installation results";
        _ = dialog.ShowDialog();
    }

    private static void ShowStartupProvisioningFailureExitReason(int failedAssetCount)
    {
        string message =
            $"AudioScript cannot continue because {failedAssetCount} required startup dependency " +
            "failed to install. Resolve the dependency issue and start the app again.";
        _ = System.Windows.MessageBox.Show(
            message,
            "Startup dependencies unavailable",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
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
        try
        {
            _appUpdateService?.StopAsync().GetAwaiter().GetResult();
            _mainViewModel?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _appUpdateService?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _entitlementService?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _appUpdateService = null;
            _entitlementService = null;
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
}

