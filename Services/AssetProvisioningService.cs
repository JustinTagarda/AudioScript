using System.IO.Compression;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioScript.Services;

public sealed class AssetProvisioningService : IAssetProvisioningService, IDisposable
{
    private const int StateSchemaVersion = 1;
    private const int CopyBufferSize = 128 * 1024;
    private const int MaxDownloadAttempts = 3;

    private readonly ProcessLogService _processLogService;
    private readonly AppDataPathProvider _paths;
    private readonly string _repoRootPath;
    private readonly string _stateFilePath;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Dictionary<string, ProvisionedAssetDescriptor> _assets;
    private readonly SemaphoreSlim _installSemaphore = new(1, 1);

    public AssetProvisioningService(
        ProcessLogService processLogService,
        AppDataPathProvider paths,
        string? manifestPath = null,
        HttpClient? httpClient = null,
        string? repoRootPath = null)
    {
        _processLogService = processLogService;
        _paths = paths;
        _repoRootPath = string.IsNullOrWhiteSpace(repoRootPath)
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."))
            : Path.GetFullPath(repoRootPath);
        string resolvedManifestPath = string.IsNullOrWhiteSpace(manifestPath)
            ? Path.Combine(AppContext.BaseDirectory, "assets", "bootstrap", "asset-manifest.json")
            : Path.GetFullPath(manifestPath);

        AssetProvisioningManifest manifest = LoadManifest(resolvedManifestPath);
        _assets = manifest.Assets.ToDictionary(asset => asset.Id, StringComparer.OrdinalIgnoreCase);
        _stateFilePath = Path.Combine(_paths.ProvisioningPath, "asset-state.json");
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public AssetProvisioningStatus GetStatus(string assetId)
    {
        ProvisionedAssetDescriptor descriptor = GetDescriptor(assetId);
        string installPath = ResolveInstallPath(descriptor.Id);

        if (!IsSupportedOnCurrentArchitecture(descriptor))
        {
            return new AssetProvisioningStatus(
                descriptor.Id,
                descriptor.DisplayName,
                AssetProvisioningState.Unsupported,
                installPath,
                "This asset is not supported on the current architecture.");
        }

        if (IsInstalled(descriptor.Id))
        {
            return new AssetProvisioningStatus(
                descriptor.Id,
                descriptor.DisplayName,
                AssetProvisioningState.Ready,
                installPath);
        }

        if (!CanResolveSource(descriptor, out _))
        {
            return new AssetProvisioningStatus(
                descriptor.Id,
                descriptor.DisplayName,
                AssetProvisioningState.Unconfigured,
                installPath,
                "Asset source is not configured.");
        }

        return new AssetProvisioningStatus(
            descriptor.Id,
            descriptor.DisplayName,
            AssetProvisioningState.Missing,
            installPath);
    }

    public string ResolveInstallPath(string assetId)
    {
        ProvisionedAssetDescriptor descriptor = GetDescriptor(assetId);
        string root = descriptor.InstallRoot switch
        {
            ProvisioningInstallRoot.Models => _paths.ModelsPath,
            ProvisioningInstallRoot.Pyannote => _paths.PyannoteAssetsPath,
            ProvisioningInstallRoot.Python => _paths.PythonRuntimesPath,
            _ => throw new InvalidOperationException($"Unsupported install root '{descriptor.InstallRoot}'."),
        };

        return Path.Combine(root, descriptor.InstallRelativePath);
    }

    public bool IsInstalled(string assetId)
    {
        ProvisionedAssetDescriptor descriptor = GetDescriptor(assetId);
        if (!IsSupportedOnCurrentArchitecture(descriptor))
        {
            return false;
        }

        string installPath = ResolveInstallPath(descriptor.Id);
        bool pathExists = descriptor.InstallKind switch
        {
            ProvisioningInstallKind.File => File.Exists(installPath),
            ProvisioningInstallKind.Directory => Directory.Exists(installPath),
            _ => false,
        };

        if (!pathExists)
        {
            return false;
        }

        ProvisionedAssetStateItem? state = LoadStateDocument()
            .Assets
            .FirstOrDefault(item => string.Equals(item.AssetId, descriptor.Id, StringComparison.OrdinalIgnoreCase));
        if (state is null)
        {
            SaveInstalledState(descriptor, installPath);
            return true;
        }

        return string.Equals(state.Version, descriptor.Version, StringComparison.Ordinal)
            && string.Equals(Path.GetFullPath(state.InstallPath), Path.GetFullPath(installPath), StringComparison.OrdinalIgnoreCase);
    }

    public async Task InstallAssetAsync(
        string assetId,
        IProgress<AssetProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        ProvisionedAssetDescriptor descriptor = GetDescriptor(assetId);
        if (!IsSupportedOnCurrentArchitecture(descriptor))
        {
            throw new PlatformNotSupportedException(
                $"{descriptor.DisplayName} is not supported on this architecture.");
        }

        if (!CanResolveSource(descriptor, out string? resolvedSource))
        {
            throw new InvalidOperationException(
                $"{descriptor.DisplayName} download source is not configured.");
        }

        await _installSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (IsInstalled(descriptor.Id))
            {
                Report(progress, descriptor, "Ready", descriptor.ExpectedBytes ?? 0, descriptor.ExpectedBytes, 100);
                return;
            }

            Directory.CreateDirectory(_paths.ProvisioningPath);
            Directory.CreateDirectory(_paths.TempPath);

            string installPath = ResolveInstallPath(descriptor.Id);
            string tempRoot = Path.Combine(_paths.TempPath, "asset-provisioning", $"{descriptor.Id}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempRoot);

            try
            {
                Report(progress, descriptor, "Preparing...", 0, descriptor.ExpectedBytes, 0);
                if (descriptor.InstallKind == ProvisioningInstallKind.File)
                {
                    string tempFilePath = Path.Combine(tempRoot, Path.GetFileName(installPath));
                    await MaterializeFileAsync(descriptor, resolvedSource!, tempFilePath, progress, cancellationToken);
                    await PromoteFileAsync(tempFilePath, installPath, cancellationToken);
                }
                else
                {
                    string tempDirectoryPath = Path.Combine(tempRoot, "content");
                    await MaterializeDirectoryAsync(descriptor, resolvedSource!, tempDirectoryPath, progress, cancellationToken);
                    await PromoteDirectoryAsync(tempDirectoryPath, installPath, cancellationToken);
                }

                SaveInstalledState(descriptor, installPath);
                Report(progress, descriptor, "Ready", descriptor.ExpectedBytes ?? 0, descriptor.ExpectedBytes, 100);
                Log($"Provisioned asset '{descriptor.Id}' to '{installPath}'.");
            }
            catch
            {
                Report(progress, descriptor, "Failed", 0, descriptor.ExpectedBytes, 0);
                throw;
            }
            finally
            {
                DeleteDirectoryBestEffort(tempRoot);
            }
        }
        finally
        {
            _installSemaphore.Release();
        }
    }

    public async Task RemoveAssetAsync(string assetId, CancellationToken cancellationToken)
    {
        ProvisionedAssetDescriptor descriptor = GetDescriptor(assetId);
        string installPath = ResolveInstallPath(descriptor.Id);
        await _installSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (descriptor.InstallKind == ProvisioningInstallKind.File)
            {
                if (File.Exists(installPath))
                {
                    File.Delete(installPath);
                }
            }
            else if (Directory.Exists(installPath))
            {
                DeleteDirectoryBestEffort(installPath);
            }

            ProvisionedAssetStateDocument state = LoadStateDocument();
            state.Assets.RemoveAll(item => string.Equals(item.AssetId, descriptor.Id, StringComparison.OrdinalIgnoreCase));
            SaveStateDocument(state);
        }
        finally
        {
            _installSemaphore.Release();
        }
    }

    public void Dispose()
    {
        _installSemaphore.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static AssetProvisioningManifest LoadManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("Asset provisioning manifest was not found.", manifestPath);
        }

        AssetProvisioningManifest? manifest = JsonSerializer.Deserialize<AssetProvisioningManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            });
        if (manifest is null || manifest.Assets.Length == 0)
        {
            throw new InvalidOperationException("Asset provisioning manifest did not contain any assets.");
        }

        return manifest;
    }

    private ProvisionedAssetDescriptor GetDescriptor(string assetId)
    {
        string trimmed = assetId?.Trim() ?? string.Empty;
        return _assets.TryGetValue(trimmed, out ProvisionedAssetDescriptor? descriptor)
            ? descriptor
            : throw new InvalidOperationException($"Unknown provisioned asset '{assetId}'.");
    }

    private bool IsSupportedOnCurrentArchitecture(ProvisionedAssetDescriptor descriptor)
    {
        if (descriptor.SupportedArchitectures.Length == 0)
        {
            return true;
        }

        string current = RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        };

        return descriptor.SupportedArchitectures.Any(architecture =>
            string.Equals(architecture, current, StringComparison.OrdinalIgnoreCase));
    }

    private bool CanResolveSource(ProvisionedAssetDescriptor descriptor, out string? resolvedSource)
    {
        resolvedSource = null;

        if (!string.IsNullOrWhiteSpace(descriptor.DevelopmentSourceRelativePath))
        {
            string developmentPath = Path.GetFullPath(Path.Combine(_repoRootPath, descriptor.DevelopmentSourceRelativePath));
            bool developmentExists = descriptor.InstallKind == ProvisioningInstallKind.File
                ? File.Exists(developmentPath)
                : Directory.Exists(developmentPath);
            if (developmentExists)
            {
                resolvedSource = developmentPath;
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(descriptor.DownloadUri))
        {
            resolvedSource = descriptor.DownloadUri.Trim();
            return true;
        }

        return false;
    }

    private async Task MaterializeFileAsync(
        ProvisionedAssetDescriptor descriptor,
        string source,
        string tempFilePath,
        IProgress<AssetProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(tempFilePath)!);

        if (File.Exists(source))
        {
            FileInfo sourceInfo = new(source);
            await using FileStream sourceStream = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using FileStream destinationStream = File.Open(tempFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await CopyWithProgressAsync(descriptor, sourceStream, destinationStream, sourceInfo.Length, progress, cancellationToken);
        }
        else
        {
            await DownloadFileWithRetryAsync(descriptor, source, tempFilePath, progress, cancellationToken);
        }

        VerifyExpectedBytesIfConfigured(descriptor, tempFilePath);
        VerifyFileHashIfConfigured(descriptor, tempFilePath);
    }

    private async Task MaterializeDirectoryAsync(
        ProvisionedAssetDescriptor descriptor,
        string source,
        string tempDirectoryPath,
        IProgress<AssetProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(source))
        {
            CopyDirectory(source, tempDirectoryPath);
            Report(progress, descriptor, "Copied", descriptor.ExpectedBytes ?? 0, descriptor.ExpectedBytes, 100);
            return;
        }

        Directory.CreateDirectory(tempDirectoryPath);
        string archivePath = Path.Combine(Path.GetDirectoryName(tempDirectoryPath)!, $"{descriptor.Id}.zip");
        await MaterializeFileAsync(descriptor, source, archivePath, progress, cancellationToken);
        ZipFile.ExtractToDirectory(archivePath, tempDirectoryPath);
    }

    private async Task PromoteFileAsync(string tempFilePath, string installPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(installPath)!);
        string tempInstallPath = $"{installPath}.{Guid.NewGuid():N}.tmp";
        if (File.Exists(tempInstallPath))
        {
            File.Delete(tempInstallPath);
        }

        File.Move(tempFilePath, tempInstallPath);
        if (File.Exists(installPath))
        {
            File.Delete(installPath);
        }

        File.Move(tempInstallPath, installPath);
    }

    private async Task PromoteDirectoryAsync(string tempDirectoryPath, string installPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(installPath)!);
        string tempInstallPath = $"{installPath}.{Guid.NewGuid():N}.tmp";
        if (Directory.Exists(tempInstallPath))
        {
            DeleteDirectoryBestEffort(tempInstallPath);
        }

        Directory.Move(tempDirectoryPath, tempInstallPath);
        if (Directory.Exists(installPath))
        {
            DeleteDirectoryBestEffort(installPath);
        }

        Directory.Move(tempInstallPath, installPath);
        await Task.CompletedTask;
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

    private async Task CopyWithProgressAsync(
        ProvisionedAssetDescriptor descriptor,
        Stream source,
        Stream destination,
        long? totalBytes,
        IProgress<AssetProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[CopyBufferSize];
        long bytesReceived = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            bytesReceived += read;
            double percent = totalBytes is > 0
                ? Math.Min(99, bytesReceived * 100d / totalBytes.Value)
                : 0;
            Report(progress, descriptor, "Downloading...", bytesReceived, totalBytes, percent);
        }

        await destination.FlushAsync(cancellationToken);
    }

    private async Task DownloadFileWithRetryAsync(
        ProvisionedAssetDescriptor descriptor,
        string source,
        string tempFilePath,
        IProgress<AssetProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (int attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using HttpResponseMessage response = await _httpClient.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                long? totalBytes = response.Content.Headers.ContentLength ?? descriptor.ExpectedBytes;
                await using Stream networkStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using FileStream destinationStream = File.Open(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await CopyWithProgressAsync(descriptor, networkStream, destinationStream, totalBytes, progress, cancellationToken);
                return;
            }
            catch (Exception ex) when (IsRetryableDownloadFailure(ex, cancellationToken) && attempt < MaxDownloadAttempts)
            {
                lastException = ex;
                TryDeleteFile(tempFilePath);
                _processLogService.Log(
                    nameof(AssetProvisioningService),
                    $"Retrying download for '{descriptor.DisplayName}' after attempt {attempt} failed: {ex.Message}",
                    ProcessLogLevel.Warning);
                Report(progress, descriptor, $"Retrying download ({attempt + 1}/{MaxDownloadAttempts})...", 0, descriptor.ExpectedBytes, 0);
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
            catch (Exception ex)
            {
                lastException = ex;
                break;
            }
        }

        TryDeleteFile(tempFilePath);
        throw new InvalidOperationException(
            $"Failed to download '{descriptor.DisplayName}' after {MaxDownloadAttempts} attempts.",
            lastException);
    }

    private static void VerifyFileHashIfConfigured(ProvisionedAssetDescriptor descriptor, string filePath)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Sha256))
        {
            return;
        }

        using FileStream stream = File.OpenRead(filePath);
        using SHA256 sha256 = SHA256.Create();
        string actual = Convert.ToHexString(sha256.ComputeHash(stream));
        if (!string.Equals(actual, descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Checksum verification failed for '{descriptor.DisplayName}'.");
        }
    }

    private static void VerifyExpectedBytesIfConfigured(ProvisionedAssetDescriptor descriptor, string filePath)
    {
        if (!descriptor.ExpectedBytes.HasValue)
        {
            return;
        }

        long actualBytes = new FileInfo(filePath).Length;
        if (actualBytes != descriptor.ExpectedBytes.Value)
        {
            throw new InvalidOperationException(
                $"Downloaded size mismatch for '{descriptor.DisplayName}'. Expected {descriptor.ExpectedBytes.Value} bytes but received {actualBytes} bytes.");
        }
    }

    private ProvisionedAssetStateDocument LoadStateDocument()
    {
        if (!File.Exists(_stateFilePath))
        {
            return new ProvisionedAssetStateDocument { SchemaVersion = StateSchemaVersion };
        }

        ProvisionedAssetStateDocument? state = JsonSerializer.Deserialize<ProvisionedAssetStateDocument>(
            File.ReadAllText(_stateFilePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        state ??= new ProvisionedAssetStateDocument();
        state.Assets ??= new List<ProvisionedAssetStateItem>();
        return state;
    }

    private void SaveInstalledState(ProvisionedAssetDescriptor descriptor, string installPath)
    {
        ProvisionedAssetStateDocument state = LoadStateDocument();
        state.SchemaVersion = StateSchemaVersion;
        state.Assets.RemoveAll(item => string.Equals(item.AssetId, descriptor.Id, StringComparison.OrdinalIgnoreCase));
        state.Assets.Add(new ProvisionedAssetStateItem
        {
            AssetId = descriptor.Id,
            Version = descriptor.Version,
            InstallPath = installPath,
            InstalledAtUtc = DateTimeOffset.UtcNow,
        });
        SaveStateDocument(state);
    }

    private void SaveStateDocument(ProvisionedAssetStateDocument state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
        File.WriteAllText(
            _stateFilePath,
            JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
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
        }
    }

    private static bool IsRetryableDownloadFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exception is HttpRequestException
            or IOException
            or TimeoutException
            or TaskCanceledException;
    }

    private void Report(
        IProgress<AssetProvisioningProgress>? progress,
        ProvisionedAssetDescriptor descriptor,
        string status,
        long bytesReceived,
        long? totalBytes,
        double percent)
    {
        progress?.Report(new AssetProvisioningProgress(
            descriptor.Id,
            descriptor.DisplayName,
            status,
            bytesReceived,
            totalBytes,
            percent));
    }

    private void Log(string message)
    {
        _processLogService.Log("AssetProvisioning", message);
    }
}
