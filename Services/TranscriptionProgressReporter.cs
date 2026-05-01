using AudioScript.Abstractions;

namespace AudioScript.Services;

internal sealed class TranscriptionProgressReporter
{
    private static readonly TimeSpan MinimumReportInterval = TimeSpan.FromMilliseconds(200);

    private readonly IProgress<TranscriptionProgressSnapshot>? _progress;
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastReportedUtc = DateTimeOffset.MinValue;

    public TranscriptionProgressReporter(IProgress<TranscriptionProgressSnapshot>? progress)
    {
        _progress = progress;
    }

    public TimeSpan Elapsed => DateTimeOffset.UtcNow - _startedUtc;

    public void Report(
        TranscriptionProgressPhase phase,
        double percent,
        TimeSpan processedAudio,
        TimeSpan totalAudio,
        string detailMessage,
        double? overallPercent = null,
        int? currentChunk = null,
        int? totalChunks = null,
        bool force = false)
    {
        if (_progress is null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!force && now - _lastReportedUtc < MinimumReportInterval)
        {
            return;
        }

        _lastReportedUtc = now;
        _progress.Report(TranscriptionProgressSnapshot.Create(
            phase,
            percent,
            overallPercent,
            currentChunk,
            totalChunks,
            processedAudio,
            totalAudio,
            now - _startedUtc,
            detailMessage));
    }
}
