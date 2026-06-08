using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioScript.Services;

public sealed record OfficialSourceBootstrapStepResult(
    string Id,
    bool Succeeded,
    string Message);

public sealed record OfficialSourceBootstrapResult(
    bool Succeeded,
    bool WasCanceled,
    IReadOnlyList<OfficialSourceBootstrapStepResult> Steps,
    string Message);

public interface IOfficialSourceBootstrapService
{
    Task<OfficialSourceBootstrapResult> BootstrapAsync(
        IProgress<AssetProvisioningProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class OfficialSourceBootstrapService : IOfficialSourceBootstrapService
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "python.org",
        "www.python.org",
        "files.pythonhosted.org",
        "pypi.org",
        "www.pypi.org",
        "aka.ms",
        "download.microsoft.com",
        "download.visualstudio.microsoft.com",
        "huggingface.co",
    };

    private readonly OfficialSourceBootstrapManifest _manifest;
    private readonly AppDataPathProvider _paths;
    private readonly ProcessLogService _processLogService;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public OfficialSourceBootstrapService(
        AppDataPathProvider paths,
        ProcessLogService processLogService,
        string? manifestPath = null,
        HttpClient? httpClient = null)
    {
        _paths = paths;
        _processLogService = processLogService;
        string resolvedManifestPath = string.IsNullOrWhiteSpace(manifestPath)
            ? Path.Combine(AppContext.BaseDirectory, "assets", "bootstrap", "asset-manifest.json")
            : Path.GetFullPath(manifestPath);
        _manifest = LoadBootstrapManifest(resolvedManifestPath);
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
        });
        _ownsHttpClient = httpClient is null;
    }

    public async Task<OfficialSourceBootstrapResult> BootstrapAsync(
        IProgress<AssetProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        OfficialSourceBootstrapManifest manifest = _manifest;
        if (manifest.Sources.Length == 0)
        {
            return new OfficialSourceBootstrapResult(false, false, Array.Empty<OfficialSourceBootstrapStepResult>(), "No bootstrap sources are configured.");
        }

        var steps = new List<OfficialSourceBootstrapStepResult>();
        string runtimeRoot = Path.Combine(_paths.PythonRuntimesPath, "win-x64");
        string modelRoot = Path.Combine(_paths.PyannoteAssetsPath, "speaker-diarization-community-1");
        string cacheRoot = Path.Combine(_paths.ProvisioningPath, "source-cache", "official-bootstrap");
        string sitePackagesRoot = Path.Combine(runtimeRoot, "Lib", "site-packages");
        var cachedWheelPaths = new List<string>();
        string? pipWheelPath = null;
        Directory.CreateDirectory(cacheRoot);

        try
        {
            foreach (OfficialSourceBootstrapDescriptor source in manifest.Sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OfficialSourceBootstrapStepResult step = source.Kind switch
                {
                    OfficialSourceBootstrapPayloadKind.PythonEmbeddableZip => await InstallPythonRuntimeAsync(source, runtimeRoot, cacheRoot, progress, cancellationToken).ConfigureAwait(false),
                    OfficialSourceBootstrapPayloadKind.PythonWheel => await CachePythonWheelAsync(
                        source,
                        cacheRoot,
                        progress,
                        cancellationToken).ConfigureAwait(false),
                    OfficialSourceBootstrapPayloadKind.HuggingFaceModelRepository => await InstallHuggingFaceModelAsync(source, modelRoot, cacheRoot, progress, cancellationToken).ConfigureAwait(false),
                    OfficialSourceBootstrapPayloadKind.MicrosoftVcRedist => await StageMicrosoftVcRedistAsync(source, cacheRoot, progress, cancellationToken).ConfigureAwait(false),
                    _ => new OfficialSourceBootstrapStepResult(source.Id, false, $"Unsupported bootstrap source kind '{source.Kind}'."),
                };
                steps.Add(step);
                if (!step.Succeeded)
                {
                    return new OfficialSourceBootstrapResult(false, false, steps, step.Message);
                }

                if (source.Kind == OfficialSourceBootstrapPayloadKind.PythonWheel)
                {
                    string cachedWheelPath = Path.Combine(cacheRoot, Path.GetFileName(new Uri(source.SourceUrl).LocalPath));
                    cachedWheelPaths.Add(cachedWheelPath);
                    if (string.Equals(source.Id, "pip", StringComparison.OrdinalIgnoreCase))
                    {
                        pipWheelPath = cachedWheelPath;
                    }
                }
            }

            if (pipWheelPath is null || !File.Exists(pipWheelPath))
            {
                return new OfficialSourceBootstrapResult(
                    false,
                    false,
                    steps,
                    "Bootstrap failed because the pip wheel is not configured.");
            }

            Directory.CreateDirectory(sitePackagesRoot);
            ZipFile.ExtractToDirectory(pipWheelPath, sitePackagesRoot, overwriteFiles: true);

            OfficialSourceBootstrapStepResult wheelInstallStep = await InstallWheelhouseAsync(
                runtimeRoot,
                cacheRoot,
                cachedWheelPaths.Where(path => !string.Equals(path, pipWheelPath, StringComparison.OrdinalIgnoreCase)).ToArray(),
                progress,
                cancellationToken).ConfigureAwait(false);
            steps.Add(wheelInstallStep);
            if (!wheelInstallStep.Succeeded)
            {
                return new OfficialSourceBootstrapResult(false, false, steps, wheelInstallStep.Message);
            }

            return new OfficialSourceBootstrapResult(true, false, steps, "Official-source bootstrap completed.");
        }
        catch (OperationCanceledException)
        {
            return new OfficialSourceBootstrapResult(false, true, steps, "Bootstrap canceled.");
        }
        catch (Exception ex)
        {
            _processLogService.LogException(nameof(OfficialSourceBootstrapService), "Official-source bootstrap failed.", ex);
            return new OfficialSourceBootstrapResult(false, false, steps, ex.Message);
        }
    }

    private static OfficialSourceBootstrapManifest LoadBootstrapManifest(string manifestPath)
    {
        AssetProvisioningManifest? manifest = JsonSerializer.Deserialize<AssetProvisioningManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                Converters = { new JsonStringEnumConverter() },
            });

        if (manifest?.Bootstrap is null)
        {
            return new OfficialSourceBootstrapManifest();
        }

        return manifest.Bootstrap;
    }

    private async Task<OfficialSourceBootstrapStepResult> InstallPythonRuntimeAsync(
        OfficialSourceBootstrapDescriptor source,
        string runtimeRoot,
        string cacheRoot,
        IProgress<AssetProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new AssetProvisioningProgress(source.Id, source.DisplayName, "Downloading...", 0, null, 0));
        string zipPath = await DownloadToCacheAsync(source, cacheRoot, progress, cancellationToken).ConfigureAwait(false);
        progress?.Report(new AssetProvisioningProgress(source.Id, source.DisplayName, "Installing...", 0, null, 70));
        DeleteDirectoryBestEffort(runtimeRoot);
        Directory.CreateDirectory(runtimeRoot);
        ZipFile.ExtractToDirectory(zipPath, runtimeRoot, overwriteFiles: true);
        EnsureNativeRuntimeCompatibilityFiles(runtimeRoot);
        EnsureEmbeddedRuntimeSitePackagesAreImportable(runtimeRoot);
        progress?.Report(new AssetProvisioningProgress(source.Id, source.DisplayName, "Ready", 0, null, 100));
        return new OfficialSourceBootstrapStepResult(source.Id, true, $"Installed {source.DisplayName}.");
    }

    private async Task<OfficialSourceBootstrapStepResult> CachePythonWheelAsync(
        OfficialSourceBootstrapDescriptor source,
        string cacheRoot,
        IProgress<AssetProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new AssetProvisioningProgress(source.Id, source.DisplayName, "Downloading...", 0, null, 0));
        await DownloadToCacheAsync(source, cacheRoot, progress, cancellationToken).ConfigureAwait(false);
        progress?.Report(new AssetProvisioningProgress(source.Id, source.DisplayName, "Ready", 0, null, 100));
        return new OfficialSourceBootstrapStepResult(source.Id, true, $"Cached {source.DisplayName}.");
    }

    private async Task<OfficialSourceBootstrapStepResult> InstallWheelhouseAsync(
        string runtimeRoot,
        string cacheRoot,
        IReadOnlyList<string> wheelPaths,
        IProgress<AssetProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new AssetProvisioningProgress(
            "python-wheelhouse",
            "Python wheelhouse",
            "Installing...",
            0,
            null,
            70));

        if (wheelPaths.Count == 0)
        {
            progress?.Report(new AssetProvisioningProgress(
                "python-wheelhouse",
                "Python wheelhouse",
                "Ready",
                0,
                null,
                100));
            return new OfficialSourceBootstrapStepResult("python-wheelhouse", true, "No Python wheels required installation.");
        }

        string requirementsPath = Path.Combine(cacheRoot, "python-wheelhouse.requirements.txt");
        File.WriteAllLines(
            requirementsPath,
            wheelPaths.Select(path => "./" + Path.GetFileName(path)));

        string pythonExePath = Path.Combine(runtimeRoot, "python.exe");
        string sitePackagesPath = Path.Combine(runtimeRoot, "Lib", "site-packages");

        progress?.Report(new AssetProvisioningProgress(
            "python-wheelhouse",
            "Python wheelhouse",
            "Downloading...",
            0,
            null,
            35));
        (int downloadExitCode, string downloadStdout, string downloadStderr) = await RunPythonModuleAsync(
            pythonExePath,
            cacheRoot,
            ["-m", "pip", "download", "--disable-pip-version-check", "--no-input", "--no-cache-dir", "--prefer-binary", "--index-url", "https://pypi.org/simple", "--dest", cacheRoot, "-r", requirementsPath],
            cancellationToken).ConfigureAwait(false);

        if (downloadExitCode != 0)
        {
            string message = $"Python wheelhouse download failed with exit code {downloadExitCode}. {Truncate(downloadStderr)}";
            return new OfficialSourceBootstrapStepResult("python-wheelhouse", false, message);
        }

        progress?.Report(new AssetProvisioningProgress(
            "python-wheelhouse",
            "Python wheelhouse",
            "Installing...",
            0,
            null,
            70));
        (int installExitCode, string installStdout, string installStderr) = await RunPythonModuleAsync(
            pythonExePath,
            cacheRoot,
            ["-m", "pip", "install", "--disable-pip-version-check", "--no-input", "--no-cache-dir", "--prefer-binary", "--no-index", "--find-links", cacheRoot, "--upgrade", "--force-reinstall", "--target", sitePackagesPath, "-r", requirementsPath],
            cancellationToken).ConfigureAwait(false);

        if (installExitCode != 0)
        {
            string message = $"Python wheelhouse installation failed with exit code {installExitCode}. {Truncate(installStderr)}";
            return new OfficialSourceBootstrapStepResult("python-wheelhouse", false, message);
        }

        _processLogService.Log(
            nameof(OfficialSourceBootstrapService),
            $"Downloaded official Python wheelhouse. {Truncate(downloadStdout)}");
        _processLogService.Log(
            nameof(OfficialSourceBootstrapService),
            $"Installed official Python wheelhouse. {Truncate(installStdout)}");
        progress?.Report(new AssetProvisioningProgress(
            "python-wheelhouse",
            "Python wheelhouse",
            "Ready",
            0,
            null,
            100));
        return new OfficialSourceBootstrapStepResult("python-wheelhouse", true, "Installed Python wheelhouse.");
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunPythonModuleAsync(
        string pythonExePath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pythonExePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory,
            }
        };

        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return (process.ExitCode, stdout, stderr);
    }

    private async Task<OfficialSourceBootstrapStepResult> InstallHuggingFaceModelAsync(
        OfficialSourceBootstrapDescriptor source,
        string modelRoot,
        string cacheRoot,
        IProgress<AssetProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new AssetProvisioningProgress(source.Id, source.DisplayName, "Downloading...", 0, null, 0));
        Directory.CreateDirectory(modelRoot);
        string cachePath = Path.Combine(cacheRoot, source.Id);
        string sourceUrl = source.SourceUrl.Trim();
        using HttpResponseMessage metadataResponse = await _httpClient.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        metadataResponse.EnsureSuccessStatusCode();
        await using Stream metadataStream = await metadataResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument metadata = await JsonDocument.ParseAsync(metadataStream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!metadata.RootElement.TryGetProperty("siblings", out JsonElement siblings) || siblings.ValueKind != JsonValueKind.Array)
        {
            return new OfficialSourceBootstrapStepResult(source.Id, false, $"Hugging Face model metadata for '{source.DisplayName}' did not include files.");
        }

        DeleteDirectoryBestEffort(cachePath);
        Directory.CreateDirectory(cachePath);
        foreach (JsonElement sibling in siblings.EnumerateArray())
        {
            if (!sibling.TryGetProperty("rfilename", out JsonElement fileNameNode))
            {
                continue;
            }

            string? relativeFile = fileNameNode.GetString();
            if (string.IsNullOrWhiteSpace(relativeFile)
                || relativeFile.EndsWith("/", StringComparison.Ordinal)
                || relativeFile.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                || relativeFile.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
                || string.Equals(relativeFile, ".gitattributes", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Uri resolveUri = BuildHuggingFaceResolveUri(sourceUrl, relativeFile);
            string destinationPath = Path.Combine(cachePath, relativeFile.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await DownloadFileAsync(resolveUri, destinationPath, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new AssetProvisioningProgress(source.Id, source.DisplayName, "Installing...", 0, null, 70));
        DeleteDirectoryBestEffort(modelRoot);
        CopyDirectory(cachePath, modelRoot);
        progress?.Report(new AssetProvisioningProgress(source.Id, source.DisplayName, "Ready", 0, null, 100));
        return new OfficialSourceBootstrapStepResult(source.Id, true, $"Installed {source.DisplayName}.");
    }

    private async Task<OfficialSourceBootstrapStepResult> StageMicrosoftVcRedistAsync(
        OfficialSourceBootstrapDescriptor source,
        string cacheRoot,
        IProgress<AssetProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new AssetProvisioningProgress(source.Id, source.DisplayName, "Downloading...", 0, null, 0));
        string exePath = await DownloadToCacheAsync(source, cacheRoot, progress, cancellationToken).ConfigureAwait(false);
        progress?.Report(new AssetProvisioningProgress(source.Id, source.DisplayName, "Installing...", 0, null, 70));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "/install /quiet /norestart",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };

        process.Start();
        string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode is not 0 and not 3010)
        {
            string message = $"Microsoft Visual C++ Redistributable failed with exit code {process.ExitCode}. {Truncate(stderr)}";
            return new OfficialSourceBootstrapStepResult(source.Id, false, message);
        }

        string stagingMessage = $"Installed {source.DisplayName}. {Truncate(stdout)}";
        _processLogService.Log(nameof(OfficialSourceBootstrapService), stagingMessage);
        progress?.Report(new AssetProvisioningProgress(source.Id, source.DisplayName, "Ready", 0, null, 100));
        return new OfficialSourceBootstrapStepResult(source.Id, true, stagingMessage);
    }

    private async Task<string> DownloadToCacheAsync(
        OfficialSourceBootstrapDescriptor source,
        string cacheRoot,
        IProgress<AssetProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        string fileName = Path.GetFileName(new Uri(source.SourceUrl).LocalPath);
        ValidateSourceUri(source.SourceUrl);
        string cachedPath = Path.Combine(cacheRoot, fileName);
        if (File.Exists(cachedPath))
        {
            if (ValidateSha256(source, cachedPath))
            {
                return cachedPath;
            }
            File.Delete(cachedPath);
        }

        string tempPath = $"{cachedPath}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(cachedPath)!);

        try
        {
            await DownloadFileAsync(new Uri(source.SourceUrl), tempPath, cancellationToken).ConfigureAwait(false);
            if (!ValidateSha256(source, tempPath))
            {
                throw new InvalidOperationException($"Checksum validation failed for '{source.DisplayName}'.");
            }

            File.Move(tempPath, cachedPath);
            return cachedPath;
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private async Task DownloadFileAsync(Uri source, string destinationPath, CancellationToken cancellationToken)
    {
        ValidateSourceUri(source.ToString());
        using HttpResponseMessage response = await _httpClient.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream destination = File.Open(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private static bool ValidateSha256(OfficialSourceBootstrapDescriptor source, string path)
    {
        if (string.IsNullOrWhiteSpace(source.ExpectedSha256))
        {
            return true;
        }

        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        string actual = Convert.ToHexString(hash);
        return string.Equals(actual, source.ExpectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static Uri BuildHuggingFaceResolveUri(string metadataUrl, string relativeFilePath)
    {
        Uri metadata = new(metadataUrl);
        string repositoryPath = metadata.AbsolutePath.Trim('/');
        string repositoryId = repositoryPath.StartsWith("api/models/", StringComparison.OrdinalIgnoreCase)
            ? repositoryPath["api/models/".Length..]
            : repositoryPath;
        string escapedPath = string.Join("/", relativeFilePath.Split('/').Select(Uri.EscapeDataString));
        return new Uri($"{metadata.Scheme}://{metadata.Host}/{repositoryId}/resolve/main/{escapedPath}?download=true");
    }

    private static void ValidateSourceUri(string sourceUrl)
    {
        Uri source = new(sourceUrl);
        if (!AllowedHosts.Contains(source.Host))
        {
            throw new InvalidOperationException($"Bootstrap source host '{source.Host}' is not allowlisted.");
        }
    }

    private static void EnsureNativeRuntimeCompatibilityFiles(string runtimeRoot)
    {
        CopyRuntimeFileIfNeeded(runtimeRoot, "Lib\\site-packages\\sklearn\\.libs\\msvcp140.dll", "msvcp140.dll");
        CopyRuntimeFileIfNeeded(runtimeRoot, "Lib\\site-packages\\sklearn\\.libs\\vcomp140.dll", "vcomp140.dll");
    }

    internal static void EnsureEmbeddedRuntimeSitePackagesAreImportable(string runtimeRoot)
    {
        string? pthFilePath = Directory
            .GetFiles(runtimeRoot, "python*_pth", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();

        if (pthFilePath is null)
        {
            throw new FileNotFoundException(
                "The embedded Python runtime is missing its ._pth bootstrap file.",
                Path.Combine(runtimeRoot, "python*_pth"));
        }

        const string sitePackagesEntry = "Lib\\site-packages";
        string[] lines = File.ReadAllLines(pthFilePath);

        if (lines.Any(line => string.Equals(line.Trim(), sitePackagesEntry, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        int importSiteIndex = Array.FindIndex(lines, line => string.Equals(line.Trim(), "import site", StringComparison.OrdinalIgnoreCase));
        if (importSiteIndex < 0)
        {
            importSiteIndex = lines.Length;
        }

        var updatedLines = new List<string>(lines.Length + 1);
        updatedLines.AddRange(lines.Take(importSiteIndex));
        updatedLines.Add(sitePackagesEntry);
        updatedLines.AddRange(lines.Skip(importSiteIndex));
        File.WriteAllLines(pthFilePath, updatedLines);
    }

    private static void CopyRuntimeFileIfNeeded(string runtimeRoot, string sourceRelativePath, string destinationRelativePath)
    {
        string sourcePath = Path.Combine(runtimeRoot, sourceRelativePath);
        string destinationPath = Path.Combine(runtimeRoot, destinationRelativePath);
        if (File.Exists(destinationPath) || !File.Exists(sourcePath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath);
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);
        foreach (string directoryPath in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourcePath, directoryPath);
            Directory.CreateDirectory(Path.Combine(destinationPath, relative));
        }

        foreach (string filePath in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourcePath, filePath);
            string destinationFilePath = Path.Combine(destinationPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);
            File.Copy(filePath, destinationFilePath, overwrite: true);
        }
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static string Truncate(string? text, int maxLength = 400)
    {
        string value = text?.Trim() ?? string.Empty;
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
