using NAudio.Wave;

namespace VoxTranscribe.Audio;

public sealed class WasapiLoopbackCaptureService : IAudioLoopbackCaptureService {
    private readonly object _sync = new();
    private WasapiLoopbackCapture? _capture;
    private bool _disposed;

    public event EventHandler<LoopbackAudioFrameEventArgs>? AudioFrameCaptured;
    public event EventHandler<Exception>? CaptureFaulted;

    public bool IsCapturing {
        get {
            lock (_sync) {
                return _capture is not null;
            }
        }
    }

    public WaveFormat? CaptureFormat {
        get {
            lock (_sync) {
                return _capture?.WaveFormat;
            }
        }
    }

    public void StartCapture() {
        ThrowIfDisposed();

        WasapiLoopbackCapture capture;

        lock (_sync) {
            if (_capture is not null) {
                return;
            }

            capture = new WasapiLoopbackCapture();
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            _capture = capture;
        }

        try {
            capture.StartRecording();
        }
        catch {
            lock (_sync) {
                if (ReferenceEquals(_capture, capture)) {
                    _capture = null;
                }
            }

            ReleaseCapture(capture, stopRecording: false);
            throw;
        }
    }

    public void StopCapture() {
        WasapiLoopbackCapture? capture;

        lock (_sync) {
            capture = _capture;
            _capture = null;
        }

        if (capture is null) {
            return;
        }

        ReleaseCapture(capture, stopRecording: true);
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }

        _disposed = true;
        StopCapture();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e) {
        WasapiLoopbackCapture? capture;

        lock (_sync) {
            capture = _capture;
        }

        if (capture is null || !ReferenceEquals(sender, capture) || e.BytesRecorded <= 0) {
            return;
        }

        byte[] copied = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, copied, 0, e.BytesRecorded);
        AudioFrameCaptured?.Invoke(this, new LoopbackAudioFrameEventArgs(copied, capture.WaveFormat));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e) {
        if (sender is WasapiLoopbackCapture capture) {
            bool shouldRelease;

            lock (_sync) {
                shouldRelease = ReferenceEquals(_capture, capture);
                if (shouldRelease) {
                    _capture = null;
                }
            }

            if (shouldRelease) {
                ReleaseCapture(capture, stopRecording: false);
            }
        }

        if (e.Exception is not null) {
            CaptureFaulted?.Invoke(this, e.Exception);
        }
    }

    private void ReleaseCapture(WasapiLoopbackCapture capture, bool stopRecording) {
        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;

        try {
            if (stopRecording) {
                capture.StopRecording();
            }
        }
        catch (Exception ex) {
            CaptureFaulted?.Invoke(this, ex);
        }
        finally {
            capture.Dispose();
        }
    }

    private void ThrowIfDisposed() {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}


