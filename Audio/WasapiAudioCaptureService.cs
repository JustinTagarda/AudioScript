using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioTranscript.Audio;

public sealed class WasapiAudioCaptureService : IAudioCaptureService {
    private readonly AudioStandardizer _standardizer;
    private readonly object _sync = new();
    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDevice? _device;
    private WasapiLoopbackCapture? _capture;

    public WasapiAudioCaptureService(AudioStandardizer standardizer) {
        _standardizer = standardizer;
    }

    public event EventHandler<AudioFrame>? FrameCaptured;

    public bool IsCapturing => _capture is not null;

    public Task StartDefaultPlaybackAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync) {
            if (_capture is not null) {
                return Task.CompletedTask;
            }

            _deviceEnumerator = new MMDeviceEnumerator();
            // Playback-only capture path: default render endpoint loopback.
            _device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            _capture = new WasapiLoopbackCapture(_device);
            _capture.DataAvailable += OnDataAvailable;
            _capture.StartRecording();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync() {
        lock (_sync) {
            if (_capture is null) {
                return Task.CompletedTask;
            }

            _capture.DataAvailable -= OnDataAvailable;
            _capture.StopRecording();
            _capture.Dispose();
            _capture = null;

            _device?.Dispose();
            _device = null;

            _deviceEnumerator?.Dispose();
            _deviceEnumerator = null;
        }

        return Task.CompletedTask;
    }

    public void Dispose() {
        _ = StopAsync();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs) {
        WasapiLoopbackCapture? capture;

        lock (_sync) {
            capture = _capture;
        }

        if (capture is null || eventArgs.BytesRecorded <= 0) {
            return;
        }

        var normalized = _standardizer.NormalizeBuffer(
            eventArgs.Buffer.AsSpan(0, eventArgs.BytesRecorded),
            capture.WaveFormat);

        if (normalized.Length == 0) {
            return;
        }

        FrameCaptured?.Invoke(
            this,
            new AudioFrame(normalized, AudioFormatConstants.EngineWaveFormat, DateTimeOffset.UtcNow));
    }
}
