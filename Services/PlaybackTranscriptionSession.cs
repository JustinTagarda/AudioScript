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

    public PlaybackTranscriptionSession(
        IAudioLoopbackCaptureService captureService,
        IPlaybackTranscriptionService transcriptionService,
        ProcessLogService processLogService,
        PlaybackTranscriptionSessionOptions? options = null) {
        _captureService = captureService;
        _transcriptionService = transcriptionService;
        _processLogService = processLogService;
        _options = (options ?? PlaybackTranscriptionSessionOptions.Default).Validate();
    }

    public event EventHandler<PlaybackTranscriptionUpdate>? PlaybackInterimTranscriptionUpdated;
    public event EventHandler<PlaybackTranscriptionUpdate>? PlaybackFinalTranscriptionAvailable;
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
        }

        _processingCts = new CancellationTokenSource();
        _captureService.AudioFrameCaptured += OnAudioFrameCaptured;
        _captureService.CaptureFaulted += OnCaptureFaulted;

        try {
            _captureService.StartCapture();
            _processingTask = Task.Run(() => RunProcessingLoopAsync(_processingCts.Token));
            Log($"Playback transcription session '{SessionId}' started using model '{validatedModel}'.");
        }
        catch {
            StopCaptureCore();
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

        Log($"Stopping playback transcription session '{SessionId}'.");
        StopCaptureCore();

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

        Log($"Playback transcription session '{SessionId}' stopped.");
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
            Log($"Playback transcription session '{SessionId}' failed: {ex.Message}");
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
        string text = await _transcriptionService.TranscribePcmChunkAsync(
            request.PcmBytes,
            request.SourceFormat,
            _model,
            cancellationToken).ConfigureAwait(false);

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
                _pendingBytesAtLastInterim = pendingBytes;

                return new PendingTranscriptionRequest(
                    IsFinal: false,
                    SequenceIndex: _interimSequenceIndex++,
                    PcmBytes: SnapshotPendingBytes(),
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
            long minimumSegmentBytes = GetAlignedByteCount(_options.MinimumSegmentDuration, _captureFormat);

            if (pendingBytes >= finalWindowBytes) {
                byte[] finalChunk = DequeuePendingBytes(finalWindowBytes);
                _pendingBytesAtLastInterim = 0;
                _lastInterimText = string.Empty;

                return new PendingTranscriptionRequest(
                    IsFinal: true,
                    SequenceIndex: _finalSequenceIndex++,
                    PcmBytes: finalChunk,
                    SourceFormat: _captureFormat);
            }

            if (pendingBytes >= minimumSegmentBytes) {
                byte[] finalChunk = DequeuePendingBytes(pendingBytes);
                _pendingBytesAtLastInterim = 0;
                _lastInterimText = string.Empty;

                return new PendingTranscriptionRequest(
                    IsFinal: true,
                    SequenceIndex: _finalSequenceIndex++,
                    PcmBytes: finalChunk,
                    SourceFormat: _captureFormat);
            }

            Log(
                $"Dropping trailing playback audio shorter than the minimum segment duration " +
                $"({_pendingPcm.Length} bytes).");
            _pendingPcm.SetLength(0);
            _pendingBytesAtLastInterim = 0;
            _lastInterimText = string.Empty;
            return null;
        }
    }

    private void OnAudioFrameCaptured(object? sender, LoopbackAudioFrameEventArgs e) {
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
        }
    }

    private void OnCaptureFaulted(object? sender, Exception ex) {
        lock (_sync) {
            _captureFault ??= ex;
            _stopRequested = true;
            _isRunning = false;
        }
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
            Log($"Playback audio capture stop failed: {ex.Message}");
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

    private static string ValidateModel(string model) {
        string trimmed = model?.Trim() ?? string.Empty;

        if (!OpenAiTranscriptionModelCatalog.IsSupported(trimmed)) {
            throw new InvalidOperationException(
                $"Unsupported playback transcription model '{model}'. " +
                $"Use '{OpenAiTranscriptionModelCatalog.Gpt4oTranscribe}' or '{OpenAiTranscriptionModelCatalog.Gpt4oMiniTranscribe}'.");
        }

        return trimmed;
    }

    private void Log(string message) {
        _processLogService.Log("PlaybackSession", message);
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




