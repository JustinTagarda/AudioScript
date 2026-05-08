using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioScript.Audio;
using AudioScript.Services;
using AudioScript.ViewModels;

namespace AudioScript;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\AudioScript_SingleInstance";
    private const string ActivateEventName = @"Local\AudioScript_Activate";
    private const string PremiumStoreId = "9PD5288V5Q49";

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

    [STAThread]
    private static void Main(string[] args)
    {
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

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
        processLogService.Log("App", $"Application startup initiated. Log file: {processLogService.LogFilePath}");
        processLogService.Log(
            "AppData",
            $"App data root resolved. root='{appDataPathProvider.RootPath}', packaged={appDataPathProvider.IsPackaged}.");

        var transcriptionOptions = new TranscriptionOptions();
        var assetProvisioningService = new AssetProvisioningService(processLogService, appDataPathProvider);
        _assetProvisioningService = assetProvisioningService;
        var whisperModelManager = new WhisperModelManager(
            processLogService,
            appDataPathProvider.ModelsPath,
            assetProvisioningService: assetProvisioningService);
        var appDataMigrationService = new AppDataMigrationService(appDataPathProvider, processLogService);
        appDataMigrationService.MigrateLegacyData(whisperModelManager.Models);
        _appPreferencesStore = new AppPreferencesStore(appDataPathProvider.SettingsFilePath);
        _appThemeService = new AppThemeService();

        AppPreferencesSnapshot appPreferencesSnapshot = _appPreferencesStore.Load();
        _appThemeService.Apply(appPreferencesSnapshot.ThemePreference);
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
            whisperModelManager);
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

        MainWindow? updateBusyWindow = null;
        IntPtr updateOwnerWindowHandle = IntPtr.Zero;
        var appVersionProvider = new AppVersionProvider();
        _entitlementService = new StoreEntitlementService(
            appVersionProvider,
            processLogService,
            () => updateOwnerWindowHandle,
            new StoreEntitlementServiceOptions
            {
                PremiumProductDisplayName = "AudioScript Premium",
                PremiumStoreId = PremiumStoreId,
                PremiumKeyword = "premium",
            });
        _appUpdateService = new AppUpdateService(
            appVersionProvider,
            new StoreUpdateClient(processLogService, () => updateOwnerWindowHandle),
            processLogService,
            () => updateBusyWindow is MainWindow window && window.IsBusyForAppUpdate);

        _mainViewModel = new MainViewModel(
            whisperModelManager.GetSelectableTranscriptionModels(),
            whisperTranscriptionService,
            chunkedSpeakerDiarizationService,
            audioPlaybackService,
            processLogService,
            sessionStore,
            _appPreferencesStore,
            _appThemeService,
            appPreferencesSnapshot,
            _appUpdateService,
            _entitlementService,
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
        updateBusyWindow = mainWindow;
        _windowPlacementService.Apply(mainWindow);
        _windowPlacementService.Attach(mainWindow);

        MainWindow = mainWindow;
        mainWindow.Show();
        updateOwnerWindowHandle = new WindowInteropHelper(mainWindow).Handle;
        _ = _entitlementService.RefreshAsync();
        _ = _appUpdateService.StartAsync();
        processLogService.Log("App", "Application startup completed.");
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
        _processLogService?.Dispose();
    }

    private void RegisterGlobalExceptionLogging(ProcessLogService processLogService)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            processLogService.LogException("App", "Unhandled dispatcher exception.", e.Exception);
        }

        void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception exception)
            {
                processLogService.LogException("App", $"Unhandled AppDomain exception. IsTerminating={e.IsTerminating}.", exception);
                return;
            }

            processLogService.Log(
                "App",
                $"Unhandled AppDomain exception object of type '{e.ExceptionObject?.GetType().FullName ?? "unknown"}'. IsTerminating={e.IsTerminating}.",
                ProcessLogLevel.Error);
        }

        void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
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

