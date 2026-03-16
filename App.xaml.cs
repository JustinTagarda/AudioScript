using System.Net.Http;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AudioTranscript.Audio;
using AudioTranscript.Services;
using AudioTranscript.ViewModels;
using Velopack;

namespace AudioTranscript;

public partial class App : System.Windows.Application {
    private const string SingleInstanceMutexName = @"Local\AudioTranscript_SingleInstance";
    private const string ActivateEventName = @"Local\AudioTranscript_Activate";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private CancellationTokenSource? _activationListenerCts;
    private Task? _activationListenerTask;
    private ApplicationUpdateService? _applicationUpdateService;
    private ProcessLogService? _processLogService;
    private UpdateProgressWindow? _updateProgressWindow;
    private bool _allowMainWindowClose;
    private int _shutdownUpdateFlowStarted;

    private HttpClient? _httpClient;
    private MainViewModel? _mainViewModel;
    private WindowPlacementService? _windowPlacementService;
    private OpenAiSettingsStore? _openAiSettingsStore;
    private OpenAiApiKeyValidationService? _openAiApiKeyValidationService;
    private AppPreferencesStore? _appPreferencesStore;

    [STAThread]
    private static void Main(string[] args) {
        VelopackApp.Build().Run();

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

        OpenAiSettingsSnapshot openAiSnapshot = _openAiSettingsStore.Load(openAiOptions.ApiKey);
        AppPreferencesSnapshot appPreferencesSnapshot = _appPreferencesStore.Load();
        openAiOptions.ApiKey = openAiSnapshot.ApiKey;

        _httpClient = new HttpClient();
        _openAiApiKeyValidationService = new OpenAiApiKeyValidationService(_httpClient);
        var processLogService = new ProcessLogService();
        _processLogService = processLogService;
        _applicationUpdateService = new ApplicationUpdateService(processLogService);
        var responseParser = new OpenAiTranscriptionResponseParser();
        var audioStandardizer = new AudioStandardizer();
        var audioPlaybackService = new NaudioAudioPlaybackService();
        var sessionStore = new TranscriptSessionStore(processLogService: processLogService);
        var playbackTranscriptionService = new PlaybackAudioTranscriptionService(
            audioStandardizer,
            _httpClient,
            openAiOptions,
            processLogService,
            responseParser);
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
            processLogService,
            sessionStore,
            _appPreferencesStore,
            appPreferencesSnapshot,
            _applicationUpdateService);

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
        mainWindow.Closing += (_, _) => _windowPlacementService.Save(mainWindow);
        mainWindow.Closing += OnMainWindowClosing;

        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e) {
        try {
            _mainViewModel?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally {
            TryApplyPendingUpdates();

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
            _updateProgressWindow = null;
            _applicationUpdateService = null;
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

    private async void OnMainWindowClosing(object? sender, CancelEventArgs e) {
        if (_allowMainWindowClose) {
            return;
        }

        if (sender is not MainWindow mainWindow || _applicationUpdateService is null) {
            return;
        }

        if (Interlocked.Exchange(ref _shutdownUpdateFlowStarted, 1) != 0) {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;

        try {
            ApplicationExitUpdateCheckResult checkResult =
                await _applicationUpdateService.CheckForUpdateOnExitAsync(CancellationToken.None);

            if (!checkResult.ShouldInstallOnExit) {
                _allowMainWindowClose = true;
                mainWindow.Close();
                return;
            }

            UpdateProgressWindow progressWindow = ShowUpdateProgressWindow(mainWindow);

            if (checkResult.Update is not null) {
                progressWindow.ShowDownloading(checkResult.TargetVersion, progressPercent: 0);
            }
            else {
                progressWindow.ShowInstalling(checkResult.TargetVersion);
            }

            await mainWindow.PrepareForRequiredUpdateShutdownAsync();

            if (checkResult.Update is not null) {
                var progress = new Progress<int>(value =>
                    progressWindow.ShowDownloading(checkResult.TargetVersion, value));

                bool isReadyToInstall = await _applicationUpdateService.DownloadUpdateOnExitAsync(
                    checkResult.Update,
                    progress,
                    CancellationToken.None);

                if (!isReadyToInstall) {
                    CloseUpdateProgressWindow();
                    _allowMainWindowClose = true;
                    mainWindow.Close();
                    return;
                }
            }

            progressWindow.ShowInstalling(checkResult.TargetVersion);
            progressWindow.AllowClose();
            _allowMainWindowClose = true;
            mainWindow.Close();
        }
        catch (Exception ex) {
            _processLogService?.Log("AutoUpdate", $"Exit update flow failed unexpectedly: {ex.Message}");
            CloseUpdateProgressWindow();
            _allowMainWindowClose = true;
            mainWindow.Close();
        }
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

    private void TryApplyPendingUpdates() {
        if (_applicationUpdateService is null) {
            return;
        }

        try {
            _applicationUpdateService.ApplyPendingUpdatesOnExitAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) {
            _processLogService?.Log("AutoUpdate", $"Failed to apply a staged update during shutdown: {ex.Message}");
        }
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

    private UpdateProgressWindow ShowUpdateProgressWindow(MainWindow owner) {
        if (_updateProgressWindow is not null) {
            return _updateProgressWindow;
        }

        owner.IsEnabled = false;

        _updateProgressWindow = new UpdateProgressWindow {
            Owner = owner,
        };

        _updateProgressWindow.Closed += (_, _) => {
            _updateProgressWindow = null;

            if (!_allowMainWindowClose && owner.IsLoaded) {
                owner.IsEnabled = true;
            }
        };

        _updateProgressWindow.Show();
        _updateProgressWindow.Activate();
        return _updateProgressWindow;
    }

    private void CloseUpdateProgressWindow() {
        if (_updateProgressWindow is null) {
            if (MainWindow is MainWindow mainWindow && mainWindow.IsLoaded) {
                mainWindow.IsEnabled = true;
            }

            return;
        }

        _updateProgressWindow.AllowClose();
        _updateProgressWindow.Close();
        _updateProgressWindow = null;

        if (MainWindow is MainWindow owner && owner.IsLoaded) {
            owner.IsEnabled = true;
        }
    }
}
