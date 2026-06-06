using System.IO.Compression;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioScript.Services;

public sealed class AssetProvisioningService : IAssetProvisioningService, IDisposable
{
    private const int MinimumRequiredSourceCount = 3;
    private const int StateSchemaVersion = 1;
    private const int CopyBufferSize = 128 * 1024;
    private const int MaxDownloadAttemptsPerSource = 1;
    private const int MaxDownloadConnectionsPerServer = 3;
    private static readonly TimeSpan HttpClientTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan HttpConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HttpPooledConnectionLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan HttpPooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);

    private readonly ProcessLogService _processLogService;
    private readonly AppDataPathProvider _paths;
    private readonly string _repoRootPath;
    private readonly string _stateFilePath;
    private readonly Uri? _downloadBaseUriOverride;
    private readonly Uri? _downloadMirrorBaseUriOverride;
    private readonly HashSet<string> _allowedDownloadHosts;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IReadOnlyList<ProvisionedAssetDescriptor> _manifestAssets;
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
        _downloadBaseUriOverride = TryParseDownloadBaseUriOverride(processLogService);
        _downloadMirrorBaseUriOverride = TryParseDownloadUriOverride(
            "AUDIOSCRIPT_ASSET_DOWNLOAD_MIRROR_URI",
            processLogService);
        _allowedDownloadHosts = BuildAllowedDownloadHosts();
        string resolvedManifestPath = string.IsNullOrWhiteSpace(manifestPath)
            ? Path.Combine(AppContext.BaseDirectory, "assets", "bootstrap", "asset-manifest.json")
            : Path.GetFullPath(manifestPath);

        AssetProvisioningManifest manifest = LoadManifest(
            resolvedManifestPath,
            _allowedDownloadHosts);
        _manifestAssets = manifest.Assets.ToArray();
        _assets = manifest.Assets.ToDictionary(asset => asset.Id, StringComparer.OrdinalIgnoreCase);
        _stateFilePath = Path.Combine(_paths.ProvisioningPath, "asset-state.json");
        _httpClient = httpClient ?? CreateDefaultHttpClient();
        _ownsHttpClient = httpClient is null;
        LogHttpClientConfiguration();
    }

    public IReadOnlyList<ProvisionedAssetDescriptor> GetManifestAssets()
    {
        return _manifestAssets;
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

        if (!TryResolveSourceCandidates(descriptor, out _))
        {
            return new AssetProvisioningStatus(
                descriptor.Id,
                descriptor.DisplayName,
                AssetProvisioningState.Unconfigured,
                installPath,
                descriptor.IsPackagedRequired
                    ? "Required packaged asset was not found in this installation."
                    : "Asset source is not configured.");
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
        if (descriptor.IsPackagedRequired && IsProductionContext())
        {
            return ResolvePackagedPath(descriptor);
        }

        string root = descriptor.InstallRoot switch
        {
            ProvisioningInstallRoot.Models => _paths.ModelsPath,
            ProvisioningInstallRoot.Pyannote => _paths.PyannoteAssetsPath,
            ProvisioningInstallRoot.Python => _paths.PythonRuntimesPath,
            ProvisioningInstallRoot.Tools => _paths.ToolsPath,
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
        if (descriptor.IsPackagedRequired && IsProductionContext())
        {
            return DoesInstallPathExist(descriptor, installPath);
        }

        bool pathExists = DoesInstallPathExist(descriptor, installPath);
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

        if (descriptor.IsPackagedRequired && IsProductionContext())
        {
            throw new InvalidOperationException(
                $"{descriptor.DisplayName} is a bundled production asset. Reinstall AudioScript from Microsoft Store if this asset is missing or corrupted.");
        }

        if (!CanResolveSource(descriptor, out _))
        {
            throw new InvalidOperationException(
                $"{descriptor.DisplayName} download source is not configured.");
        }

        if (!TryResolveSourceCandidates(descriptor, out IReadOnlyList<string> sourceCandidates))
        {
            throw new InvalidOperationException(
                $"{descriptor.DisplayName} download source is not configured.");
        }

        LogDownloadCandidates(descriptor, sourceCandidates);

        await _installSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (IsInstalled(descriptor.Id))
            {
                Report(progress, descriptor, "Ready", descriptor.ExpectedBytes ?? 0, descriptor.ExpectedBytes, 100);
                return;
            }

            string installPath = ResolveInstallPath(descriptor.Id);
            Directory.CreateDirectory(_paths.ProvisioningPath);
            Directory.CreateDirectory(_paths.TempPath);

            List<Exception> sourceFailures = new();
            for (int sourceIndex = 0; sourceIndex < sourceCandidates.Count; sourceIndex++)
            {
                string source = sourceCandidates[sourceIndex];
                string tempRoot = Path.Combine(_paths.TempPath, "asset-provisioning", $"{descriptor.Id}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempRoot);

                try
                {
                    Report(progress, descriptor, "Preparing...", 0, descriptor.ExpectedBytes, 0);
                    _processLogService.Log(
                        nameof(AssetProvisioningService),
                        $"download_candidate asset='{descriptor.Id}' display='{descriptor.DisplayName}' candidate={sourceIndex + 1}/{sourceCandidates.Count} source='{source}'.");

                    if (descriptor.InstallKind == ProvisioningInstallKind.File)
                    {
                        string tempFilePath = Path.Combine(tempRoot, Path.GetFileName(installPath));
                        await MaterializeFileAsync(descriptor, source, tempFilePath, progress, cancellationToken);
                        await PromoteFileAsync(tempFilePath, installPath, cancellationToken);
                    }
                    else
                    {
                        string tempDirectoryPath = Path.Combine(tempRoot, "content");
                        await MaterializeDirectoryAsync(descriptor, source, tempDirectoryPath, progress, cancellationToken);
                        await PromoteDirectoryAsync(tempDirectoryPath, installPath, cancellationToken);
                    }

                    SaveInstalledState(descriptor, installPath);
                    Report(progress, descriptor, "Ready", descriptor.ExpectedBytes ?? 0, descriptor.ExpectedBytes, 100);
                    Log($"Provisioned asset '{descriptor.Id}' to '{installPath}'.");
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    sourceFailures.Add(ex);
                    _processLogService.LogException(
                        nameof(AssetProvisioningService),
                        $"Asset '{descriptor.DisplayName}' failed from source candidate {sourceIndex + 1}/{sourceCandidates.Count}.",
                        ex);

                    if (sourceIndex + 1 < sourceCandidates.Count)
                    {
                        Report(progress, descriptor, $"Trying alternate source ({sourceIndex + 2}/{sourceCandidates.Count})...", 0, descriptor.ExpectedBytes, 0);
                        continue;
                    }

                    throw;
                }
                finally
                {
                    DeleteDirectoryBestEffort(tempRoot);
                }
            }

            Report(progress, descriptor, "Failed", 0, descriptor.ExpectedBytes, 0);
            throw new InvalidOperationException(
                $"{descriptor.DisplayName} could not be installed from any configured source.",
                sourceFailures.Count == 0 ? null : new AggregateException(sourceFailures));
        }
        finally
        {
            _installSemaphore.Release();
        }
    }

    private bool DoesInstallPathExist(ProvisionedAssetDescriptor descriptor, string installPath)
    {
        bool pathExists = descriptor.InstallKind switch
        {
            ProvisioningInstallKind.File => File.Exists(installPath),
            ProvisioningInstallKind.Directory => Directory.Exists(installPath),
            _ => false,
        };
        return pathExists;
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

    private static AssetProvisioningManifest LoadManifest(
        string manifestPath,
        IReadOnlySet<string> allowedDownloadHosts)
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

        ValidateManifestContract(manifest, allowedDownloadHosts);
        return manifest;
    }

    private static void ValidateManifestContract(
        AssetProvisioningManifest manifest,
        IReadOnlySet<string> allowedDownloadHosts)
    {
        foreach (ProvisionedAssetDescriptor asset in manifest.Assets)
        {
            string[] sources = (asset.DownloadSources ?? Array.Empty<string>())
                .Where(source => !string.IsNullOrWhiteSpace(source))
                .Select(source => source.Trim())
                .ToArray();

            int minimumDownloadSources = asset.MinimumDownloadSources.GetValueOrDefault(
                asset.Required && !asset.IsPackagedRequired
                    ? MinimumRequiredSourceCount
                    : 0);
            if (minimumDownloadSources < 0)
            {
                throw new InvalidOperationException(
                    $"Asset '{asset.Id}' must not define a negative minimumDownloadSources value.");
            }

            if (asset.Required)
            {
                if (asset.IsPackagedRequired)
                {
                    if (string.IsNullOrWhiteSpace(asset.PackagedSourceRelativePath))
                    {
                        throw new InvalidOperationException(
                            $"Required packaged asset '{asset.Id}' must define packagedSourceRelativePath.");
                    }

                    continue;
                }

                if (sources.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Required asset '{asset.Id}' must define at least one download source.");
                }
            }

            if (minimumDownloadSources > 0 && sources.Length < minimumDownloadSources)
            {
                throw new InvalidOperationException(
                    $"Asset '{asset.Id}' must define at least {minimumDownloadSources} download source(s).");
            }

            if (sources.Length > 0)
            {
                if (string.IsNullOrWhiteSpace(asset.DownloadUri)
                    || !string.Equals(asset.DownloadUri.Trim(), sources[0], StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Asset '{asset.Id}' must keep downloadUri aligned with the first downloadSources entry.");
                }
            }

            foreach (string source in sources)
            {
                ValidateRemoteSourcePolicy(asset, source, allowedDownloadHosts);
            }
        }
    }

    private static void ValidateRemoteSourcePolicy(
        ProvisionedAssetDescriptor asset,
        string source,
        IReadOnlySet<string> allowedDownloadHosts)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? sourceUri))
        {
            throw new InvalidOperationException(
                $"Asset '{asset.Id}' has non-absolute source '{source}'.");
        }

        if (!string.Equals(sourceUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Asset '{asset.Id}' source '{source}' must use https.");
        }

        if (!allowedDownloadHosts.Contains(sourceUri.Host))
        {
            throw new InvalidOperationException(
                $"Asset '{asset.Id}' source host '{sourceUri.Host}' is not allowlisted.");
        }

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
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        };

        return descriptor.SupportedArchitectures.Any(architecture =>
            string.Equals(architecture, current, StringComparison.OrdinalIgnoreCase));
    }

    private bool CanResolveSource(ProvisionedAssetDescriptor descriptor, out string? resolvedSource)
    {
        if (TryResolveSourceCandidates(descriptor, out IReadOnlyList<string> sourceCandidates)
            && sourceCandidates.Count > 0)
        {
            resolvedSource = sourceCandidates[0];
            return true;
        }

        resolvedSource = null;
        return false;
    }

    private static Uri? TryParseDownloadBaseUriOverride(ProcessLogService processLogService)
    {
        return TryParseDownloadUriOverride("AUDIOSCRIPT_ASSET_DOWNLOAD_BASE_URI", processLogService);
    }

    private static Uri? TryParseDownloadUriOverride(string environmentVariableName, ProcessLogService processLogService)
    {
        string? rawValue = Environment.GetEnvironmentVariable(environmentVariableName);
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        string trimmed = rawValue.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsed))
        {
            return parsed;
        }

        processLogService.Log(
            nameof(AssetProvisioningService),
            $"Ignoring invalid {environmentVariableName} value '{trimmed}'.",
            ProcessLogLevel.Warning);
        return null;
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = HttpConnectTimeout,
            MaxConnectionsPerServer = MaxDownloadConnectionsPerServer,
            PooledConnectionIdleTimeout = HttpPooledConnectionIdleTimeout,
            PooledConnectionLifetime = HttpPooledConnectionLifetime,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            },
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = HttpClientTimeout,
        };
    }

    private void LogHttpClientConfiguration()
    {
        string overrideDescription = _downloadBaseUriOverride is null
            ? "none"
            : $"{_downloadBaseUriOverride.Scheme}://{_downloadBaseUriOverride.Host}:{_downloadBaseUriOverride.Port}{_downloadBaseUriOverride.AbsolutePath}";

        string mirrorDescription = _downloadMirrorBaseUriOverride is null
            ? "none"
            : $"{_downloadMirrorBaseUriOverride.Scheme}://{_downloadMirrorBaseUriOverride.Host}:{_downloadMirrorBaseUriOverride.Port}{_downloadMirrorBaseUriOverride.AbsolutePath}";

        _processLogService.Log(
            nameof(AssetProvisioningService),
            $"http_client_config timeout='{HttpClientTimeout}' connectTimeout='{HttpConnectTimeout}' pooledConnectionLifetime='{HttpPooledConnectionLifetime}' pooledConnectionIdleTimeout='{HttpPooledConnectionIdleTimeout}' tls='Tls12,Tls13' maxConnectionsPerServer={MaxDownloadConnectionsPerServer} downloadBaseUriOverride='{overrideDescription}' mirrorBaseUriOverride='{mirrorDescription}'.");
    }

    private static HashSet<string> BuildAllowedDownloadHosts()
    {
        string? raw = Environment.GetEnvironmentVariable("AUDIOSCRIPT_ASSET_ALLOWED_HOSTS");
        string[] configuredHosts = string.IsNullOrWhiteSpace(raw)
            ? ["huggingface.co", "hf-mirror.com", "www.python.org", "python.org", "github.com", "objects.githubusercontent.com"]
            : raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return new HashSet<string>(configuredHosts, StringComparer.OrdinalIgnoreCase);
    }

    private bool IsProductionContext()
    {
        if (_paths.IsPackaged)
        {
            return true;
        }

        string? forceProduction = Environment.GetEnvironmentVariable("AUDIOSCRIPT_FORCE_PRODUCTION_PROVISIONING");
        return string.Equals(forceProduction, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(forceProduction, "true", StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldUseDevelopmentSources()
    {
        return !IsProductionContext();
    }

    private string ResolveDownloadSource(string source)
    {
        return ResolveDownloadSource(source, _downloadBaseUriOverride);
    }

    private string ResolveDownloadSource(string source, Uri? baseOverride)
    {
        if (baseOverride is null || !Uri.TryCreate(source, UriKind.Absolute, out Uri? parsedSource))
        {
            return source;
        }

        if (!string.Equals(parsedSource.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(parsedSource.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        string relativePath = parsedSource.PathAndQuery.TrimStart('/');
        Uri resolved = new(baseOverride, relativePath);
        return resolved.ToString();
    }

    private bool TryResolveSourceCandidates(ProvisionedAssetDescriptor descriptor, out IReadOnlyList<string> sourceCandidates)
    {
        var candidates = new List<string>();

        if (descriptor.IsPackagedRequired && IsProductionContext())
        {
            string packagedPath = ResolvePackagedPath(descriptor);
            bool packagedExists = descriptor.InstallKind == ProvisioningInstallKind.File
                ? File.Exists(packagedPath)
                : Directory.Exists(packagedPath);
            if (packagedExists)
            {
                candidates.Add(packagedPath);
                sourceCandidates = candidates;
                return true;
            }

            sourceCandidates = candidates;
            return false;
        }

        if (ShouldUseDevelopmentSources() && !string.IsNullOrWhiteSpace(descriptor.DevelopmentSourceRelativePath))
        {
            string developmentPath = Path.GetFullPath(Path.Combine(_repoRootPath, descriptor.DevelopmentSourceRelativePath));
            bool developmentExists = descriptor.InstallKind == ProvisioningInstallKind.File
                ? File.Exists(developmentPath)
                : Directory.Exists(developmentPath);
            if (developmentExists)
            {
                candidates.Add(developmentPath);
                sourceCandidates = candidates;
                return true;
            }
        }

        foreach (string remoteSource in EnumerateRemoteSources(descriptor))
        {
            TryAddResolvedSourceCandidates(candidates, remoteSource);
        }

        sourceCandidates = candidates;
        return candidates.Count > 0;
    }

    private string ResolvePackagedPath(ProvisionedAssetDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.PackagedSourceRelativePath))
        {
            throw new InvalidOperationException(
                $"Packaged asset '{descriptor.Id}' is missing packagedSourceRelativePath.");
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, descriptor.PackagedSourceRelativePath));
    }

    private static IEnumerable<string> EnumerateRemoteSources(ProvisionedAssetDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(descriptor.DownloadUri))
        {
            yield return descriptor.DownloadUri.Trim();
        }

        foreach (string source in descriptor.DownloadSources ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(source))
            {
                yield return source.Trim();
            }
        }
    }

    private bool TryAddResolvedSourceCandidates(List<string> candidates, string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        string resolvedSource = ResolveDownloadSource(source.Trim());
        AddUniqueCandidate(candidates, resolvedSource);

        if (_downloadMirrorBaseUriOverride is not null)
        {
            string mirrorSource = ResolveDownloadSource(source.Trim(), _downloadMirrorBaseUriOverride);
            AddUniqueCandidate(candidates, mirrorSource);
        }

        return true;
    }

    private void AddUniqueCandidate(List<string> candidates, string candidate)
    {
        if (candidates.Any(existing => string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        candidates.Add(candidate);
    }

    private void LogDownloadCandidates(ProvisionedAssetDescriptor descriptor, IReadOnlyList<string> sourceCandidates)
    {
        if (sourceCandidates.Count == 1 && !Uri.TryCreate(sourceCandidates[0], UriKind.Absolute, out Uri? uri))
        {
            _processLogService.Log(
                nameof(AssetProvisioningService),
                $"download_source asset='{descriptor.Id}' display='{descriptor.DisplayName}' source='{sourceCandidates[0]}' kind='local'.");
            return;
        }

        for (int i = 0; i < sourceCandidates.Count; i++)
        {
            string source = sourceCandidates[i];
            if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? candidateUri))
            {
                _processLogService.Log(
                    nameof(AssetProvisioningService),
                    $"download_source asset='{descriptor.Id}' display='{descriptor.DisplayName}' candidate={i + 1}/{sourceCandidates.Count} source='{source}' kind='local'.");
                continue;
            }

            _processLogService.Log(
                nameof(AssetProvisioningService),
                $"download_source asset='{descriptor.Id}' display='{descriptor.DisplayName}' candidate={i + 1}/{sourceCandidates.Count} scheme='{candidateUri.Scheme}' host='{candidateUri.Host}' port={candidateUri.Port} path='{candidateUri.AbsolutePath}' query='{candidateUri.Query}'.");
        }
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
            await DownloadFileAsync(descriptor, source, tempFilePath, progress, cancellationToken);
        }

        if (TryGetExpectedBytesMismatch(descriptor, tempFilePath, out long expectedBytes, out long actualBytes))
        {
            _processLogService.Log(
                nameof(AssetProvisioningService),
                $"size_mismatch_nonblocking asset='{descriptor.Id}' display='{descriptor.DisplayName}' expectedBytes={expectedBytes} actualBytes={actualBytes}.",
                ProcessLogLevel.Warning);
        }

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

        if (TryParseHuggingFaceModelApiSource(source, out Uri modelApiUri, out string modelRepositoryId))
        {
            await DownloadHuggingFaceModelRepositoryAsync(
                descriptor,
                modelApiUri,
                modelRepositoryId,
                tempDirectoryPath,
                progress,
                cancellationToken);
            return;
        }

        if (CanStreamExtractDirectoryArchive(descriptor, source))
        {
            await DownloadDirectoryAsync(descriptor, source, tempDirectoryPath, progress, cancellationToken);
            return;
        }

        Directory.CreateDirectory(tempDirectoryPath);
        string archivePath = Path.Combine(Path.GetDirectoryName(tempDirectoryPath)!, $"{descriptor.Id}.zip");
        await MaterializeFileAsync(descriptor, source, archivePath, progress, cancellationToken);
        ZipFile.ExtractToDirectory(archivePath, tempDirectoryPath);
    }

    private async Task DownloadHuggingFaceModelRepositoryAsync(
        ProvisionedAssetDescriptor descriptor,
        Uri modelApiUri,
        string modelRepositoryId,
        string tempDirectoryPath,
        IProgress<AssetProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage metadataResponse = await _httpClient.GetAsync(modelApiUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        metadataResponse.EnsureSuccessStatusCode();
        await using Stream metadataStream = await metadataResponse.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument metadata = await JsonDocument.ParseAsync(metadataStream, cancellationToken: cancellationToken);

        if (!metadata.RootElement.TryGetProperty("siblings", out JsonElement siblings)
            || siblings.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Hugging Face model API response for '{modelRepositoryId}' did not include siblings.");
        }

        string[] files = siblings.EnumerateArray()
            .Select(item => item.TryGetProperty("rfilename", out JsonElement fileNode) ? fileNode.GetString() : null)
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Select(file => file!)
            .Where(file => !file.EndsWith("/", StringComparison.Ordinal))
            .Where(file => !file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            .Where(file => !string.Equals(file, ".gitattributes", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (files.Length == 0)
        {
            throw new InvalidOperationException(
                $"Hugging Face model '{modelRepositoryId}' did not contain downloadable model files.");
        }

        DeleteDirectoryBestEffort(tempDirectoryPath);
        Directory.CreateDirectory(tempDirectoryPath);
        int completed = 0;
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Uri fileUri = BuildHuggingFaceResolveUri(modelApiUri, modelRepositoryId, file);
            string destinationPath = Path.Combine(tempDirectoryPath, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await DownloadRepositoryFileAsync(fileUri, destinationPath, cancellationToken);

            completed++;
            double percent = completed * 100d / files.Length;
            Report(progress, descriptor, $"Downloading ({completed}/{files.Length})...", completed, files.Length, percent);
        }

        Report(progress, descriptor, "Finalizing install...", files.Length, files.Length, 99);
    }

    private async Task DownloadRepositoryFileAsync(
        Uri fileUri,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        string endpointDescription = DescribeEndpoint(fileUri.ToString());
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(fileUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using FileStream destination = File.Open(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(destination, cancellationToken);
        }
        catch
        {
            TryDeleteFile(destinationPath);
            throw;
        }
    }

    private static bool TryParseHuggingFaceModelApiSource(string source, out Uri modelApiUri, out string modelRepositoryId)
    {
        modelApiUri = null!;
        modelRepositoryId = string.Empty;
        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }

        if (!string.Equals(parsed.Host, "huggingface.co", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(parsed.Host, "hf-mirror.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] segments = parsed.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4
            || !string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[1], "models", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        modelRepositoryId = $"{segments[2]}/{segments[3]}";
        modelApiUri = parsed;
        return true;
    }

    private static Uri BuildHuggingFaceResolveUri(Uri modelApiUri, string modelRepositoryId, string relativeFilePath)
    {
        string escapedPath = string.Join(
            "/",
            relativeFilePath.Split('/').Select(Uri.EscapeDataString));
        string uri = $"{modelApiUri.Scheme}://{modelApiUri.Host}/{modelRepositoryId}/resolve/main/{escapedPath}?download=true";
        return new Uri(uri);
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

    private async Task DownloadFileAsync(
        ProvisionedAssetDescriptor descriptor,
        string source,
        string tempFilePath,
        IProgress<AssetProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        string endpointDescription = DescribeEndpoint(source);
        _processLogService.Log(
            nameof(AssetProvisioningService),
            $"download_begin asset='{descriptor.Id}' display='{descriptor.DisplayName}' endpoint='{endpointDescription}' attempts={MaxDownloadAttemptsPerSource}.");

        cancellationToken.ThrowIfCancellationRequested();
        _processLogService.Log(
            nameof(AssetProvisioningService),
            $"download_attempt asset='{descriptor.Id}' display='{descriptor.DisplayName}' attempt=1/{MaxDownloadAttemptsPerSource} endpoint='{endpointDescription}'.");

        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            ValidateAssetDownloadResponse(descriptor, endpointDescription, response);
            long? totalBytes = response.Content.Headers.ContentLength ?? descriptor.ExpectedBytes;
            string? contentType = response.Content.Headers.ContentType?.MediaType;
            Uri? responseUri = response.RequestMessage?.RequestUri;
            _processLogService.Log(
                nameof(AssetProvisioningService),
                $"download_response asset='{descriptor.Id}' display='{descriptor.DisplayName}' attempt=1 statusCode={(int)response.StatusCode} contentType='{contentType ?? "(none)"}' contentLength={response.Content.Headers.ContentLength?.ToString() ?? "(unknown)"} responseUri='{responseUri?.ToString() ?? endpointDescription}'.");
            await using Stream networkStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using FileStream destinationStream = File.Open(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await CopyWithProgressAsync(descriptor, networkStream, destinationStream, totalBytes, progress, cancellationToken);
            _processLogService.Log(
                nameof(AssetProvisioningService),
                $"download_complete asset='{descriptor.Id}' display='{descriptor.DisplayName}' endpoint='{endpointDescription}' attempt=1.");
        }
        catch
        {
            TryDeleteFile(tempFilePath);
            throw;
        }
    }

    private async Task DownloadDirectoryAsync(
        ProvisionedAssetDescriptor descriptor,
        string source,
        string tempDirectoryPath,
        IProgress<AssetProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        string endpointDescription = DescribeEndpoint(source);
        _processLogService.Log(
            nameof(AssetProvisioningService),
            $"download_begin asset='{descriptor.Id}' display='{descriptor.DisplayName}' endpoint='{endpointDescription}' attempts={MaxDownloadAttemptsPerSource} extraction='stream'.");

        cancellationToken.ThrowIfCancellationRequested();
        _processLogService.Log(
            nameof(AssetProvisioningService),
            $"download_attempt asset='{descriptor.Id}' display='{descriptor.DisplayName}' attempt=1/{MaxDownloadAttemptsPerSource} endpoint='{endpointDescription}' extraction='stream'.");

        try
        {
            DeleteDirectoryBestEffort(tempDirectoryPath);

            using HttpResponseMessage response = await _httpClient.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            ValidateAssetDownloadResponse(descriptor, endpointDescription, response);

            long? totalBytes = response.Content.Headers.ContentLength ?? descriptor.ExpectedBytes;
            string? contentType = response.Content.Headers.ContentType?.MediaType;
            Uri? responseUri = response.RequestMessage?.RequestUri;
            _processLogService.Log(
                nameof(AssetProvisioningService),
                $"download_response asset='{descriptor.Id}' display='{descriptor.DisplayName}' attempt=1 statusCode={(int)response.StatusCode} contentType='{contentType ?? "(none)"}' contentLength={response.Content.Headers.ContentLength?.ToString() ?? "(unknown)"} responseUri='{responseUri?.ToString() ?? endpointDescription}' extraction='stream'.");

            Directory.CreateDirectory(Path.GetDirectoryName(tempDirectoryPath)!);
            await using Stream networkStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var progressStream = new ProgressReadStream(
                networkStream,
                bytesReceived =>
                {
                    double percent = totalBytes is > 0
                        ? Math.Min(99, bytesReceived * 100d / totalBytes.Value)
                        : 0;
                    Report(progress, descriptor, "Downloading...", bytesReceived, totalBytes, percent);
                });
            ZipFile.ExtractToDirectory(progressStream, tempDirectoryPath);

            _processLogService.Log(
                nameof(AssetProvisioningService),
                $"download_complete asset='{descriptor.Id}' display='{descriptor.DisplayName}' endpoint='{endpointDescription}' attempt=1 extraction='stream'.");
            Report(progress, descriptor, "Finalizing install...", totalBytes ?? 0, totalBytes, 99);
        }
        catch
        {
            DeleteDirectoryBestEffort(tempDirectoryPath);
            throw;
        }
    }

    private static bool CanStreamExtractDirectoryArchive(ProvisionedAssetDescriptor descriptor, string source)
    {
        if (descriptor.InstallKind != ProvisioningInstallKind.Directory)
        {
            return false;
        }

        if (descriptor.ExpectedBytes.HasValue || !string.IsNullOrWhiteSpace(descriptor.Sha256))
        {
            return false;
        }

        return Uri.TryCreate(source, UriKind.Absolute, out Uri? uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    private static string DescribeEndpoint(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri))
        {
            return source;
        }

        return $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath}{uri.Query}";
    }

    private static void ValidateAssetDownloadResponse(
        ProvisionedAssetDescriptor descriptor,
        string endpointDescription,
        HttpResponseMessage response)
    {
        string? contentType = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return;
        }

        if (string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contentType, "text/plain", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Asset endpoint '{endpointDescription}' for '{descriptor.DisplayName}' returned '{contentType}' instead of an asset payload.");
        }
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

    private static bool TryGetExpectedBytesMismatch(
        ProvisionedAssetDescriptor descriptor,
        string filePath,
        out long expectedBytes,
        out long actualBytes)
    {
        expectedBytes = 0;
        actualBytes = 0;
        if (!descriptor.ExpectedBytes.HasValue)
        {
            return false;
        }

        expectedBytes = descriptor.ExpectedBytes.Value;
        actualBytes = new FileInfo(filePath).Length;
        if (actualBytes != expectedBytes)
        {
            return true;
        }

        return false;
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

        if (exception is HttpRequestException httpRequestException
            && httpRequestException.StatusCode is HttpStatusCode statusCode)
        {
            return IsRetryableStatusCode(statusCode);
        }

        return exception is HttpRequestException
            or IOException
            or TimeoutException
            or TaskCanceledException;
    }

    private static bool IsRetryableStatusCode(HttpStatusCode statusCode)
    {
        int statusCodeValue = (int)statusCode;
        if (statusCodeValue >= 500 && statusCodeValue <= 599)
        {
            return true;
        }

        return statusCode == HttpStatusCode.RequestTimeout
            || statusCode == HttpStatusCode.TooManyRequests;
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

    private sealed class ProgressReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly Action<long> _onBytesRead;
        private long _bytesRead;

        public ProgressReadStream(Stream inner, Action<long> onBytesRead)
        {
            _inner = inner;
            _onBytesRead = onBytesRead;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            OnRead(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = _inner.Read(buffer);
            OnRead(read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            OnRead(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await _inner.ReadAsync(buffer, cancellationToken);
            OnRead(read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => _inner.DisposeAsync();

        private void OnRead(int read)
        {
            if (read <= 0)
            {
                return;
            }

            _bytesRead += read;
            _onBytesRead(_bytesRead);
        }
    }

    private void Log(string message)
    {
        _processLogService.Log("AssetProvisioning", message);
    }
}
