using System.Buffers.Binary;
using System.IO;
using NAudio.Wave;
using VoxTranscribe.Abstractions;

namespace VoxTranscribe.Audio;

public sealed record SilenceIntervalDetectorOptions(
    TimeSpan AnalysisFrameDuration,
    TimeSpan MinimumSilenceDuration,
    TimeSpan MergeGapDuration,
    double NoiseFloorPercentile,
    double DecibelsAboveNoiseFloor,
    double MinimumSilenceThresholdDb,
    double MaximumSilenceThresholdDb
) {
    public static SilenceIntervalDetectorOptions Default { get; } = new(
        AnalysisFrameDuration: TimeSpan.FromMilliseconds(30),
        MinimumSilenceDuration: TimeSpan.FromMilliseconds(450),
        MergeGapDuration: TimeSpan.FromMilliseconds(120),
        NoiseFloorPercentile: 0.20,
        DecibelsAboveNoiseFloor: 9,
        MinimumSilenceThresholdDb: -60,
        MaximumSilenceThresholdDb: -35);
}

public sealed class SilenceIntervalDetector {
    private const double MinimumRms = 1e-8;
    private const double MinimumDbFallback = -60;

    private readonly SilenceIntervalDetectorOptions _options;

    public SilenceIntervalDetector(SilenceIntervalDetectorOptions? options = null) {
        _options = options ?? SilenceIntervalDetectorOptions.Default;
    }

    public IReadOnlyList<TimeSpanRange> DetectSilenceIntervals(string waveFilePath) {
        if (string.IsNullOrWhiteSpace(waveFilePath)) {
            throw new ArgumentException("Wave file path is required.", nameof(waveFilePath));
        }

        string fullPath = Path.GetFullPath(waveFilePath.Trim());
        if (!File.Exists(fullPath)) {
            throw new FileNotFoundException("Wave file was not found.", fullPath);
        }

        using var reader = new WaveFileReader(fullPath);
        EnsureSupportedFormat(reader.WaveFormat);

        int frameBytes = ResolveFrameBytes(reader.WaveFormat, _options.AnalysisFrameDuration);
        byte[] buffer = new byte[frameBytes];
        var frames = new List<FrameWindow>();
        long bytesProcessed = 0;

        while (true) {
            int bytesRead = reader.Read(buffer, 0, buffer.Length);
            if (bytesRead <= 0) {
                break;
            }

            TimeSpan start = TimeSpan.FromSeconds((double)bytesProcessed / reader.WaveFormat.AverageBytesPerSecond);
            bytesProcessed += bytesRead;
            TimeSpan end = TimeSpan.FromSeconds((double)bytesProcessed / reader.WaveFormat.AverageBytesPerSecond);
            double decibels = ComputeFrameDecibels(buffer.AsSpan(0, bytesRead));

            frames.Add(new FrameWindow(start, end, decibels));
        }

        if (frames.Count == 0) {
            return Array.Empty<TimeSpanRange>();
        }

        double thresholdDb = ResolveSilenceThreshold(frames.Select(frame => frame.Decibels).ToArray());
        IReadOnlyList<TimeSpanRange> rawIntervals = BuildRawIntervals(frames, thresholdDb);
        return MergeIntervals(rawIntervals);
    }

    private IReadOnlyList<TimeSpanRange> BuildRawIntervals(
        IReadOnlyList<FrameWindow> frames,
        double silenceThresholdDb) {
        var results = new List<TimeSpanRange>();
        TimeSpan? silenceStart = null;
        TimeSpan silenceEnd = TimeSpan.Zero;

        foreach (FrameWindow frame in frames) {
            bool isSilent = frame.Decibels <= silenceThresholdDb;
            if (isSilent) {
                silenceStart ??= frame.Start;
                silenceEnd = frame.End;
                continue;
            }

            if (silenceStart is not null) {
                AddIntervalIfLongEnough(results, silenceStart.Value, silenceEnd);
                silenceStart = null;
            }
        }

        if (silenceStart is not null) {
            AddIntervalIfLongEnough(results, silenceStart.Value, silenceEnd);
        }

        return results;
    }

    private IReadOnlyList<TimeSpanRange> MergeIntervals(IReadOnlyList<TimeSpanRange> rawIntervals) {
        if (rawIntervals.Count <= 1) {
            return rawIntervals;
        }

        var merged = new List<TimeSpanRange>();
        TimeSpanRange current = rawIntervals[0];

        for (int index = 1; index < rawIntervals.Count; index++) {
            TimeSpanRange next = rawIntervals[index];
            if (next.Start - current.End <= _options.MergeGapDuration) {
                current = new TimeSpanRange(current.Start, next.End).Normalize();
                continue;
            }

            merged.Add(current);
            current = next;
        }

        merged.Add(current);
        return merged;
    }

    private void AddIntervalIfLongEnough(
        ICollection<TimeSpanRange> results,
        TimeSpan start,
        TimeSpan end) {
        var interval = new TimeSpanRange(start, end).Normalize();
        if (interval.Duration >= _options.MinimumSilenceDuration) {
            results.Add(interval);
        }
    }

    private double ResolveSilenceThreshold(IReadOnlyList<double> decibels) {
        if (decibels.Count == 0) {
            return _options.MaximumSilenceThresholdDb;
        }

        double[] ordered = decibels
            .Where(value => !double.IsNaN(value) && !double.IsInfinity(value))
            .OrderBy(value => value)
            .ToArray();

        if (ordered.Length == 0) {
            return _options.MaximumSilenceThresholdDb;
        }

        int percentileIndex = (int)Math.Floor((ordered.Length - 1) * _options.NoiseFloorPercentile);
        percentileIndex = Math.Clamp(percentileIndex, 0, ordered.Length - 1);

        double noiseFloorDb = ordered[percentileIndex];
        double thresholdDb = noiseFloorDb + _options.DecibelsAboveNoiseFloor;
        thresholdDb = Math.Max(thresholdDb, _options.MinimumSilenceThresholdDb);
        thresholdDb = Math.Min(thresholdDb, _options.MaximumSilenceThresholdDb);
        return thresholdDb;
    }

    private static double ComputeFrameDecibels(ReadOnlySpan<byte> bytes) {
        if (bytes.Length < sizeof(short)) {
            return MinimumDbFallback;
        }

        long sampleCount = bytes.Length / sizeof(short);
        double sumSquares = 0;

        for (int index = 0; index + sizeof(short) <= bytes.Length; index += sizeof(short)) {
            short sample = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(index, sizeof(short)));
            double normalized = sample / (double)short.MaxValue;
            sumSquares += normalized * normalized;
        }

        double rms = Math.Sqrt(sumSquares / sampleCount);
        return 20 * Math.Log10(Math.Max(rms, MinimumRms));
    }

    private static int ResolveFrameBytes(WaveFormat format, TimeSpan frameDuration) {
        double rawBytes = format.AverageBytesPerSecond * frameDuration.TotalSeconds;
        int rounded = Math.Max(format.BlockAlign, (int)Math.Round(rawBytes, MidpointRounding.AwayFromZero));
        int remainder = rounded % format.BlockAlign;

        if (remainder == 0) {
            return rounded;
        }

        return rounded + (format.BlockAlign - remainder);
    }

    private static void EnsureSupportedFormat(WaveFormat format) {
        if (format.Encoding != WaveFormatEncoding.Pcm
            || format.BitsPerSample != 16
            || format.Channels != 1) {
            throw new InvalidOperationException(
                "Silence detection requires mono 16-bit PCM audio. Standardize the audio before detecting silence.");
        }
    }

    private sealed record FrameWindow(
        TimeSpan Start,
        TimeSpan End,
        double Decibels
    );
}
