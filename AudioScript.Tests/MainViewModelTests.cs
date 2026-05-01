using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Text;
using AudioScript.Abstractions;
using AudioScript.Audio;
using AudioScript.Services;
using AudioScript.ViewModels;
using Xunit;

namespace AudioScript.Tests;

public sealed class MainViewModelTests
{
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
                        SelectedTranscriptMode: TranscriptGenerationMode.TranscribeAudio.ToString(),
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
                        SelectedTranscriptMode: TranscriptGenerationMode.TranscribeAudio.ToString(),
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    bool imported = viewModel.TryImportAudioFileFromPath(audioPath);
                    Assert.True(imported);

                    TranscriptModeOptionViewModel transcribeMode = viewModel.TranscriptModes
                        .Single(mode => mode.Mode == TranscriptGenerationMode.TranscribeAudio);
                    viewModel.SelectedTranscriptMode = transcribeMode;

                    bool created = await viewModel.GenerateTranscribeAudioTranscriptAsync(CancellationToken.None);

                    Assert.True(created);
                    Assert.Same(transcribeMode, viewModel.SelectedTranscriptMode);
                    Assert.True(viewModel.IsTranscribeAudioModeSelected);
                    Assert.Equal(2, viewModel.FinalizedTranscriptLines.Count);
                    Assert.Equal("hello", viewModel.FinalizedTranscriptLines[0].Text);
                    Assert.Equal("Speaker 1", viewModel.FinalizedTranscriptLines[0].SpeakerLabel);
                    Assert.Equal("Speaker 2", viewModel.FinalizedTranscriptLines[1].SpeakerLabel);
                    Assert.Equal(TimeSpan.Zero, viewModel.FinalizedTranscriptLines[0].StartOffset);
                    Assert.Equal(TimeSpan.FromSeconds(2), viewModel.FinalizedTranscriptLines[1].StartOffset);
                    Assert.Contains("Speaker 1: hello", viewModel.BuildClipboardTranscriptText());
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
                        SelectedTranscriptMode: TranscriptGenerationMode.TranscribeAudio.ToString(),
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
    public async Task TranscriptModes_DoesNotExposeSeparateSpeakerDiarizationMode()
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
                    new TranscriptionTimedLine("hello.", TimeSpan.Zero, TimeSpan.FromSeconds(1), false),
                    new TranscriptionTimedLine("reply", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), false),
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
                        SelectedTranscriptMode: "SpeakerDiarization",
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    Assert.DoesNotContain(viewModel.TranscriptModes, mode =>
                        string.Equals(mode.DisplayName, "Speaker diarization", StringComparison.OrdinalIgnoreCase));
                    Assert.True(viewModel.IsTranscribeAudioModeSelected);
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
        ProcessLogService processLogService)
    {
        var waveClipExtractor = new WaveClipExtractor();
        var audioChunkingService = new AudioChunkingService(
            new AudioStandardizer(),
            new SilenceIntervalDetector(),
            new SilenceAwareChunkPlanner(),
            waveClipExtractor);
        var offlineDiarizationService = new OfflineSpeakerDiarizationService(
            new TestSpeakerDiarizationEngine(),
            processLogService);

        return new ChunkedSpeakerDiarizationService(
            audioChunkingService,
            offlineDiarizationService,
            processLogService);
    }

    private sealed class StubAudioTranscriptionService : IAudioTranscriptionService
    {
        private readonly IReadOnlyList<TranscriptionTimedLine> _timedLines;

        public StubAudioTranscriptionService(IReadOnlyList<TranscriptionTimedLine> timedLines)
        {
            _timedLines = timedLines;
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
        public Task<IReadOnlyList<SpeakerDiarizationTurn>> DiarizeAudioFileAsync(
            string audioFilePath,
            CancellationToken cancellationToken,
            IProgress<SpeakerDiarizationProgress>? progress = null)
        {
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

