using System.IO;
using NAudio.Wave;

namespace AudioScript.Audio;

public sealed class StandardizingAudioCaptureService : IAudioLoopbackCaptureService
{
    public static readonly WaveFormat StandardFormat = new(16000, 16, 1);

    private readonly object _sync = new();
    private readonly IAudioLoopbackCaptureService _inner;
    private bool _disposed;

    public StandardizingAudioCaptureService(IAudioLoopbackCaptureService inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public event EventHandler<LoopbackAudioFrameEventArgs>? AudioFrameCaptured;
    public event EventHandler<Exception>? CaptureFaulted;

    public bool IsCapturing => _inner.IsCapturing;

    public WaveFormat? CaptureFormat => StandardFormat;

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
            byte[] buffer = ConvertToStandardPcm(e.Buffer, e.WaveFormat);
            if (buffer.Length == 0)
            {
                return;
            }

            AudioFrameCaptured?.Invoke(this, new LoopbackAudioFrameEventArgs(buffer, StandardFormat, e.SourceName));
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

    private static byte[] ConvertToStandardPcm(byte[] buffer, WaveFormat sourceFormat)
    {
        if (WaveFormatsMatch(sourceFormat, StandardFormat))
        {
            return buffer;
        }

        lock (typeof(StandardizingAudioCaptureService))
        {
            using var input = new RawSourceWaveStream(new MemoryStream(buffer), sourceFormat);
            using var resampler = new MediaFoundationResampler(input, StandardFormat)
            {
                ResamplerQuality = 60,
            };
            using var output = new MemoryStream();
            byte[] temp = new byte[8192];
            int read;
            while ((read = resampler.Read(temp, 0, temp.Length)) > 0)
            {
                output.Write(temp, 0, read);
            }

            return output.ToArray();
        }
    }

    private static bool WaveFormatsMatch(WaveFormat left, WaveFormat right)
    {
        return left.Encoding == right.Encoding
            && left.SampleRate == right.SampleRate
            && left.BitsPerSample == right.BitsPerSample
            && left.Channels == right.Channels
            && left.BlockAlign == right.BlockAlign;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
