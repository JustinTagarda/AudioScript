using System.IO;
using AudioScript.Abstractions;

namespace AudioScript.Services;

public sealed class OfflineSpeakerDiarizationService {
    private static readonly TimeSpan NearestSpeakerTolerance = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StrongTurnGap = TimeSpan.FromSeconds(1.25);
    private static readonly TimeSpan SentenceTurnGap = TimeSpan.FromSeconds(0.75);

    private readonly ISpeakerDiarizationEngine _diarizationEngine;
    private readonly ProcessLogService _processLogService;

    public OfflineSpeakerDiarizationService(
        ISpeakerDiarizationEngine diarizationEngine,
        ProcessLogService processLogService) {
        _diarizationEngine = diarizationEngine;
        _processLogService = processLogService;
    }

    public async Task<SpeakerDiarizationResult> ApplySpeakerLabelsAsync(
        string audioFilePath,
        TranscriptionResult transcriptionResult,
        CancellationToken cancellationToken,
        IProgress<TranscriptionProgressSnapshot>? progress = null) {
        ArgumentNullException.ThrowIfNull(transcriptionResult);
        IReadOnlyList<TranscriptionTimedLine> timedLines =
            transcriptionResult.TimedLines ?? Array.Empty<TranscriptionTimedLine>();
        if (timedLines.Count == 0) {
            throw new InvalidOperationException("Transcription did not return any timed transcript segments.");
        }

        var progressReporter = new TranscriptionProgressReporter(progress);
        TimeSpan totalAudio = transcriptionResult.Duration ?? ResolveLastLineEnd(timedLines);
        Log($"Offline speaker-label generation requested for {timedLines.Count:N0} timed line(s).");

        IReadOnlyList<SpeakerDiarizationTurn> speakerTurns;
        try {
            progressReporter.Report(
                TranscriptionProgressPhase.RunningSpeakerDiarization,
                0,
                TimeSpan.Zero,
                totalAudio,
                "Running speaker diarization.",
                force: true);
            speakerTurns = await _diarizationEngine.DiarizeAudioFileAsync(
                audioFilePath,
                cancellationToken,
                new Progress<SpeakerDiarizationProgress>(diarizationProgress =>
                {
                    double percent = diarizationProgress.Percent;
                    TimeSpan processedAudio = totalAudio > TimeSpan.Zero
                        ? TimeSpan.FromTicks((long)(totalAudio.Ticks * (percent / 100d)))
                        : TimeSpan.Zero;
                    progressReporter.Report(
                        TranscriptionProgressPhase.RunningSpeakerDiarization,
                        percent,
                        processedAudio,
                        totalAudio,
                        "Running speaker diarization.",
                        force: percent >= 100);
                }));
        }
        catch (Exception ex) when (IsEmergencyFallbackException(ex)) {
            _processLogService.LogException(
                "SpeakerDiarization",
                "Real offline diarization failed. Falling back to heuristic speaker labels.",
                ex);
            progressReporter.Report(
                TranscriptionProgressPhase.MergingSpeakerLabels,
                99,
                totalAudio,
                totalAudio,
                "Applying heuristic speaker labels.",
                force: true);
            IReadOnlyList<SpeakerDiarizationSegment> fallbackSegments = BuildHeuristicSpeakerSegments(timedLines);
            return new SpeakerDiarizationResult(
                Text: BuildResultText(fallbackSegments),
                Model: transcriptionResult.Model,
                CreatedAt: transcriptionResult.CreatedAt,
                Duration: transcriptionResult.Duration,
                Segments: fallbackSegments);
        }

        progressReporter.Report(
            TranscriptionProgressPhase.MergingSpeakerLabels,
            99,
            totalAudio,
            totalAudio,
            "Applying speaker labels.",
            force: true);
        IReadOnlyList<SpeakerDiarizationSegment> mergedSegments = MergeTranscriptWithSpeakerTurns(
            timedLines,
            speakerTurns);

        return new SpeakerDiarizationResult(
            Text: BuildResultText(mergedSegments),
            Model: transcriptionResult.Model,
            CreatedAt: transcriptionResult.CreatedAt,
            Duration: transcriptionResult.Duration,
            Segments: mergedSegments);
    }

    public static IReadOnlyList<SpeakerDiarizationSegment> MergeTranscriptWithSpeakerTurns(
        IReadOnlyList<TranscriptionTimedLine> timedLines,
        IReadOnlyList<SpeakerDiarizationTurn> speakerTurns) {
        SpeakerDiarizationTurn[] orderedTurns = speakerTurns
            .Where(turn => !string.IsNullOrWhiteSpace(turn.Speaker) && turn.EndOffset > turn.StartOffset)
            .OrderBy(turn => turn.StartOffset)
            .ToArray();
        var segments = new List<SpeakerDiarizationSegment>();

        foreach (TranscriptionTimedLine line in timedLines
                     .Where(line => !string.IsNullOrWhiteSpace(line.Text))
                     .OrderBy(line => line.StartOffset)) {
            TimeSpan lineEnd = ResolveLineEnd(line);
            string speaker = ResolveSpeaker(line.StartOffset, lineEnd, orderedTurns);
            segments.Add(new SpeakerDiarizationSegment(
                Speaker: speaker,
                Text: line.Text.Trim(),
                StartOffset: line.StartOffset,
                EndOffset: line.EndOffset));
        }

        return segments;
    }

    private static string ResolveSpeaker(
        TimeSpan lineStart,
        TimeSpan lineEnd,
        IReadOnlyList<SpeakerDiarizationTurn> orderedTurns) {
        if (orderedTurns.Count == 0) {
            return "speaker_unknown";
        }

        SpeakerDiarizationTurn? bestTurn = null;
        TimeSpan bestOverlap = TimeSpan.Zero;
        foreach (SpeakerDiarizationTurn turn in orderedTurns) {
            TimeSpan overlap = ResolveOverlap(lineStart, lineEnd, turn.StartOffset, turn.EndOffset);
            if (overlap <= TimeSpan.Zero) {
                continue;
            }

            if (bestTurn is null
                || overlap > bestOverlap
                || (overlap == bestOverlap && turn.StartOffset < bestTurn.StartOffset)) {
                bestTurn = turn;
                bestOverlap = overlap;
            }
        }

        if (bestTurn is not null) {
            return bestTurn.Speaker;
        }

        SpeakerDiarizationTurn? precedingTurn = orderedTurns
            .Where(turn => turn.EndOffset <= lineStart)
            .OrderBy(turn => lineStart - turn.EndOffset)
            .FirstOrDefault();
        if (precedingTurn is not null && lineStart - precedingTurn.EndOffset <= NearestSpeakerTolerance) {
            return precedingTurn.Speaker;
        }

        return "speaker_unknown";
    }

    private static TimeSpan ResolveOverlap(
        TimeSpan leftStart,
        TimeSpan leftEnd,
        TimeSpan rightStart,
        TimeSpan rightEnd) {
        TimeSpan overlapStart = leftStart > rightStart ? leftStart : rightStart;
        TimeSpan overlapEnd = leftEnd < rightEnd ? leftEnd : rightEnd;
        return overlapEnd <= overlapStart ? TimeSpan.Zero : overlapEnd - overlapStart;
    }

    private static TimeSpan ResolveLineEnd(TranscriptionTimedLine line) {
        return line.EndOffset is null || line.EndOffset <= line.StartOffset
            ? line.StartOffset
            : line.EndOffset.Value;
    }

    private static IReadOnlyList<SpeakerDiarizationSegment> BuildHeuristicSpeakerSegments(
        IReadOnlyList<TranscriptionTimedLine> timedLines) {
        var segments = new List<SpeakerDiarizationSegment>();
        string currentSpeaker = "speaker_1";
        TranscriptionTimedLine? previousLine = null;

        foreach (TranscriptionTimedLine line in timedLines
                     .Where(line => !string.IsNullOrWhiteSpace(line.Text))
                     .OrderBy(line => line.StartOffset)) {
            if (previousLine is not null && ShouldSwitchSpeaker(previousLine, line)) {
                currentSpeaker = string.Equals(currentSpeaker, "speaker_1", StringComparison.OrdinalIgnoreCase)
                    ? "speaker_2"
                    : "speaker_1";
            }

            segments.Add(new SpeakerDiarizationSegment(
                Speaker: currentSpeaker,
                Text: line.Text.Trim(),
                StartOffset: line.StartOffset,
                EndOffset: line.EndOffset));
            previousLine = line;
        }

        return segments;
    }

    private static bool ShouldSwitchSpeaker(TranscriptionTimedLine previousLine, TranscriptionTimedLine currentLine) {
        TimeSpan previousEnd = previousLine.EndOffset is not null && previousLine.EndOffset > previousLine.StartOffset
            ? previousLine.EndOffset.Value
            : previousLine.StartOffset;
        TimeSpan gap = currentLine.StartOffset - previousEnd;
        if (gap >= StrongTurnGap) {
            return true;
        }

        return gap >= SentenceTurnGap && EndsSentence(previousLine.Text);
    }

    private static bool EndsSentence(string? text) {
        string trimmed = text?.Trim() ?? string.Empty;
        return trimmed.EndsWith(".", StringComparison.Ordinal)
            || trimmed.EndsWith("?", StringComparison.Ordinal)
            || trimmed.EndsWith("!", StringComparison.Ordinal);
    }

    private static bool IsEmergencyFallbackException(Exception exception) {
        return exception is FileNotFoundException
            or DllNotFoundException
            or BadImageFormatException
            or InvalidOperationException;
    }

    private static string BuildResultText(IReadOnlyList<SpeakerDiarizationSegment> orderedSegments) {
        return string.Join(
            Environment.NewLine,
            orderedSegments.Select(segment => $"{segment.Speaker}: {segment.Text}".Trim()));
    }

    private void Log(string message) {
        _processLogService.Log("SpeakerDiarization", message);
    }

    private static TimeSpan ResolveLastLineEnd(IReadOnlyList<TranscriptionTimedLine> timedLines) {
        return timedLines
            .Select(line => ResolveLineEnd(line))
            .DefaultIfEmpty(TimeSpan.Zero)
            .Max();
    }
}
