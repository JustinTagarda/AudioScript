using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using AudioScript.Audio;
using NAudio.Wave;

namespace AudioScript.Services;

public sealed class LiveRecordingSession : IAsyncDisposable
{
    public static readonly TimeSpan DefaultSegmentDuration = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly string _manifestPath;
    private readonly string _manifestRelativePath;
    private readonly string _recordingDirectoryPath;
    private readonly string _recordingRelativeDirectoryPath;
    private readonly string _inputSource;
    private readonly ProcessLogService? _processLogService;
    private readonly TimeSpan _segmentDuration;
    private readonly BlockingCollection<LiveRecordingFrame> _queue = new(256);
    private readonly WaveFormat _waveFormat = StandardizingAudioCaptureService.StandardFormat;

    private Task? _writerTask;
    private WaveFileWriter? _writer;
    private string? _currentTempPath;
    private string? _currentSegmentPath;
    private long _currentSegmentBytes;
    private long _totalPcmBytes;
    private long _enqueuedFrameCount;
    private long _enqueuedBytes;
    private int _nextSegmentNumber = 1;
    private bool _started;
    private bool _acceptingFrames;
    private bool _faulted;
    private string? _faultMessage;

    public LiveRecordingSession(
        string manifestPath,
        string manifestRelativePath,
        string inputSource,
        ProcessLogService? processLogService = null,
        TimeSpan? segmentDuration = null)
    {
        _manifestPath = Path.GetFullPath(manifestPath);
        _manifestRelativePath = NormalizeRelativePath(manifestRelativePath);
        _recordingDirectoryPath = Path.GetDirectoryName(_manifestPath)
            ?? throw new ArgumentException("Manifest path must include a directory.", nameof(manifestPath));
        _recordingRelativeDirectoryPath = NormalizeRelativePath(Path.GetDirectoryName(_manifestRelativePath) ?? string.Empty);
        _inputSource = inputSource?.Trim() ?? string.Empty;
        _processLogService = processLogService;
        _segmentDuration = segmentDuration.GetValueOrDefault(DefaultSegmentDuration);
    }

    public event EventHandler<Exception>? Faulted;

    public string ManifestPath => _manifestPath;

    public string ManifestRelativePath => _manifestRelativePath;

    public LiveRecordingManifest Manifest { get; private set; } = new();

    public bool IsFaulted
    {
        get
        {
            lock (_sync)
            {
                return _faulted;
            }
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_started)
            {
                throw new InvalidOperationException("Live recording sessions are single-use.");
            }

            _started = true;
            _acceptingFrames = true;
        }

        Directory.CreateDirectory(_recordingDirectoryPath);
        CleanupStaleTempSegments();

        Manifest = new LiveRecordingManifest
        {
            Status = LiveRecordingManifestStatuses.Recording,
            Format = "wav-pcm",
            SampleRate = _waveFormat.SampleRate,
            BitsPerSample = _waveFormat.BitsPerSample,
            Channels = _waveFormat.Channels,
            StartedUtc = DateTimeOffset.UtcNow,
            InputSource = _inputSource,
        };
        SaveManifest();

        _writerTask = Task.Run(ProcessFrames);
        Log(
            $"Live recording started. manifest='{_manifestPath}', relativeManifest='{_manifestRelativePath}', " +
            $"source='{_inputSource}', segmentDuration='{_segmentDuration}', queueCapacity={_queue.BoundedCapacity}, " +
            $"waveFormat={_waveFormat.SampleRate}Hz/{_waveFormat.BitsPerSample}bit/{_waveFormat.Channels}ch.");
    }

    public void WriteFrame(LoopbackAudioFrameEventArgs frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_sync)
        {
            if (!_acceptingFrames || _faulted)
            {
                return;
            }
        }

        if (!WaveFormatsMatch(frame.WaveFormat, _waveFormat))
        {
            ReportFault(new InvalidOperationException("Live recording received audio that was not in the standard PCM format."));
            return;
        }

        byte[] copy = frame.Buffer.ToArray();
        if (!_queue.TryAdd(new LiveRecordingFrame(copy), millisecondsTimeout: 0))
        {
            ReportFault(new InvalidOperationException("Live recording could not keep up with incoming audio."));
            LogError($"Live recording queue overflow. pendingQueueCount={_queue.Count}, attemptedBytes={copy.Length:N0}.");
            return;
        }

        _enqueuedFrameCount++;
        _enqueuedBytes += copy.Length;
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(LiveRecordingManifestStatuses.Completed, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task InterruptAsync(string? reason, CancellationToken cancellationToken = default)
    {
        await StopAsync(LiveRecordingManifestStatuses.Interrupted, reason, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_started)
        {
            await InterruptAsync("Live recording disposed before completion.").ConfigureAwait(false);
        }

        _queue.Dispose();
    }

    private async Task StopAsync(string status, string? reason, CancellationToken cancellationToken)
    {
        Task? writerTask;

        lock (_sync)
        {
            _acceptingFrames = false;
            writerTask = _writerTask;
        }

        _queue.CompleteAdding();

        if (writerTask is not null)
        {
            await writerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        FinalizeCurrentSegment();

        if (IsFaulted)
        {
            status = LiveRecordingManifestStatuses.Interrupted;
            reason ??= _faultMessage;
        }

        Manifest.Status = status;
        Manifest.EndedUtc = DateTimeOffset.UtcNow;
        Manifest.ErrorMessage = string.IsNullOrWhiteSpace(reason) ? Manifest.ErrorMessage : reason;
        UpdateManifestTotals();
        SaveManifest();
        Log(
            $"Live recording stopped. status='{Manifest.Status}', segments={Manifest.Segments.Count:N0}, " +
            $"totalDuration={Manifest.TotalDurationSeconds:0.###}s, totalFileSizeBytes={Manifest.TotalFileSizeBytes:N0}, " +
            $"enqueuedFrames={_enqueuedFrameCount:N0}, enqueuedBytes={_enqueuedBytes:N0}, " +
            $"reason='{Manifest.ErrorMessage ?? "(none)"}'.");

        lock (_sync)
        {
            _started = false;
        }
    }

    private void ProcessFrames()
    {
        try
        {
            foreach (LiveRecordingFrame frame in _queue.GetConsumingEnumerable())
            {
                WriteFrameCore(frame.Buffer);
            }
        }
        catch (Exception ex)
        {
            ReportFault(ex);
        }
    }

    private void WriteFrameCore(byte[] buffer)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        long maxSegmentBytes = GetAlignedByteCount(_segmentDuration, _waveFormat);
        if (_writer is null || _currentSegmentBytes >= maxSegmentBytes)
        {
            FinalizeCurrentSegment();
            OpenNextSegment();
        }

        _writer!.Write(buffer, 0, buffer.Length);
        _currentSegmentBytes += buffer.Length;
        _totalPcmBytes += buffer.Length;
    }

    private void OpenNextSegment()
    {
        string fileName = $"segment-{_nextSegmentNumber:000000}.wav";
        _nextSegmentNumber++;
        _currentSegmentPath = Path.Combine(_recordingDirectoryPath, fileName);
        _currentTempPath = $"{_currentSegmentPath}.tmp";
        _currentSegmentBytes = 0;
        _writer = new WaveFileWriter(_currentTempPath, _waveFormat);
        Log(
            $"Opened live recording segment file='{_currentSegmentPath}', tempFile='{_currentTempPath}', " +
            $"segmentIndex={_nextSegmentNumber - 1:N0}.");
    }

    private void FinalizeCurrentSegment()
    {
        WaveFileWriter? writer = _writer;
        string? tempPath = _currentTempPath;
        string? segmentPath = _currentSegmentPath;
        long segmentBytes = _currentSegmentBytes;

        _writer = null;
        _currentTempPath = null;
        _currentSegmentPath = null;
        _currentSegmentBytes = 0;

        if (writer is null || string.IsNullOrWhiteSpace(tempPath) || string.IsNullOrWhiteSpace(segmentPath))
        {
            return;
        }

        writer.Dispose();

        if (segmentBytes <= 0)
        {
            TryDelete(tempPath);
            return;
        }

        if (File.Exists(segmentPath))
        {
            File.Replace(tempPath, segmentPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, segmentPath);
        }

        var info = new FileInfo(segmentPath);
        double startSeconds = Manifest.Segments.Sum(segment => segment.DurationSeconds);
        double durationSeconds = segmentBytes / (double)Math.Max(_waveFormat.AverageBytesPerSecond, 1);
        string relativePath = string.IsNullOrWhiteSpace(_recordingRelativeDirectoryPath)
            ? Path.GetFileName(segmentPath)
            : NormalizeRelativePath(Path.Combine(_recordingRelativeDirectoryPath, Path.GetFileName(segmentPath)));

        Manifest.Segments.Add(new LiveRecordingSegmentManifest
        {
            RelativePath = relativePath,
            StartSeconds = startSeconds,
            DurationSeconds = durationSeconds,
            FileSizeBytes = info.Length,
        });
        UpdateManifestTotals();
        SaveManifest();
        Log(
            $"Finalized live recording segment file='{segmentPath}', relativePath='{relativePath}', " +
            $"duration={durationSeconds:0.###}s, pcmBytes={segmentBytes:N0}, fileSizeBytes={info.Length:N0}, " +
            $"segmentCount={Manifest.Segments.Count:N0}.");
    }

    private void ReportFault(Exception ex)
    {
        bool shouldRaise;
        lock (_sync)
        {
            shouldRaise = !_faulted;
            _faulted = true;
            _faultMessage = ex.Message;
            _acceptingFrames = false;
        }

        if (!shouldRaise)
        {
            return;
        }

        Manifest.Status = LiveRecordingManifestStatuses.Interrupted;
        Manifest.ErrorMessage = ex.Message;
        Manifest.EndedUtc = DateTimeOffset.UtcNow;
        SaveManifest();
        LogError(
            $"Live recording faulted: {ex.GetType().Name}: {ex.Message}. " +
            $"segments={Manifest.Segments.Count:N0}, totalPcmBytes={_totalPcmBytes:N0}, queueCount={_queue.Count:N0}.");
        Faulted?.Invoke(this, ex);
    }

    private void UpdateManifestTotals()
    {
        Manifest.TotalDurationSeconds = Manifest.Segments.Sum(segment => segment.DurationSeconds);
        Manifest.TotalFileSizeBytes = Manifest.Segments.Sum(segment => segment.FileSizeBytes);
    }

    private void SaveManifest()
    {
        Directory.CreateDirectory(_recordingDirectoryPath);
        string json = JsonSerializer.Serialize(Manifest, JsonOptions);
        WriteAllTextAtomic(_manifestPath, json);
        LogDebug(
            $"Manifest saved. status='{Manifest.Status}', segments={Manifest.Segments.Count:N0}, " +
            $"totalDuration={Manifest.TotalDurationSeconds:0.###}s, totalFileSizeBytes={Manifest.TotalFileSizeBytes:N0}.");
    }

    private void CleanupStaleTempSegments()
    {
        if (!Directory.Exists(_recordingDirectoryPath))
        {
            return;
        }

        int deletedCount = 0;
        foreach (string tempFile in Directory.EnumerateFiles(_recordingDirectoryPath, "segment-*.wav.tmp"))
        {
            TryDelete(tempFile);
            deletedCount++;
        }

        if (deletedCount > 0)
        {
            Log($"Deleted {deletedCount:N0} stale temporary live recording segment file(s) in '{_recordingDirectoryPath}'.");
        }
    }

    private static void WriteAllTextAtomic(string targetPath, string content)
    {
        string directory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(directory);

        string tempPath = Path.Combine(directory, $"{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, content);

        try
        {
            if (File.Exists(targetPath))
            {
                File.Replace(tempPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, targetPath);
            }
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static long GetAlignedByteCount(TimeSpan duration, WaveFormat waveFormat)
    {
        long rawByteCount = (long)Math.Ceiling(duration.TotalSeconds * Math.Max(waveFormat.AverageBytesPerSecond, 1));
        long blockAlign = Math.Max(waveFormat.BlockAlign, 1);
        long aligned = rawByteCount - (rawByteCount % blockAlign);
        return aligned > 0 ? aligned : blockAlign;
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

    private static string NormalizeRelativePath(string relativePath)
    {
        return relativePath.Replace('\\', '/');
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup for stale temporary recording files.
        }
    }

    private void Log(string message)
    {
        _processLogService?.Log("LiveRecording", message);
    }

    private void LogDebug(string message)
    {
        _processLogService?.Log("LiveRecording", message, ProcessLogLevel.Debug);
    }

    private void LogError(string message)
    {
        _processLogService?.Log("LiveRecording", message, ProcessLogLevel.Error);
    }

    private sealed record LiveRecordingFrame(byte[] Buffer);
}
