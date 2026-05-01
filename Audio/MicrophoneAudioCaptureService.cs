using NAudio.Wave;

namespace AudioScript.Audio;

public sealed class MicrophoneAudioCaptureService : IAudioLoopbackCaptureService
{
    private readonly object _sync = new();
    private readonly int _deviceNumber;
    private readonly string _sourceName;
    private WaveInEvent? _waveIn;
    private bool _isCapturing;
    private bool _disposed;
    private WaveFormat? _captureFormat;

    public MicrophoneAudioCaptureService(int deviceNumber)
    {
        _deviceNumber = deviceNumber;
        _sourceName = $"Microphone:{deviceNumber}";
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

    public WaveFormat? CaptureFormat
    {
        get
        {
            lock (_sync)
            {
                return _captureFormat;
            }
        }
    }

    public static IReadOnlyList<AudioInputDeviceOption> GetInputDevices()
    {
        var devices = new List<AudioInputDeviceOption>();

        for (int index = 0; index < WaveIn.DeviceCount; index++)
        {
            WaveInCapabilities capabilities = WaveIn.GetCapabilities(index);
            string name = string.IsNullOrWhiteSpace(capabilities.ProductName)
                ? $"Input Device {index + 1}"
                : capabilities.ProductName;
            devices.Add(new AudioInputDeviceOption(LiveAudioSourceKind.Microphone, index, name));
        }

        return devices;
    }

    public void StartCapture()
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            if (_isCapturing)
            {
                return;
            }

            _captureFormat = new WaveFormat(16000, 16, 1);
            _waveIn = new WaveInEvent
            {
                DeviceNumber = _deviceNumber,
                WaveFormat = _captureFormat,
                BufferMilliseconds = 100,
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _isCapturing = true;
        }

        try
        {
            _waveIn.StartRecording();
        }
        catch
        {
            StopCapture();
            throw;
        }
    }

    public void StopCapture()
    {
        WaveInEvent? waveIn;

        lock (_sync)
        {
            if (!_isCapturing && _waveIn is null)
            {
                return;
            }

            waveIn = _waveIn;
            _waveIn = null;
            _isCapturing = false;
        }

        if (waveIn is null)
        {
            return;
        }

        waveIn.DataAvailable -= OnDataAvailable;
        waveIn.RecordingStopped -= OnRecordingStopped;

        try
        {
            waveIn.StopRecording();
        }
        finally
        {
            waveIn.Dispose();
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
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        WaveFormat? waveFormat;

        lock (_sync)
        {
            if (!_isCapturing || e.BytesRecorded <= 0)
            {
                return;
            }

            waveFormat = _captureFormat;
        }

        if (waveFormat is null)
        {
            return;
        }

        byte[] buffer = new byte[e.BytesRecorded];
        Array.Copy(e.Buffer, buffer, e.BytesRecorded);
        AudioFrameCaptured?.Invoke(this, new LoopbackAudioFrameEventArgs(buffer, waveFormat, _sourceName));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null)
        {
            return;
        }

        CaptureFaulted?.Invoke(this, e.Exception);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
