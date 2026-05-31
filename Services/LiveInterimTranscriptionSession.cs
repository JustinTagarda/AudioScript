using System.IO;
using AudioScript.Abstractions;
using NAudio.Wave;

namespace AudioScript.Services;

public sealed class LiveInterimTranscriptionSession : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly IAudioTranscriptionService _transcriptionService;
    private readonly ProcessLogService _processLogService;
    private readonly LiveInterimTranscriptionOptions _options;
    private readonly MemoryStream _pendingPcm = new();
    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private WaveFormat? _captureFormat;
    private string _model = string.Empty;
    private string _lastInterimText = string.Empty;
    private int _interimSequenceIndex;
    private bool _hasStarted;
    private bool _isDisposed;
    private TimeSpan _capturedDuration;

    public LiveInterimTranscriptionSession(
        IAudioTranscriptionService transcriptionService,
        ProcessLogService processLogService,
        LiveInterimTranscriptionOptions? options = null)
    {
        _transcriptionService = transcriptionService;
        _processLogService = processLogService;
        _options = (options ?? LiveInterimTranscriptionOptions.Default).Validate();
    }

    public event EventHandler<LiveInterimTranscriptionUpdatedEventArgs>? InterimUpdated;
    public event EventHandler<Exception>? Faulted;

    public void Start(string model)
    {
        ThrowIfDisposed();

        if (_hasStarted)
        {
            throw new InvalidOperationException("Live interim transcription sessions are single-use.");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("A transcription model is required.", nameof(model));
        }

        _model = model.Trim();
        _hasStarted = true;
        _cts = new CancellationTokenSource();
        _workerTask = Task.Run(() => RunWorkerAsync(_cts.Token));
        Log($"Live interim transcription session started using model '{_model}'.");
    }

    public void AddAudioFrame(byte[] buffer, WaveFormat waveFormat)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(waveFormat);
        if (buffer.Length == 0)
        {
            return;
        }

        lock (_sync)
        {
            if (!_hasStarted)
            {
                return;
            }

            if (_captureFormat is null)
            {
                _captureFormat = waveFormat;
            }
            else if (!WaveFormatsMatch(_captureFormat, waveFormat))
            {
                throw new InvalidOperationException("Live interim transcription received a mismatched wave format.");
            }

            _pendingPcm.Position = _pendingPcm.Length;
            _pendingPcm.Write(buffer, 0, buffer.Length);
            _capturedDuration += TimeSpan.FromSeconds(buffer.Length / (double)Math.Max(waveFormat.AverageBytesPerSecond, 1));
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_hasStarted)
        {
            return;
        }

        Task? workerTask = _workerTask;
        if (workerTask is not null)
        {
            await workerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        _cts?.Dispose();
        _cts = null;
        _workerTask = null;
        Log("Live interim transcription session stopped.");
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
            _pendingPcm.Dispose();
            _isDisposed = true;
        }
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.PollInterval);
        TimeSpan lastEmittedAt = TimeSpan.Zero;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                InterimSnapshot? snapshot = TryCreateInterimSnapshot(lastEmittedAt);
                if (snapshot is null)
                {
                    continue;
                }

                string text = await TranscribeSnapshotAsync(snapshot.Value, cancellationToken).ConfigureAwait(false);
                string normalized = text.Trim();
                if (normalized.Length == 0)
                {
                    continue;
                }

                bool shouldEmit;
                int sequenceIndex;
                lock (_sync)
                {
                    shouldEmit = !string.Equals(_lastInterimText, normalized, StringComparison.Ordinal);
                    if (!shouldEmit)
                    {
                        continue;
                    }

                    _lastInterimText = normalized;
                    _interimSequenceIndex++;
                    sequenceIndex = _interimSequenceIndex;
                    lastEmittedAt = snapshot.Value.CapturedDuration;
                }

                InterimUpdated?.Invoke(
                    this,
                    new LiveInterimTranscriptionUpdatedEventArgs(
                        normalized,
                        sequenceIndex,
                        snapshot.Value.StartOffset,
                        snapshot.Value.EndOffset));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            Log($"Live interim transcription failed: {ex.GetType().Name}: {ex.Message}.");
            Faulted?.Invoke(this, ex);
            throw;
        }
    }

    private InterimSnapshot? TryCreateInterimSnapshot(TimeSpan lastEmittedAt)
    {
        lock (_sync)
        {
            if (_captureFormat is null || _pendingPcm.Length == 0)
            {
                return null;
            }

            TimeSpan sinceLastEmit = _capturedDuration - lastEmittedAt;
            if (sinceLastEmit < _options.InterimCadence)
            {
                return null;
            }

            long windowBytes = GetAlignedByteCount(_options.InterimWindowDuration, _captureFormat);
            byte[] allBytes = _pendingPcm.ToArray();
            int offset = Math.Max(0, allBytes.Length - (int)Math.Min(windowBytes, allBytes.LongLength));
            int count = allBytes.Length - offset;
            if (count <= 0)
            {
                return null;
            }

            byte[] window = new byte[count];
            Buffer.BlockCopy(allBytes, offset, window, 0, count);
            if (!IsChunkAboveThreshold(window, _options.MinimumPeakLevel))
            {
                return null;
            }

            TimeSpan windowDuration = TimeSpan.FromSeconds(count / (double)Math.Max(_captureFormat.AverageBytesPerSecond, 1));
            TimeSpan end = _capturedDuration;
            TimeSpan start = end - windowDuration;
            if (start < TimeSpan.Zero)
            {
                start = TimeSpan.Zero;
            }

            return new InterimSnapshot(window, _captureFormat, start, end, _capturedDuration);
        }
    }

    private async Task<string> TranscribeSnapshotAsync(InterimSnapshot snapshot, CancellationToken cancellationToken)
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(),
            $"AudioScript-live-interim-{Guid.NewGuid():N}.wav");

        try
        {
            using (var writer = new WaveFileWriter(tempPath, snapshot.Format))
            {
                writer.Write(snapshot.PcmBytes, 0, snapshot.PcmBytes.Length);
            }

            TranscriptionResult result = await TranscribeWithOptionsAsync(tempPath, cancellationToken).ConfigureAwait(false);
            return result.Text ?? string.Empty;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private Task<TranscriptionResult> TranscribeWithOptionsAsync(
        string audioFilePath,
        CancellationToken cancellationToken)
    {
        if (_transcriptionService is IConfigurableAudioTranscriptionService configurable)
        {
            return configurable.TranscribeAudioFileAsync(
                audioFilePath,
                _model,
                new AudioTranscriptionRequestOptions(SuppressPrompt: true),
                cancellationToken);
        }

        return _transcriptionService.TranscribeAudioFileAsync(
            audioFilePath,
            _model,
            cancellationToken);
    }

    private void Log(string message)
    {
        _processLogService.Log("LiveInterimTranscription", message);
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(LiveInterimTranscriptionSession));
        }
    }

    private static long GetAlignedByteCount(TimeSpan duration, WaveFormat waveFormat)
    {
        long rawByteCount = (long)Math.Ceiling(duration.TotalSeconds * Math.Max(waveFormat.AverageBytesPerSecond, 1));
        long blockAlign = Math.Max(waveFormat.BlockAlign, 1);
        long aligned = rawByteCount - (rawByteCount % blockAlign);
        return aligned > 0 ? aligned : blockAlign;
    }

    private static bool IsChunkAboveThreshold(byte[] pcmBytes, double minimumPeakLevel)
    {
        if (minimumPeakLevel <= 0)
        {
            return true;
        }

        return CalculatePcm16Peak(pcmBytes) >= minimumPeakLevel;
    }

    private static double CalculatePcm16Peak(byte[] buffer)
    {
        if (buffer.Length < 2)
        {
            return 0;
        }

        short peak = 0;
        int length = buffer.Length - (buffer.Length % 2);
        for (int index = 0; index < length; index += 2)
        {
            short sample = BitConverter.ToInt16(buffer, index);
            short magnitude = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        return peak / (double)short.MaxValue;
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

    private static void TryDelete(string filePath)
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
            // Best-effort cleanup.
        }
    }

    private readonly record struct InterimSnapshot(
        byte[] PcmBytes,
        WaveFormat Format,
        TimeSpan StartOffset,
        TimeSpan EndOffset,
        TimeSpan CapturedDuration);
}

public sealed class LiveInterimTranscriptionUpdatedEventArgs : EventArgs
{
    public LiveInterimTranscriptionUpdatedEventArgs(
        string text,
        int sequenceIndex,
        TimeSpan startOffset,
        TimeSpan endOffset)
    {
        Text = text;
        SequenceIndex = sequenceIndex;
        StartOffset = startOffset;
        EndOffset = endOffset;
    }

    public string Text { get; }

    public int SequenceIndex { get; }

    public TimeSpan StartOffset { get; }

    public TimeSpan EndOffset { get; }
}

public sealed record LiveInterimTranscriptionOptions(
    TimeSpan InterimWindowDuration,
    TimeSpan InterimCadence,
    TimeSpan PollInterval,
    double MinimumPeakLevel = 0)
{
    public static LiveInterimTranscriptionOptions Default { get; } = new(
        InterimWindowDuration: TimeSpan.FromSeconds(4),
        InterimCadence: TimeSpan.FromSeconds(1.5),
        PollInterval: TimeSpan.FromMilliseconds(200),
        MinimumPeakLevel: 0);

    public LiveInterimTranscriptionOptions Validate()
    {
        if (InterimWindowDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Live interim window duration must be greater than zero.");
        }

        if (InterimCadence <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Live interim cadence must be greater than zero.");
        }

        if (PollInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Live interim poll interval must be greater than zero.");
        }

        if (MinimumPeakLevel < 0 || MinimumPeakLevel > 1)
        {
            throw new InvalidOperationException("Live interim minimum peak level must be between 0 and 1.");
        }

        return this;
    }
}
