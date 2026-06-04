using AudioScript.Abstractions;
using AudioScript.Audio;
using AudioScript.Services;
using NAudio.Wave;
using Xunit;

namespace AudioScript.Tests;

public sealed class LiveSegmentTranscriptionSessionTests
{
    [Fact]
    public async Task Session_TranscribesFinalizedRecordingSegments_InTimelineOrder()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), $"AudioScript-live-segment-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            string manifestPath = Path.Combine(rootPath, "audio", "live", "manifest.json");
            await using var recordingSession = new LiveRecordingSession(
                manifestPath,
                "audio/live/manifest.json",
                "Test Source",
                new ProcessLogService(),
                TimeSpan.FromMilliseconds(100));
            var transcriptionService = new FakeAudioTranscriptionService();
            await using var transcriptionSession = new LiveSegmentTranscriptionSession(
                recordingSession,
                transcriptionService,
                new ProcessLogService());

            var completed = new List<LiveSegmentTranscriptionCompletedEventArgs>();
            transcriptionSession.SegmentTranscriptionCompleted += (_, e) => completed.Add(e);

            transcriptionSession.Start(TranscriptionModelCatalog.WhisperSmall);
            recordingSession.Start();
            recordingSession.WriteFrame(new LoopbackAudioFrameEventArgs(
                CreatePcmChunk(TimeSpan.FromMilliseconds(100)),
                StandardizingAudioCaptureService.StandardFormat));
            recordingSession.WriteFrame(new LoopbackAudioFrameEventArgs(
                CreatePcmChunk(TimeSpan.FromMilliseconds(100)),
                StandardizingAudioCaptureService.StandardFormat));

            await recordingSession.CompleteAsync();
            await transcriptionSession.StopAsync();

            Assert.Equal(2, completed.Count);
            Assert.Equal(2, transcriptionService.RequestedPaths.Count);
            Assert.All(transcriptionService.RequestedPaths, path => Assert.EndsWith(".wav", path, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(TimeSpan.Zero, completed[0].Result.TimedLines![0].StartOffset);
            Assert.Equal(TimeSpan.FromMilliseconds(100), completed[1].Result.TimedLines![0].StartOffset);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Session_UsesNextSegmentLookahead_AndCommitsOnlyCurrentSegmentRows()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), $"AudioScript-live-segment-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            string manifestPath = Path.Combine(rootPath, "audio", "live", "manifest.json");
            await using var recordingSession = new LiveRecordingSession(
                manifestPath,
                "audio/live/manifest.json",
                "Test Source",
                new ProcessLogService(),
                TimeSpan.FromMilliseconds(100));
            var transcriptionService = new BoundaryAwareFakeAudioTranscriptionService();
            await using var transcriptionSession = new LiveSegmentTranscriptionSession(
                recordingSession,
                transcriptionService,
                new ProcessLogService(),
                TimeSpan.FromMilliseconds(50));

            var completed = new List<LiveSegmentTranscriptionCompletedEventArgs>();
            transcriptionSession.SegmentTranscriptionCompleted += (_, e) => completed.Add(e);

            transcriptionSession.Start(TranscriptionModelCatalog.WhisperSmall);
            recordingSession.Start();
            recordingSession.WriteFrame(new LoopbackAudioFrameEventArgs(
                CreatePcmChunk(TimeSpan.FromMilliseconds(100)),
                StandardizingAudioCaptureService.StandardFormat));
            recordingSession.WriteFrame(new LoopbackAudioFrameEventArgs(
                CreatePcmChunk(TimeSpan.FromMilliseconds(100)),
                StandardizingAudioCaptureService.StandardFormat));

            await recordingSession.CompleteAsync();
            await transcriptionSession.StopAsync();

            Assert.Equal(2, completed.Count);
            Assert.Equal(2, transcriptionService.RequestDurations.Count);
            Assert.Contains(
                transcriptionService.RequestDurations,
                duration => duration >= TimeSpan.FromMilliseconds(149) && duration <= TimeSpan.FromMilliseconds(151));
            Assert.Contains(
                transcriptionService.RequestDurations,
                duration => duration >= TimeSpan.FromMilliseconds(99) && duration <= TimeSpan.FromMilliseconds(101));

            TranscriptionTimedLine[] firstSegmentLines = completed[0].Result.TimedLines!.ToArray();
            Assert.Equal(new[] { "Current segment", "Boundary phrase" }, firstSegmentLines.Select(line => line.Text).ToArray());
            Assert.Equal(TimeSpan.FromMilliseconds(80), firstSegmentLines[1].StartOffset);
            Assert.Equal(TimeSpan.FromMilliseconds(100), firstSegmentLines[1].EndOffset);
            Assert.DoesNotContain(firstSegmentLines, line => line.Text == "Lookahead only");

            TranscriptionTimedLine secondSegmentLine = Assert.Single(completed[1].Result.TimedLines!);
            Assert.Equal("Final segment", secondSegmentLine.Text);
            Assert.Equal(TimeSpan.FromMilliseconds(100), secondSegmentLine.StartOffset);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Session_SuppressesPrompt_ForLiveSegmentTranscription()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), $"AudioScript-live-segment-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            string manifestPath = Path.Combine(rootPath, "audio", "live", "manifest.json");
            await using var recordingSession = new LiveRecordingSession(
                manifestPath,
                "audio/live/manifest.json",
                "Test Source",
                new ProcessLogService(),
                TimeSpan.FromMilliseconds(100));
            var transcriptionService = new ConfigurableFakeAudioTranscriptionService();
            await using var transcriptionSession = new LiveSegmentTranscriptionSession(
                recordingSession,
                transcriptionService,
                new ProcessLogService());

            transcriptionSession.Start(TranscriptionModelCatalog.WhisperSmall);
            recordingSession.Start();
            recordingSession.WriteFrame(new LoopbackAudioFrameEventArgs(
                CreatePcmChunk(TimeSpan.FromMilliseconds(100)),
                StandardizingAudioCaptureService.StandardFormat));

            await recordingSession.CompleteAsync();
            await transcriptionSession.StopAsync();

            AudioTranscriptionRequestOptions options = Assert.Single(transcriptionService.Options);
            Assert.True(options.SuppressPrompt);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    private static byte[] CreatePcmChunk(TimeSpan duration)
    {
        int byteCount = (int)(StandardizingAudioCaptureService.StandardFormat.AverageBytesPerSecond * duration.TotalSeconds);
        byteCount -= byteCount % StandardizingAudioCaptureService.StandardFormat.BlockAlign;
        return new byte[Math.Max(byteCount, StandardizingAudioCaptureService.StandardFormat.BlockAlign)];
    }

    private sealed class FakeAudioTranscriptionService : IAudioTranscriptionService
    {
        public List<string> RequestedPaths { get; } = new();

        public Task<TranscriptionResult> TranscribeAudioFileAsync(
            string audioFilePath,
            string model,
            CancellationToken cancellationToken,
            IProgress<TranscriptionProgressSnapshot>? progress = null,
            string? diagnosticRoute = null)
        {
            RequestedPaths.Add(audioFilePath);
            return Task.FromResult(new TranscriptionResult(
                Text: $"Segment {RequestedPaths.Count}",
                Model: model,
                CreatedAt: DateTimeOffset.UtcNow,
                Duration: TimeSpan.FromMilliseconds(100),
                TokenLogprobs: Array.Empty<TranscriptionTokenLogprob>(),
                LowConfidenceTokens: Array.Empty<LowConfidenceToken>(),
                TimedLines: new[]
                {
                    new TranscriptionTimedLine(
                        $"Segment {RequestedPaths.Count}",
                        TimeSpan.Zero,
                        TimeSpan.FromMilliseconds(50),
                        false),
                }));
        }
    }

    private sealed class BoundaryAwareFakeAudioTranscriptionService : IAudioTranscriptionService
    {
        public List<TimeSpan> RequestDurations { get; } = new();

        public Task<TranscriptionResult> TranscribeAudioFileAsync(
            string audioFilePath,
            string model,
            CancellationToken cancellationToken,
            IProgress<TranscriptionProgressSnapshot>? progress = null,
            string? diagnosticRoute = null)
        {
            using var reader = new WaveFileReader(audioFilePath);
            TimeSpan duration = reader.TotalTime;
            RequestDurations.Add(duration);
            TranscriptionTimedLine[] lines = duration > TimeSpan.FromMilliseconds(120)
                ? new[]
                {
                    new TranscriptionTimedLine(
                        "Current segment",
                        TimeSpan.Zero,
                        TimeSpan.FromMilliseconds(40),
                        false),
                    new TranscriptionTimedLine(
                        "Boundary phrase",
                        TimeSpan.FromMilliseconds(80),
                        TimeSpan.FromMilliseconds(130),
                        false),
                    new TranscriptionTimedLine(
                        "Lookahead only",
                        TimeSpan.FromMilliseconds(125),
                        TimeSpan.FromMilliseconds(145),
                        false),
                }
                : new[]
                {
                    new TranscriptionTimedLine(
                        "Final segment",
                        TimeSpan.Zero,
                        TimeSpan.FromMilliseconds(40),
                        false),
                };

            return Task.FromResult(new TranscriptionResult(
                Text: string.Join(" ", lines.Select(line => line.Text)),
                Model: model,
                CreatedAt: DateTimeOffset.UtcNow,
                Duration: duration,
                TokenLogprobs: Array.Empty<TranscriptionTokenLogprob>(),
                LowConfidenceTokens: Array.Empty<LowConfidenceToken>(),
                TimedLines: lines));
        }
    }

    private sealed class ConfigurableFakeAudioTranscriptionService : IConfigurableAudioTranscriptionService
    {
        public List<AudioTranscriptionRequestOptions> Options { get; } = new();

        public Task<TranscriptionResult> TranscribeAudioFileAsync(
            string audioFilePath,
            string model,
            CancellationToken cancellationToken,
            IProgress<TranscriptionProgressSnapshot>? progress = null,
            string? diagnosticRoute = null)
        {
            return TranscribeAudioFileAsync(
                audioFilePath,
                model,
                new AudioTranscriptionRequestOptions(),
                cancellationToken,
                progress,
                diagnosticRoute);
        }

        public Task<TranscriptionResult> TranscribeAudioFileAsync(
            string audioFilePath,
            string model,
            AudioTranscriptionRequestOptions options,
            CancellationToken cancellationToken,
            IProgress<TranscriptionProgressSnapshot>? progress = null,
            string? diagnosticRoute = null)
        {
            Options.Add(options);
            return Task.FromResult(new TranscriptionResult(
                Text: "Live audio",
                Model: model,
                CreatedAt: DateTimeOffset.UtcNow,
                Duration: TimeSpan.FromMilliseconds(100),
                TokenLogprobs: Array.Empty<TranscriptionTokenLogprob>(),
                LowConfidenceTokens: Array.Empty<LowConfidenceToken>(),
                TimedLines: new[]
                {
                    new TranscriptionTimedLine(
                        "Live audio",
                        TimeSpan.Zero,
                        TimeSpan.FromMilliseconds(50),
                        false),
                }));
        }
    }
}
