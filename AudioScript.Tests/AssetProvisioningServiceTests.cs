using System.Text.Json;
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
}
