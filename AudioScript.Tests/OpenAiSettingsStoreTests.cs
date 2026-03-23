using System.Collections.Concurrent;
using System.Net;
using System.Text;
using AudioScript.Audio;
using AudioScript.Services;
using AudioScript.ViewModels;
using Xunit;

namespace AudioScript.Tests;

public sealed class OpenAiSettingsStoreTests {
    [Fact]
    public void Clear_RemovesSettingsFile() {
        string rootPath = CreateTempDirectory();
        try {
            string settingsPath = Path.Combine(rootPath, "openai-settings.json");
            var store = new OpenAiSettingsStore(settingsPath);
            store.Save("sk-test-key-1234");

            Assert.True(File.Exists(settingsPath));

            store.Clear();

            Assert.False(File.Exists(settingsPath));
        }
        finally {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void Clear_RemovesOpenAiApiKeyFromProcessEnvironment() {
        const string envName = "OPENAI_API_KEY";
        string? previous = Environment.GetEnvironmentVariable(envName, EnvironmentVariableTarget.Process);
        string rootPath = CreateTempDirectory();
        try {
            Environment.SetEnvironmentVariable(envName, "sk-test-process-env");
            string settingsPath = Path.Combine(rootPath, "openai-settings.json");
            var store = new OpenAiSettingsStore(settingsPath);
            store.Save("sk-test-key-1234");

            store.Clear();

            Assert.True(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envName, EnvironmentVariableTarget.Process)));
        }
        finally {
            Environment.SetEnvironmentVariable(envName, previous, EnvironmentVariableTarget.Process);
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task RemoveOpenAiSettings_ClearsRuntimeAndPersistenceImmediately() {
        await RunInStaAsync(async () => {
            string rootPath = CreateTempDirectory();
            string audioPath = CreateSilentWaveFile(16000);
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try {
                string settingsPath = Path.Combine(rootPath, "openai-settings.json");
                var settingsStore = new OpenAiSettingsStore(settingsPath);
                settingsStore.Save("sk-test-key-1234");

                using var validationHttpClient = new HttpClient(new StubHttpMessageHandler());
                using var diarizationHttpClient = new HttpClient(new StubHttpMessageHandler());
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var options = new OpenAiTranscriptionOptions {
                    ApiKey = "sk-test-key-1234",
                };

                var viewModel = new MainViewModel(
                    OpenAiTranscriptionModelCatalog.Models,
                    playbackService,
                    options,
                    settingsStore,
                    new OpenAiApiKeyValidationService(validationHttpClient),
                    new ChunkedSpeakerDiarizationService(
                        new AudioStandardizer(),
                        new SilenceIntervalDetector(),
                        new SilenceAwareChunkPlanner(),
                        new WaveClipExtractor(),
                        new OpenAiSpeakerDiarizationService(
                            diarizationHttpClient,
                            options,
                            processLogService,
                            new OpenAiSpeakerDiarizationResponseParser()),
                        processLogService),
                    processLogService,
                    new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService),
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: true,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true));

                try {
                    Assert.False(string.IsNullOrWhiteSpace(viewModel.OpenAiApiKey));

                    viewModel.RemoveOpenAiSettings();
                    queuedContext.Drain();

                    Assert.Equal(string.Empty, viewModel.OpenAiApiKey);
                    Assert.Equal(string.Empty, options.ApiKey);
                    Assert.False(File.Exists(settingsPath));
                    Assert.Contains("required", viewModel.AutoTranscribeAssistStatusText, StringComparison.OrdinalIgnoreCase);
                }
                finally {
                    await viewModel.DisposeAsync();
                }
            }
            finally {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
                File.Delete(audioPath);
            }
        });
    }

    private static Task RunInStaAsync(Func<Task> action) {
        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            try {
                action().GetAwaiter().GetResult();
                completionSource.SetResult();
            }
            catch (Exception ex) {
                completionSource.SetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completionSource.Task;
    }

    private static string CreateTempDirectory() {
        string path = Path.Combine(Path.GetTempPath(), $"AudioScript-openai-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateSilentWaveFile(long dataBytes) {
        string path = Path.Combine(Path.GetTempPath(), $"AudioScript-openai-audio-{Guid.NewGuid():N}.wav");
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

    private static void DeleteDirectory(string path) {
        if (!Directory.Exists(path)) {
            return;
        }

        Directory.Delete(path, recursive: true);
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();

        public override void Post(SendOrPostCallback d, object? state) {
            _callbacks.Enqueue((d, state));
        }

        public void Drain() {
            while (_callbacks.TryDequeue(out var callback)) {
                callback.Callback(callback.State);
            }
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class FakeAudioPlaybackService : IAudioPlaybackService {
        private string? _loadedFilePath;
        private bool _isPlaying;
        private bool _isMuted;
        private TimeSpan _position;

        public event EventHandler? PlaybackStateChanged;

        public string? LoadedFilePath => _loadedFilePath;

        public bool IsLoaded => !string.IsNullOrWhiteSpace(_loadedFilePath);

        public bool IsPlaying => _isPlaying;

        public bool IsMuted {
            get => _isMuted;
            set => _isMuted = value;
        }

        public TimeSpan Duration => IsLoaded ? TimeSpan.FromSeconds(10) : TimeSpan.Zero;

        public TimeSpan Position => _position;

        public void LoadFile(string filePath) {
            _loadedFilePath = Path.GetFullPath(filePath);
            _position = TimeSpan.Zero;
            _isPlaying = false;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void UnloadFile() {
            _loadedFilePath = null;
            _position = TimeSpan.Zero;
            _isPlaying = false;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Play() {
            if (!IsLoaded) {
                throw new InvalidOperationException("No audio file is loaded.");
            }

            _isPlaying = true;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Pause() {
            _isPlaying = false;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Stop() {
            _isPlaying = false;
            _position = TimeSpan.Zero;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Seek(TimeSpan position) {
            _position = position < TimeSpan.Zero
                ? TimeSpan.Zero
                : position > Duration
                    ? Duration
                    : position;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose() {
            UnloadFile();
        }
    }
}

