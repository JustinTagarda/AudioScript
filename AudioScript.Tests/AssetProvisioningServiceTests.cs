using System.Text.Json;
using System.Net;
using System.Net.Http;
using System.IO.Compression;
using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class AssetProvisioningServiceTests
{
    [Fact]
    public async Task InstallAssetAsync_InstallsFileAssetIntoModelsPath()
    {
        string rootPath = CreateTempDirectory();

        try
        {
            string repoRoot = Path.Combine(rootPath, "repo");
            string localAppData = Path.Combine(rootPath, "local");
            Directory.CreateDirectory(Path.Combine(repoRoot, "assets", "models"));
            File.WriteAllBytes(Path.Combine(repoRoot, "assets", "models", "ggml-small.bin"), [1, 2, 3, 4]);
            string manifestPath = WriteManifest(rootPath, new
            {
                schemaVersion = 1,
                assets = new[]
                {
                    new
                    {
                        id = "whisper-small",
                        displayName = "Whisper small",
                        version = "2.0.0.0",
                        downloadUri = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                        downloadSources = new[]
                        {
                            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                            "https://huggingface.co/mobilint/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                            "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-small.bin"
                        },
                        installKind = "File",
                        installRoot = "Models",
                        installRelativePath = "ggml-small.bin",
                        developmentSourceRelativePath = "assets/models/ggml-small.bin",
                        required = true
                    }
                }
            });

            using var logs = new ProcessLogService(Path.Combine(rootPath, "logs"));
            AppDataPathProvider paths = new(localAppDataPath: localAppData);
            using var service = new AssetProvisioningService(logs, paths, manifestPath, repoRootPath: repoRoot);

            await service.InstallAssetAsync("whisper-small", progress: null, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(paths.ModelsPath, "ggml-small.bin")));
            Assert.True(service.IsInstalled("whisper-small"));
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task InstallAssetAsync_InstallsDirectoryAssetIntoPythonPath()
    {
        string rootPath = CreateTempDirectory();

        try
        {
            string repoRoot = Path.Combine(rootPath, "repo");
            string localAppData = Path.Combine(rootPath, "local");
            string runtimeSource = Path.Combine(repoRoot, "assets", "python", "win-x64");
            Directory.CreateDirectory(runtimeSource);
            File.WriteAllText(Path.Combine(runtimeSource, "python.exe"), "stub");
            string manifestPath = WriteManifest(rootPath, new
            {
                schemaVersion = 1,
                assets = new[]
                {
                    new
                    {
                        id = "pyannote-python-x64",
                        displayName = "Pyannote Python runtime (x64)",
                        version = "2.0.0.0",
                        downloadUri = "https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip",
                        downloadSources = new[]
                        {
                            "https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip",
                            "https://www.python.org/ftp/python/3.12.9/python-3.12.9-embed-amd64.zip",
                            "https://www.python.org/ftp/python/3.12.8/python-3.12.8-embed-amd64.zip"
                        },
                        installKind = "Directory",
                        installRoot = "Python",
                        installRelativePath = "win-x64",
                        developmentSourceRelativePath = "assets/python/win-x64",
                        required = true,
                        supportedArchitectures = new[] { "x64" }
                    }
                }
            });

            using var logs = new ProcessLogService(Path.Combine(rootPath, "logs"));
            AppDataPathProvider paths = new(localAppDataPath: localAppData);
            using var service = new AssetProvisioningService(logs, paths, manifestPath, repoRootPath: repoRoot);

            await service.InstallAssetAsync("pyannote-python-x64", progress: null, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(paths.PythonRuntimesPath, "win-x64", "python.exe")));
            Assert.True(service.IsInstalled("pyannote-python-x64"));
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void GetStatus_ReturnsReadyForPackagedDirectoryAssetInPackagedContext()
    {
        string rootPath = CreateTempDirectory();
        string packagedRelativePath = Path.Combine("assets", "prebuilt", "python", $"unit-test-packaged-runtime-{Guid.NewGuid():N}");
        string packagedSourcePath = Path.Combine(AppContext.BaseDirectory, packagedRelativePath);
        try
        {
            string localAppData = Path.Combine(rootPath, "local");
            AppDataPathProvider paths = new(localAppDataPath: localAppData, packageFamilyName: "AudioScript.Test.Package");
            Directory.CreateDirectory(Path.Combine(packagedSourcePath, "Lib", "site-packages", "torch"));
            Directory.CreateDirectory(Path.Combine(packagedSourcePath, "Lib", "site-packages", "torchaudio"));
            Directory.CreateDirectory(Path.Combine(packagedSourcePath, "Lib", "site-packages", "pyannote", "audio"));
            File.WriteAllText(Path.Combine(packagedSourcePath, "python.exe"), "stub");

            string manifestPath = WriteManifest(rootPath, new
            {
                schemaVersion = 1,
                assets = new[]
                {
                    new
                    {
                        id = "pyannote-python-x64",
                        displayName = "Pyannote Python runtime (x64)",
                        version = "2.0.0.0",
                        packagedSourceRelativePath = packagedRelativePath,
                        deliveryMode = "PackagedRequired",
                        installKind = "Directory",
                        installRoot = "Python",
                        installRelativePath = "win-x64",
                        required = false,
                        supportedArchitectures = new[] { "x64" }
                    }
                }
            });

            using var logs = new ProcessLogService(Path.Combine(rootPath, "logs"));
            using var service = new AssetProvisioningService(logs, paths, manifestPath, repoRootPath: rootPath);

            AssetProvisioningStatus status = service.GetStatus("pyannote-python-x64");
            Assert.Equal(AssetProvisioningState.Ready, status.State);
            Assert.True(service.IsInstalled("pyannote-python-x64"));
        }
        finally
        {
            DeleteDirectory(rootPath);
            DeleteDirectory(packagedSourcePath);
        }
    }

    [Fact]
    public async Task PackagedContext_ProvisionedOptionalDirectoryAsset_InstallsAndRepairsUnderLocalState()
    {
        string rootPath = CreateTempDirectory();

        try
        {
            string localAppData = Path.Combine(rootPath, "local");
            AppDataPathProvider paths = new(
                localAppDataPath: localAppData,
                packageFamilyName: "AudioScript.Test.Package");
            byte[] runtimeArchive = CreateDirectoryArchive(("python.exe", "stub runtime"));
            string sourceCachePath = Path.Combine(paths.ProvisioningPath, "source-cache");
            Directory.CreateDirectory(sourceCachePath);
            File.WriteAllBytes(Path.Combine(sourceCachePath, "pyannote-python-x64.zip"), runtimeArchive);
            string manifestPath = WriteManifest(rootPath, new
            {
                schemaVersion = 1,
                assets = new[]
                {
                    new
                    {
                        id = "pyannote-python-x64",
                        displayName = "Pyannote Python runtime (x64)",
                        version = "2.0.0.0",
                        downloadUri = "https://github.com/JustinTagarda/AudioScript/releases/latest/download/AudioScript.PyannotePythonRuntime.win-x64.zip",
                        downloadSources = new[]
                        {
                            "https://github.com/JustinTagarda/AudioScript/releases/latest/download/AudioScript.PyannotePythonRuntime.win-x64.zip"
                        },
                        deliveryMode = "ProvisionedOptional",
                        installKind = "Directory",
                        installRoot = "Python",
                        installRelativePath = "win-x64",
                        required = false,
                        releaseRequired = true,
                        minimumDownloadSources = 1,
                        supportedArchitectures = new[] { "x64" }
                    }
                }
            });

            using var logs = new ProcessLogService(Path.Combine(rootPath, "logs"));
            using var httpClient = CreateHttpClient((_, _) => throw new InvalidOperationException("Remote source should not be used when the source cache is available."));
            using var service = new AssetProvisioningService(logs, paths, manifestPath, httpClient, repoRootPath: rootPath);

            string installPath = service.ResolveInstallPath("pyannote-python-x64");
            Assert.StartsWith(paths.RootPath, installPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(AppContext.BaseDirectory, installPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(AssetProvisioningState.Missing, service.GetStatus("pyannote-python-x64").State);

            await service.InstallAssetAsync("pyannote-python-x64", progress: null, CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(installPath, "python.exe")));

            await service.RemoveAssetAsync("pyannote-python-x64", CancellationToken.None);
            Assert.False(Directory.Exists(installPath));

            await service.InstallAssetAsync("pyannote-python-x64", progress: null, CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(installPath, "python.exe")));
            Assert.Equal(AssetProvisioningState.Ready, service.GetStatus("pyannote-python-x64").State);
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task InstallAssetAsync_RejectsChecksumMismatchAndLeavesAssetUninstalled()
    {
        string rootPath = CreateTempDirectory();

        try
        {
            string repoRoot = Path.Combine(rootPath, "repo");
            string localAppData = Path.Combine(rootPath, "local");
            Directory.CreateDirectory(Path.Combine(repoRoot, "assets", "models"));
            File.WriteAllBytes(Path.Combine(repoRoot, "assets", "models", "ggml-small.bin"), [1, 2, 3, 4]);
            string manifestPath = WriteManifest(rootPath, new
            {
                schemaVersion = 1,
                assets = new[]
                {
                    new
                    {
                        id = "whisper-small",
                        displayName = "Whisper small",
                        version = "2.0.0.0",
                        downloadUri = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                        downloadSources = new[]
                        {
                            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                            "https://huggingface.co/mobilint/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                            "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-small.bin"
                        },
                        installKind = "File",
                        installRoot = "Models",
                        installRelativePath = "ggml-small.bin",
                        developmentSourceRelativePath = "assets/models/ggml-small.bin",
                        sha256 = "DEADBEEF",
                        required = true
                    }
                }
            });

            using var logs = new ProcessLogService(Path.Combine(rootPath, "logs"));
            AppDataPathProvider paths = new(localAppDataPath: localAppData);
            using var service = new AssetProvisioningService(logs, paths, manifestPath, repoRootPath: repoRoot);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.InstallAssetAsync("whisper-small", progress: null, CancellationToken.None));

            Assert.False(File.Exists(Path.Combine(paths.ModelsPath, "ggml-small.bin")));
            Assert.False(service.IsInstalled("whisper-small"));
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task InstallAssetAsync_DoesNotFailOnExpectedBytesMismatch_WhenSha256IsNotConfigured()
    {
        string rootPath = CreateTempDirectory();

        try
        {
            string repoRoot = Path.Combine(rootPath, "repo");
            string localAppData = Path.Combine(rootPath, "local");
            byte[] payload = [1, 2, 3, 4];
            string manifestPath = WriteManifest(rootPath, new
            {
                schemaVersion = 1,
                assets = new[]
                {
                    new
                    {
                        id = "whisper-small",
                        displayName = "Whisper small",
                        version = "2.0.0.0",
                        downloadUri = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                        downloadSources = new[]
                        {
                            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                            "https://huggingface.co/mobilint/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                            "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-small.bin"
                        },
                        installKind = "File",
                        installRoot = "Models",
                        installRelativePath = "ggml-small.bin",
                        expectedBytes = 999999999,
                        required = true
                    }
                }
            });

            using var logs = new ProcessLogService(Path.Combine(rootPath, "logs"));
            AppDataPathProvider paths = new(localAppDataPath: localAppData);
            using var httpClient = CreateHttpClient((_, _) => CreateBinaryResponse(payload));
            using var service = new AssetProvisioningService(logs, paths, manifestPath, httpClient, repoRootPath: repoRoot);

            await service.InstallAssetAsync("whisper-small", progress: null, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(paths.ModelsPath, "ggml-small.bin")));
            Assert.True(service.IsInstalled("whisper-small"));
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task InstallAssetAsync_FallsBackToNextDownloadSource_WhenFirstSourceFails()
    {
        string rootPath = CreateTempDirectory();

        try
        {
            string repoRoot = Path.Combine(rootPath, "repo");
            string localAppData = Path.Combine(rootPath, "local");
            byte[] payload = [1, 2, 3, 4];
            string manifestPath = WriteManifest(rootPath, new
            {
                schemaVersion = 1,
                assets = new[]
                {
                    new
                    {
                        id = "whisper-small",
                        displayName = "Whisper small",
                        version = "2.0.0.0",
                        downloadUri = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                        installKind = "File",
                        installRoot = "Models",
                        installRelativePath = "ggml-small.bin",
                        downloadSources = new[]
                        {
                            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                            "https://huggingface.co/mobilint/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                            "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-small.bin"
                        },
                        required = true
                    }
                }
            });

            int firstSourceCalls = 0;
            int secondSourceCalls = 0;
            using var logs = new ProcessLogService(Path.Combine(rootPath, "logs"));
            AppDataPathProvider paths = new(localAppDataPath: localAppData);
            using var httpClient = CreateHttpClient((request, _) =>
            {
                if (request.RequestUri is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }

                if (request.RequestUri.Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase)
                    && request.RequestUri.AbsolutePath.Contains("/ggerganov/", StringComparison.OrdinalIgnoreCase))
                {
                    firstSourceCalls++;
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                if (request.RequestUri.Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase)
                    && request.RequestUri.AbsolutePath.Contains("/mobilint/", StringComparison.OrdinalIgnoreCase))
                {
                    secondSourceCalls++;
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                return CreateBinaryResponse(payload);
            });
            using var service = new AssetProvisioningService(logs, paths, manifestPath, httpClient, repoRootPath: repoRoot);

            await service.InstallAssetAsync("whisper-small", progress: null, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(paths.ModelsPath, "ggml-small.bin")));
            Assert.True(service.IsInstalled("whisper-small"));
            Assert.Equal(1, firstSourceCalls);
            Assert.Equal(1, secondSourceCalls);
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task InstallAssetAsync_ContinuesToNextSource_WhenFirstSourceReturns404()
    {
        string rootPath = CreateTempDirectory();

        try
        {
            string repoRoot = Path.Combine(rootPath, "repo");
            string localAppData = Path.Combine(rootPath, "local");
            byte[] payload = [4, 3, 2, 1];
            string manifestPath = WriteManifest(rootPath, new
            {
                schemaVersion = 1,
                assets = new[]
                {
                    new
                    {
                        id = "whisper-small",
                        displayName = "Whisper small",
                        version = "2.0.0.0",
                        downloadUri = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                        installKind = "File",
                        installRoot = "Models",
                        installRelativePath = "ggml-small.bin",
                        downloadSources = new[]
                        {
                            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                            "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-small.bin",
                            "https://huggingface.co/mobilint/whisper.cpp/resolve/main/ggml-small.bin?download=true"
                        },
                        required = true
                    }
                }
            });

            int firstSourceCalls = 0;
            using var logs = new ProcessLogService(Path.Combine(rootPath, "logs"));
            AppDataPathProvider paths = new(localAppDataPath: localAppData);
            using var httpClient = CreateHttpClient((request, _) =>
            {
                if (request.RequestUri is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }

                if (request.RequestUri.Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase)
                    && request.RequestUri.AbsolutePath.Contains("/ggerganov/", StringComparison.OrdinalIgnoreCase))
                {
                    firstSourceCalls++;
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                return CreateBinaryResponse(payload);
            });
            using var service = new AssetProvisioningService(logs, paths, manifestPath, httpClient, repoRootPath: repoRoot);

            await service.InstallAssetAsync("whisper-small", progress: null, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(paths.ModelsPath, "ggml-small.bin")));
            Assert.Equal(1, firstSourceCalls);
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void Ctor_Throws_WhenRequiredAssetHasNonHttpsSource()
    {
        string rootPath = CreateTempDirectory();

        try
        {
            string manifestPath = WriteManifest(rootPath, new
            {
                schemaVersion = 1,
                assets = new[]
                {
                    new
                    {
                        id = "whisper-small",
                        displayName = "Whisper small",
                        version = "2.0.0.0",
                        downloadUri = "http://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                        downloadSources = new[]
                        {
                            "http://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                            "https://huggingface.co/mobilint/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                            "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-small.bin"
                        },
                        installKind = "File",
                        installRoot = "Models",
                        installRelativePath = "ggml-small.bin",
                        required = true
                    }
                }
            });

            using var logs = new ProcessLogService(Path.Combine(rootPath, "logs"));
            AppDataPathProvider paths = new(localAppDataPath: Path.Combine(rootPath, "local"));

            Assert.Throws<InvalidOperationException>(() =>
                new AssetProvisioningService(logs, paths, manifestPath, repoRootPath: rootPath));
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void Ctor_Throws_WhenRequiredAssetHasFewerThanThreeSources()
    {
        string rootPath = CreateTempDirectory();

        try
        {
            string manifestPath = WriteManifest(rootPath, new
            {
                schemaVersion = 1,
                assets = new[]
                {
                    new
                    {
                        id = "whisper-small",
                        displayName = "Whisper small",
                        version = "2.0.0.0",
                        downloadUri = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                        downloadSources = new[]
                        {
                            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                            "https://huggingface.co/mobilint/whisper.cpp/resolve/main/ggml-small.bin?download=true"
                        },
                        installKind = "File",
                        installRoot = "Models",
                        installRelativePath = "ggml-small.bin",
                        required = true
                    }
                }
            });

            using var logs = new ProcessLogService(Path.Combine(rootPath, "logs"));
            AppDataPathProvider paths = new(localAppDataPath: Path.Combine(rootPath, "local"));

            Assert.Throws<InvalidOperationException>(() =>
                new AssetProvisioningService(logs, paths, manifestPath, repoRootPath: rootPath));
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void Ctor_Throws_WhenDownloadUriDoesNotMatchFirstSource()
    {
        string rootPath = CreateTempDirectory();

        try
        {
            string manifestPath = WriteManifest(rootPath, new
            {
                schemaVersion = 1,
                assets = new[]
                {
                    new
                    {
                        id = "whisper-small",
                        displayName = "Whisper small",
                        version = "2.0.0.0",
                        downloadUri = "https://huggingface.co/mobilint/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                        downloadSources = new[]
                        {
                            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                            "https://huggingface.co/mobilint/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                            "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-small.bin"
                        },
                        installKind = "File",
                        installRoot = "Models",
                        installRelativePath = "ggml-small.bin",
                        required = true
                    }
                }
            });

            using var logs = new ProcessLogService(Path.Combine(rootPath, "logs"));
            AppDataPathProvider paths = new(localAppDataPath: Path.Combine(rootPath, "local"));

            Assert.Throws<InvalidOperationException>(() =>
                new AssetProvisioningService(logs, paths, manifestPath, repoRootPath: rootPath));
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void Ctor_AllowsOptionalReleaseManagedAsset_WhenMinimumDownloadSourcesIsSatisfied()
    {
        string rootPath = CreateTempDirectory();

        try
        {
            string manifestPath = WriteManifest(rootPath, new
            {
                schemaVersion = 1,
                assets = new[]
                {
                    new
                    {
                        id = "pyannote-python-x64",
                        displayName = "Pyannote Python runtime (x64)",
                        version = "2.0.0.0",
                        downloadUri = "https://github.com/JustinTagarda/AudioScript/releases/latest/download/AudioScript.PyannotePythonRuntime.win-x64.zip",
                        downloadSources = new[]
                        {
                            "https://github.com/JustinTagarda/AudioScript/releases/latest/download/AudioScript.PyannotePythonRuntime.win-x64.zip"
                        },
                        installKind = "Directory",
                        installRoot = "Python",
                        installRelativePath = "win-x64",
                        releaseRequired = true,
                        minimumDownloadSources = 1,
                        required = false
                    }
                }
            });

            using var logs = new ProcessLogService(Path.Combine(rootPath, "logs"));
            AppDataPathProvider paths = new(localAppDataPath: Path.Combine(rootPath, "local"));

            using var service = new AssetProvisioningService(logs, paths, manifestPath, repoRootPath: rootPath);
            Assert.NotNull(service);
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void Ctor_Throws_WhenOptionalReleaseManagedAssetHasTooFewSources()
    {
        string rootPath = CreateTempDirectory();

        try
        {
            string manifestPath = WriteManifest(rootPath, new
            {
                schemaVersion = 1,
                assets = new[]
                {
                    new
                    {
                        id = "pyannote-python-x64",
                        displayName = "Pyannote Python runtime (x64)",
                        version = "2.0.0.0",
                        downloadUri = "https://github.com/JustinTagarda/AudioScript/releases/latest/download/AudioScript.PyannotePythonRuntime.win-x64.zip",
                        downloadSources = Array.Empty<string>(),
                        installKind = "Directory",
                        installRoot = "Python",
                        installRelativePath = "win-x64",
                        releaseRequired = true,
                        minimumDownloadSources = 1,
                        required = false
                    }
                }
            });

            using var logs = new ProcessLogService(Path.Combine(rootPath, "logs"));
            AppDataPathProvider paths = new(localAppDataPath: Path.Combine(rootPath, "local"));

            Assert.Throws<InvalidOperationException>(() =>
                new AssetProvisioningService(logs, paths, manifestPath, repoRootPath: rootPath));
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void IsInstalled_AdoptsExistingAssetWithoutStateEntry()
    {
        string rootPath = CreateTempDirectory();

        try
        {
            string repoRoot = Path.Combine(rootPath, "repo");
            string localAppData = Path.Combine(rootPath, "local");
            Directory.CreateDirectory(Path.Combine(repoRoot, "assets", "models"));
            string manifestPath = WriteManifest(rootPath, new
            {
                schemaVersion = 1,
                assets = new[]
                {
                    new
                    {
                        id = "whisper-small",
                        displayName = "Whisper small",
                        version = "2.0.0.0",
                        downloadUri = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                        downloadSources = new[]
                        {
                            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                            "https://huggingface.co/mobilint/whisper.cpp/resolve/main/ggml-small.bin?download=true",
                            "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-small.bin"
                        },
                        installKind = "File",
                        installRoot = "Models",
                        installRelativePath = "ggml-small.bin",
                        developmentSourceRelativePath = "assets/models/ggml-small.bin",
                        required = true
                    }
                }
            });

            using var logs = new ProcessLogService(Path.Combine(rootPath, "logs"));
            AppDataPathProvider paths = new(localAppDataPath: localAppData);
            Directory.CreateDirectory(paths.ModelsPath);
            File.WriteAllBytes(Path.Combine(paths.ModelsPath, "ggml-small.bin"), [1, 2, 3, 4]);

            using var service = new AssetProvisioningService(logs, paths, manifestPath, repoRootPath: repoRoot);

            Assert.True(service.IsInstalled("whisper-small"));
            Assert.Contains("\"AssetId\": \"whisper-small\"", File.ReadAllText(Path.Combine(paths.ProvisioningPath, "asset-state.json")));
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    private static string WriteManifest(string rootPath, object manifest)
    {
        string manifestPath = Path.Combine(rootPath, "asset-manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        return manifestPath;
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"AudioScript-asset-provisioning-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
    {
        return new HttpClient(new StubHttpMessageHandler(responder));
    }

    private static HttpResponseMessage CreateBinaryResponse(byte[] payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        };
    }

    private static byte[] CreateDirectoryArchive(params (string RelativePath, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string relativePath, string content) in files)
            {
                ZipArchiveEntry entry = archive.CreateEntry(relativePath);
                using StreamWriter writer = new(entry.Open());
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request, cancellationToken));
        }
    }
}
