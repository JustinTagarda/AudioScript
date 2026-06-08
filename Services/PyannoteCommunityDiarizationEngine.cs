using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioScript.Abstractions;
using AudioScript.Audio;

namespace AudioScript.Services;

public sealed class PyannoteCommunityDiarizationEngine : ISpeakerDiarizationEngine, IPyannoteExecutionProbe
{
    private readonly AudioStandardizer _audioStandardizer;
    private readonly PyannoteCommunityModelManager _modelManager;
    private readonly ProcessLogService _processLogService;
    private readonly IPyannoteCommunityProcessRunner _processRunner;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _disposed;

    public PyannoteCommunityDiarizationEngine(
        AudioStandardizer audioStandardizer,
        PyannoteCommunityModelManager modelManager,
        ProcessLogService processLogService,
        IPyannoteCommunityProcessRunner? processRunner = null)
    {
        _audioStandardizer = audioStandardizer;
        _modelManager = modelManager;
        _processLogService = processLogService;
        _processRunner = processRunner ?? new PyannoteCommunityProcessRunner();
    }

    public async Task<IReadOnlyList<SpeakerDiarizationTurn>> DiarizeAudioFileAsync(
        string audioFilePath,
        CancellationToken cancellationToken,
        IProgress<SpeakerDiarizationProgress>? progress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string fullPath = ValidateAudioFilePath(audioFilePath);
        string fileName = Path.GetFileName(fullPath);
        var stopwatch = Stopwatch.StartNew();

        Log(
            $"Starting pyannote Community-1 diarization for '{fileName}'. " +
            $"audioPath='{fullPath}', runtimeContext={_modelManager.DescribeExecutionContext()}");
        _modelManager.EnsureExecutionReady();
        progress?.Report(new SpeakerDiarizationProgress(0, 1));

        string standardizedPath = _audioStandardizer.ConvertFileToEngineWav(fullPath);
        Log(
            $"Prepared pyannote input WAV for '{fileName}'. " +
            $"standardizedPath='{standardizedPath}', standardizedExists={File.Exists(standardizedPath)}, " +
            $"standardizedBytes={(File.Exists(standardizedPath) ? new FileInfo(standardizedPath).Length : 0):N0}.");
        try
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                PyannoteCommunityProcessResult result = await RunPyannoteProcessAsync(
                    standardizedPath,
                    fileName,
                    HandleStandardErrorLine,
                    cancellationToken);

                if (result.ExitCode != 0)
                {
                    Log(
                        $"Pyannote Community-1 process exited with code {result.ExitCode} for '{fileName}'. " +
                        $"stdoutBytes={result.StandardOutput.Length:N0}, stderrBytes={result.StandardError.Length:N0}.");
                    throw new InvalidOperationException(
                        $"Pyannote Community-1 exited with code {result.ExitCode}. {FormatProcessError(result.StandardError)}");
                }

                SpeakerDiarizationTurn[] turns = ParseTurns(result.StandardOutput);
                progress?.Report(new SpeakerDiarizationProgress(1, 1));

                stopwatch.Stop();
                Log(
                    $"Pyannote Community-1 diarization for '{fileName}' completed in {stopwatch.Elapsed.TotalSeconds:F2}s " +
                    $"with {turns.Length:N0} speaker turn(s). stdoutBytes={result.StandardOutput.Length:N0}, stderrBytes={result.StandardError.Length:N0}.");
                return turns;
            }
            finally
            {
                _semaphore.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _processLogService.LogException("PyannoteDiarization", $"Pyannote Community-1 diarization failed for '{fileName}'.", ex);
            throw;
        }
        finally
        {
            DeleteTemporaryFile(standardizedPath);
        }
    }

    public async Task<PyannoteExecutionProbeResult> ProbeExecutionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string probeDirectory = Path.GetDirectoryName(_modelManager.RunnerScriptPath) ?? Path.GetTempPath();
        Directory.CreateDirectory(probeDirectory);
        string probeWavePath = Path.Combine(
            probeDirectory,
            $"pyannote-execution-probe-{Guid.NewGuid():N}.wav");

        try
        {
            _modelManager.EnsureExecutionReady();
            CreateProbeWaveFile(probeWavePath);
            Log(
                $"Starting pyannote execution probe. audioPath='{probeWavePath}', " +
                $"runtimeContext={_modelManager.DescribeExecutionContext()}");

            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                PyannoteCommunityProcessResult result = await RunPyannoteProcessAsync(
                    probeWavePath,
                    Path.GetFileName(probeWavePath),
                    HandleStandardErrorLine,
                    cancellationToken);
                if (result.ExitCode != 0)
                {
                    string failureMessage = $"Pyannote Community-1 exited with code {result.ExitCode}. {FormatProcessError(result.StandardError)}";
                    Log($"Pyannote execution probe failed. {failureMessage}");
                    return new PyannoteExecutionProbeResult(false, failureMessage);
                }

                _ = ParseTurns(result.StandardOutput);
                Log("Pyannote execution probe completed successfully.");
                return new PyannoteExecutionProbeResult(true, "Speaker detection runtime is ready.");
            }
            finally
            {
                _semaphore.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _processLogService.LogException("PyannoteDiarization", "Pyannote execution probe failed.", ex);
            return new PyannoteExecutionProbeResult(false, ex.Message);
        }
        finally
        {
            DeleteTemporaryFile(probeWavePath);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _semaphore.Dispose();
    }

    private static SpeakerDiarizationTurn[] ParseTurns(string standardOutput)
    {
        PyannoteTurnDto[]? dtos = JsonSerializer.Deserialize<PyannoteTurnDto[]>(
            standardOutput,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (dtos is null)
        {
            throw new InvalidOperationException("Pyannote Community-1 did not return diarization output.");
        }

        var speakerLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return dtos
            .Where(turn => !string.IsNullOrWhiteSpace(turn.Speaker) && turn.End > turn.Start)
            .OrderBy(turn => turn.Start)
            .Select(turn => new SpeakerDiarizationTurn(
                Speaker: ResolveSpeakerLabel(turn.Speaker, speakerLabels),
                StartOffset: TimeSpan.FromSeconds(Math.Max(0, turn.Start)),
                EndOffset: TimeSpan.FromSeconds(Math.Max(turn.Start, turn.End))))
            .Where(turn => turn.EndOffset > turn.StartOffset)
            .ToArray();
    }

    private static string ResolveSpeakerLabel(string sourceLabel, Dictionary<string, string> speakerLabels)
    {
        string trimmed = sourceLabel.Trim();
        if (speakerLabels.TryGetValue(trimmed, out string? mapped))
        {
            return mapped;
        }

        if (trimmed.StartsWith("SPEAKER_", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(trimmed["SPEAKER_".Length..], out int zeroBasedIndex))
        {
            mapped = $"speaker_{zeroBasedIndex + 1}";
        }
        else
        {
            mapped = $"speaker_{speakerLabels.Count + 1}";
        }

        speakerLabels[trimmed] = mapped;
        return mapped;
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

    private static string FormatProcessError(string standardError)
    {
        string trimmed = standardError.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "No error output was returned.";
        }

        const int maxLength = 1_000;
        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[..maxLength] + "...";
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
            // Best-effort cleanup for generated diarization WAV files.
        }
    }

    private async Task<PyannoteCommunityProcessResult> RunPyannoteProcessAsync(
        string audioFilePath,
        string fileName,
        Action<string>? onStandardErrorLine,
        CancellationToken cancellationToken)
    {
        Log(
            $"Launching pyannote Community-1 process for '{fileName}'. " +
            $"pythonExe='{_modelManager.PythonExecutablePath}', runnerScript='{_modelManager.RunnerScriptPath}', " +
            $"modelDir='{_modelManager.ModelDirectoryPath}', audioPath='{audioFilePath}'.");
        return await _processRunner.RunAsync(
            _modelManager.PythonExecutablePath,
            _modelManager.RunnerScriptPath,
            _modelManager.ModelDirectoryPath,
            audioFilePath,
            onStandardErrorLine,
            cancellationToken);
    }

    private void Log(string message)
    {
        _processLogService.Log("PyannoteDiarization", message);
    }

    private void HandleStandardErrorLine(string line)
    {
        string trimmed = line?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return;
        }

        string? mappedMessage = trimmed switch
        {
            "runner_started" => "Pyannote runner started.",
            "model_loading" => "Pyannote model loading.",
            "model_loaded" => "Pyannote model loaded.",
            "waveform_loading" => "Pyannote waveform loading.",
            "waveform_loaded" => "Pyannote waveform loaded.",
            "inference_started" => "Pyannote inference started.",
            "inference_finished" => "Pyannote inference finished.",
            "serializing_turns" => "Pyannote serializing speaker turns.",
            "completed" => "Pyannote runner completed.",
            _ => null,
        };

        Log(mappedMessage ?? $"Pyannote stderr: {trimmed}");
    }

    private static void CreateProbeWaveFile(string filePath)
    {
        const short channels = 1;
        const int sampleRate = 16000;
        const short bitsPerSample = 16;
        const short bytesPerSample = bitsPerSample / 8;
        const int durationMilliseconds = 1000;
        int sampleCount = sampleRate * durationMilliseconds / 1000;
        short blockAlign = (short)(channels * bytesPerSample);
        int byteRate = sampleRate * blockAlign;
        int dataSize = sampleCount * blockAlign;

        using var stream = File.Create(filePath);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        for (int i = 0; i < sampleCount; i++)
        {
            writer.Write((short)0);
        }
    }

    private sealed record PyannoteTurnDto(
        [property: JsonPropertyName("speaker")] string Speaker,
        [property: JsonPropertyName("start")] double Start,
        [property: JsonPropertyName("end")] double End
    );
}
