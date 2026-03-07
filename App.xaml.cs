using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AudioTranscript.Audio;
using AudioTranscript.Services;
using AudioTranscript.ViewModels;

namespace AudioTranscript;

public partial class App : System.Windows.Application {
    private const string SingleInstanceMutexName = @"Local\AudioTranscript_SingleInstance";
    private const string ActivateEventName = @"Local\AudioTranscript_Activate";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private CancellationTokenSource? _activationListenerCts;
    private Task? _activationListenerTask;

    private HttpClient? _httpClient;
    private MainViewModel? _mainViewModel;
    private WindowPlacementService? _windowPlacementService;
    private OpenAiSettingsStore? _openAiSettingsStore;
    private OpenAiApiKeyValidationService? _openAiApiKeyValidationService;
    private AppPreferencesStore? _appPreferencesStore;

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

        OpenAiSettingsSnapshot openAiSnapshot = _openAiSettingsStore.Load(openAiOptions.ApiKey);
        AppPreferencesSnapshot appPreferencesSnapshot = _appPreferencesStore.Load();
        openAiOptions.ApiKey = openAiSnapshot.ApiKey;

        _httpClient = new HttpClient();
        _openAiApiKeyValidationService = new OpenAiApiKeyValidationService(_httpClient);
        var processLogService = new ProcessLogService();
        var responseParser = new OpenAiTranscriptionResponseParser();
        var audioStandardizer = new AudioStandardizer();
        var audioChunkPlanner = new AudioChunkPlanner();
        var segmentMerger = new TranscriptionSegmentMerger();
        var audioPlaybackService = new NaudioAudioPlaybackService();
        var sessionStore = new TranscriptSessionStore(processLogService: processLogService);
        var transcriptionService = new OpenAiAudioTranscriptionService(
            audioStandardizer,
            audioChunkPlanner,
            segmentMerger,
            _httpClient,
            openAiOptions,
            processLogService,
            responseParser);

        _windowPlacementService = new WindowPlacementService();

        _mainViewModel = new MainViewModel(
            OpenAiTranscriptionModelCatalog.Models,
            transcriptionService,
            audioPlaybackService,
            openAiOptions,
            _openAiSettingsStore,
            _openAiApiKeyValidationService,
            processLogService,
            sessionStore,
            _appPreferencesStore,
            appPreferencesSnapshot);

        var mainWindow = new MainWindow {
            DataContext = _mainViewModel,
        };
        _windowPlacementService.Apply(mainWindow);
        mainWindow.Closing += (_, _) => _windowPlacementService.Save(mainWindow);

        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e) {
        try {
            _mainViewModel?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally {
            _httpClient?.Dispose();

            _activationListenerCts?.Cancel();

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
