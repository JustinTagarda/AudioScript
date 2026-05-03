using NAudio.Wave;

namespace AudioScript.Audio;

public sealed class AutomaticGainAudioCaptureService : IAudioLoopbackCaptureService
{
    private readonly object _sync = new();
    private readonly IAudioLoopbackCaptureService _inner;
    private readonly Pcm16AutomaticGainProcessor _gainProcessor;
    private bool _disposed;

    public AutomaticGainAudioCaptureService(
        IAudioLoopbackCaptureService inner,
        LiveAudioGainOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _gainProcessor = new Pcm16AutomaticGainProcessor(options);
    }

    public event EventHandler<LoopbackAudioFrameEventArgs>? AudioFrameCaptured;

    public event EventHandler<Exception>? CaptureFaulted;

    public bool IsCapturing => _inner.IsCapturing;

    public WaveFormat? CaptureFormat => _inner.CaptureFormat;

    public void StartCapture()
    {
        ThrowIfDisposed();
        _inner.AudioFrameCaptured += OnAudioFrameCaptured;
        _inner.CaptureFaulted += OnCaptureFaulted;

        try
        {
            _inner.StartCapture();
        }
        catch
        {
            _inner.AudioFrameCaptured -= OnAudioFrameCaptured;
            _inner.CaptureFaulted -= OnCaptureFaulted;
            throw;
        }
    }

    public void StopCapture()
    {
        _inner.AudioFrameCaptured -= OnAudioFrameCaptured;
        _inner.CaptureFaulted -= OnCaptureFaulted;
        _inner.StopCapture();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopCapture();
        _inner.Dispose();
    }

    private void OnAudioFrameCaptured(object? sender, LoopbackAudioFrameEventArgs e)
    {
        try
        {
            AudioGainProcessingResult result;
            lock (_sync)
            {
                result = _gainProcessor.Process(e.Buffer, e.WaveFormat);
            }

            AudioFrameCaptured?.Invoke(
                this,
                new LoopbackAudioFrameEventArgs(
                    result.Buffer,
                    e.WaveFormat,
                    e.SourceName,
                    result.GainMultiplier,
                    result.IsAutomaticGainEnabled));
        }
        catch (Exception ex)
        {
            CaptureFaulted?.Invoke(this, ex);
        }
    }

    private void OnCaptureFaulted(object? sender, Exception ex)
    {
        CaptureFaulted?.Invoke(this, ex);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
