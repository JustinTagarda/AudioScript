using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AudioScript.Abstractions;
using AudioScript.Audio;
using NAudio.Wave;

namespace AudioScript.Services;

public sealed class WhisperAudioTranscriptionService : IConfigurableAudioTranscriptionService, IPlaybackTranscriptionService
{
    private const string WhisperCliAssetId = "whisper-cpp-cli-x64";
    private const string WhisperCliExecutableRelativePath = "Release\\whisper-cli.exe";
    private static readonly TimeSpan WhisperHeartbeatInterval = TimeSpan.FromSeconds(1);
    private const double WhisperHeartbeatStartPercent = 50d;
    private const double WhisperHeartbeatMaxPercent = 95d;
    private const double WhisperRealtimeDivisor = 1.2d;
    private const int MaxConcurrentTranscriptions = 2;
    internal static readonly TimeSpan ShortAudioPromptSuppressionDuration = TimeSpan.FromSeconds(10);

    private readonly AudioStandardizer _audioStandardizer;
    private readonly TranscriptionOptions _options;
    private readonly ProcessLogService _processLogService;
    private readonly WhisperModelManager _whisperModelManager;
    private readonly IAssetProvisioningService _assetProvisioningService;
    private readonly AppDataPathProvider _paths;
    private readonly SemaphoreSlim _transcriptionSemaphore = new(MaxConcurrentTranscriptions, MaxConcurrentTranscriptions);

    public WhisperAudioTranscriptionService(
        AudioStandardizer audioStandardizer,
        TranscriptionOptions options,
        ProcessLogService processLogService,
        WhisperModelManager whisperModelManager,
        IAssetProvisioningService assetProvisioningService,
        AppDataPathProvider paths)
    {
        _audioStandardizer = audioStandardizer;
        _options = options;
        _processLogService = processLogService;
        _whisperModelManager = whisperModelManager;
        _assetProvisioningService = assetProvisioningService;
        _paths = paths;
    }

    public async Task<TranscriptionResult> TranscribeAudioFileAsync(
        string audioFilePath,
        string model,
        CancellationToken cancellationToken,
        IProgress<TranscriptionProgressSnapshot>? progress = null)
    {
        return await TranscribeAudioFileAsync(
            audioFilePath,
            model,
            new AudioTranscriptionRequestOptions(),
            cancellationToken,
            progress);
    }

    public async Task<TranscriptionResult> TranscribeAudioFileAsync(
        string audioFilePath,
        string model,
        AudioTranscriptionRequestOptions options,
        CancellationToken cancellationToken,
        IProgress<TranscriptionProgressSnapshot>? progress = null)
    {
        ValidateModel(model);
        string fullPath = ValidateAudioFilePath(audioFilePath);
        string fileName = Path.GetFileName(fullPath);
        var fileInfo = new FileInfo(fullPath);
        var progressReporter = new TranscriptionProgressReporter(progress);

        Log($"Starting whisper.cpp transcription for '{fileName}' ({fileInfo.Length:N0} bytes) using '{model}'.");
        _processLogService.UpdateCrashContext(
            "whispercpp.file.start",
            $"model='{model}', file='{fullPath}', bytes={fileInfo.Length}");
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

        bool usingEngineWaveInput = options.IsEngineWaveInput;
        string standardizedPath = usingEngineWaveInput
            ? fullPath
            : _audioStandardizer.ConvertFileToEngineWav(fullPath);
        try
        {
            TimeSpan duration = ResolveAudioDuration(standardizedPath);
            _processLogService.UpdateCrashContext(
                "whispercpp.file.standardized_ready",
                $"model='{model}', standardizedPath='{standardizedPath}', durationMs={duration.TotalMilliseconds:F0}");
            IReadOnlyList<TranscriptionTimedLine> timedLines = await TranscribeWaveFileAsync(
                standardizedPath,
                model,
                duration,
                options,
                cancellationToken,
                progressReporter);
            stopwatch.Stop();

            Log(
                $"whisper.cpp transcription for '{fileName}' completed in {stopwatch.Elapsed.TotalSeconds:F2}s " +
                $"with {timedLines.Count:N0} timed line(s).");
            _processLogService.UpdateCrashContext(
                "whispercpp.file.completed",
                $"model='{model}', lines={timedLines.Count}, elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F0}");
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
            _processLogService.UpdateCrashContext("whispercpp.file.failed", ex.GetType().FullName);
            _processLogService.LogException("WhisperLocal", $"whisper.cpp transcription failed for '{fileName}'.", ex);
            throw;
        }
        finally
        {
            if (!usingEngineWaveInput)
            {
                DeleteTemporaryFile(standardizedPath);
            }
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
        Log($"Starting whisper.cpp playback transcription chunk ({standardizedWaveBytes.Length:N0} wav bytes) using '{model}'.");

        string tempWavePath = CreateTemporaryWavePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tempWavePath)!);
            await File.WriteAllBytesAsync(tempWavePath, standardizedWaveBytes, cancellationToken);
            TimeSpan duration = ResolveAudioDuration(tempWavePath);
            IReadOnlyList<TranscriptionTimedLine> timedLines = await TranscribeWaveFileAsync(
                tempWavePath,
                model,
                duration,
                new AudioTranscriptionRequestOptions(SuppressPrompt: true),
                cancellationToken,
                progressReporter: null);

            return BuildText(timedLines).Trim();
        }
        catch (Exception ex)
        {
            _processLogService.LogException("WhisperLocal", "whisper.cpp playback transcription failed.", ex);
            throw;
        }
        finally
        {
            DeleteTemporaryFile(tempWavePath);
        }
    }

    private async Task<IReadOnlyList<TranscriptionTimedLine>> TranscribeWaveFileAsync(
        string waveFilePath,
        string model,
        TimeSpan duration,
        AudioTranscriptionRequestOptions options,
        CancellationToken cancellationToken,
        TranscriptionProgressReporter? progressReporter)
    {
        string modelPath = _whisperModelManager.ResolveInstalledModelPath(model);
        string cliPath = await EnsureWhisperCliExecutableAsync(cancellationToken);
        _processLogService.UpdateCrashContext(
            "whispercpp.runtime.ready",
            $"model='{model}', cli='{cliPath}', modelPath='{modelPath}'");

        await _transcriptionSemaphore.WaitAsync(cancellationToken);
        try
        {
            progressReporter?.Report(
                TranscriptionProgressPhase.TranscribingChunk,
                5,
                TimeSpan.Zero,
                duration,
                "Starting Whisper transcription.",
                currentChunk: 1,
                totalChunks: 1,
                force: true);

            WhisperCliResult cliResult = await RunWhisperCliAsync(
                cliPath,
                modelPath,
                waveFilePath,
                duration,
                options,
                cancellationToken,
                progressReporter);

            _processLogService.UpdateCrashContext(
                "whispercpp.process.completed",
                $"model='{model}', segments={cliResult.TimedLines.Count}, elapsedMs={cliResult.Elapsed.TotalMilliseconds:F0}");
            return cliResult.TimedLines;
        }
        finally
        {
            _transcriptionSemaphore.Release();
        }
    }

    private async Task<string> EnsureWhisperCliExecutableAsync(CancellationToken cancellationToken)
    {
        AssetProvisioningStatus status = _assetProvisioningService.GetStatus(WhisperCliAssetId);
        if (status.State == AssetProvisioningState.Unsupported)
        {
            throw new PlatformNotSupportedException(
                "The local whisper.cpp runtime is not supported on this device architecture.");
        }

        if (!_assetProvisioningService.IsInstalled(WhisperCliAssetId))
        {
            Log("whisper.cpp CLI runtime is missing. Installing on demand.");
            _processLogService.UpdateCrashContext("whispercpp.runtime.installing");
            await _assetProvisioningService.InstallAssetAsync(WhisperCliAssetId, progress: null, cancellationToken);
        }

        string installDirectory = _assetProvisioningService.ResolveInstallPath(WhisperCliAssetId);
        string executablePath = Path.Combine(installDirectory, WhisperCliExecutableRelativePath);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                "The installed whisper.cpp CLI executable was not found.",
                executablePath);
        }

        return executablePath;
    }

    private async Task<WhisperCliResult> RunWhisperCliAsync(
        string cliPath,
        string modelPath,
        string waveFilePath,
        TimeSpan duration,
        AudioTranscriptionRequestOptions options,
        CancellationToken cancellationToken,
        TranscriptionProgressReporter? progressReporter)
    {
        string outputRoot = Path.Combine(_paths.TempPath, "whispercpp", Guid.NewGuid().ToString("N"));
        string workingDirectory = Path.GetDirectoryName(cliPath) ?? Environment.CurrentDirectory;
        Directory.CreateDirectory(Path.GetDirectoryName(outputRoot)!);

        string arguments = BuildCliArguments(modelPath, waveFilePath, outputRoot, duration, options);
        var startInfo = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        Log($"Launching whisper.cpp CLI. exe='{cliPath}', args='{arguments}'.");
        _processLogService.UpdateCrashContext(
            "whispercpp.process.start",
            $"cli='{cliPath}', wave='{waveFilePath}', output='{outputRoot}'");

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? heartbeatTask = null;

        try
        {
            progressReporter?.Report(
                TranscriptionProgressPhase.TranscribingChunk,
                50,
                TimeSpan.Zero,
                duration,
                "Transcribing audio.",
                currentChunk: 1,
                totalChunks: 1,
                force: true);

            var stopwatch = Stopwatch.StartNew();
            heartbeatTask = ReportWhisperHeartbeatAsync(
                progressReporter,
                stopwatch,
                duration,
                heartbeatCts.Token);
            await process.WaitForExitAsync(cancellationToken);
            heartbeatCts.Cancel();
            if (heartbeatTask is not null)
            {
                try
                {
                    await heartbeatTask;
                }
                catch (OperationCanceledException)
                {
                }
            }
            stopwatch.Stop();

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            string srtPath = outputRoot + ".srt";

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"whisper.cpp CLI exited with code {process.ExitCode}. {BuildProcessFailureDetails(stdout, stderr)}");
            }

            if (!File.Exists(srtPath))
            {
                throw new InvalidOperationException(
                    $"whisper.cpp CLI did not produce the expected SRT output. {BuildProcessFailureDetails(stdout, stderr)}");
            }

            string srtContent = await File.ReadAllTextAsync(srtPath, cancellationToken);
            IReadOnlyList<TranscriptionTimedLine> timedLines = ParseSrtTimedLines(srtContent);
            Log($"whisper.cpp CLI completed in {stopwatch.Elapsed.TotalSeconds:F2}s with exitCode=0.");
            return new WhisperCliResult(timedLines, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            heartbeatCts.Cancel();
            TryTerminateProcess(process);
            throw;
        }
        finally
        {
            DeleteTemporaryFile(outputRoot + ".srt");
            DeleteTemporaryFile(outputRoot + ".txt");
        }
    }

    private static async Task ReportWhisperHeartbeatAsync(
        TranscriptionProgressReporter? progressReporter,
        Stopwatch stopwatch,
        TimeSpan totalAudio,
        CancellationToken cancellationToken)
    {
        if (progressReporter is null || totalAudio <= TimeSpan.Zero)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(WhisperHeartbeatInterval, cancellationToken);

            double elapsedSeconds = Math.Max(0, stopwatch.Elapsed.TotalSeconds);
            double estimatedPercent = WhisperHeartbeatStartPercent +
                                      ((elapsedSeconds / Math.Max(1, totalAudio.TotalSeconds * WhisperRealtimeDivisor)) * 100d);
            estimatedPercent = Math.Clamp(estimatedPercent, WhisperHeartbeatStartPercent, WhisperHeartbeatMaxPercent);
            TimeSpan estimatedProcessedAudio = TimeSpan.FromTicks((long)(totalAudio.Ticks * (estimatedPercent / 100d)));

            progressReporter.Report(
                TranscriptionProgressPhase.TranscribingChunk,
                estimatedPercent,
                estimatedProcessedAudio,
                totalAudio,
                "Transcribing audio.",
                currentChunk: 1,
                totalChunks: 1);
        }
    }

    private string BuildCliArguments(
        string modelPath,
        string waveFilePath,
        string outputRoot,
        TimeSpan duration,
        AudioTranscriptionRequestOptions options)
    {
        var arguments = new List<string>
        {
            "-m", QuoteArgument(modelPath),
            "-f", QuoteArgument(waveFilePath),
            "-l", "auto",
            "-osrt",
            "-of", QuoteArgument(outputRoot),
            "-t", Math.Max(1, Environment.ProcessorCount / MaxConcurrentTranscriptions).ToString(CultureInfo.InvariantCulture),
            "-ng",
            "-np",
        };

        string prompt = _options.Prompt.Trim();
        bool suppressPrompt = options.SuppressPrompt;
        if (!suppressPrompt && ShouldApplyPrompt(duration) && !string.IsNullOrWhiteSpace(prompt))
        {
            arguments.Add("--prompt");
            arguments.Add(QuoteArgument(prompt));
            arguments.Add("--carry-initial-prompt");
        }

        return string.Join(" ", arguments);
    }

    internal static IReadOnlyList<TranscriptionTimedLine> ParseSrtTimedLines(string srtContent)
    {
        if (string.IsNullOrWhiteSpace(srtContent))
        {
            return Array.Empty<TranscriptionTimedLine>();
        }

        var lines = new List<TranscriptionTimedLine>();
        string normalized = srtContent.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] blocks = normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        foreach (string rawBlock in blocks)
        {
            string[] blockLines = rawBlock
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (blockLines.Length < 2)
            {
                continue;
            }

            int timestampIndex = blockLines[0].Contains("-->", StringComparison.Ordinal) ? 0 : 1;
            if (timestampIndex >= blockLines.Length)
            {
                continue;
            }

            string timestampLine = blockLines[timestampIndex];
            string[] timestamps = timestampLine.Split("-->", StringSplitOptions.TrimEntries);
            if (timestamps.Length != 2
                || !TryParseSrtTimestamp(timestamps[0], out TimeSpan start)
                || !TryParseSrtTimestamp(timestamps[1], out TimeSpan end))
            {
                continue;
            }

            string text = string.Join(" ", blockLines.Skip(timestampIndex + 1)).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            lines.Add(new TranscriptionTimedLine(text, start, end, IsTimestampEstimated: false));
        }

        return lines;
    }

    private static bool TryParseSrtTimestamp(string value, out TimeSpan timestamp)
    {
        return TimeSpan.TryParseExact(
            value.Trim(),
            "hh\\:mm\\:ss\\,fff",
            CultureInfo.InvariantCulture,
            out timestamp);
    }

    private static string QuoteArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static string BuildProcessFailureDetails(string stdout, string stderr)
    {
        string stderrSnippet = BuildSnippet(stderr);
        string stdoutSnippet = BuildSnippet(stdout);
        return $"stderr='{stderrSnippet}', stdout='{stdoutSnippet}'.";
    }

    private static string BuildSnippet(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(empty)";
        }

        string normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }

    private static void TryTerminateProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cancellation cleanup.
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

    private string CreateTemporaryWavePath()
    {
        return Path.Combine(_paths.TempPath, "whispercpp", $"{Guid.NewGuid():N}.wav");
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
            // Best-effort cleanup for generated audio/output files.
        }
    }

    private void Log(string message)
    {
        _processLogService.Log("WhisperLocal", message);
    }

    private sealed record WhisperCliResult(
        IReadOnlyList<TranscriptionTimedLine> TimedLines,
        TimeSpan Elapsed);
}
