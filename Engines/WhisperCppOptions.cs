using System.IO;

namespace AudioTranscript.Engines;

public sealed class WhisperCppOptions {
    public string ExecutablePath { get; set; } = ResolveExecutablePath();

    public string ModelPath { get; set; } = ResolveModelPath();

    public string? Language { get; set; } = "en";

    public string AdditionalArguments { get; set; } = string.Empty;

    public int Threads { get; set; } = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);

    public int TimeoutSeconds { get; set; } = 180;

    private static string ResolveExecutablePath() {
        string? fromEnv = Environment.GetEnvironmentVariable("WHISPER_CPP_CLI_PATH");

        if (!string.IsNullOrWhiteSpace(fromEnv)) {
            return fromEnv.Trim();
        }

        string bundledRoot = Path.Combine(AppContext.BaseDirectory, "whisper");
        string[] candidates = {
            Path.Combine(bundledRoot, "Release", "whisper-cli.exe"),
            Path.Combine(bundledRoot, "whisper-cli.exe"),
        };

        foreach (string candidate in candidates) {
            if (File.Exists(candidate)) {
                return candidate;
            }
        }

        return "whisper-cli.exe";
    }

    private static string ResolveModelPath() {
        string? fromEnv = Environment.GetEnvironmentVariable("WHISPER_CPP_MODEL_PATH");

        if (!string.IsNullOrWhiteSpace(fromEnv)) {
            return fromEnv.Trim();
        }

        string bundledModelDirectory = Path.Combine(AppContext.BaseDirectory, "whisper", "models");

        if (!Directory.Exists(bundledModelDirectory)) {
            return string.Empty;
        }

        // Prefer smaller defaults for first-run responsiveness if multiple models are bundled.
        var modelCandidates = Directory
            .EnumerateFiles(bundledModelDirectory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path).Contains("base", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return modelCandidates.FirstOrDefault() ?? string.Empty;
    }
}
