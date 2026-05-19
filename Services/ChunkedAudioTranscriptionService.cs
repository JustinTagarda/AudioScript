using AudioScript.Abstractions;
using AudioScript.Audio;

namespace AudioScript.Services;

public sealed class ChunkedAudioTranscriptionService {
    private readonly AudioChunkingService _audioChunkingService;
    private readonly IAudioTranscriptionService _requestService;
    private readonly ProcessLogService _processLogService;

    public ChunkedAudioTranscriptionService(
        AudioChunkingService audioChunkingService,
        IAudioTranscriptionService requestService,
        ProcessLogService processLogService) {
        _audioChunkingService = audioChunkingService;
        _requestService = requestService;
        _processLogService = processLogService;
    }

    public async Task<TranscriptionResult> TranscribeAudioFileAsync(
        string audioFilePath,
        string model,
        CancellationToken cancellationToken,
        IProgress<TranscriptionProgressSnapshot>? progress = null,
        int startChunkIndex = 0,
        IReadOnlyList<TranscriptionTimedLine>? existingCommittedLines = null,
        Action<TranscriptionChunkCommit>? chunkCommitted = null) {
        var progressReporter = new TranscriptionProgressReporter(progress);
        AudioSourceInfo sourceInfo = _audioChunkingService.GetSourceInfo(audioFilePath);
        progressReporter.Report(
            TranscriptionProgressPhase.PreparingAudio,
            0,
            TimeSpan.Zero,
            sourceInfo.Duration,
            $"Preparing {sourceInfo.Name}.",
            force: true);

        Log(
            $"Audio transcription will use chunked requests for '{sourceInfo.Name}' " +
            $"({FormatDuration(sourceInfo.Duration)}, {sourceInfo.FileSizeBytes:N0} bytes).");
        progressReporter.Report(
            TranscriptionProgressPhase.Chunking,
            1,
            TimeSpan.Zero,
            sourceInfo.Duration,
            $"Detecting quiet split points in {sourceInfo.Name}.",
            force: true);

        using ChunkedAudioFile chunkedAudioFile = _audioChunkingService.PrepareChunks(sourceInfo);
        Log(
            $"Prepared {chunkedAudioFile.Chunks.Count:N0} transcription chunk(s) " +
            $"from {chunkedAudioFile.SilenceIntervals.Count:N0} detected silence interval(s).");
        progressReporter.Report(
            TranscriptionProgressPhase.Chunking,
            2,
            TimeSpan.Zero,
            sourceInfo.Duration,
            $"Prepared {chunkedAudioFile.Chunks.Count:N0} audio chunk(s).",
            totalChunks: chunkedAudioFile.Chunks.Count,
            force: true);

        startChunkIndex = Math.Clamp(startChunkIndex, 0, chunkedAudioFile.Chunks.Count);
        var mergedLines = existingCommittedLines?
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .OrderBy(line => line.StartOffset)
            .ToList()
            ?? new List<TranscriptionTimedLine>();
        var tokenLogprobs = new List<TranscriptionTokenLogprob>();
        var lowConfidenceTokens = new List<LowConfidenceToken>();
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;

        for (int chunkIndex = startChunkIndex; chunkIndex < chunkedAudioFile.Chunks.Count; chunkIndex++) {
            cancellationToken.ThrowIfCancellationRequested();

            AudioChunkFile chunkFile = chunkedAudioFile.Chunks[chunkIndex];
            AudioChunkPlan chunkPlan = chunkFile.Plan;
            int currentChunk = chunkIndex + 1;
            Log(
                $"Submitting transcription chunk {currentChunk}/{chunkedAudioFile.Chunks.Count} " +
                $"[{FormatDuration(chunkPlan.RequestStart)} - {FormatDuration(chunkPlan.RequestEnd)}].");
            progressReporter.Report(
                TranscriptionProgressPhase.TranscribingChunk,
                ResolveChunkedPercent(chunkPlan.KeepStart, sourceInfo.Duration),
                chunkPlan.KeepStart,
                sourceInfo.Duration,
                $"Transcribing chunk {currentChunk:N0} of {chunkedAudioFile.Chunks.Count:N0}.",
                currentChunk: currentChunk,
                totalChunks: chunkedAudioFile.Chunks.Count,
                force: true);

            IProgress<TranscriptionProgressSnapshot> chunkProgress =
                new Progress<TranscriptionProgressSnapshot>(snapshot =>
                {
                    TimeSpan chunkProcessed = snapshot.ProcessedAudio;
                    if (chunkProcessed < TimeSpan.Zero) {
                        chunkProcessed = TimeSpan.Zero;
                    }

                    TimeSpan chunkDuration = chunkPlan.RequestEnd - chunkPlan.RequestStart;
                    if (chunkDuration > TimeSpan.Zero && chunkProcessed > chunkDuration) {
                        chunkProcessed = chunkDuration;
                    }

                    TimeSpan processedAudio = chunkPlan.RequestStart + chunkProcessed;
                    if (processedAudio > sourceInfo.Duration) {
                        processedAudio = sourceInfo.Duration;
                    }

                    progressReporter.Report(
                        TranscriptionProgressPhase.TranscribingChunk,
                        ResolveChunkedPercent(processedAudio, sourceInfo.Duration),
                        processedAudio,
                        sourceInfo.Duration,
                        $"Transcribing chunk {currentChunk:N0} of {chunkedAudioFile.Chunks.Count:N0}.",
                        currentChunk: currentChunk,
                        totalChunks: chunkedAudioFile.Chunks.Count);
                });

            TranscriptionResult chunkResult = await _requestService.TranscribeAudioFileAsync(
                chunkFile.FilePath,
                model,
                cancellationToken,
                chunkProgress);

            if (chunkIndex == 0) {
                createdAt = chunkResult.CreatedAt;
            }

            progressReporter.Report(
                TranscriptionProgressPhase.MergingResults,
                ResolveChunkedPercent(chunkPlan.KeepEnd, sourceInfo.Duration),
                chunkPlan.KeepEnd,
                sourceInfo.Duration,
                $"Merging chunk {currentChunk:N0} of {chunkedAudioFile.Chunks.Count:N0}.",
                currentChunk: currentChunk,
                totalChunks: chunkedAudioFile.Chunks.Count,
                force: true);

            IReadOnlyList<TranscriptionTimedLine> keepLines = TranslateAndFilterTimedLines(
                chunkResult.TimedLines ?? Array.Empty<TranscriptionTimedLine>(),
                chunkPlan,
                isLastChunk: chunkIndex == chunkedAudioFile.Chunks.Count - 1);

            mergedLines.AddRange(keepLines);
            chunkCommitted?.Invoke(new TranscriptionChunkCommit(
                chunkIndex,
                chunkedAudioFile.Chunks.Count,
                keepLines));
            tokenLogprobs.AddRange(chunkResult.TokenLogprobs);
            lowConfidenceTokens.AddRange(chunkResult.LowConfidenceTokens);

            Log(
                $"Transcription chunk {chunkIndex + 1}/{chunkedAudioFile.Chunks.Count} produced " +
                $"{chunkResult.TimedLines?.Count ?? 0:N0} timed line(s); {keepLines.Count:N0} kept after overlap merge.");
        }

        IReadOnlyList<TranscriptionTimedLine> orderedLines = mergedLines
            .OrderBy(line => line.StartOffset)
            .ToArray();
        progressReporter.Report(
            TranscriptionProgressPhase.Completed,
            100,
            sourceInfo.Duration,
            sourceInfo.Duration,
            $"Completed {sourceInfo.Name}.",
            currentChunk: chunkedAudioFile.Chunks.Count,
            totalChunks: chunkedAudioFile.Chunks.Count,
            force: true);

        return new TranscriptionResult(
            Text: BuildResultText(orderedLines),
            Model: model,
            CreatedAt: createdAt,
            Duration: sourceInfo.Duration,
            TokenLogprobs: tokenLogprobs,
            LowConfidenceTokens: lowConfidenceTokens,
            TimedLines: orderedLines);
    }

    private static IReadOnlyList<TranscriptionTimedLine> TranslateAndFilterTimedLines(
        IReadOnlyList<TranscriptionTimedLine> chunkLines,
        AudioChunkPlan chunkPlan,
        bool isLastChunk) {
        var results = new List<TranscriptionTimedLine>();

        foreach (TranscriptionTimedLine chunkLine in chunkLines.OrderBy(line => line.StartOffset)) {
            if (string.IsNullOrWhiteSpace(chunkLine.Text)) {
                continue;
            }

            TimeSpan absoluteStart = chunkPlan.RequestStart + chunkLine.StartOffset;
            TimeSpan? absoluteEnd = chunkLine.EndOffset is null
                ? null
                : chunkPlan.RequestStart + chunkLine.EndOffset.Value;
            TimeSpan midpoint = AudioChunkingService.ResolveMidpoint(absoluteStart, absoluteEnd);

            if (midpoint < chunkPlan.KeepStart) {
                continue;
            }

            if ((!isLastChunk && midpoint >= chunkPlan.KeepEnd)
                || (isLastChunk && midpoint > chunkPlan.KeepEnd)) {
                continue;
            }

            results.Add(new TranscriptionTimedLine(
                Text: chunkLine.Text.Trim(),
                StartOffset: absoluteStart,
                EndOffset: absoluteEnd,
                IsTimestampEstimated: chunkLine.IsTimestampEstimated));
        }

        return results;
    }

    private static string BuildResultText(IReadOnlyList<TranscriptionTimedLine> orderedLines) {
        return string.Join(
            Environment.NewLine,
            orderedLines.Select(line => line.Text.Trim()).Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private void Log(string message) {
        _processLogService.Log("AudioTranscription", message);
    }

    private static string FormatDuration(TimeSpan duration) {
        return duration.TotalHours >= 1
            ? duration.ToString(@"hh\:mm\:ss")
            : duration.ToString(@"mm\:ss");
    }

    private static double ResolvePercent(TimeSpan processedAudio, TimeSpan totalAudio) {
        if (totalAudio <= TimeSpan.Zero) {
            return 0;
        }

        return Math.Clamp((processedAudio.TotalSeconds / totalAudio.TotalSeconds) * 100d, 0, 100);
    }

    private static double ResolveChunkedPercent(TimeSpan processedAudio, TimeSpan totalAudio) {
        return 2 + (ResolvePercent(processedAudio, totalAudio) * 0.97);
    }
}

public sealed record TranscriptionChunkCommit(
    int ChunkIndex,
    int TotalChunks,
    IReadOnlyList<TranscriptionTimedLine> CommittedLines);
