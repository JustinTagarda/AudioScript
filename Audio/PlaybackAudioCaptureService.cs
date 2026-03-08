using NAudio.Wave;

namespace AudioTranscript.Audio;

public sealed class PlaybackAudioCaptureService : IAudioLoopbackCaptureService {
    private readonly object _sync = new();
    private readonly IPlaybackAudioTapSource _tapSource;
    private bool _isCapturing;
    private bool _disposed;
    private WaveFormat? _captureFormat;

    public PlaybackAudioCaptureService(IPlaybackAudioTapSource tapSource) {
        ArgumentNullException.ThrowIfNull(tapSource);
        _tapSource = tapSource;
    }

    public event EventHandler<LoopbackAudioFrameEventArgs>? AudioFrameCaptured;
    public event EventHandler<Exception>? CaptureFaulted;

    public bool IsCapturing {
        get {
            lock (_sync) {
                return _isCapturing;
            }
        }
    }

    public WaveFormat? CaptureFormat {
        get {
            lock (_sync) {
                return _captureFormat;
            }
        }
    }

    public void StartCapture() {
        ThrowIfDisposed();

        lock (_sync) {
            if (_isCapturing) {
                return;
            }

            _tapSource.PlaybackAudioFrameProduced += OnPlaybackAudioFrameProduced;
            _tapSource.PlaybackAudioFaulted += OnPlaybackAudioFaulted;
            _captureFormat = _tapSource.PlaybackAudioFormat;
            _isCapturing = true;
        }
    }

    public void StopCapture() {
        bool shouldDetach;

        lock (_sync) {
            shouldDetach = _isCapturing;
            _isCapturing = false;
        }

        if (!shouldDetach) {
            return;
        }

        _tapSource.PlaybackAudioFrameProduced -= OnPlaybackAudioFrameProduced;
        _tapSource.PlaybackAudioFaulted -= OnPlaybackAudioFaulted;
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }

        _disposed = true;
        StopCapture();
    }

    private void OnPlaybackAudioFrameProduced(object? sender, PlaybackAudioFrameEventArgs e) {
        lock (_sync) {
            if (!_isCapturing) {
                return;
            }

            _captureFormat = e.WaveFormat;
        }

        AudioFrameCaptured?.Invoke(this, new LoopbackAudioFrameEventArgs(e.Buffer, e.WaveFormat));
    }

    private void OnPlaybackAudioFaulted(object? sender, Exception ex) {
        lock (_sync) {
            if (!_isCapturing) {
                return;
            }
        }

        CaptureFaulted?.Invoke(this, ex);
    }

    private void ThrowIfDisposed() {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
