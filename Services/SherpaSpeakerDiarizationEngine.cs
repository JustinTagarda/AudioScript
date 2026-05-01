using System.Diagnostics;
using System.IO;
using AudioScript.Abstractions;
using AudioScript.Audio;
using NAudio.Wave;
using SherpaOnnx;

namespace AudioScript.Services;

public sealed class SherpaSpeakerDiarizationEngine : ISpeakerDiarizationEngine, IDisposable {
    private readonly AudioStandardizer _audioStandardizer;
    private readonly SherpaDiarizationModelManager _modelManager;
    private readonly ProcessLogService _processLogService;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private OfflineSpeakerDiarization? _diarizer;
    private bool _disposed;

    public SherpaSpeakerDiarizationEngine(
        AudioStandardizer audioStandardizer,
        SherpaDiarizationModelManager modelManager,
        ProcessLogService processLogService) {
        _audioStandardizer = audioStandardizer;
        _modelManager = modelManager;
        _processLogService = processLogService;
    }

    public async Task<IReadOnlyList<SpeakerDiarizationTurn>> DiarizeAudioFileAsync(
        string audioFilePath,
        CancellationToken cancellationToken,
        IProgress<SpeakerDiarizationProgress>? progress = null) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string fullPath = ValidateAudioFilePath(audioFilePath);
        string fileName = Path.GetFileName(fullPath);
        var stopwatch = Stopwatch.StartNew();

        Log($"Starting sherpa-onnx diarization for '{fileName}'.");
        string standardizedPath = _audioStandardizer.ConvertFileToEngineWav(fullPath);
        try {
            float[] samples = ReadPcm16MonoSamples(standardizedPath);
            await _semaphore.WaitAsync(cancellationToken);
            try {
                OfflineSpeakerDiarization diarizer = GetOrCreateDiarizer();
                int lastReportedPercent = -1;
                DateTimeOffset lastReportedUtc = DateTimeOffset.MinValue;
                OfflineSpeakerDiarizationSegment[] segments = await Task.Run(
                    () => diarizer.ProcessWithCallback(
                        samples,
                        (processed, total, _) => {
                            if (cancellationToken.IsCancellationRequested) {
                                return 0;
                            }

                            ReportDiarizationProgress(
                                progress,
                                processed,
                                total,
                                ref lastReportedPercent,
                                ref lastReportedUtc);
                            return 1;
                        },
                        IntPtr.Zero),
                    cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                SpeakerDiarizationTurn[] turns = segments
                    .OrderBy(segment => segment.Start)
                    .Select(segment => new SpeakerDiarizationTurn(
                        Speaker: $"speaker_{segment.Speaker + 1}",
                        StartOffset: TimeSpan.FromSeconds(Math.Max(0, segment.Start)),
                        EndOffset: TimeSpan.FromSeconds(Math.Max(segment.Start, segment.End))))
                    .Where(turn => turn.EndOffset > turn.StartOffset)
                    .ToArray();

                stopwatch.Stop();
                Log(
                    $"Sherpa-onnx diarization for '{fileName}' completed in {stopwatch.Elapsed.TotalSeconds:F2}s " +
                    $"with {turns.Length:N0} speaker turn(s).");
                return turns;
            }
            finally {
                _semaphore.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            _processLogService.LogException("SherpaDiarization", $"Sherpa-onnx diarization failed for '{fileName}'.", ex);
            throw;
        }
        finally {
            DeleteTemporaryFile(standardizedPath);
        }
    }

    private static void ReportDiarizationProgress(
        IProgress<SpeakerDiarizationProgress>? progress,
        long processed,
        long total,
        ref int lastReportedPercent,
        ref DateTimeOffset lastReportedUtc) {
        if (progress is null || total <= 0) {
            return;
        }

        int percent = (int)Math.Clamp(Math.Floor(processed * 100d / total), 0, 100);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (percent == lastReportedPercent && now - lastReportedUtc < TimeSpan.FromMilliseconds(500)) {
            return;
        }

        lastReportedPercent = percent;
        lastReportedUtc = now;
        progress.Report(new SpeakerDiarizationProgress(processed, total));
    }

    public void Dispose() {
        _disposed = true;
        _diarizer?.Dispose();
        _semaphore.Dispose();
    }

    private OfflineSpeakerDiarization GetOrCreateDiarizer() {
        if (_diarizer is not null) {
            return _diarizer;
        }

        _modelManager.EnsureInstalled();
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = _modelManager.SegmentationModelPath;
        config.Segmentation.NumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        config.Segmentation.Provider = "cpu";
        config.Embedding.Model = _modelManager.EmbeddingModelPath;
        config.Embedding.NumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        config.Embedding.Provider = "cpu";
        config.Clustering.NumClusters = -1;
        config.Clustering.Threshold = 0.5f;

        _diarizer = new OfflineSpeakerDiarization(config);
        Log(
            $"Loaded sherpa-onnx diarization models. sampleRate={_diarizer.SampleRate}, " +
            $"segmentation='{Path.GetFileName(_modelManager.SegmentationModelPath)}', " +
            $"embedding='{Path.GetFileName(_modelManager.EmbeddingModelPath)}'.");
        return _diarizer;
    }

    private static float[] ReadPcm16MonoSamples(string waveFilePath) {
        using var reader = new WaveFileReader(waveFilePath);
        WaveFormat format = reader.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.Pcm
            || format.SampleRate != AudioFormatConstants.EngineWaveFormat.SampleRate
            || format.BitsPerSample != 16
            || format.Channels != 1) {
            throw new InvalidOperationException(
                $"Expected {AudioFormatConstants.EngineWaveFormat.SampleRate} Hz mono 16-bit PCM WAV for diarization.");
        }

        byte[] bytes = new byte[reader.Length];
        int offset = 0;
        while (offset < bytes.Length) {
            int read = reader.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) {
                break;
            }

            offset += read;
        }

        int sampleCount = offset / 2;
        var samples = new float[sampleCount];
        for (int index = 0; index < sampleCount; index++) {
            short sample = BitConverter.ToInt16(bytes, index * 2);
            samples[index] = sample / 32768f;
        }

        return samples;
    }

    private static string ValidateAudioFilePath(string audioFilePath) {
        if (string.IsNullOrWhiteSpace(audioFilePath)) {
            throw new ArgumentException("Audio file path is required.", nameof(audioFilePath));
        }

        string fullPath = Path.GetFullPath(audioFilePath.Trim());
        if (!File.Exists(fullPath)) {
            throw new FileNotFoundException("Audio file was not found.", fullPath);
        }

        using FileStream _ = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return fullPath;
    }

    private static void DeleteTemporaryFile(string filePath) {
        try {
            if (File.Exists(filePath)) {
                File.Delete(filePath);
            }
        }
        catch {
            // Best-effort cleanup for generated diarization WAV files.
        }
    }

    private void Log(string message) {
        _processLogService.Log("SherpaDiarization", message);
    }
}
