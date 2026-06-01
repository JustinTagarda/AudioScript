using System.IO;
using System.Diagnostics;
using System.Threading.Channels;
using AudioScript.Abstractions;
using NAudio.Wave;

namespace AudioScript.Services;

public sealed class LiveSegmentTranscriptionSession : IAsyncDisposable
{
    private static readonly TimeSpan DefaultOverlapDuration = TimeSpan.FromSeconds(3);
    private const int DefaultMaxParallelTranscriptions = 2;

    private readonly LiveRecordingSession _recordingSession;
    private readonly IAudioTranscriptionService _transcriptionService;
    private readonly ProcessLogService _processLogService;
    private readonly TimeSpan _overlapDuration;
    private readonly int _maxParallelTranscriptions;
    private readonly Channel<LiveSegmentTranscriptionWorkItem> _queue =
        Channel.CreateUnbounded<LiveSegmentTranscriptionWorkItem>();
    private int _bufferedSegmentCount;
    private int _queuedRequestCount;
    private int _activeWorkerCount;

    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private string _model = string.Empty;
    private bool _hasStarted;
    private bool _isDisposed;

    public LiveSegmentTranscriptionSession(
        LiveRecordingSession recordingSession,
        IAudioTranscriptionService transcriptionService,
        ProcessLogService processLogService,
        TimeSpan? overlapDuration = null,
        int maxParallelTranscriptions = DefaultMaxParallelTranscriptions)
    {
        _recordingSession = recordingSession;
        _transcriptionService = transcriptionService;
        _processLogService = processLogService;
        _overlapDuration = overlapDuration.GetValueOrDefault(DefaultOverlapDuration);
        if (_overlapDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(overlapDuration), "Overlap duration must not be negative.");
        }

        if (maxParallelTranscriptions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxParallelTranscriptions), "Parallel transcription count must be at least one.");
        }

        _maxParallelTranscriptions = maxParallelTranscriptions;
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
        Log($"Live segment transcription session started using model '{_model}', maxParallel={_maxParallelTranscriptions:N0}.");
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
        var item = new LiveSegmentTranscriptionWorkItem(e.Segment, e.SegmentPath, DateTimeOffset.UtcNow);
        if (_queue.Writer.TryWrite(item))
        {
            int buffered = Interlocked.Increment(ref _bufferedSegmentCount);
            SegmentTranscriptionQueued?.Invoke(
                this,
                new LiveSegmentTranscriptionQueuedEventArgs(e.Segment, e.SegmentPath));
            Log(
                $"[queued] path='{Path.GetFileName(e.SegmentPath)}' start={FormatDuration(e.Segment.StartSeconds)} " +
                $"dur={e.Segment.DurationSeconds:F2}s bufferedSegments={buffered} queuedRequests={Volatile.Read(ref _queuedRequestCount)} activeWorkers={Volatile.Read(ref _activeWorkerCount)}");
        }
        else
        {
            Log($"Live segment transcription queue rejected '{e.SegmentPath}'.");
        }
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        var transcriptionQueue = Channel.CreateBounded<LiveSegmentTranscriptionRequest>(
            new BoundedChannelOptions(_maxParallelTranscriptions * 2)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true,
            });
        var completionQueue = Channel.CreateUnbounded<LiveSegmentTranscriptionOutcome>();
        Task requestBuilderTask = BuildTranscriptionRequestsAsync(transcriptionQueue.Writer, cancellationToken);
        Task[] transcriptionTasks = Enumerable
            .Range(0, _maxParallelTranscriptions)
            .Select(_ => RunTranscriptionWorkerAsync(transcriptionQueue.Reader, completionQueue.Writer, cancellationToken))
            .ToArray();
        Task completionTask = CompleteCompletionQueueAsync(
            requestBuilderTask,
            transcriptionTasks,
            transcriptionQueue.Writer,
            completionQueue.Writer);
        Task emitterTask = EmitCompletedTranscriptionsAsync(completionQueue.Reader, cancellationToken);
        await Task.WhenAll(completionTask, emitterTask).ConfigureAwait(false);
    }

    private async Task BuildTranscriptionRequestsAsync(
        ChannelWriter<LiveSegmentTranscriptionRequest> requestWriter,
        CancellationToken cancellationToken)
    {
        LiveSegmentTranscriptionWorkItem? pending = null;
        int sequence = 0;

        try
        {
            await foreach (LiveSegmentTranscriptionWorkItem item in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (pending is not null)
                {
                    int bufferedAfterDequeue = Interlocked.Decrement(ref _bufferedSegmentCount);
                    var request = new LiveSegmentTranscriptionRequest(
                        sequence++,
                        pending,
                        item,
                        DateTimeOffset.UtcNow,
                        bufferedAfterDequeue);
                    await requestWriter.WriteAsync(
                        request,
                        cancellationToken).ConfigureAwait(false);
                    int queuedRequests = Interlocked.Increment(ref _queuedRequestCount);
                    Log(
                        $"[request-queued] seq={request.Sequence} path='{Path.GetFileName(request.Item.SegmentPath)}' " +
                        $"waitBufferedMs={FormatMs(request.RequestQueuedAt - request.Item.QueuedAt)} " +
                        $"bufferedSegments={bufferedAfterDequeue} queuedRequests={queuedRequests} activeWorkers={Volatile.Read(ref _activeWorkerCount)}");
                }

                pending = item;
                Log(
                    $"[lookahead-hold] path='{Path.GetFileName(item.SegmentPath)}' waitingForNextSegment=true " +
                    $"holdMs={FormatMs(DateTimeOffset.UtcNow - item.QueuedAt)} bufferedSegments={Volatile.Read(ref _bufferedSegmentCount)}");
            }

            if (pending is not null)
            {
                int bufferedAfterDequeue = Interlocked.Decrement(ref _bufferedSegmentCount);
                var request = new LiveSegmentTranscriptionRequest(
                    sequence,
                    pending,
                    NextItem: null,
                    DateTimeOffset.UtcNow,
                    bufferedAfterDequeue);
                await requestWriter.WriteAsync(
                    request,
                    cancellationToken).ConfigureAwait(false);
                int queuedRequests = Interlocked.Increment(ref _queuedRequestCount);
                Log(
                    $"[request-queued-final] seq={request.Sequence} path='{Path.GetFileName(request.Item.SegmentPath)}' " +
                    $"waitBufferedMs={FormatMs(request.RequestQueuedAt - request.Item.QueuedAt)} " +
                    $"bufferedSegments={bufferedAfterDequeue} queuedRequests={queuedRequests} activeWorkers={Volatile.Read(ref _activeWorkerCount)}");
            }

            requestWriter.TryComplete();
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            requestWriter.TryComplete(ex);
            throw;
        }
        catch (Exception ex)
        {
            requestWriter.TryComplete(ex);
            throw;
        }
    }

    private async Task RunTranscriptionWorkerAsync(
        ChannelReader<LiveSegmentTranscriptionRequest> requestReader,
        ChannelWriter<LiveSegmentTranscriptionOutcome> completionWriter,
        CancellationToken cancellationToken)
    {
        await foreach (LiveSegmentTranscriptionRequest request in requestReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            int queuedRequests = Interlocked.Decrement(ref _queuedRequestCount);
            int activeWorkers = Interlocked.Increment(ref _activeWorkerCount);
            DateTimeOffset workerPickedAt = DateTimeOffset.UtcNow;
            Log(
                $"[worker-picked] seq={request.Sequence} path='{Path.GetFileName(request.Item.SegmentPath)}' " +
                $"requestQueueWaitMs={FormatMs(workerPickedAt - request.RequestQueuedAt)} " +
                $"endToStartMs={FormatMs(workerPickedAt - request.Item.QueuedAt)} " +
                $"queuedRequests={queuedRequests} activeWorkers={activeWorkers}");

            LiveSegmentTranscriptionOutcome outcome = await TranscribeWorkItemAsync(
                request.Item,
                request.NextItem,
                request.Sequence,
                cancellationToken).ConfigureAwait(false);
            await completionWriter.WriteAsync(outcome, cancellationToken).ConfigureAwait(false);

            int activeWorkersAfter = Interlocked.Decrement(ref _activeWorkerCount);
            Log(
                $"[worker-released] seq={request.Sequence} path='{Path.GetFileName(request.Item.SegmentPath)}' " +
                $"activeWorkers={activeWorkersAfter} queuedRequests={Volatile.Read(ref _queuedRequestCount)}");
        }
    }

    private static async Task CompleteCompletionQueueAsync(
        Task requestBuilderTask,
        IReadOnlyCollection<Task> transcriptionTasks,
        ChannelWriter<LiveSegmentTranscriptionRequest> requestWriter,
        ChannelWriter<LiveSegmentTranscriptionOutcome> completionWriter)
    {
        try
        {
            await requestBuilderTask.ConfigureAwait(false);
            await Task.WhenAll(transcriptionTasks).ConfigureAwait(false);
            completionWriter.TryComplete();
        }
        catch (Exception ex)
        {
            requestWriter.TryComplete(ex);
            completionWriter.TryComplete(ex);
            throw;
        }
    }

    private async Task EmitCompletedTranscriptionsAsync(
        ChannelReader<LiveSegmentTranscriptionOutcome> completionReader,
        CancellationToken cancellationToken)
    {
        var pendingOutcomes = new SortedDictionary<int, LiveSegmentTranscriptionOutcome>();
        int nextSequenceToEmit = 0;

        await foreach (LiveSegmentTranscriptionOutcome outcome in completionReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            pendingOutcomes[outcome.Sequence] = outcome;
            Log(
                $"[completion-buffered] seq={outcome.Sequence} waitingForEmit={pendingOutcomes.Count} " +
                $"transcribeMs={FormatMs(outcome.TranscriptionElapsed)} e2eMs={FormatMs(outcome.EndToEndElapsed)}");
            while (pendingOutcomes.Remove(nextSequenceToEmit, out LiveSegmentTranscriptionOutcome? nextOutcome))
            {
                EmitTranscriptionOutcome(nextOutcome);
                nextSequenceToEmit++;
            }
        }
    }

    private void EmitTranscriptionOutcome(LiveSegmentTranscriptionOutcome outcome)
    {
        if (outcome.Exception is not null)
        {
            Log(
                $"[emit-failed] seq={outcome.Sequence} path='{Path.GetFileName(outcome.Item.SegmentPath)}' " +
                $"bufferWaitMs={FormatMs(outcome.EmitReadyAt - outcome.TranscriptionFinishedAt)} " +
                $"transcribeMs={FormatMs(outcome.TranscriptionElapsed)} e2eMs={FormatMs(outcome.EndToEndElapsed)}");
            SegmentTranscriptionFailed?.Invoke(
                this,
                new LiveSegmentTranscriptionFailedEventArgs(outcome.Item.Segment, outcome.Item.SegmentPath, outcome.Exception));
            return;
        }

        if (outcome.Result is not null)
        {
            Log(
                $"[emit-complete] seq={outcome.Sequence} path='{Path.GetFileName(outcome.Item.SegmentPath)}' " +
                $"bufferWaitMs={FormatMs(outcome.EmitReadyAt - outcome.TranscriptionFinishedAt)} " +
                $"transcribeMs={FormatMs(outcome.TranscriptionElapsed)} e2eMs={FormatMs(outcome.EndToEndElapsed)}");
            SegmentTranscriptionCompleted?.Invoke(
                this,
                new LiveSegmentTranscriptionCompletedEventArgs(outcome.Item.Segment, outcome.Item.SegmentPath, outcome.Result));
        }
    }

    private async Task<LiveSegmentTranscriptionOutcome> TranscribeWorkItemAsync(
        LiveSegmentTranscriptionWorkItem item,
        LiveSegmentTranscriptionWorkItem? nextItem,
        int sequence,
        CancellationToken cancellationToken)
    {
        string requestPath = item.SegmentPath;
        bool deleteRequestPath = false;
        TimeSpan requestStart = TimeSpan.FromSeconds(item.Segment.StartSeconds);
        var transcriptionStopwatch = Stopwatch.StartNew();
        DateTimeOffset transcribeStartedAt = DateTimeOffset.UtcNow;

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
            transcriptionStopwatch.Stop();

            return new LiveSegmentTranscriptionOutcome(
                sequence,
                item,
                translated,
                Exception: null,
                transcribeStartedAt,
                DateTimeOffset.UtcNow,
                transcriptionStopwatch.Elapsed,
                DateTimeOffset.UtcNow - item.QueuedAt,
                DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            transcriptionStopwatch.Stop();
            Log(
                $"Live segment transcription failed for '{item.SegmentPath}': " +
                $"{ex.GetType().Name}: {ex.Message}.");
            return new LiveSegmentTranscriptionOutcome(
                sequence,
                item,
                Result: null,
                ex,
                transcribeStartedAt,
                DateTimeOffset.UtcNow,
                transcriptionStopwatch.Elapsed,
                DateTimeOffset.UtcNow - item.QueuedAt,
                DateTimeOffset.UtcNow);
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
                new AudioTranscriptionRequestOptions(SuppressPrompt: true, IsEngineWaveInput: true),
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

    private static string FormatMs(TimeSpan duration)
    {
        return duration.TotalMilliseconds.ToString("F0");
    }

    private void Log(string message)
    {
        _processLogService.Log("LiveSegmentTranscription", message);
    }

    private sealed record LiveSegmentTranscriptionWorkItem(
        LiveRecordingSegmentManifest Segment,
        string SegmentPath,
        DateTimeOffset QueuedAt);

    private sealed record LiveSegmentTranscriptionRequest(
        int Sequence,
        LiveSegmentTranscriptionWorkItem Item,
        LiveSegmentTranscriptionWorkItem? NextItem,
        DateTimeOffset RequestQueuedAt,
        int BufferedSegmentsAfterDequeue);

    private sealed record LiveSegmentTranscriptionOutcome(
        int Sequence,
        LiveSegmentTranscriptionWorkItem Item,
        TranscriptionResult? Result,
        Exception? Exception,
        DateTimeOffset TranscriptionStartedAt,
        DateTimeOffset TranscriptionFinishedAt,
        TimeSpan TranscriptionElapsed,
        TimeSpan EndToEndElapsed,
        DateTimeOffset EmitReadyAt);
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
