using NAudio.Wave;

namespace AudioScript.Audio;

public sealed class CompositeAudioCaptureService : IAudioLoopbackCaptureService
{
    private readonly object _sync = new();
    private readonly IReadOnlyList<IAudioLoopbackCaptureService> _sources;
    private bool _isCapturing;
    private bool _disposed;

    public CompositeAudioCaptureService(params IAudioLoopbackCaptureService[] sources)
    {
        if (sources is null || sources.Length == 0)
        {
            throw new ArgumentException("At least one audio source is required.", nameof(sources));
        }

        _sources = sources;
    }

    public event EventHandler<LoopbackAudioFrameEventArgs>? AudioFrameCaptured;
    public event EventHandler<Exception>? CaptureFaulted;

    public bool IsCapturing
    {
        get
        {
            lock (_sync)
            {
                return _isCapturing;
            }
        }
    }

    public WaveFormat? CaptureFormat => StandardizingAudioCaptureService.StandardFormat;

    public void StartCapture()
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            if (_isCapturing)
            {
                return;
            }

            _isCapturing = true;
        }

        try
        {
            foreach (IAudioLoopbackCaptureService source in _sources)
            {
                source.AudioFrameCaptured += OnSourceAudioFrameCaptured;
                source.CaptureFaulted += OnSourceCaptureFaulted;
                source.StartCapture();
            }
        }
        catch
        {
            StopCapture();
            throw;
        }
    }

    public void StopCapture()
    {
        lock (_sync)
        {
            if (!_isCapturing)
            {
                return;
            }

            _isCapturing = false;
        }

        foreach (IAudioLoopbackCaptureService source in _sources)
        {
            source.AudioFrameCaptured -= OnSourceAudioFrameCaptured;
            source.CaptureFaulted -= OnSourceCaptureFaulted;
            source.StopCapture();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopCapture();

        foreach (IAudioLoopbackCaptureService source in _sources)
        {
            source.Dispose();
        }
    }

    private void OnSourceAudioFrameCaptured(object? sender, LoopbackAudioFrameEventArgs e)
    {
        lock (_sync)
        {
            if (!_isCapturing)
            {
                return;
            }
        }

        AudioFrameCaptured?.Invoke(this, e);
    }

    private void OnSourceCaptureFaulted(object? sender, Exception ex)
    {
        CaptureFaulted?.Invoke(this, ex);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
