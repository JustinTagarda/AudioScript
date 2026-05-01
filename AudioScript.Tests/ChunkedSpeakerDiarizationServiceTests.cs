using System.Text;
using AudioScript.Abstractions;
using AudioScript.Audio;
using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class ChunkedSpeakerDiarizationServiceTests {
    [Fact]
    public async Task DiarizeAudioFileAsync_AppliesSpeakerLabelsToExistingTranscription() {
        string audioPath = CreateSilentWaveFile(TimeSpan.FromSeconds(1));
        TranscriptionResult transcriptionResult = CreateTranscriptionResult([
            new TranscriptionTimedLine("single", TimeSpan.Zero, TimeSpan.FromSeconds(1), false),
        ]);

        try {
            ChunkedSpeakerDiarizationService service = CreateService();

            SpeakerDiarizationResult result = await service.DiarizeAudioFileAsync(
                audioPath,
                transcriptionResult,
                CancellationToken.None);

            Assert.Equal("single", result.Segments.Single().Text);
            Assert.Equal("speaker_1", result.Segments.Single().Speaker);
        }
        finally {
            File.Delete(audioPath);
        }
    }

    [Fact]
    public async Task DiarizeAudioFileAsync_ReportsDiarizationProgress() {
        string audioPath = CreateSilentWaveFile(TimeSpan.FromSeconds(8));
        TranscriptionResult transcriptionResult = CreateTranscriptionResult([
            new TranscriptionTimedLine("first", TimeSpan.Zero, TimeSpan.FromSeconds(1), false),
            new TranscriptionTimedLine("second", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), false),
        ]);
        var snapshots = new List<TranscriptionProgressSnapshot>();

        try {
            ChunkedSpeakerDiarizationService service = CreateService(forceChunking: true);

            SpeakerDiarizationResult result = await service.DiarizeAudioFileAsync(
                audioPath,
                transcriptionResult,
                CancellationToken.None,
                new Progress<TranscriptionProgressSnapshot>(snapshots.Add));

            Assert.Equal(2, result.Segments.Count);
            Assert.Contains(snapshots, snapshot => snapshot.Phase == TranscriptionProgressPhase.RunningSpeakerDiarization);
            Assert.Contains(snapshots, snapshot => snapshot.Phase == TranscriptionProgressPhase.MergingSpeakerLabels);
        }
        finally {
            File.Delete(audioPath);
        }
    }

    private static ChunkedSpeakerDiarizationService CreateService(
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
        var offlineService = new OfflineSpeakerDiarizationService(
            new CoveringSpeakerDiarizationEngine(),
            processLogService);

        return new ChunkedSpeakerDiarizationService(
            chunkingService,
            offlineService,
            processLogService);
    }

    private static TranscriptionResult CreateTranscriptionResult(IReadOnlyList<TranscriptionTimedLine> timedLines) {
        return new TranscriptionResult(
            Text: string.Join(Environment.NewLine, timedLines.Select(line => line.Text)),
            Model: TranscriptionModelCatalog.WhisperSmall,
            CreatedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(10),
            TokenLogprobs: Array.Empty<TranscriptionTokenLogprob>(),
            LowConfidenceTokens: Array.Empty<LowConfidenceToken>(),
            TimedLines: timedLines);
    }

    private static string CreateSilentWaveFile(TimeSpan duration) {
        string path = Path.Combine(Path.GetTempPath(), $"AudioScript-chunked-speaker-{Guid.NewGuid():N}.wav");
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

    private sealed class CoveringSpeakerDiarizationEngine : ISpeakerDiarizationEngine {
        public Task<IReadOnlyList<SpeakerDiarizationTurn>> DiarizeAudioFileAsync(
            string audioFilePath,
            CancellationToken cancellationToken,
            IProgress<SpeakerDiarizationProgress>? progress = null) {
            progress?.Report(new SpeakerDiarizationProgress(1, 2));
            progress?.Report(new SpeakerDiarizationProgress(2, 2));
            IReadOnlyList<SpeakerDiarizationTurn> turns = [
                new SpeakerDiarizationTurn("speaker_1", TimeSpan.Zero, TimeSpan.FromHours(1)),
            ];
            return Task.FromResult(turns);
        }
    }
}
