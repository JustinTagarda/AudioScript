using AudioScript.Audio;
using NAudio.Wave;

namespace AudioScript.Services;

public sealed class LiveRecordingCaptureSession : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly IAudioLoopbackCaptureService _captureService;
    private readonly LiveRecordingSession _recordingSession;
    private readonly ProcessLogService _processLogService;
    private WaveFormat? _captureFormat;
    private bool _hasStarted;
    private bool _isRunning;
    private bool _isDisposed;

    public LiveRecordingCaptureSession(
        IAudioLoopbackCaptureService captureService,
        LiveRecordingSession recordingSession,
        ProcessLogService processLogService)
    {
        _captureService = captureService;
        _recordingSession = recordingSession;
        _processLogService = processLogService;
    }

    public event EventHandler<PlaybackAudioLevelChangedEventArgs>? AudioLevelChanged;
    public event EventHandler<Exception>? Faulted;

    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _isRunning;
            }
        }
    }

    public void Start()
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            if (_hasStarted)
            {
                throw new InvalidOperationException("Live recording capture sessions are single-use.");
            }

            _hasStarted = true;
            _isRunning = true;
            _captureFormat = null;
        }

        _captureService.AudioFrameCaptured += OnAudioFrameCaptured;
        _captureService.CaptureFaulted += OnCaptureFaulted;

        try
        {
            _recordingSession.Start();
            _captureService.StartCapture();
            Log($"Live recording capture session '{SessionId}' started.");
        }
        catch
        {
            StopCaptureCore();
            lock (_sync)
            {
                _isRunning = false;
            }

            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            return Task.CompletedTask;
        }

        bool shouldStop;
        lock (_sync)
        {
            shouldStop = _hasStarted;
            _isRunning = false;
        }

        if (!shouldStop)
        {
            return Task.CompletedTask;
        }

        StopCaptureCore();
        Log($"Live recording capture session '{SessionId}' stopped.");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup during shutdown.
        }
        finally
        {
            _isDisposed = true;
            _captureService.Dispose();
        }
    }

    private void OnAudioFrameCaptured(object? sender, LoopbackAudioFrameEventArgs e)
    {
        lock (_sync)
        {
            if (!_isRunning)
            {
                return;
            }

            if (_captureFormat is null)
            {
                _captureFormat = e.WaveFormat;
            }
            else if (!WaveFormatsMatch(_captureFormat, e.WaveFormat))
            {
                OnCaptureFaulted(this, new InvalidOperationException("Live recording capture format changed."));
                return;
            }
        }

        _recordingSession.WriteFrame(e);
        AudioLevelChanged?.Invoke(
            this,
            new PlaybackAudioLevelChangedEventArgs(
                SessionId,
                CalculatePcm16Peak(e.Buffer),
                e.SourceName,
                e.AppliedGain,
                e.AutomaticGainApplied));
    }

    private void OnCaptureFaulted(object? sender, Exception ex)
    {
        lock (_sync)
        {
            _isRunning = false;
        }

        Log($"Live recording capture session '{SessionId}' failed: {ex.GetType().Name}: {ex.Message}.");
        StopCaptureCore();
        Faulted?.Invoke(this, ex);
    }

    private void StopCaptureCore()
    {
        _captureService.AudioFrameCaptured -= OnAudioFrameCaptured;
        _captureService.CaptureFaulted -= OnCaptureFaulted;

        try
        {
            _captureService.StopCapture();
        }
        catch (Exception ex)
        {
            Log($"Live recording capture stop failed: {ex.GetType().Name}: {ex.Message}.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(LiveRecordingCaptureSession));
        }
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

    private void Log(string message)
    {
        _processLogService.Log("LiveRecordingCapture", message);
    }
}
