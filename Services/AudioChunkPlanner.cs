namespace AudioTranscript.Services;

public sealed class AudioChunkPlanner {
    public const long MaxUploadBytes = 25L * 1024L * 1024L;
    public const long WaveHeaderBytes = 44L;
    public const long DefaultTargetUploadBytes = (24L * 1024L * 1024L) + (512L * 1024L);
    public static readonly TimeSpan DefaultOverlap = TimeSpan.FromSeconds(2);

    public IReadOnlyList<AudioChunkPlan> PlanWaveChunks(
        long waveDataBytes,
        int averageBytesPerSecond,
        int blockAlign,
        long targetUploadBytes = DefaultTargetUploadBytes,
        TimeSpan? overlap = null) {
        if (waveDataBytes <= 0) {
            return Array.Empty<AudioChunkPlan>();
        }

        if (averageBytesPerSecond <= 0) {
            throw new ArgumentOutOfRangeException(nameof(averageBytesPerSecond));
        }

        if (blockAlign <= 0) {
            throw new ArgumentOutOfRangeException(nameof(blockAlign));
        }

        long maxFileBytes = Math.Min(Math.Max(targetUploadBytes, WaveHeaderBytes + blockAlign), MaxUploadBytes - 1024L);
        long maxDataBytes = AlignDown(maxFileBytes - WaveHeaderBytes, blockAlign);
        maxDataBytes = Math.Max(maxDataBytes, blockAlign);

        TimeSpan requestedOverlap = overlap ?? DefaultOverlap;
        long overlapBytes = AlignDown((long)(requestedOverlap.TotalSeconds * averageBytesPerSecond), blockAlign);

        if (overlapBytes >= maxDataBytes) {
            overlapBytes = 0;
        }

        long stepBytes = Math.Max(maxDataBytes - overlapBytes, blockAlign);
        var plans = new List<AudioChunkPlan>();
        long startDataOffset = 0;
        int chunkIndex = 0;

        while (startDataOffset < waveDataBytes) {
            long remainingBytes = waveDataBytes - startDataOffset;
            long dataBytes = AlignDown(Math.Min(maxDataBytes, remainingBytes), blockAlign);

            if (dataBytes <= 0) {
                break;
            }

            TimeSpan startOffset = TimeSpan.FromSeconds(startDataOffset / (double)averageBytesPerSecond);
            TimeSpan duration = TimeSpan.FromSeconds(dataBytes / (double)averageBytesPerSecond);
            TimeSpan overlapDuration = chunkIndex == 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(overlapBytes / (double)averageBytesPerSecond);
            long estimatedFileSizeBytes = dataBytes + WaveHeaderBytes;

            plans.Add(new AudioChunkPlan(
                ChunkIndex: chunkIndex,
                StartDataOffsetBytes: startDataOffset,
                DataBytes: dataBytes,
                StartOffset: startOffset,
                Duration: duration,
                OverlapDuration: overlapDuration,
                EstimatedFileSizeBytes: estimatedFileSizeBytes));

            if (startDataOffset + dataBytes >= waveDataBytes) {
                break;
            }

            startDataOffset += stepBytes;
            chunkIndex++;
        }

        return plans;
    }

    private static long AlignDown(long value, int alignment) {
        if (alignment <= 1) {
            return value;
        }

        return value - (value % alignment);
    }
}

public sealed record AudioChunkPlan(
    int ChunkIndex,
    long StartDataOffsetBytes,
    long DataBytes,
    TimeSpan StartOffset,
    TimeSpan Duration,
    TimeSpan OverlapDuration,
    long EstimatedFileSizeBytes
);
