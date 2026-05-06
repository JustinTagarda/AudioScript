using System.IO;
using System.Threading.Channels;
using AudioScript.Abstractions;
using NAudio.Wave;

namespace AudioScript.Services;

public sealed class LiveSegmentTranscriptionSession : IAsyncDisposable
{
    private static readonly TimeSpan DefaultOverlapDuration = TimeSpan.FromSeconds(3);

    private readonly LiveRecordingSession _recordingSession;
    private readonly IAudioTranscriptionService _transcriptionService;
    private readonly ProcessLogService _processLogService;
    private readonly TimeSpan _overlapDuration;
    private readonly Channel<LiveSegmentTranscriptionWorkItem> _queue =
        Channel.CreateUnbounded<LiveSegmentTranscriptionWorkItem>();

    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private string _model = string.Empty;
    private bool _hasStarted;
    private bool _isDisposed;

    public LiveSegmentTranscriptionSession(
        LiveRecordingSession recordingSession,
        IAudioTranscriptionService transcriptionService,
        ProcessLogService processLogService,
        TimeSpan? overlapDuration = null)
    {
        _recordingSession = recordingSession;
        _transcriptionService = transcriptionService;
        _processLogService = processLogService;
        _overlapDuration = overlapDuration.GetValueOrDefault(DefaultOverlapDuration);
        if (_overlapDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(overlapDuration), "Overlap duration must not be negative.");
        }
    }

    public event EventHandler<LiveSegmentTranscriptionStartedEventArgs>? SegmentTranscriptionStarted;
    public event EventHandler<LiveSegmentTranscriptionQueuedEventArgs>? SegmentTranscriptionQueued;
    public event EventHandler<LiveSegmentTranscriptionCompletedEventArgs>? SegmentTranscriptionCompleted;
    public event EventHandler<LiveSegmentTranscriptionFailedEventArgs>? SegmentTranscriptionFailed;

    public void Start(string model)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(LiveSegmentTranscriptionSession));
        }

        if (_hasStarted)
        {
            throw new InvalidOperationException("Live segment transcription sessions are single-use.");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("A transcription model is required.", nameof(model));
        }

        _model = model.Trim();
        _hasStarted = true;
        _cts = new CancellationTokenSource();
        _recordingSession.SegmentFinalized += OnRecordingSegmentFinalized;
        _workerTask = Task.Run(() => RunWorkerAsync(_cts.Token));
        Log($"Live segment transcription session started using model '{_model}'.");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_hasStarted)
        {
            return;
        }

        _recordingSession.SegmentFinalized -= OnRecordingSegmentFinalized;
        _queue.Writer.TryComplete();

        Task? workerTask = _workerTask;
        if (workerTask is not null)
        {
            await workerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        _cts?.Dispose();
        _cts = null;
        _workerTask = null;
        Log("Live segment transcription session stopped.");
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        if (!_hasStarted)
        {
            return;
        }

        _recordingSession.SegmentFinalized -= OnRecordingSegmentFinalized;
        CancellationTokenSource? cts = _cts;
        cts?.Cancel();
        _queue.Writer.TryComplete();

        Task? workerTask = _workerTask;
        if (workerTask is not null)
        {
            try
            {
                await workerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Expected when canceling pending live segment transcription.
            }
        }

        cts?.Dispose();
        _cts = null;
        _workerTask = null;
        Log("Live segment transcription session canceled.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            _cts?.Cancel();
            await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort disposal during shutdown.
        }
        finally
        {
            _isDisposed = true;
        }
    }

    private void OnRecordingSegmentFinalized(object? sender, LiveRecordingSegmentFinalizedEventArgs e)
    {
        var item = new LiveSegmentTranscriptionWorkItem(e.Segment, e.SegmentPath);
        if (_queue.Writer.TryWrite(item))
        {
            SegmentTranscriptionQueued?.Invoke(
                this,
                new LiveSegmentTranscriptionQueuedEventArgs(e.Segment, e.SegmentPath));
        }
        else
        {
            Log($"Live segment transcription queue rejected '{e.SegmentPath}'.");
        }
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        LiveSegmentTranscriptionWorkItem? pending = null;
        await foreach (LiveSegmentTranscriptionWorkItem item in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (pending is not null)
            {
                await TranscribeWorkItemAsync(pending, item, cancellationToken).ConfigureAwait(false);
            }

            pending = item;
        }

        if (pending is not null)
        {
            await TranscribeWorkItemAsync(pending, nextItem: null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TranscribeWorkItemAsync(
        LiveSegmentTranscriptionWorkItem item,
        LiveSegmentTranscriptionWorkItem? nextItem,
        CancellationToken cancellationToken)
    {
        string requestPath = item.SegmentPath;
        bool deleteRequestPath = false;
        TimeSpan requestStart = TimeSpan.FromSeconds(item.Segment.StartSeconds);

        try
        {
            if (nextItem is not null && _overlapDuration > TimeSpan.Zero)
            {
                requestPath = CreateSegmentRequestWaveFile(item, nextItem, out TimeSpan resolvedRequestStart);
                requestStart = resolvedRequestStart;
                deleteRequestPath = true;
            }

            SegmentTranscriptionStarted?.Invoke(
                this,
                new LiveSegmentTranscriptionStartedEventArgs(item.Segment, requestPath));
            Log(
                $"Transcribing live segment '{requestPath}' " +
                $"[{FormatDuration(item.Segment.StartSeconds)} - {FormatDuration(item.Segment.StartSeconds + item.Segment.DurationSeconds)}].");

            TranscriptionResult result = await TranscribeLiveSegmentAsync(
                requestPath,
                cancellationToken).ConfigureAwait(false);
            TranscriptionResult translated = TranslateResultToSessionTimeline(result, item.Segment, requestStart);

            SegmentTranscriptionCompleted?.Invoke(
                this,
                new LiveSegmentTranscriptionCompletedEventArgs(item.Segment, item.SegmentPath, translated));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log(
                $"Live segment transcription failed for '{item.SegmentPath}': " +
                $"{ex.GetType().Name}: {ex.Message}.");
            SegmentTranscriptionFailed?.Invoke(
                this,
                new LiveSegmentTranscriptionFailedEventArgs(item.Segment, item.SegmentPath, ex));
        }
        finally
        {
            if (deleteRequestPath)
            {
                DeleteTemporaryFile(requestPath);
            }
        }
    }

    private Task<TranscriptionResult> TranscribeLiveSegmentAsync(
        string requestPath,
        CancellationToken cancellationToken)
    {
        if (_transcriptionService is IConfigurableAudioTranscriptionService configurableService)
        {
            return configurableService.TranscribeAudioFileAsync(
                requestPath,
                _model,
                new AudioTranscriptionRequestOptions(SuppressPrompt: true),
                cancellationToken);
        }

        return _transcriptionService.TranscribeAudioFileAsync(
            requestPath,
            _model,
            cancellationToken);
    }

    private static TranscriptionResult TranslateResultToSessionTimeline(
        TranscriptionResult result,
        LiveRecordingSegmentManifest segment,
        TimeSpan requestStart)
    {
        TimeSpan segmentStart = TimeSpan.FromSeconds(segment.StartSeconds);
        TimeSpan segmentEnd = segmentStart + TimeSpan.FromSeconds(segment.DurationSeconds);
        IReadOnlyList<TranscriptionTimedLine> sourceLines = result.TimedLines ?? Array.Empty<TranscriptionTimedLine>();
        IReadOnlyList<TranscriptionTimedLine> translatedLines = sourceLines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .Select(line =>
            {
                TimeSpan absoluteStart = requestStart + line.StartOffset;
                TimeSpan? absoluteEnd = line.EndOffset is null ? null : requestStart + line.EndOffset.Value;
                return new TranscriptionTimedLine(
                    line.Text.Trim(),
                    absoluteStart < segmentStart ? segmentStart : absoluteStart,
                    absoluteEnd is null || absoluteEnd > segmentEnd ? segmentEnd : absoluteEnd,
                    line.IsTimestampEstimated);
            })
            .Where(line =>
            {
                TimeSpan midpoint = ResolveMidpoint(line.StartOffset, line.EndOffset);
                return midpoint >= segmentStart && midpoint <= segmentEnd;
            })
            .OrderBy(line => line.StartOffset)
            .ToArray();

        if (translatedLines.Count == 0 && !string.IsNullOrWhiteSpace(result.Text))
        {
            translatedLines = new[]
            {
                new TranscriptionTimedLine(
                    result.Text.Trim(),
                    segmentStart,
                    segmentStart + TimeSpan.FromSeconds(segment.DurationSeconds),
                    true),
            };
        }

        return new TranscriptionResult(
            Text: BuildText(translatedLines),
            Model: result.Model,
            CreatedAt: result.CreatedAt,
            Duration: segmentStart + TimeSpan.FromSeconds(segment.DurationSeconds),
            TokenLogprobs: result.TokenLogprobs,
            LowConfidenceTokens: result.LowConfidenceTokens,
            TimedLines: translatedLines);
    }

    private string CreateSegmentRequestWaveFile(
        LiveSegmentTranscriptionWorkItem item,
        LiveSegmentTranscriptionWorkItem nextItem,
        out TimeSpan requestStart)
    {
        TimeSpan currentStart = TimeSpan.FromSeconds(item.Segment.StartSeconds);
        TimeSpan currentDuration = TimeSpan.FromSeconds(item.Segment.DurationSeconds);
        TimeSpan nextDuration = TimeSpan.FromSeconds(nextItem.Segment.DurationSeconds);
        TimeSpan nextOverlap = Min(_overlapDuration, nextDuration);
        requestStart = currentStart;

        string tempPath = Path.Combine(
            Path.GetTempPath(),
            $"AudioScript-live-segment-{Guid.NewGuid():N}.wav");

        using var currentReader = new WaveFileReader(item.SegmentPath);
        using var nextReader = new WaveFileReader(nextItem.SegmentPath);
        if (!WaveFormatsMatch(currentReader.WaveFormat, nextReader.WaveFormat))
        {
            throw new InvalidOperationException("Adjacent live recording segments do not use the same audio format.");
        }

        using var writer = new WaveFileWriter(tempPath, currentReader.WaveFormat);
        CopyRange(currentReader, writer, TimeSpan.Zero, currentDuration);
        CopyRange(nextReader, writer, TimeSpan.Zero, nextOverlap);
        return tempPath;
    }

    private static void CopyRange(WaveFileReader reader, WaveFileWriter writer, TimeSpan start, TimeSpan duration)
    {
        long startPosition = ResolveBytePosition(reader.WaveFormat, start, reader.Length);
        long endPosition = ResolveBytePosition(reader.WaveFormat, start + duration, reader.Length);
        if (endPosition <= startPosition)
        {
            return;
        }

        reader.Position = startPosition;
        byte[] buffer = new byte[81920];
        while (reader.Position < endPosition)
        {
            int bytesToRead = (int)Math.Min(buffer.Length, endPosition - reader.Position);
            int bytesRead = reader.Read(buffer, 0, bytesToRead);
            if (bytesRead <= 0)
            {
                break;
            }

            writer.Write(buffer, 0, bytesRead);
        }
    }

    private static long ResolveBytePosition(WaveFormat format, TimeSpan offset, long maxLength)
    {
        long rawPosition = (long)Math.Round(
            offset.TotalSeconds * format.AverageBytesPerSecond,
            MidpointRounding.AwayFromZero);
        int remainder = (int)(rawPosition % Math.Max(format.BlockAlign, 1));
        if (remainder != 0)
        {
            rawPosition -= remainder;
        }

        return Math.Clamp(rawPosition, 0, maxLength);
    }

    private static TimeSpan ResolveMidpoint(TimeSpan start, TimeSpan? end)
    {
        TimeSpan resolvedEnd = end is null || end <= start
            ? start
            : end.Value;
        return resolvedEnd <= start
            ? start
            : start + TimeSpan.FromTicks((resolvedEnd - start).Ticks / 2);
    }

    private static bool WaveFormatsMatch(WaveFormat left, WaveFormat right)
    {
        return left.Encoding == right.Encoding
            && left.SampleRate == right.SampleRate
            && left.BitsPerSample == right.BitsPerSample
            && left.Channels == right.Channels
            && left.BlockAlign == right.BlockAlign
            && left.AverageBytesPerSecond == right.AverageBytesPerSecond;
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right)
    {
        return left <= right ? left : right;
    }

    private static void DeleteTemporaryFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Best-effort temporary file cleanup.
        }
    }

    private static string BuildText(IReadOnlyList<TranscriptionTimedLine> lines)
    {
        return string.Join(
            Environment.NewLine,
            lines
                .Where(line => !string.IsNullOrWhiteSpace(line.Text))
                .OrderBy(line => line.StartOffset)
                .Select(line => line.Text.Trim()));
    }

    private static string FormatDuration(double seconds)
    {
        return TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss");
    }

    private void Log(string message)
    {
        _processLogService.Log("LiveSegmentTranscription", message);
    }

    private sealed record LiveSegmentTranscriptionWorkItem(
        LiveRecordingSegmentManifest Segment,
        string SegmentPath);
}

public sealed class LiveSegmentTranscriptionStartedEventArgs : EventArgs
{
    public LiveSegmentTranscriptionStartedEventArgs(
        LiveRecordingSegmentManifest segment,
        string segmentPath)
    {
        Segment = segment;
        SegmentPath = segmentPath;
    }

    public LiveRecordingSegmentManifest Segment { get; }

    public string SegmentPath { get; }
}

public sealed class LiveSegmentTranscriptionQueuedEventArgs : EventArgs
{
    public LiveSegmentTranscriptionQueuedEventArgs(
        LiveRecordingSegmentManifest segment,
        string segmentPath)
    {
        Segment = segment;
        SegmentPath = segmentPath;
    }

    public LiveRecordingSegmentManifest Segment { get; }

    public string SegmentPath { get; }
}

public sealed class LiveSegmentTranscriptionCompletedEventArgs : EventArgs
{
    public LiveSegmentTranscriptionCompletedEventArgs(
        LiveRecordingSegmentManifest segment,
        string segmentPath,
        TranscriptionResult result)
    {
        Segment = segment;
        SegmentPath = segmentPath;
        Result = result;
    }

    public LiveRecordingSegmentManifest Segment { get; }

    public string SegmentPath { get; }

    public TranscriptionResult Result { get; }
}

public sealed class LiveSegmentTranscriptionFailedEventArgs : EventArgs
{
    public LiveSegmentTranscriptionFailedEventArgs(
        LiveRecordingSegmentManifest segment,
        string segmentPath,
        Exception exception)
    {
        Segment = segment;
        SegmentPath = segmentPath;
        Exception = exception;
    }

    public LiveRecordingSegmentManifest Segment { get; }

    public string SegmentPath { get; }

    public Exception Exception { get; }
}
