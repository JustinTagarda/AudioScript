using System.IO;
using AudioScript.Services;
using NAudio.Wave;

namespace AudioScript.Audio;

public sealed class SegmentedLiveRecordingWaveStream : WaveStream
{
    private readonly List<WaveFileReader> _readers = new();
    private readonly List<long> _segmentStartPositions = new();
    private readonly WaveFormat _waveFormat;
    private readonly long _length;
    private long _position;

    public SegmentedLiveRecordingWaveStream(string manifestPath)
    {
        string manifestDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidOperationException("Live recording manifest path must include a directory.");
        string sessionDirectory = Directory.GetParent(Directory.GetParent(manifestDirectory)!.FullName)!.FullName;
        LiveRecordingManifest manifest = TranscriptSessionStore.LoadLiveRecordingManifest(manifestPath);
        if (manifest.Segments.Count == 0)
        {
            throw new InvalidOperationException("Live recording does not contain any playable audio segments.");
        }

        long offset = 0;
        foreach (LiveRecordingSegmentManifest segment in manifest.Segments.OrderBy(item => item.StartSeconds))
        {
            string segmentPath = Path.Combine(sessionDirectory, segment.RelativePath);
            if (!File.Exists(segmentPath))
            {
                throw new FileNotFoundException("Live recording segment was not found.", segmentPath);
            }

            var reader = new WaveFileReader(segmentPath);
            if (_readers.Count > 0 && !WaveFormatsMatch(_readers[0].WaveFormat, reader.WaveFormat))
            {
                reader.Dispose();
                throw new InvalidOperationException("Live recording segments do not use a consistent audio format.");
            }

            _segmentStartPositions.Add(offset);
            _readers.Add(reader);
            offset += reader.Length;
        }

        _waveFormat = _readers[0].WaveFormat;
        _length = offset;
    }

    public override WaveFormat WaveFormat => _waveFormat;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            long clamped = Math.Clamp(value, 0, _length);
            clamped -= clamped % Math.Max(_waveFormat.BlockAlign, 1);
            _position = clamped;
            ApplyReaderPositions();
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _length)
        {
            return 0;
        }

        int totalRead = 0;
        while (totalRead < count && _position < _length)
        {
            int readerIndex = FindReaderIndex(_position);
            WaveFileReader reader = _readers[readerIndex];
            long segmentStart = _segmentStartPositions[readerIndex];
            reader.Position = _position - segmentStart;
            int read = reader.Read(buffer, offset + totalRead, count - totalRead);
            if (read <= 0)
            {
                _position = readerIndex + 1 < _readers.Count
                    ? _segmentStartPositions[readerIndex + 1]
                    : _length;
                continue;
            }

            totalRead += read;
            _position += read;
        }

        return totalRead;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (WaveFileReader reader in _readers)
            {
                reader.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private void ApplyReaderPositions()
    {
        for (int index = 0; index < _readers.Count; index++)
        {
            long segmentStart = _segmentStartPositions[index];
            long segmentEnd = segmentStart + _readers[index].Length;
            if (_position >= segmentStart && _position <= segmentEnd)
            {
                _readers[index].Position = Math.Clamp(_position - segmentStart, 0, _readers[index].Length);
            }
            else
            {
                _readers[index].Position = _position > segmentEnd ? _readers[index].Length : 0;
            }
        }
    }

    private int FindReaderIndex(long position)
    {
        for (int index = _segmentStartPositions.Count - 1; index >= 0; index--)
        {
            if (position >= _segmentStartPositions[index])
            {
                return index;
            }
        }

        return 0;
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
}
