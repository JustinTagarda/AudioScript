using System.Diagnostics;
using System.IO;
using AudioScript.Abstractions;
using AudioScript.Audio;
using NAudio.Wave;
using Whisper.net;

namespace AudioScript.Services;

public sealed class WhisperAudioTranscriptionService : IAudioTranscriptionService, IPlaybackTranscriptionService
{
    internal static readonly TimeSpan ShortAudioPromptSuppressionDuration = TimeSpan.FromSeconds(10);
    private readonly AudioStandardizer _audioStandardizer;
    private readonly TranscriptionOptions _options;
    private readonly ProcessLogService _processLogService;
    private readonly WhisperModelManager _whisperModelManager;
    private readonly SemaphoreSlim _modelSemaphore = new(1, 1);
    private readonly SemaphoreSlim _transcriptionSemaphore = new(1, 1);

    private WhisperFactory? _factory;
    private string? _loadedModelPath;

    public WhisperAudioTranscriptionService(
        AudioStandardizer audioStandardizer,
        TranscriptionOptions options,
        ProcessLogService processLogService,
        WhisperModelManager whisperModelManager)
    {
        _audioStandardizer = audioStandardizer;
        _options = options;
        _processLogService = processLogService;
        _whisperModelManager = whisperModelManager;
    }

    public async Task<TranscriptionResult> TranscribeAudioFileAsync(
        string audioFilePath,
        string model,
        CancellationToken cancellationToken,
        IProgress<TranscriptionProgressSnapshot>? progress = null)
    {
        ValidateModel(model);
        string fullPath = ValidateAudioFilePath(audioFilePath);
        string fileName = Path.GetFileName(fullPath);
        var fileInfo = new FileInfo(fullPath);
        var progressReporter = new TranscriptionProgressReporter(progress);

        Log($"Starting local Whisper transcription for '{fileName}' ({fileInfo.Length:N0} bytes) using '{model}'.");
        progressReporter.Report(
            TranscriptionProgressPhase.PreparingAudio,
            0,
            TimeSpan.Zero,
            TimeSpan.Zero,
            $"Preparing {fileName}.",
            currentChunk: 1,
            totalChunks: 1,
            force: true);
        var stopwatch = Stopwatch.StartNew();

        string standardizedPath = _audioStandardizer.ConvertFileToEngineWav(fullPath);
        try
        {
            TimeSpan duration = ResolveAudioDuration(standardizedPath);
            IReadOnlyList<TranscriptionTimedLine> timedLines = await ProcessWaveFileAsync(
                standardizedPath,
                model,
                duration,
                cancellationToken,
                progressReporter);
            stopwatch.Stop();

            Log(
                $"Local Whisper transcription for '{fileName}' completed in {stopwatch.Elapsed.TotalSeconds:F2}s " +
                $"with {timedLines.Count:N0} timed line(s).");
            progressReporter.Report(
                TranscriptionProgressPhase.Completed,
                100,
                duration,
                duration,
                $"Completed {fileName}.",
                currentChunk: 1,
                totalChunks: 1,
                force: true);

            return new TranscriptionResult(
                Text: BuildText(timedLines),
                Model: model,
                CreatedAt: DateTimeOffset.UtcNow,
                Duration: duration,
                TokenLogprobs: Array.Empty<TranscriptionTokenLogprob>(),
                LowConfidenceTokens: Array.Empty<LowConfidenceToken>(),
                TimedLines: timedLines);
        }
        catch (Exception ex)
        {
            _processLogService.LogException("WhisperLocal", $"Local Whisper transcription failed for '{fileName}'.", ex);
            throw;
        }
        finally
        {
            DeleteTemporaryFile(standardizedPath);
        }
    }

    public async Task<string> TranscribePcmChunkAsync(
        byte[] pcmAudio,
        WaveFormat sourceFormat,
        string model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pcmAudio);
        ArgumentNullException.ThrowIfNull(sourceFormat);
        ValidateModel(model);

        if (pcmAudio.Length == 0)
        {
            throw new InvalidOperationException("Playback audio chunk was empty.");
        }

        byte[] standardizedWaveBytes = _audioStandardizer.ConvertPcmBytesToEngineWav(pcmAudio, sourceFormat);
        Log($"Starting local Whisper playback transcription chunk ({standardizedWaveBytes.Length:N0} wav bytes) using '{model}'.");

        try
        {
            IReadOnlyList<TranscriptionTimedLine> timedLines;
            await using (var waveStream = new MemoryStream(standardizedWaveBytes, writable: false))
            {
                timedLines = await ProcessWaveStreamAsync(waveStream, model, cancellationToken);
            }

            return BuildText(timedLines).Trim();
        }
        catch (Exception ex)
        {
            _processLogService.LogException("WhisperLocal", "Local Whisper playback transcription failed.", ex);
            throw;
        }
    }

    private async Task<IReadOnlyList<TranscriptionTimedLine>> ProcessWaveFileAsync(
        string waveFilePath,
        string model,
        TimeSpan duration,
        CancellationToken cancellationToken,
        TranscriptionProgressReporter? progressReporter = null)
    {
        using FileStream stream = File.OpenRead(waveFilePath);
        return await ProcessWaveStreamAsync(stream, model, cancellationToken, duration, progressReporter);
    }

    private async Task<IReadOnlyList<TranscriptionTimedLine>> ProcessWaveStreamAsync(
        Stream waveStream,
        string model,
        CancellationToken cancellationToken,
        TimeSpan? duration = null,
        TranscriptionProgressReporter? progressReporter = null)
    {
        WhisperFactory factory = await GetFactoryAsync(model, cancellationToken);

        await _transcriptionSemaphore.WaitAsync(cancellationToken);
        try
        {
            var builder = factory
                .CreateBuilder()
                .WithLanguageDetection()
                .WithTemperature(0)
                .WithPrintProgress()
                .WithPrintTimestamps(true);

            string prompt = _options.Prompt.Trim();
            if (ShouldApplyPrompt(duration) && !string.IsNullOrWhiteSpace(prompt))
            {
                builder.WithPrompt(prompt);
            }

            var lines = new List<TranscriptionTimedLine>();
            await using WhisperProcessor processor = builder.Build();
            TimeSpan totalAudio = duration ?? TimeSpan.Zero;
            progressReporter?.Report(
                TranscriptionProgressPhase.TranscribingChunk,
                0,
                TimeSpan.Zero,
                totalAudio,
                "Transcribing audio.",
                currentChunk: 1,
                totalChunks: 1,
                force: true);

            await foreach (SegmentData segment in processor.ProcessAsync(waveStream, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (totalAudio > TimeSpan.Zero)
                {
                    TimeSpan processedAudio = segment.End > TimeSpan.Zero
                        ? segment.End
                        : segment.Start;
                    if (processedAudio > totalAudio)
                    {
                        processedAudio = totalAudio;
                    }

                    progressReporter?.Report(
                        TranscriptionProgressPhase.TranscribingChunk,
                        (processedAudio.TotalSeconds / totalAudio.TotalSeconds) * 100d,
                        processedAudio,
                        totalAudio,
                        "Transcribing audio.",
                        currentChunk: 1,
                        totalChunks: 1);
                }

                string text = segment.Text.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                lines.Add(new TranscriptionTimedLine(
                    Text: text,
                    StartOffset: segment.Start,
                    EndOffset: segment.End,
                    IsTimestampEstimated: false));
            }

            return lines;
        }
        finally
        {
            _transcriptionSemaphore.Release();
        }
    }

    private async Task<WhisperFactory> GetFactoryAsync(string model, CancellationToken cancellationToken)
    {
        string modelPath = _whisperModelManager.ResolveInstalledModelPath(model);

        if (_factory is not null
            && string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
        {
            return _factory;
        }

        await _modelSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_factory is not null
                && string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                return _factory;
            }

            WhisperFactory factory = WhisperFactory.FromPath(
                modelPath,
                new WhisperFactoryOptions
                {
                    UseGpu = true,
                });
            string runtimeInfo = WhisperFactory.GetRuntimeInfo() ?? "unknown";
            _factory?.Dispose();
            _factory = factory;
            _loadedModelPath = modelPath;
            Log($"Loaded local Whisper model '{Path.GetFileName(modelPath)}' with runtime: {runtimeInfo}.");
            return factory;
        }
        finally
        {
            _modelSemaphore.Release();
        }
    }

    private static string ValidateModel(string model)
    {
        string trimmed = model?.Trim() ?? string.Empty;
        if (!TranscriptionModelCatalog.IsLocalWhisper(trimmed))
        {
            throw new InvalidOperationException(
                $"Unsupported local Whisper model '{model}'.");
        }

        return trimmed;
    }

    private static string ValidateAudioFilePath(string audioFilePath)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath))
        {
            throw new ArgumentException("Audio file path is required.", nameof(audioFilePath));
        }

        string fullPath = Path.GetFullPath(audioFilePath.Trim());
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Audio file was not found.", fullPath);
        }

        using FileStream _ = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return fullPath;
    }

    private static TimeSpan ResolveAudioDuration(string waveFilePath)
    {
        using var reader = new WaveFileReader(waveFilePath);
        return reader.TotalTime;
    }

    private static string BuildText(IReadOnlyList<TranscriptionTimedLine> timedLines)
    {
        return string.Join(
            Environment.NewLine,
            timedLines.Select(line => line.Text.Trim()).Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    internal static bool ShouldApplyPrompt(TimeSpan? duration)
    {
        return duration is null || duration.Value > ShortAudioPromptSuppressionDuration;
    }

    private static void DeleteTemporaryFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Best-effort cleanup for generated audio/model download files.
        }
    }

    private void Log(string message)
    {
        _processLogService.Log("WhisperLocal", message);
    }
}
