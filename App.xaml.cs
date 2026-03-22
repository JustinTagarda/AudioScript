using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VoxTranscribe.Audio;
using VoxTranscribe.Services;
using VoxTranscribe.ViewModels;

namespace VoxTranscribe;

public partial class App : System.Windows.Application {
    private const string SingleInstanceMutexName = @"Local\VoxTranscribe_SingleInstance";
    private const string ActivateEventName = @"Local\VoxTranscribe_Activate";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private CancellationTokenSource? _activationListenerCts;
    private Task? _activationListenerTask;
    private ProcessLogService? _processLogService;

    private HttpClient? _httpClient;
    private MainViewModel? _mainViewModel;
    private WindowPlacementService? _windowPlacementService;
    private OpenAiSettingsStore? _openAiSettingsStore;
    private OpenAiApiKeyValidationService? _openAiApiKeyValidationService;
    private AppPreferencesStore? _appPreferencesStore;
    private AppThemeService? _appThemeService;

    [STAThread]
    private static void Main(string[] args) {
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(System.Windows.StartupEventArgs e) {
        base.OnStartup(e);

        bool createdNew;
        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, createdNew: out createdNew);

        if (!createdNew) {
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

        var openAiOptions = new OpenAiTranscriptionOptions();
        _openAiSettingsStore = new OpenAiSettingsStore();
        _appPreferencesStore = new AppPreferencesStore();
        _appThemeService = new AppThemeService();

        OpenAiSettingsSnapshot openAiSnapshot = _openAiSettingsStore.Load();
        AppPreferencesSnapshot appPreferencesSnapshot = _appPreferencesStore.Load();
        openAiOptions.ApiKey = openAiSnapshot.ApiKey;
        _appThemeService.Apply(appPreferencesSnapshot.ThemePreference);

        _httpClient = new HttpClient {
            // Long transcription and diarization uploads use service-level cancellation tokens.
            // The framework default HttpClient timeout is 100 seconds and cancels valid long requests too early.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _openAiApiKeyValidationService = new OpenAiApiKeyValidationService(_httpClient);
        var processLogService = new ProcessLogService();
        _processLogService = processLogService;
        var responseParser = new OpenAiTranscriptionResponseParser();
        var speakerDiarizationResponseParser = new OpenAiSpeakerDiarizationResponseParser();
        var audioStandardizer = new AudioStandardizer();
        var silenceIntervalDetector = new SilenceIntervalDetector();
        var diarizationChunkPlanner = new SilenceAwareChunkPlanner(
            ChunkedSpeakerDiarizationService.BuildRecommendedChunkPlannerOptions());
        var waveClipExtractor = new WaveClipExtractor();
        var audioPlaybackService = new NaudioAudioPlaybackService();
        var sessionStore = new TranscriptSessionStore(processLogService: processLogService);
        var playbackTranscriptionService = new PlaybackTranscriptionService(
            audioStandardizer,
            _httpClient,
            openAiOptions,
            processLogService,
            responseParser);
        var speakerDiarizationService = new OpenAiSpeakerDiarizationService(
            _httpClient,
            openAiOptions,
            processLogService,
            speakerDiarizationResponseParser);
        var chunkedSpeakerDiarizationService = new ChunkedSpeakerDiarizationService(
            audioStandardizer,
            silenceIntervalDetector,
            diarizationChunkPlanner,
            waveClipExtractor,
            speakerDiarizationService,
            processLogService);
        var playbackEditTranscriptionOptions = new PlaybackTranscriptionSessionOptions(
            MinimumSegmentDuration: TimeSpan.FromSeconds(1.5),
            InterimWindowDuration: TimeSpan.FromSeconds(10),
            InterimCadence: TimeSpan.FromSeconds(10),
            FinalWindowDuration: TimeSpan.FromSeconds(10),
            PollInterval: TimeSpan.FromMilliseconds(100));

        _windowPlacementService = new WindowPlacementService();

        _mainViewModel = new MainViewModel(
            OpenAiTranscriptionModelCatalog.Models,
            audioPlaybackService,
            openAiOptions,
            _openAiSettingsStore,
            _openAiApiKeyValidationService,
            chunkedSpeakerDiarizationService,
            processLogService,
            sessionStore,
            _appPreferencesStore,
            _appThemeService,
            appPreferencesSnapshot);

        var mainWindow = new MainWindow(
            playbackTranscriptionSessionFactory: () => new PlaybackTranscriptionSession(
                new PlaybackAudioCaptureService(audioPlaybackService),
                playbackTranscriptionService,
                processLogService,
                playbackEditTranscriptionOptions),
            processLogService: processLogService) {
            DataContext = _mainViewModel,
        };
        _windowPlacementService.Apply(mainWindow);
        _windowPlacementService.Attach(mainWindow);

        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e) {
        try {
            _mainViewModel?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally {
            _activationListenerCts?.Cancel();
            _httpClient?.Dispose();

            try {
                _activateEvent?.Set();
            }
            catch {
                // Ignore wake failures during shutdown.
            }

            if (_activationListenerTask is not null) {
                try {
                    _activationListenerTask.Wait(TimeSpan.FromMilliseconds(500));
                }
                catch {
                    // Ignore listener shutdown failures.
                }
            }

            _activationListenerTask = null;
            _activationListenerCts?.Dispose();
            _activationListenerCts = null;
            _activateEvent?.Dispose();
            _activateEvent = null;

            if (_singleInstanceMutex is not null) {
                try {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch (ApplicationException) {
                    // Ignore release errors when mutex is not owned.
                }

                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }
        }

        base.OnExit(e);
    }

    private void ListenForActivationRequests(CancellationToken cancellationToken) {
        if (_activateEvent is null) {
            return;
        }

        WaitHandle[] handles = {
            _activateEvent,
            cancellationToken.WaitHandle,
        };

        try {
            while (!cancellationToken.IsCancellationRequested) {
                int signalIndex = WaitHandle.WaitAny(handles);

                if (signalIndex == 1 || cancellationToken.IsCancellationRequested) {
                    break;
                }

                Dispatcher.BeginInvoke(new Action(ActivateMainWindow));
            }
        }
        catch (ObjectDisposedException) {
            // Ignore shutdown races.
        }
    }

    private void ActivateMainWindow() {
        if (MainWindow is null) {
            return;
        }

        if (MainWindow.WindowState == WindowState.Minimized) {
            MainWindow.WindowState = WindowState.Normal;
        }

        if (!MainWindow.IsVisible) {
            MainWindow.Show();
        }

        MainWindow.Activate();
        MainWindow.Topmost = true;
        MainWindow.Topmost = false;
        MainWindow.Focus();
    }

    private static void NotifyRunningInstance() {
        try {
            using EventWaitHandle activateEvent = EventWaitHandle.OpenExisting(ActivateEventName);
            activateEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException) {
            // Existing instance is not yet ready to receive activation signal.
        }
        catch {
            // Ignore activation signal failures for secondary instances.
        }
    }
}
