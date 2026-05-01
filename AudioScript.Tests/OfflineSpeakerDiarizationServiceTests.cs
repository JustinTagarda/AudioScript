using AudioScript.Abstractions;
using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class OfflineSpeakerDiarizationServiceTests {
    [Fact]
    public async Task DiarizeAudioFileAsync_MergesTimedLinesWithStrongestSpeakerTurnOverlap() {
        TranscriptionResult transcriptionResult = CreateTranscriptionResult([
            new TranscriptionTimedLine("hello", TimeSpan.Zero, TimeSpan.FromSeconds(1.2), false),
            new TranscriptionTimedLine("reply", TimeSpan.FromSeconds(1.3), TimeSpan.FromSeconds(2.4), false),
        ]);
        var diarizationEngine = new StubSpeakerDiarizationEngine([
            new SpeakerDiarizationTurn("speaker_1", TimeSpan.Zero, TimeSpan.FromSeconds(1.0)),
            new SpeakerDiarizationTurn("speaker_2", TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(2.5)),
        ]);
        var service = new OfflineSpeakerDiarizationService(
            diarizationEngine,
            new ProcessLogService());

        SpeakerDiarizationResult result = await service.ApplySpeakerLabelsAsync(
            "test.wav",
            transcriptionResult,
            CancellationToken.None);

        Assert.Equal(TranscriptionModelCatalog.WhisperSmall, result.Model);
        Assert.Equal(new[] { "speaker_1", "speaker_2" }, result.Segments.Select(segment => segment.Speaker).ToArray());
        Assert.Equal(new[] { "hello", "reply" }, result.Segments.Select(segment => segment.Text).ToArray());
        Assert.Contains("speaker_1: hello", result.Text);
        Assert.Contains("speaker_2: reply", result.Text);
    }

    [Fact]
    public void MergeTranscriptWithSpeakerTurns_UsesEarliestTurn_WhenOverlapTies() {
        IReadOnlyList<SpeakerDiarizationSegment> segments = OfflineSpeakerDiarizationService.MergeTranscriptWithSpeakerTurns(
            [
                new TranscriptionTimedLine("tie", TimeSpan.Zero, TimeSpan.FromSeconds(2), false),
            ],
            [
                new SpeakerDiarizationTurn("speaker_1", TimeSpan.Zero, TimeSpan.FromSeconds(1)),
                new SpeakerDiarizationTurn("speaker_2", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)),
            ]);

        Assert.Equal("speaker_1", segments.Single().Speaker);
    }

    [Fact]
    public void MergeTranscriptWithSpeakerTurns_UsesNearestPrecedingTurn_WhenNoOverlapWithinTolerance() {
        IReadOnlyList<SpeakerDiarizationSegment> segments = OfflineSpeakerDiarizationService.MergeTranscriptWithSpeakerTurns(
            [
                new TranscriptionTimedLine("nearby", TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(3), false),
            ],
            [
                new SpeakerDiarizationTurn("speaker_1", TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            ]);

        Assert.Equal("speaker_1", segments.Single().Speaker);
    }

    [Fact]
    public void MergeTranscriptWithSpeakerTurns_UsesUnknownSpeaker_WhenNoOverlapOrNearbyTurnExists() {
        IReadOnlyList<SpeakerDiarizationSegment> segments = OfflineSpeakerDiarizationService.MergeTranscriptWithSpeakerTurns(
            [
                new TranscriptionTimedLine("unknown", TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(7), false),
            ],
            [
                new SpeakerDiarizationTurn("speaker_1", TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            ]);

        Assert.Equal("speaker_unknown", segments.Single().Speaker);
    }

    [Fact]
    public async Task DiarizeAudioFileAsync_FallsBackToHeuristic_WhenRealDiarizationAssetsFail() {
        TranscriptionResult transcriptionResult = CreateTranscriptionResult([
            new TranscriptionTimedLine("hello.", TimeSpan.Zero, TimeSpan.FromSeconds(1), false),
            new TranscriptionTimedLine("reply", TimeSpan.FromSeconds(1.8), TimeSpan.FromSeconds(2.5), false),
        ]);
        var service = new OfflineSpeakerDiarizationService(
            new FailingSpeakerDiarizationEngine(),
            new ProcessLogService());

        SpeakerDiarizationResult result = await service.ApplySpeakerLabelsAsync(
            "test.wav",
            transcriptionResult,
            CancellationToken.None);

        Assert.Equal(new[] { "speaker_1", "speaker_2" }, result.Segments.Select(segment => segment.Speaker).ToArray());
    }

    [Fact]
    public async Task DiarizeAudioFileAsync_PropagatesCancellation() {
        var service = new OfflineSpeakerDiarizationService(
            new StubSpeakerDiarizationEngine([]),
            new ProcessLogService());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.ApplySpeakerLabelsAsync(
                "test.wav",
                CreateTranscriptionResult([
                    new TranscriptionTimedLine("canceled", TimeSpan.Zero, TimeSpan.FromSeconds(1), false),
                ]),
                new CancellationToken(canceled: true)));
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

    private sealed class StubSpeakerDiarizationEngine : ISpeakerDiarizationEngine {
        private readonly IReadOnlyList<SpeakerDiarizationTurn> _turns;

        public StubSpeakerDiarizationEngine(IReadOnlyList<SpeakerDiarizationTurn> turns) {
            _turns = turns;
        }

        public Task<IReadOnlyList<SpeakerDiarizationTurn>> DiarizeAudioFileAsync(
            string audioFilePath,
            CancellationToken cancellationToken,
            IProgress<SpeakerDiarizationProgress>? progress = null) {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SpeakerDiarizationProgress(1, 2));
            progress?.Report(new SpeakerDiarizationProgress(2, 2));
            return Task.FromResult(_turns);
        }
    }

    private sealed class FailingSpeakerDiarizationEngine : ISpeakerDiarizationEngine {
        public Task<IReadOnlyList<SpeakerDiarizationTurn>> DiarizeAudioFileAsync(
            string audioFilePath,
            CancellationToken cancellationToken,
            IProgress<SpeakerDiarizationProgress>? progress = null) {
            throw new FileNotFoundException("missing bundled sherpa asset");
        }
    }
}

