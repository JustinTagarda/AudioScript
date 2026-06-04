using System.IO;
using AudioScript.Abstractions;
using AudioScript.Audio;
using NAudio.Wave;

namespace AudioScript.Services;

public sealed class PlaybackTranscriptionSession : IAsyncDisposable {
    private readonly object _sync = new();
    private readonly IAudioLoopbackCaptureService _captureService;
    private readonly IPlaybackTranscriptionService _transcriptionService;
    private readonly ProcessLogService _processLogService;
    private readonly PlaybackTranscriptionSessionOptions _options;
    private readonly LiveRecordingSession? _liveRecordingSession;
    private readonly MemoryStream _pendingPcm = new();

    private CancellationTokenSource? _processingCts;
    private Task? _processingTask;
    private WaveFormat? _captureFormat;
    private Exception? _captureFault;
    private string _model = string.Empty;
    private string _lastInterimText = string.Empty;
    private bool _hasStarted;
    private bool _stopRequested;
    private bool _isRunning;
    private bool _isDisposed;
    private long _pendingBytesAtLastInterim;
    private int _interimSequenceIndex;
    private int _finalSequenceIndex;
    private DateTimeOffset _nextCaptureStatsLogUtc;
    private long _captureStatsFrames;
    private long _captureStatsBytes;
    private double _captureStatsPeak;
    private readonly Dictionary<string, SourceCaptureStats> _sourceCaptureStats = new(StringComparer.OrdinalIgnoreCase);
    private long _capturedAudioBytes;
    private long _droppedAudioBytes;

    public PlaybackTranscriptionSession(
        IAudioLoopbackCaptureService captureService,
        IPlaybackTranscriptionService transcriptionService,
        ProcessLogService processLogService,
        PlaybackTranscriptionSessionOptions? options = null,
        LiveRecordingSession? liveRecordingSession = null) {
        _captureService = captureService;
        _transcriptionService = transcriptionService;
        _processLogService = processLogService;
        _options = (options ?? PlaybackTranscriptionSessionOptions.Default).Validate();
        _liveRecordingSession = liveRecordingSession;
    }

    public event EventHandler<PlaybackTranscriptionUpdate>? PlaybackInterimTranscriptionUpdated;
    public event EventHandler<PlaybackTranscriptionUpdate>? PlaybackFinalTranscriptionAvailable;
    public event EventHandler<PlaybackAudioLevelChangedEventArgs>? AudioLevelChanged;
    public event EventHandler<Exception>? Faulted;

    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    public bool IsRunning {
        get {
            lock (_sync) {
                return _isRunning;
            }
        }
    }

    public void StartPlaybackTranscription(string model) {
        ThrowIfDisposed();

        string validatedModel = ValidateModel(model);

        lock (_sync) {
            if (_hasStarted) {
                throw new InvalidOperationException("Playback transcription sessions are single-use. Create a new session for each run.");
            }

            _hasStarted = true;
            _stopRequested = false;
            _isRunning = true;
            _captureFault = null;
            _captureFormat = null;
            _pendingBytesAtLastInterim = 0;
            _interimSequenceIndex = 0;
            _finalSequenceIndex = 0;
            _lastInterimText = string.Empty;
            _model = validatedModel;
            _pendingPcm.SetLength(0);
            _nextCaptureStatsLogUtc = DateTimeOffset.UtcNow.AddSeconds(2);
            _captureStatsFrames = 0;
            _captureStatsBytes = 0;
            _captureStatsPeak = 0;
            _sourceCaptureStats.Clear();
            _capturedAudioBytes = 0;
            _droppedAudioBytes = 0;
        }

        _processingCts = new CancellationTokenSource();
        _captureService.AudioFrameCaptured += OnAudioFrameCaptured;
        _captureService.CaptureFaulted += OnCaptureFaulted;
        if (_liveRecordingSession is not null) {
            _liveRecordingSession.Faulted += OnLiveRecordingFaulted;
        }

        try {
            _liveRecordingSession?.Start();
            _captureService.StartCapture();
            _processingTask = Task.Run(() => RunProcessingLoopAsync(_processingCts.Token));
            Log(
                $"Playback transcription session '{SessionId}' started using model '{validatedModel}'. " +
                $"recordingAttached={_liveRecordingSession is not null}, pollInterval='{_options.PollInterval}', " +
                $"interimWindow='{_options.InterimWindowDuration}', finalWindow='{_options.FinalWindowDuration}', " +
                $"minimumSegment='{_options.MinimumSegmentDuration}', interimCadence='{_options.InterimCadence}', " +
                $"minimumPeak={_options.MinimumPeakLevel:0.0000}.");
        }
        catch {
            StopCaptureCore();
            if (_liveRecordingSession is not null) {
                _liveRecordingSession.Faulted -= OnLiveRecordingFaulted;
            }
            _processingCts.Dispose();
            _processingCts = null;

            lock (_sync) {
                _stopRequested = true;
                _isRunning = false;
            }

            throw;
        }
    }

    public async Task StopPlaybackTranscriptionAsync(CancellationToken cancellationToken = default) {
        if (_isDisposed) {
            return;
        }

        CancellationTokenSource? processingCts;
        Task? processingTask;
        bool shouldStop;

        lock (_sync) {
            shouldStop = _hasStarted;
            _stopRequested = true;
            _isRunning = false;
            processingCts = _processingCts;
            processingTask = _processingTask;
        }

        if (!shouldStop) {
            return;
        }

        Log(
            $"Stopping playback transcription session '{SessionId}'. " +
            $"capturedAudioBytes={_capturedAudioBytes:N0}, droppedAudioBytes={_droppedAudioBytes:N0}, " +
            $"pendingBytes={GetPendingByteCount():N0}.");
        StopCaptureCore();
        if (_liveRecordingSession is not null) {
            _liveRecordingSession.Faulted -= OnLiveRecordingFaulted;
        }

        try {
            if (processingTask is not null) {
                await processingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally {
            processingCts?.Dispose();

            lock (_sync) {
                if (ReferenceEquals(_processingCts, processingCts)) {
                    _processingCts = null;
                }

                if (ReferenceEquals(_processingTask, processingTask)) {
                    _processingTask = null;
                }
            }
        }

        Log(
            $"Playback transcription session '{SessionId}' stopped. " +
            $"capturedAudioBytes={_capturedAudioBytes:N0}, droppedAudioBytes={_droppedAudioBytes:N0}, " +
            $"pendingBytes={GetPendingByteCount():N0}.");
    }

    public async ValueTask DisposeAsync() {
        if (_isDisposed) {
            return;
        }

        try {
            await StopPlaybackTranscriptionAsync().ConfigureAwait(false);
        }
        catch {
            // Best-effort shutdown during disposal.
        }
        finally {
            _isDisposed = true;
            _captureService.Dispose();
            if (_liveRecordingSession is not null) {
                await _liveRecordingSession.DisposeAsync().ConfigureAwait(false);
            }
            _pendingPcm.Dispose();
        }
    }

    private async Task RunProcessingLoopAsync(CancellationToken cancellationToken) {
        using var timer = new PeriodicTimer(_options.PollInterval);

        try {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) {
                ThrowIfCaptureFaulted();

                if (IsStopRequested()) {
                    await FlushPendingAudioAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                PendingTranscriptionRequest? request = TryCreateNextRegularRequest();
                if (request is null) {
                    continue;
                }

                await ExecuteRequestAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            // Session shutdown canceled the worker.
        }
        catch (Exception ex) {
            LogError(
                $"Playback transcription session '{SessionId}' failed: {ex.GetType().Name}: {ex.Message}. " +
                $"pendingBytes={GetPendingByteCount():N0}, capturedAudioBytes={_capturedAudioBytes:N0}, " +
                $"droppedAudioBytes={_droppedAudioBytes:N0}.");
            Faulted?.Invoke(this, ex);
            StopCaptureCore();
            throw;
        }
        finally {
            lock (_sync) {
                _isRunning = false;
            }
        }
    }

    private async Task FlushPendingAudioAsync(CancellationToken cancellationToken) {
        while (true) {
            PendingTranscriptionRequest? request = TryCreateNextFinalFlushRequest();
            if (request is null) {
                return;
            }

            await ExecuteRequestAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteRequestAsync(PendingTranscriptionRequest request, CancellationToken cancellationToken) {
        double requestDurationSeconds = request.PcmBytes.Length / (double)Math.Max(request.SourceFormat.AverageBytesPerSecond, 1);
        string route = request.IsFinal
            ? $"playback.final:{SessionId}#{request.SequenceIndex:N0}"
            : $"playback.interim:{SessionId}#{request.SequenceIndex:N0}";
        LogDebug(
            $"Dispatching {(request.IsFinal ? "final" : "interim")} transcription request route='{route}', seq={request.SequenceIndex:N0}, " +
            $"bytes={request.PcmBytes.Length:N0}, duration={requestDurationSeconds:0.###}s, " +
            $"pendingBytesAfterDequeue={GetPendingByteCount():N0}.");
        string text = await _transcriptionService.TranscribePcmChunkAsync(
            request.PcmBytes,
            request.SourceFormat,
            _model,
            cancellationToken,
            route).ConfigureAwait(false);

        if (request.IsFinal) {
            EmitFinal(request.SequenceIndex, text);
        }
        else {
            EmitInterim(request.SequenceIndex, text);
        }
    }

    private PendingTranscriptionRequest? TryCreateNextRegularRequest() {
        lock (_sync) {
            if (_captureFormat is null || _pendingPcm.Length <= 0) {
                return null;
            }

            long pendingBytes = _pendingPcm.Length;
            long finalWindowBytes = GetAlignedByteCount(_options.FinalWindowDuration, _captureFormat);

            if (pendingBytes >= finalWindowBytes) {
                byte[] finalChunk = DequeuePendingBytes(finalWindowBytes);
                if (!IsChunkAboveThreshold(finalChunk, out double peak)) {
                    _droppedAudioBytes += finalChunk.Length;
                    Log(
                        $"Dropping final playback window below peak threshold " +
                        $"(peak={peak:0.0000}, threshold={_options.MinimumPeakLevel:0.0000}, bytes={finalChunk.Length:N0}).");
                    _pendingBytesAtLastInterim = 0;
                    _lastInterimText = string.Empty;
                    return null;
                }
                _pendingBytesAtLastInterim = 0;
                _lastInterimText = string.Empty;

                return new PendingTranscriptionRequest(
                    IsFinal: true,
                    SequenceIndex: _finalSequenceIndex++,
                    PcmBytes: finalChunk,
                    SourceFormat: _captureFormat);
            }

            long interimWindowBytes = GetAlignedByteCount(_options.InterimWindowDuration, _captureFormat);
            long interimCadenceBytes = GetAlignedByteCount(_options.InterimCadence, _captureFormat);

            if (pendingBytes >= interimWindowBytes
                && pendingBytes - _pendingBytesAtLastInterim >= interimCadenceBytes) {
                byte[] snapshot = SnapshotPendingBytes();
                if (!IsChunkAboveThreshold(snapshot, out double peak)) {
                    _pendingBytesAtLastInterim = pendingBytes;
                    Log(
                        $"Suppressing interim playback window below peak threshold " +
                        $"(peak={peak:0.0000}, threshold={_options.MinimumPeakLevel:0.0000}, bytes={snapshot.Length:N0}).");
                    return null;
                }

                _pendingBytesAtLastInterim = pendingBytes;

                return new PendingTranscriptionRequest(
                    IsFinal: false,
                    SequenceIndex: _interimSequenceIndex++,
                    PcmBytes: snapshot,
                    SourceFormat: _captureFormat);
            }

            return null;
        }
    }

    private PendingTranscriptionRequest? TryCreateNextFinalFlushRequest() {
        lock (_sync) {
            if (_captureFormat is null || _pendingPcm.Length <= 0) {
                return null;
            }

            long pendingBytes = _pendingPcm.Length;
            long finalWindowBytes = GetAlignedByteCount(_options.FinalWindowDuration, _captureFormat);

            if (pendingBytes >= finalWindowBytes) {
                byte[] finalChunk = DequeuePendingBytes(finalWindowBytes);
                if (!IsChunkAboveThreshold(finalChunk, out double peak)) {
                    _droppedAudioBytes += finalChunk.Length;
                    Log(
                        $"Dropping trailing playback final window below peak threshold " +
                        $"(peak={peak:0.0000}, threshold={_options.MinimumPeakLevel:0.0000}, bytes={finalChunk.Length:N0}).");
                    _pendingBytesAtLastInterim = 0;
                    _lastInterimText = string.Empty;
                    return null;
                }
                _pendingBytesAtLastInterim = 0;
                _lastInterimText = string.Empty;

                return new PendingTranscriptionRequest(
                    IsFinal: true,
                    SequenceIndex: _finalSequenceIndex++,
                    PcmBytes: finalChunk,
                    SourceFormat: _captureFormat);
            }

            byte[] trailingChunk = DequeuePendingBytes(pendingBytes);
            if (!IsChunkAboveThreshold(trailingChunk, out double trailingPeak)) {
                _droppedAudioBytes += trailingChunk.Length;
                Log(
                    $"Dropping trailing playback audio below peak threshold " +
                    $"(peak={trailingPeak:0.0000}, threshold={_options.MinimumPeakLevel:0.0000}, bytes={trailingChunk.Length:N0}).");
                _pendingBytesAtLastInterim = 0;
                _lastInterimText = string.Empty;
                return null;
            }

            _pendingBytesAtLastInterim = 0;
            _lastInterimText = string.Empty;

            return new PendingTranscriptionRequest(
                IsFinal: true,
                SequenceIndex: _finalSequenceIndex++,
                PcmBytes: trailingChunk,
                SourceFormat: _captureFormat);
        }
    }

    private void OnAudioFrameCaptured(object? sender, LoopbackAudioFrameEventArgs e) {
        double peak;

        lock (_sync) {
            if (_stopRequested) {
                return;
            }

            if (_captureFormat is null) {
                _captureFormat = e.WaveFormat;
            }
            else if (!WaveFormatsMatch(_captureFormat, e.WaveFormat)) {
                _captureFault ??= new InvalidOperationException("Playback audio capture format changed during transcription.");
                return;
            }

            _pendingPcm.Position = _pendingPcm.Length;
            _pendingPcm.Write(e.Buffer, 0, e.BytesRecorded);
            _capturedAudioBytes += e.BytesRecorded;
            peak = TrackCaptureStats(e);
        }

        _liveRecordingSession?.WriteFrame(e);
        AudioLevelChanged?.Invoke(
            this,
            new PlaybackAudioLevelChangedEventArgs(
                SessionId,
                peak,
                e.SourceName,
                e.AppliedGain,
                e.AutomaticGainApplied));
    }

    private void OnLiveRecordingFaulted(object? sender, Exception ex) {
        LogWarning(
            $"Live recording failed while transcription continued for playback session '{SessionId}': " +
            $"{ex.GetType().Name}: {ex.Message}. pendingBytes={GetPendingByteCount():N0}.");
    }

    private double TrackCaptureStats(LoopbackAudioFrameEventArgs e) {
        double peak = CalculatePcm16Peak(e.Buffer);
        string sourceName = string.IsNullOrWhiteSpace(e.SourceName) ? "Unknown" : e.SourceName;

        _captureStatsFrames++;
        _captureStatsBytes += e.BytesRecorded;
        _captureStatsPeak = Math.Max(_captureStatsPeak, peak);

        if (!_sourceCaptureStats.TryGetValue(sourceName, out SourceCaptureStats? sourceStats)) {
            sourceStats = new SourceCaptureStats();
            _sourceCaptureStats[sourceName] = sourceStats;
        }

        sourceStats.Frames++;
        sourceStats.Bytes += e.BytesRecorded;
        sourceStats.Peak = Math.Max(sourceStats.Peak, peak);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now < _nextCaptureStatsLogUtc) {
            return peak;
        }

        string sourceSummary = string.Join(
            "; ",
            _sourceCaptureStats
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => $"{item.Key}:frames={item.Value.Frames:N0},bytes={item.Value.Bytes:N0},peak={item.Value.Peak:0.0000}"));
        Log(
            $"Capture stats frames={_captureStatsFrames:N0}, bytes={_captureStatsBytes:N0}, peak={_captureStatsPeak:0.0000}, pendingBytes={_pendingPcm.Length:N0}, sources=[{sourceSummary}]");

        _captureStatsFrames = 0;
        _captureStatsBytes = 0;
        _captureStatsPeak = 0;
        _sourceCaptureStats.Clear();
        _nextCaptureStatsLogUtc = now.AddSeconds(2);

        return peak;
    }

    private void OnCaptureFaulted(object? sender, Exception ex) {
        lock (_sync) {
            _captureFault ??= ex;
            _stopRequested = true;
            _isRunning = false;
        }
        LogError(
            $"Capture fault received for playback session '{SessionId}': {ex.GetType().Name}: {ex.Message}. " +
            $"pendingBytes={GetPendingByteCount():N0}, capturedAudioBytes={_capturedAudioBytes:N0}.");
    }

    private bool IsStopRequested() {
        lock (_sync) {
            return _stopRequested;
        }
    }

    private void ThrowIfCaptureFaulted() {
        Exception? captureFault;

        lock (_sync) {
            captureFault = _captureFault;
        }

        if (captureFault is not null) {
            throw new InvalidOperationException("Playback audio capture failed.", captureFault);
        }
    }

    private void EmitInterim(int sequenceIndex, string text) {
        string normalized = text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized)) {
            return;
        }

        bool shouldEmit;

        lock (_sync) {
            shouldEmit = !string.Equals(_lastInterimText, normalized, StringComparison.Ordinal);
            if (shouldEmit) {
                _lastInterimText = normalized;
            }
        }

        if (!shouldEmit) {
            return;
        }

        Log($"Playback interim update {sequenceIndex} emitted ({normalized.Length:N0} chars).");
        PlaybackInterimTranscriptionUpdated?.Invoke(
            this,
            new PlaybackTranscriptionUpdate(SessionId, normalized, sequenceIndex));
    }

    private void EmitFinal(int sequenceIndex, string text) {
        string normalized = text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized)) {
            return;
        }

        lock (_sync) {
            _lastInterimText = string.Empty;
        }

        Log($"Playback final update {sequenceIndex} emitted ({normalized.Length:N0} chars).");
        PlaybackFinalTranscriptionAvailable?.Invoke(
            this,
            new PlaybackTranscriptionUpdate(SessionId, normalized, sequenceIndex));
    }

    private byte[] SnapshotPendingBytes() {
        return _pendingPcm.ToArray();
    }

    private byte[] DequeuePendingBytes(long bytesToRemove) {
        byte[] current = _pendingPcm.ToArray();
        int removalLength = (int)Math.Min(bytesToRemove, current.LongLength);
        byte[] head = current.AsSpan(0, removalLength).ToArray();
        byte[] tail = current.AsSpan(removalLength).ToArray();

        _pendingPcm.SetLength(0);
        _pendingPcm.Position = 0;
        if (tail.Length > 0) {
            _pendingPcm.Write(tail, 0, tail.Length);
        }

        return head;
    }

    private void StopCaptureCore() {
        _captureService.AudioFrameCaptured -= OnAudioFrameCaptured;
        _captureService.CaptureFaulted -= OnCaptureFaulted;

        try {
            _captureService.StopCapture();
        }
        catch (Exception ex) {
            LogWarning($"Playback audio capture stop failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private long GetPendingByteCount() {
        lock (_sync) {
            return _pendingPcm.Length;
        }
    }

    private static long GetAlignedByteCount(TimeSpan duration, WaveFormat waveFormat) {
        long rawByteCount = (long)Math.Ceiling(duration.TotalSeconds * Math.Max(waveFormat.AverageBytesPerSecond, 1));
        long blockAlign = Math.Max(waveFormat.BlockAlign, 1);
        long aligned = rawByteCount - (rawByteCount % blockAlign);
        return aligned > 0 ? aligned : blockAlign;
    }

    private static bool WaveFormatsMatch(WaveFormat left, WaveFormat right) {
        return left.Encoding == right.Encoding
            && left.SampleRate == right.SampleRate
            && left.BitsPerSample == right.BitsPerSample
            && left.Channels == right.Channels
            && left.BlockAlign == right.BlockAlign
            && left.AverageBytesPerSecond == right.AverageBytesPerSecond;
    }

    private static double CalculatePcm16Peak(byte[] buffer) {
        if (buffer.Length < 2) {
            return 0;
        }

        short peak = 0;
        int length = buffer.Length - (buffer.Length % 2);
        for (int index = 0; index < length; index += 2) {
            short sample = BitConverter.ToInt16(buffer, index);
            short magnitude = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
            if (magnitude > peak) {
                peak = magnitude;
            }
        }

        return peak / (double)short.MaxValue;
    }

    private bool IsChunkAboveThreshold(byte[] pcmBytes, out double peak) {
        peak = CalculatePcm16Peak(pcmBytes);
        return _options.MinimumPeakLevel <= 0 || peak >= _options.MinimumPeakLevel;
    }

    private sealed class SourceCaptureStats {
        public long Frames { get; set; }

        public long Bytes { get; set; }

        public double Peak { get; set; }
    }

    private static string ValidateModel(string model) {
        string trimmed = model?.Trim() ?? string.Empty;

        if (!TranscriptionModelCatalog.SupportsPlaybackTranscription(trimmed)) {
            throw new InvalidOperationException(
                $"Unsupported playback transcription model '{model}'. " +
                "Use an installed local Whisper model.");
        }

        return trimmed;
    }

    private void Log(string message) {
        _processLogService.Log("PlaybackSession", message);
    }

    private void LogDebug(string message) {
        _processLogService.Log("PlaybackSession", message, ProcessLogLevel.Debug);
    }

    private void LogWarning(string message) {
        _processLogService.Log("PlaybackSession", message, ProcessLogLevel.Warning);
    }

    private void LogError(string message) {
        _processLogService.Log("PlaybackSession", message, ProcessLogLevel.Error);
    }

    private void ThrowIfDisposed() {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    private sealed record PendingTranscriptionRequest(
        bool IsFinal,
        int SequenceIndex,
        byte[] PcmBytes,
        WaveFormat SourceFormat
    );
}




