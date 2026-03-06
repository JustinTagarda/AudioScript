using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AudioTranscript.Abstractions;
using AudioTranscript.Audio;
using NAudio.Wave;

namespace AudioTranscript.Engines;

public sealed class WhisperCppTranscriptionEngine : ITranscriptionEngine {
    private static readonly Regex TimestampPrefixRegex = new(@"^\[[^\]]+\]\s*", RegexOptions.Compiled);

    private readonly AudioStandardizer _audioStandardizer;
    private readonly WhisperCppOptions _options;

    public WhisperCppTranscriptionEngine(AudioStandardizer audioStandardizer, WhisperCppOptions options) {
        _audioStandardizer = audioStandardizer;
        _options = options;
    }

    public string Id => "whisper_cpp";

    public string DisplayName => "Local: whisper.cpp";

    public EngineCapability Capabilities =>
        EngineCapability.Timestamps
        | EngineCapability.Punctuation
        | EngineCapability.LanguageAutoDetect;

    public async Task<TranscriptUpdate> TranscribeFileAsync(
        string audioFilePath,
        TranscriptionRequest request,
        CancellationToken cancellationToken) {
        EnsureConfigured();

        string standardizedPath = _audioStandardizer.ConvertFileToEngineWav(audioFilePath);

        try {
            string text = await TranscribeStandardizedWavAsync(standardizedPath, request, cancellationToken);

            return new TranscriptUpdate(
                Text: text,
                IsFinal: true,
                CreatedAt: DateTimeOffset.UtcNow,
                Language: ResolveLanguage(request));
        }
        finally {
            TryDeleteFile(standardizedPath);
        }
    }

    public IRealtimeTranscriptionSession CreateRealtimeSession(TranscriptionRequest request) {
        EnsureConfigured();

        return new ChunkedRealtimeTranscriptionSession(
            async (pcm16KhzMono, _, ct) => await TranscribePcmChunkAsync(pcm16KhzMono, request, ct),
            language: ResolveLanguage(request),
            interimWindowSeconds: 1,
            finalWindowSeconds: 3,
            interimIntervalMilliseconds: 850);
    }

    private async Task<string> TranscribePcmChunkAsync(
        byte[] pcm16KhzMono,
        TranscriptionRequest request,
        CancellationToken cancellationToken) {
        string tempWavePath = Path.Combine(Path.GetTempPath(), $"audiotranscript-live-{Guid.NewGuid():N}.wav");

        try {
            using (var writer = new WaveFileWriter(tempWavePath, AudioFormatConstants.EngineWaveFormat)) {
                writer.Write(pcm16KhzMono, 0, pcm16KhzMono.Length);
            }

            return await TranscribeStandardizedWavAsync(tempWavePath, request, cancellationToken);
        }
        finally {
            TryDeleteFile(tempWavePath);
        }
    }

    private async Task<string> TranscribeStandardizedWavAsync(
        string standardizedWavePath,
        TranscriptionRequest request,
        CancellationToken cancellationToken) {
        string language = ResolveLanguage(request);
        string args = BuildArguments(standardizedWavePath, language, request.IncludeTimestamps);

        using var process = new Process {
            StartInfo = new ProcessStartInfo {
                FileName = _options.ExecutablePath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };

        if (!process.Start()) {
            throw new InvalidOperationException("Failed to start whisper.cpp process.");
        }

        Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stdErrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(_options.TimeoutSeconds, 10)));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) {
            TryKillProcess(process);

            if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested) {
                throw new TimeoutException($"whisper.cpp exceeded timeout ({_options.TimeoutSeconds}s).");
            }

            throw;
        }

        string stdout = await stdOutTask;
        string stderr = await stdErrTask;

        if (process.ExitCode != 0) {
            throw new InvalidOperationException(
                $"whisper.cpp exited with code {process.ExitCode}. stderr: {stderr.Trim()}");
        }

        string text = ExtractTranscript(stdout);

        if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(stderr)) {
            text = ExtractTranscript(stderr);
        }

        return text.Trim();
    }

    private void EnsureConfigured() {
        if (string.IsNullOrWhiteSpace(_options.ExecutablePath)) {
            throw new InvalidOperationException(
                "Whisper.cpp executable path is not configured. Bundle whisper/whisper-cli.exe or set WHISPER_CPP_CLI_PATH.");
        }

        if (LooksLikePath(_options.ExecutablePath) && !File.Exists(_options.ExecutablePath)) {
            throw new InvalidOperationException(
                $"Whisper.cpp executable was not found at '{_options.ExecutablePath}'. " +
                "Bundle whisper/whisper-cli.exe or set WHISPER_CPP_CLI_PATH.");
        }

        if (LooksLikePath(_options.ExecutablePath)) {
            string? executableDirectory = Path.GetDirectoryName(_options.ExecutablePath);

            if (string.IsNullOrWhiteSpace(executableDirectory)
                || !File.Exists(Path.Combine(executableDirectory, "ggml-base.dll"))
                || !File.Exists(Path.Combine(executableDirectory, "ggml.dll"))) {
                throw new InvalidOperationException(
                    "Whisper.cpp runtime sidecar DLLs were not found next to the executable. " +
                    "Use the bundled whisper/Release runtime or a complete whisper.cpp binary folder.");
            }
        }

        if (string.IsNullOrWhiteSpace(_options.ModelPath)) {
            throw new InvalidOperationException(
                "Whisper.cpp model path is not configured. Bundle whisper/models/*.bin or *.gguf, set WHISPER_CPP_MODEL_PATH, or update settings in-app.");
        }

        if (!File.Exists(_options.ModelPath)) {
            throw new InvalidOperationException(
                $"Whisper.cpp model file was not found at '{_options.ModelPath}'. " +
                "Bundle whisper/models/*.bin or *.gguf, set WHISPER_CPP_MODEL_PATH, or update settings in-app.");
        }
    }

    private string BuildArguments(string wavePath, string language, bool includeTimestamps) {
        var builder = new StringBuilder();

        builder.Append("-m ");
        builder.Append(Quote(_options.ModelPath));
        builder.Append(' ');

        builder.Append("-t ");
        builder.Append(Math.Max(_options.Threads, 1));
        builder.Append(' ');

        builder.Append("-f ");
        builder.Append(Quote(wavePath));
        builder.Append(' ');

        if (!string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase)) {
            builder.Append("-l ");
            builder.Append(language);
            builder.Append(' ');
        }

        if (!includeTimestamps) {
            builder.Append("-nt ");
        }

        if (!string.IsNullOrWhiteSpace(_options.AdditionalArguments)) {
            builder.Append(_options.AdditionalArguments.Trim());
        }

        return builder.ToString().Trim();
    }

    private string ResolveLanguage(TranscriptionRequest request) {
        string language = request.Language?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(language) ? "auto" : language;
    }

    private static string ExtractTranscript(string rawOutput) {
        if (string.IsNullOrWhiteSpace(rawOutput)) {
            return string.Empty;
        }

        var lines = rawOutput
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !line.StartsWith("whisper_", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.StartsWith("main:", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.StartsWith("system_info", StringComparison.OrdinalIgnoreCase))
            .Select(line => TimestampPrefixRegex.Replace(line, string.Empty))
            .Where(line => !string.IsNullOrWhiteSpace(line));

        return string.Join(" ", lines);
    }

    private static void TryDeleteFile(string filePath) {
        try {
            if (File.Exists(filePath)) {
                File.Delete(filePath);
            }
        }
        catch {
            // Ignore temporary cleanup failures.
        }
    }

    private static void TryKillProcess(Process process) {
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        }
        catch {
            // Ignore best-effort process cleanup failures.
        }
    }

    private static string Quote(string value) {
        return $"\"{value.Replace("\"", "\\\"")}\"";
    }

    private static bool LooksLikePath(string value) {
        return value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar)
            || Path.IsPathRooted(value);
    }
}
