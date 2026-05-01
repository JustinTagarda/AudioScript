using System.Text;
using AudioScript.Abstractions;
using AudioScript.Audio;
using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class ChunkedAudioTranscriptionServiceTests {
    [Fact]
    public async Task TranscribeAudioFileAsync_UsesSingleRequest_WhenBelowThresholds() {
        string audioPath = CreateSilentWaveFile(TimeSpan.FromSeconds(1));
        var requestService = new SequencedAudioTranscriptionService([
            [new TranscriptionTimedLine("single", TimeSpan.Zero, TimeSpan.FromSeconds(1), false)],
        ]);

        try {
            ChunkedAudioTranscriptionService service = CreateService(requestService);

            var result = await service.TranscribeAudioFileAsync(
                audioPath,
                TranscriptionModelCatalog.WhisperSmall,
                CancellationToken.None);

            Assert.Equal(1, requestService.RequestCount);
            Assert.Equal("single", result.Text);
            Assert.Single(result.TimedLines!);
        }
        finally {
            File.Delete(audioPath);
        }
    }

    [Fact]
    public async Task TranscribeAudioFileAsync_ChunksAndMergesTimedLines_WhenDurationExceedsThreshold() {
        string audioPath = CreateSilentWaveFile(TimeSpan.FromSeconds(8));
        var requestService = new SequencedAudioTranscriptionService([
            [
                new TranscriptionTimedLine("first", TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(0.8), false),
                new TranscriptionTimedLine("skipped", TimeSpan.FromSeconds(3.0), TimeSpan.FromSeconds(3.4), false),
            ],
            [new TranscriptionTimedLine("second", TimeSpan.FromSeconds(0.7), TimeSpan.FromSeconds(1.2), false)],
            [new TranscriptionTimedLine("third", TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(1.5), false)],
            [new TranscriptionTimedLine("fourth", TimeSpan.FromSeconds(0.7), TimeSpan.FromSeconds(1.2), false)],
        ]);

        try {
            ChunkedAudioTranscriptionService service = CreateService(requestService, forceChunking: true);

            var result = await service.TranscribeAudioFileAsync(
                audioPath,
                TranscriptionModelCatalog.WhisperSmall,
                CancellationToken.None);

            Assert.Equal(4, requestService.RequestCount);
            Assert.Equal(new[] { "first", "second", "third", "fourth" }, result.TimedLines!.Select(line => line.Text).ToArray());
            Assert.Equal(TimeSpan.FromSeconds(0.2), result.TimedLines![0].StartOffset);
            Assert.Equal(TimeSpan.FromSeconds(3.2), result.TimedLines[1].StartOffset);
            Assert.Equal(TimeSpan.FromSeconds(4.5), result.TimedLines[2].StartOffset);
            Assert.Equal(TimeSpan.FromSeconds(7.2), result.TimedLines[3].StartOffset);
            Assert.DoesNotContain(result.TimedLines, line => line.Text == "skipped");
        }
        finally {
            File.Delete(audioPath);
        }
    }

    [Fact]
    public async Task TranscribeAudioFileAsync_ReportsChunkProgressInMonotonicOrder() {
        string audioPath = CreateSilentWaveFile(TimeSpan.FromSeconds(8));
        var requestService = new SequencedAudioTranscriptionService([
            [new TranscriptionTimedLine("first", TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(0.8), false)],
            [new TranscriptionTimedLine("second", TimeSpan.FromSeconds(0.7), TimeSpan.FromSeconds(1.2), false)],
            [new TranscriptionTimedLine("third", TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(1.5), false)],
            [new TranscriptionTimedLine("fourth", TimeSpan.FromSeconds(0.7), TimeSpan.FromSeconds(1.2), false)],
        ]);
        var progressSnapshots = new List<TranscriptionProgressSnapshot>();

        try {
            ChunkedAudioTranscriptionService service = CreateService(requestService, forceChunking: true);

            await service.TranscribeAudioFileAsync(
                audioPath,
                TranscriptionModelCatalog.WhisperSmall,
                CancellationToken.None,
                new ImmediateProgress<TranscriptionProgressSnapshot>(progressSnapshots.Add));

            Assert.Contains(progressSnapshots, snapshot => snapshot.Phase == TranscriptionProgressPhase.Chunking);
            Assert.Contains(progressSnapshots, snapshot => snapshot.Phase == TranscriptionProgressPhase.TranscribingChunk);
            Assert.Contains(progressSnapshots, snapshot => snapshot.Phase == TranscriptionProgressPhase.MergingResults);
            Assert.Equal(TranscriptionProgressPhase.Completed, progressSnapshots[^1].Phase);
            Assert.Equal(100, progressSnapshots[^1].Percent);

            double previousPercent = -1;
            foreach (TranscriptionProgressSnapshot snapshot in progressSnapshots) {
                Assert.True(snapshot.Percent >= previousPercent);
                previousPercent = snapshot.Percent;
            }
        }
        finally {
            File.Delete(audioPath);
        }
    }

    private static ChunkedAudioTranscriptionService CreateService(
        IAudioTranscriptionService requestService,
        bool forceChunking = false) {
        var processLogService = new ProcessLogService();
        var waveClipExtractor = new WaveClipExtractor();
        var chunkPlanner = forceChunking
            ? new SilenceAwareChunkPlanner(new SilenceAwareChunkPlannerOptions(
                TargetChunkDuration: TimeSpan.FromSeconds(2),
                MinimumChunkDuration: TimeSpan.FromSeconds(1),
                MaximumChunkDuration: TimeSpan.FromSeconds(3),
                OverlapDuration: TimeSpan.FromSeconds(0.5),
                SearchBeforePreferredSplit: TimeSpan.FromSeconds(0.25),
                SearchAfterPreferredSplit: TimeSpan.FromSeconds(0.25),
                MinimumSilenceDuration: TimeSpan.FromMilliseconds(200)))
            : new SilenceAwareChunkPlanner();
        var chunkingOptions = forceChunking
            ? new AudioChunkingOptions(
                DirectUploadLimitBytes: long.MaxValue,
                ChunkUploadSafetyBytes: 24_000_000,
                DirectRequestMaxDuration: TimeSpan.FromSeconds(3))
            : AudioChunkingOptions.Default;
        var chunkingService = new AudioChunkingService(
            new AudioStandardizer(),
            new SilenceIntervalDetector(),
            chunkPlanner,
            waveClipExtractor,
            chunkingOptions);

        return new ChunkedAudioTranscriptionService(
            chunkingService,
            requestService,
            processLogService);
    }

    private static string CreateSilentWaveFile(TimeSpan duration) {
        string path = Path.Combine(Path.GetTempPath(), $"AudioScript-chunked-transcribe-{Guid.NewGuid():N}.wav");
        int sampleRate = 16000;
        short channels = 1;
        short bitsPerSample = 16;
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        int byteRate = sampleRate * blockAlign;
        long dataBytes = (long)Math.Ceiling(duration.TotalSeconds * byteRate);

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

    private sealed class SequencedAudioTranscriptionService : IAudioTranscriptionService {
        private readonly IReadOnlyList<IReadOnlyList<TranscriptionTimedLine>> _responses;

        public SequencedAudioTranscriptionService(IReadOnlyList<IReadOnlyList<TranscriptionTimedLine>> responses) {
            _responses = responses;
        }

        public int RequestCount { get; private set; }

        public Task<TranscriptionResult> TranscribeAudioFileAsync(
            string audioFilePath,
            string model,
            CancellationToken cancellationToken,
            IProgress<TranscriptionProgressSnapshot>? progress = null) {
            int index = RequestCount;
            RequestCount++;
            IReadOnlyList<TranscriptionTimedLine> timedLines = _responses[Math.Min(index, _responses.Count - 1)];

            return Task.FromResult(new TranscriptionResult(
                Text: string.Join(Environment.NewLine, timedLines.Select(line => line.Text)),
                Model: model,
                CreatedAt: DateTimeOffset.UtcNow,
                Duration: TimeSpan.Zero,
                TokenLogprobs: Array.Empty<TranscriptionTokenLogprob>(),
                LowConfidenceTokens: Array.Empty<LowConfidenceToken>(),
                TimedLines: timedLines));
        }
    }

    private sealed class ImmediateProgress<T> : IProgress<T> {
        private readonly Action<T> _handler;

        public ImmediateProgress(Action<T> handler) {
            _handler = handler;
        }

        public void Report(T value) {
            _handler(value);
        }
    }
}
