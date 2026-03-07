using AudioTranscript.Abstractions;
using AudioTranscript.Audio;
using AudioTranscript.Services;
using NAudio.Wave;
using Xunit;

namespace AudioTranscript.Tests;

public sealed class PlaybackTranscriptionSessionTests {
    [Fact]
    public async Task Session_EmitsInterimUpdate_AndFlushesFinalTextOnStop() {
        var captureService = new FakeLoopbackCaptureService();
        var transcriptionService = new FakePlaybackAudioTranscriptionService();
        var processLogService = new ProcessLogService();
        var session = new PlaybackTranscriptionSession(
            captureService,
            transcriptionService,
            processLogService,
            new PlaybackTranscriptionSessionOptions(
                MinimumSegmentDuration: TimeSpan.FromMilliseconds(100),
                InterimWindowDuration: TimeSpan.FromMilliseconds(200),
                InterimCadence: TimeSpan.FromMilliseconds(100),
                FinalWindowDuration: TimeSpan.FromMilliseconds(400),
                PollInterval: TimeSpan.FromMilliseconds(20)));

        try {
            var interimTcs = new TaskCompletionSource<PlaybackTranscriptionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finalTcs = new TaskCompletionSource<PlaybackTranscriptionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);

            session.PlaybackInterimTranscriptionUpdated += (_, update) => interimTcs.TrySetResult(update);
            session.PlaybackFinalTranscriptionAvailable += (_, update) => finalTcs.TrySetResult(update);

            session.StartPlaybackTranscription(OpenAiTranscriptionModelCatalog.Gpt4oMiniTranscribe);

            captureService.EmitFrame(CreatePcmChunk(100), AudioFormatConstants.EngineWaveFormat);
            captureService.EmitFrame(CreatePcmChunk(100), AudioFormatConstants.EngineWaveFormat);

            PlaybackTranscriptionUpdate interim = await interimTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(session.SessionId, interim.SessionId);
            Assert.Equal("bytes:6400", interim.Text);
            Assert.Equal(0, interim.SequenceIndex);

            await session.StopPlaybackTranscriptionAsync();

            PlaybackTranscriptionUpdate final = await finalTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(session.SessionId, final.SessionId);
            Assert.Equal("bytes:6400", final.Text);
            Assert.Equal(0, final.SequenceIndex);
            Assert.True(captureService.StopCount >= 1);
        }
        finally {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task Session_FinalizesCompletedWindows_AndFlushesRemainingAudio() {
        var captureService = new FakeLoopbackCaptureService();
        var transcriptionService = new FakePlaybackAudioTranscriptionService();
        var session = new PlaybackTranscriptionSession(
            captureService,
            transcriptionService,
            new ProcessLogService(),
            new PlaybackTranscriptionSessionOptions(
                MinimumSegmentDuration: TimeSpan.FromMilliseconds(100),
                InterimWindowDuration: TimeSpan.FromMilliseconds(200),
                InterimCadence: TimeSpan.FromMilliseconds(100),
                FinalWindowDuration: TimeSpan.FromMilliseconds(400),
                PollInterval: TimeSpan.FromMilliseconds(20)));

        var finals = new List<PlaybackTranscriptionUpdate>();

        try {
            session.PlaybackFinalTranscriptionAvailable += (_, update) => finals.Add(update);
            session.StartPlaybackTranscription(OpenAiTranscriptionModelCatalog.Gpt4oTranscribe);

            for (int index = 0; index < 5; index++) {
                captureService.EmitFrame(CreatePcmChunk(100), AudioFormatConstants.EngineWaveFormat);
            }

            await AssertEventuallyAsync(
                () => finals.Count >= 1,
                TimeSpan.FromSeconds(2));

            await session.StopPlaybackTranscriptionAsync();

            await AssertEventuallyAsync(
                () => finals.Count >= 2,
                TimeSpan.FromSeconds(2));

            Assert.Equal("bytes:12800", finals[0].Text);
            Assert.Equal(0, finals[0].SequenceIndex);
            Assert.Equal("bytes:3200", finals[1].Text);
            Assert.Equal(1, finals[1].SequenceIndex);
        }
        finally {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public void Session_StartsOnlyOnce() {
        var session = new PlaybackTranscriptionSession(
            new FakeLoopbackCaptureService(),
            new FakePlaybackAudioTranscriptionService(),
            new ProcessLogService());

        session.StartPlaybackTranscription(OpenAiTranscriptionModelCatalog.Gpt4oMiniTranscribe);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            session.StartPlaybackTranscription(OpenAiTranscriptionModelCatalog.Gpt4oMiniTranscribe));

        Assert.Contains("single-use", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertEventuallyAsync(Func<bool> predicate, TimeSpan timeout) {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline) {
            if (predicate()) {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(predicate(), "Condition was not satisfied within the timeout.");
    }

    private static byte[] CreatePcmChunk(int durationMilliseconds) {
        int bytesPerSecond = AudioFormatConstants.EngineWaveFormat.AverageBytesPerSecond;
        int byteCount = (int)(bytesPerSecond * (durationMilliseconds / 1000d));
        byteCount -= byteCount % AudioFormatConstants.EngineWaveFormat.BlockAlign;
        return new byte[Math.Max(byteCount, AudioFormatConstants.EngineWaveFormat.BlockAlign)];
    }

    private sealed class FakeLoopbackCaptureService : IAudioLoopbackCaptureService {
        public event EventHandler<LoopbackAudioFrameEventArgs>? AudioFrameCaptured;
        public event EventHandler<Exception>? CaptureFaulted;

        public bool IsCapturing { get; private set; }

        public WaveFormat? CaptureFormat { get; private set; }

        public int StopCount { get; private set; }

        public void StartCapture() {
            IsCapturing = true;
        }

        public void StopCapture() {
            StopCount++;
            IsCapturing = false;
        }

        public void Dispose() {
            StopCapture();
        }

        public void EmitFrame(byte[] buffer, WaveFormat waveFormat) {
            CaptureFormat = waveFormat;
            AudioFrameCaptured?.Invoke(this, new LoopbackAudioFrameEventArgs(buffer, waveFormat));
        }

        public void EmitFault(Exception ex) {
            CaptureFaulted?.Invoke(this, ex);
        }
    }

    private sealed class FakePlaybackAudioTranscriptionService : IPlaybackAudioTranscriptionService {
        public List<int> SeenByteCounts { get; } = new();

        public Task<string> TranscribePcmChunkAsync(
            byte[] pcmAudio,
            WaveFormat sourceFormat,
            string model,
            CancellationToken cancellationToken) {
            SeenByteCounts.Add(pcmAudio.Length);
            return Task.FromResult($"bytes:{pcmAudio.Length}");
        }
    }
}
