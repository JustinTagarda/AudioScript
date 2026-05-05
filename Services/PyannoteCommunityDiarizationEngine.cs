using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioScript.Abstractions;
using AudioScript.Audio;

namespace AudioScript.Services;

public sealed class PyannoteCommunityDiarizationEngine : ISpeakerDiarizationEngine
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

        Log($"Starting pyannote Community-1 diarization for '{fileName}'.");
        _modelManager.EnsureInstalled();
        progress?.Report(new SpeakerDiarizationProgress(0, 1));

        string standardizedPath = _audioStandardizer.ConvertFileToEngineWav(fullPath);
        try
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                PyannoteCommunityProcessResult result = await _processRunner.RunAsync(
                    _modelManager.PythonExecutablePath,
                    _modelManager.RunnerScriptPath,
                    _modelManager.ModelDirectoryPath,
                    standardizedPath,
                    HandleStandardErrorLine,
                    cancellationToken);

                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Pyannote Community-1 exited with code {result.ExitCode}. {FormatProcessError(result.StandardError)}");
                }

                SpeakerDiarizationTurn[] turns = ParseTurns(result.StandardOutput);
                progress?.Report(new SpeakerDiarizationProgress(1, 1));

                stopwatch.Stop();
                Log(
                    $"Pyannote Community-1 diarization for '{fileName}' completed in {stopwatch.Elapsed.TotalSeconds:F2}s " +
                    $"with {turns.Length:N0} speaker turn(s).");
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

    private sealed record PyannoteTurnDto(
        [property: JsonPropertyName("speaker")] string Speaker,
        [property: JsonPropertyName("start")] double Start,
        [property: JsonPropertyName("end")] double End
    );
}
