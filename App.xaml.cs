using System.Net.Http;
using System.Windows;
using AudioTranscript.Abstractions;
using AudioTranscript.Audio;
using AudioTranscript.Engines;
using AudioTranscript.Services;
using AudioTranscript.ViewModels;

namespace AudioTranscript;

public partial class App : Application {
    private HttpClient? _httpClient;
    private MainViewModel? _mainViewModel;

    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);

        var whisperOptions = new WhisperCppOptions();
        var openAiOptions = new OpenAiOptions();

        _httpClient = new HttpClient();

        var audioStandardizer = new AudioStandardizer();
        var captureService = new WasapiAudioCaptureService(audioStandardizer);
        var liveCoordinator = new LiveTranscriptionCoordinator(captureService);
        var whisperProvisioningService = new WhisperProvisioningService(_httpClient, whisperOptions);

        var engines = new List<ITranscriptionEngine> {
            new WhisperCppTranscriptionEngine(audioStandardizer, whisperOptions),
            new OpenAiGpt4oMiniTranscriptionEngine(audioStandardizer, openAiOptions, _httpClient),
        };

        var engineRegistry = new TranscriptionEngineRegistry(engines);
        _mainViewModel = new MainViewModel(
            engineRegistry,
            liveCoordinator,
            openAiOptions,
            whisperProvisioningService);

        var mainWindow = new MainWindow {
            DataContext = _mainViewModel,
        };

        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e) {
        try {
            _mainViewModel?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally {
            _httpClient?.Dispose();
        }

        base.OnExit(e);
    }
}
