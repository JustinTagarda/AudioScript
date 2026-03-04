using System.IO;
using System.IO.Compression;
using System.Net.Http;
using AudioTranscript.Engines;

namespace AudioTranscript.Services;

public sealed class WhisperProvisioningService {
    private const string DefaultBinaryZipUrl = "https://github.com/ggerganov/whisper.cpp/releases/download/v1.8.3/whisper-bin-x64.zip";
    private const string DefaultModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin";

    private static readonly string[] ExecutableCandidates = {
        "whisper-cli.exe",
        "main.exe",
    };

    private readonly HttpClient _httpClient;
    private readonly WhisperCppOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WhisperProvisioningService(HttpClient httpClient, WhisperCppOptions options) {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<WhisperProvisioningResult> EnsureReadyAsync(CancellationToken cancellationToken) {
        await _gate.WaitAsync(cancellationToken);

        try {
            if (IsConfiguredAndPresent()) {
                return new WhisperProvisioningResult(
                    IsReady: true,
                    Message: "Whisper local engine is ready.");
            }

            if (TryApplyFromRoot(GetBundledWhisperRoot(), out string bundledExecutable, out string bundledModel)) {
                _options.ExecutablePath = bundledExecutable;
                _options.ModelPath = bundledModel;

                return new WhisperProvisioningResult(
                    IsReady: true,
                    Message: "Using bundled whisper.cpp assets.");
            }

            string cacheRoot = GetUserCacheRoot();
            Directory.CreateDirectory(cacheRoot);

            if (!TryApplyFromRoot(cacheRoot, out string cacheExecutable, out string cacheModel)) {
                await DownloadAndInstallRuntimeAsync(cacheRoot, cancellationToken);
                await DownloadModelAsync(cacheRoot, cancellationToken);

                if (!TryApplyFromRoot(cacheRoot, out cacheExecutable, out cacheModel)) {
                    return new WhisperProvisioningResult(
                        IsReady: false,
                        Message: "Whisper assets were downloaded but could not be resolved.");
                }
            }

            _options.ExecutablePath = cacheExecutable;
            _options.ModelPath = cacheModel;

            return new WhisperProvisioningResult(
                IsReady: true,
                Message: "Whisper local engine prepared automatically.");
        }
        catch (Exception ex) {
            return new WhisperProvisioningResult(
                IsReady: false,
                Message: $"Unable to prepare whisper local engine automatically: {ex.Message}");
        }
        finally {
            _gate.Release();
        }
    }

    private bool IsConfiguredAndPresent() {
        if (string.IsNullOrWhiteSpace(_options.ExecutablePath)
            || string.IsNullOrWhiteSpace(_options.ModelPath)) {
            return false;
        }

        string executable = _options.ExecutablePath.Trim();
        string model = _options.ModelPath.Trim();

        bool executableReady;

        if (LooksLikePath(executable)) {
            executableReady = File.Exists(executable) && HasWhisperRuntimeSidecars(executable);
        }
        else {
            executableReady = ResolveCommandOnPath(executable) is not null;
        }

        return executableReady && File.Exists(model);
    }

    private static string GetBundledWhisperRoot() {
        return Path.Combine(AppContext.BaseDirectory, "whisper");
    }

    private static string GetUserCacheRoot() {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "AudioTranscript", "whisper");
    }

    private async Task DownloadAndInstallRuntimeAsync(string targetRoot, CancellationToken cancellationToken) {
        string zipPath = Path.Combine(targetRoot, "whisper-bin-x64.zip");

        if (!File.Exists(zipPath)) {
            await DownloadFileAsync(DefaultBinaryZipUrl, zipPath, cancellationToken);
        }

        ZipFile.ExtractToDirectory(zipPath, targetRoot, overwriteFiles: true);
    }

    private async Task DownloadModelAsync(string targetRoot, CancellationToken cancellationToken) {
        string modelDirectory = Path.Combine(targetRoot, "models");
        Directory.CreateDirectory(modelDirectory);

        string modelFileName = Path.GetFileName(new Uri(DefaultModelUrl).AbsolutePath);
        string modelPath = Path.Combine(modelDirectory, modelFileName);

        if (File.Exists(modelPath)) {
            return;
        }

        await DownloadFileAsync(DefaultModelUrl, modelPath, cancellationToken);
    }

    private async Task DownloadFileAsync(string uri, string destinationPath, CancellationToken cancellationToken) {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        string? directory = Path.GetDirectoryName(destinationPath);

        if (!string.IsNullOrWhiteSpace(directory)) {
            Directory.CreateDirectory(directory);
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        await source.CopyToAsync(destination, cancellationToken);
    }

    private static bool TryApplyFromRoot(string root, out string executablePath, out string modelPath) {
        executablePath = string.Empty;
        modelPath = string.Empty;

        if (!Directory.Exists(root)) {
            return false;
        }

        executablePath = ResolveExecutableFromRoot(root) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(executablePath)) {
            return false;
        }

        modelPath = ResolveModelFromRoot(root) ?? string.Empty;

        return !string.IsNullOrWhiteSpace(modelPath);
    }

    private static string? ResolveExecutableFromRoot(string root) {
        var candidates = ExecutableCandidates
            .SelectMany(candidate => Directory.EnumerateFiles(root, candidate, SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0) {
            return null;
        }

        // Prefer binaries that are in a runtime folder and have sidecar ggml dlls.
        return candidates
            .OrderBy(path => File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "ggml-base.dll")) ? 0 : 1)
            .ThenBy(path => path.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static string? ResolveModelFromRoot(string root) {
        string modelsRoot = Path.Combine(root, "models");

        var searchRoots = new List<string> { modelsRoot, root }
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var models = searchRoots
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            .Where(path => path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (models.Count == 0) {
            return null;
        }

        return models
            .OrderBy(path => Path.GetFileName(path).Contains("base.en", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => Path.GetFileName(path).Contains("base", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static bool LooksLikePath(string value) {
        return value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar)
            || Path.IsPathRooted(value);
    }

    private static string? ResolveCommandOnPath(string commandName) {
        string? path = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrWhiteSpace(path)) {
            return null;
        }

        var extensions = new List<string>();
        string ext = Path.GetExtension(commandName);

        if (!string.IsNullOrWhiteSpace(ext)) {
            extensions.Add(string.Empty);
        }
        else {
            string pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT";
            extensions.AddRange(pathExt
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.StartsWith('.') ? item : $".{item}"));
        }

        foreach (string folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
            string trimmed = folder.Trim();

            if (string.IsNullOrWhiteSpace(trimmed)) {
                continue;
            }

            foreach (string extension in extensions) {
                string candidate = Path.Combine(trimmed, commandName + extension);

                if (File.Exists(candidate)) {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool HasWhisperRuntimeSidecars(string executablePath) {
        string? directory = Path.GetDirectoryName(executablePath);

        if (string.IsNullOrWhiteSpace(directory)) {
            return false;
        }

        return File.Exists(Path.Combine(directory, "ggml-base.dll"))
            && File.Exists(Path.Combine(directory, "ggml.dll"));
    }
}

public sealed record WhisperProvisioningResult(bool IsReady, string Message);
