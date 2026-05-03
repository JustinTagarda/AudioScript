using System.IO;
using AudioScript.Abstractions;
using AudioScript.Services;
using NAudio.Wave;

namespace AudioScript.Audio;

public sealed class AudioChunkingService {
    private readonly AudioStandardizer _audioStandardizer;
    private readonly SilenceIntervalDetector _silenceIntervalDetector;
    private readonly SilenceAwareChunkPlanner _chunkPlanner;
    private readonly WaveClipExtractor _waveClipExtractor;
    private readonly AudioChunkingOptions _options;

    public AudioChunkingService(
        AudioStandardizer audioStandardizer,
        SilenceIntervalDetector silenceIntervalDetector,
        SilenceAwareChunkPlanner chunkPlanner,
        WaveClipExtractor waveClipExtractor,
        AudioChunkingOptions? options = null) {
        _audioStandardizer = audioStandardizer;
        _silenceIntervalDetector = silenceIntervalDetector;
        _chunkPlanner = chunkPlanner;
        _waveClipExtractor = waveClipExtractor;
        _options = options ?? AudioChunkingOptions.Default;
    }

    public AudioSourceInfo GetSourceInfo(string audioFilePath) {
        string fullPath = ValidateAudioFilePath(audioFilePath);
        var fileInfo = new FileInfo(fullPath);
        return new AudioSourceInfo(
            FullPath: fullPath,
            Name: fileInfo.Name,
            FileSizeBytes: fileInfo.Length,
            Duration: ResolveAudioDuration(fullPath));
    }

    public bool RequiresChunking(AudioSourceInfo sourceInfo) {
        ArgumentNullException.ThrowIfNull(sourceInfo);
        return _options.RequiresChunking(sourceInfo.FileSizeBytes, sourceInfo.Duration);
    }

    public ChunkedAudioFile PrepareChunks(AudioSourceInfo sourceInfo) {
        ArgumentNullException.ThrowIfNull(sourceInfo);

        string standardizedWavePath = _audioStandardizer.ConvertFileToEngineWav(sourceInfo.FullPath);
        var temporaryFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            standardizedWavePath,
        };

        try {
            IReadOnlyList<TimeSpanRange> silenceIntervals =
                _silenceIntervalDetector.DetectSilenceIntervals(standardizedWavePath);
            IReadOnlyList<AudioChunkPlan> chunkPlans =
                _chunkPlanner.PlanChunks(sourceInfo.Duration, silenceIntervals);
            var chunkFiles = new List<AudioChunkFile>(chunkPlans.Count);

            foreach (AudioChunkPlan chunkPlan in chunkPlans) {
                string chunkFilePath = _waveClipExtractor.ExtractTemporaryWaveFile(
                    standardizedWavePath,
                    chunkPlan.RequestStart,
                    chunkPlan.RequestEnd,
                    $"audio-chunk-{chunkPlan.Index + 1}");
                temporaryFiles.Add(chunkFilePath);
                chunkFiles.Add(new AudioChunkFile(chunkPlan, chunkFilePath));
            }

            return new ChunkedAudioFile(
                sourceInfo,
                standardizedWavePath,
                silenceIntervals,
                chunkFiles,
                temporaryFiles);
        }
        catch {
            CleanupTemporaryFiles(temporaryFiles, _ => { });
            throw;
        }
    }

    public ChunkedAudioFile PrepareFixedChunks(
        AudioSourceInfo sourceInfo,
        TimeSpan chunkDuration,
        TimeSpan overlapDuration) {
        ArgumentNullException.ThrowIfNull(sourceInfo);
        if (chunkDuration <= TimeSpan.Zero) {
            throw new ArgumentOutOfRangeException(nameof(chunkDuration), "Chunk duration must be greater than zero.");
        }

        if (overlapDuration < TimeSpan.Zero || overlapDuration >= chunkDuration) {
            throw new ArgumentOutOfRangeException(nameof(overlapDuration), "Overlap duration must be non-negative and shorter than the chunk duration.");
        }

        string standardizedWavePath = _audioStandardizer.ConvertFileToEngineWav(sourceInfo.FullPath);
        var temporaryFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            standardizedWavePath,
        };

        try {
            var chunkFiles = new List<AudioChunkFile>();
            TimeSpan keepStart = TimeSpan.Zero;
            int index = 0;
            while (keepStart < sourceInfo.Duration) {
                TimeSpan keepEnd = keepStart + chunkDuration;
                if (keepEnd > sourceInfo.Duration) {
                    keepEnd = sourceInfo.Duration;
                }

                TimeSpan requestStart = index == 0
                    ? keepStart
                    : keepStart - overlapDuration;
                if (requestStart < TimeSpan.Zero) {
                    requestStart = TimeSpan.Zero;
                }

                TimeSpan requestEnd = keepEnd < sourceInfo.Duration
                    ? keepEnd + overlapDuration
                    : sourceInfo.Duration;

                var plan = new AudioChunkPlan(
                    Index: index,
                    RequestStart: requestStart,
                    RequestEnd: requestEnd,
                    KeepStart: keepStart,
                    KeepEnd: keepEnd);
                string chunkFilePath = _waveClipExtractor.ExtractTemporaryWaveFile(
                    standardizedWavePath,
                    plan.RequestStart,
                    plan.RequestEnd,
                    $"speaker-diarization-chunk-{index + 1}");
                temporaryFiles.Add(chunkFilePath);
                chunkFiles.Add(new AudioChunkFile(plan, chunkFilePath));

                keepStart = keepEnd;
                index++;
            }

            return new ChunkedAudioFile(
                sourceInfo,
                standardizedWavePath,
                Array.Empty<TimeSpanRange>(),
                chunkFiles,
                temporaryFiles);
        }
        catch {
            CleanupTemporaryFiles(temporaryFiles, _ => { });
            throw;
        }
    }

    public static TimeSpan ResolveMidpoint(TimeSpan start, TimeSpan? end) {
        TimeSpan resolvedEnd = end is null || end <= start
            ? start
            : end.Value;
        return resolvedEnd <= start
            ? start
            : start + TimeSpan.FromTicks((resolvedEnd - start).Ticks / 2);
    }

    public static void CleanupTemporaryFiles(IEnumerable<string> temporaryFiles, Action<string> log) {
        foreach (string temporaryFile in temporaryFiles.Distinct(StringComparer.OrdinalIgnoreCase)) {
            try {
                if (File.Exists(temporaryFile)) {
                    File.Delete(temporaryFile);
                }
            }
            catch (Exception ex) {
                log($"Temporary file cleanup skipped for '{Path.GetFileName(temporaryFile)}': {ex.Message}");
            }
        }
    }

    private static string ValidateAudioFilePath(string audioFilePath) {
        if (string.IsNullOrWhiteSpace(audioFilePath)) {
            throw new ArgumentException("Audio file path is required.", nameof(audioFilePath));
        }

        string fullPath = Path.GetFullPath(audioFilePath.Trim());
        if (!File.Exists(fullPath)) {
            throw new FileNotFoundException("Audio file was not found.", fullPath);
        }

        return fullPath;
    }

    private static TimeSpan ResolveAudioDuration(string audioFilePath) {
        if (IsLiveRecordingManifestPath(audioFilePath)) {
            LiveRecordingManifest manifest = TranscriptSessionStore.LoadLiveRecordingManifest(audioFilePath);
            double durationSeconds = manifest.TotalDurationSeconds > 0
                ? manifest.TotalDurationSeconds
                : manifest.Segments.Sum(segment => segment.DurationSeconds);
            return TimeSpan.FromSeconds(Math.Max(0, durationSeconds));
        }

        using var reader = new AudioFileReader(audioFilePath);
        return reader.TotalTime;
    }

    private static bool IsLiveRecordingManifestPath(string sourceFilePath) {
        return string.Equals(Path.GetFileName(sourceFilePath), "manifest.json", StringComparison.OrdinalIgnoreCase)
            && sourceFilePath.Contains(
                Path.Combine("audio", "live"),
                StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record AudioSourceInfo(
    string FullPath,
    string Name,
    long FileSizeBytes,
    TimeSpan Duration
);

public sealed record AudioChunkFile(
    AudioChunkPlan Plan,
    string FilePath
);

public sealed class ChunkedAudioFile : IDisposable {
    private readonly ISet<string> _temporaryFiles;
    private bool _disposed;

    public ChunkedAudioFile(
        AudioSourceInfo sourceInfo,
        string standardizedWavePath,
        IReadOnlyList<TimeSpanRange> silenceIntervals,
        IReadOnlyList<AudioChunkFile> chunks,
        ISet<string> temporaryFiles) {
        SourceInfo = sourceInfo;
        StandardizedWavePath = standardizedWavePath;
        SilenceIntervals = silenceIntervals;
        Chunks = chunks;
        _temporaryFiles = temporaryFiles;
    }

    public AudioSourceInfo SourceInfo { get; }

    public string StandardizedWavePath { get; }

    public IReadOnlyList<TimeSpanRange> SilenceIntervals { get; }

    public IReadOnlyList<AudioChunkFile> Chunks { get; }

    public void TrackTemporaryFile(string filePath) {
        if (!string.IsNullOrWhiteSpace(filePath)) {
            _temporaryFiles.Add(Path.GetFullPath(filePath.Trim()));
        }
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }

        AudioChunkingService.CleanupTemporaryFiles(_temporaryFiles, _ => { });
        _disposed = true;
    }
}
