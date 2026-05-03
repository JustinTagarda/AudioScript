using System.IO;
using NAudio.Wave;

namespace AudioScript.Audio;

public sealed class CompositeAudioCaptureService : IAudioLoopbackCaptureService
{
    private const int MixFrameMilliseconds = 100;

    private readonly object _sync = new();
    private readonly IReadOnlyList<IAudioLoopbackCaptureService> _sources;
    private readonly Dictionary<string, MemoryStream> _sourceBuffers = new(StringComparer.OrdinalIgnoreCase);
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

            _sourceBuffers.Clear();
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
            foreach (MemoryStream buffer in _sourceBuffers.Values)
            {
                buffer.Dispose();
            }

            _sourceBuffers.Clear();
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
        List<byte[]> mixedFrames = new();

        lock (_sync)
        {
            if (!_isCapturing)
            {
                return;
            }

            if (!WaveFormatsMatch(e.WaveFormat, StandardizingAudioCaptureService.StandardFormat))
            {
                CaptureFaulted?.Invoke(this, new InvalidOperationException("Composite live capture requires standardized PCM sources."));
                return;
            }

            string sourceName = string.IsNullOrWhiteSpace(e.SourceName)
                ? $"Source:{sender?.GetHashCode() ?? 0}"
                : e.SourceName;
            if (!_sourceBuffers.TryGetValue(sourceName, out MemoryStream? sourceBuffer))
            {
                sourceBuffer = new MemoryStream();
                _sourceBuffers[sourceName] = sourceBuffer;
            }

            sourceBuffer.Position = sourceBuffer.Length;
            sourceBuffer.Write(e.Buffer, 0, e.BytesRecorded);

            int frameBytes = GetMixFrameBytes();
            while (_sourceBuffers.Values.Any(buffer => buffer.Length >= frameBytes))
            {
                mixedFrames.Add(MixNextFrame(frameBytes));
            }
        }

        foreach (byte[] mixedFrame in mixedFrames)
        {
            AudioFrameCaptured?.Invoke(
                this,
                new LoopbackAudioFrameEventArgs(mixedFrame, StandardizingAudioCaptureService.StandardFormat, "MixedLiveAudio"));
        }
    }

    private byte[] MixNextFrame(int frameBytes)
    {
        int sampleCount = frameBytes / 2;
        int[] mix = new int[sampleCount];

        foreach (MemoryStream buffer in _sourceBuffers.Values)
        {
            byte[] sourceFrame = ReadAndRemove(buffer, frameBytes);
            for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                int byteIndex = sampleIndex * 2;
                if (byteIndex + 1 >= sourceFrame.Length)
                {
                    break;
                }

                mix[sampleIndex] += BitConverter.ToInt16(sourceFrame, byteIndex);
            }
        }

        byte[] mixed = new byte[frameBytes];
        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            short clamped = (short)Math.Clamp(mix[sampleIndex], short.MinValue, short.MaxValue);
            byte[] bytes = BitConverter.GetBytes(clamped);
            mixed[sampleIndex * 2] = bytes[0];
            mixed[(sampleIndex * 2) + 1] = bytes[1];
        }

        return mixed;
    }

    private static byte[] ReadAndRemove(MemoryStream buffer, int bytesToRead)
    {
        byte[] current = buffer.ToArray();
        int readLength = Math.Min(bytesToRead, current.Length);
        byte[] frame = new byte[bytesToRead];
        if (readLength > 0)
        {
            Buffer.BlockCopy(current, 0, frame, 0, readLength);
        }

        buffer.SetLength(0);
        buffer.Position = 0;
        int remaining = current.Length - readLength;
        if (remaining > 0)
        {
            buffer.Write(current, readLength, remaining);
        }

        return frame;
    }

    private static int GetMixFrameBytes()
    {
        WaveFormat format = StandardizingAudioCaptureService.StandardFormat;
        return Math.Max(format.AverageBytesPerSecond * MixFrameMilliseconds / 1000, format.BlockAlign);
    }

    private void OnSourceCaptureFaulted(object? sender, Exception ex)
    {
        CaptureFaulted?.Invoke(this, ex);
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
