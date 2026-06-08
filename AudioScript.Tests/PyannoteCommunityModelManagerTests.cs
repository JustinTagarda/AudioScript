using System.Runtime.InteropServices;
using System.Text;
using AudioScript.Abstractions;
using AudioScript.Audio;
using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class PyannoteCommunityModelManagerTests
{
    [Fact]
    public void EnsureInstalled_Succeeds_WhenProvisionedAssetsExist()
    {
        string assetsPath = CreateTempDirectory();
        AppDataPathProvider paths = new(localAppDataPath: assetsPath);

        try
        {
            CreatePyannoteAssets(paths, Architecture.X64);

            var manager = new PyannoteCommunityModelManager(
                new StubAssetProvisioningService(paths),
                paths,
                architectureResolver: () => Architecture.X64);

            manager.EnsureInstalled();
        }
        finally
        {
            DeleteDirectory(assetsPath);
        }
    }

    [Fact]
    public void EnsureInstalled_Throws_WhenModelIsMissing()
    {
        string assetsPath = CreateTempDirectory();
        AppDataPathProvider paths = new(localAppDataPath: assetsPath);

        try
        {
            CreateRunnerScript(paths);
            CreatePythonRuntime(paths, "win-x64");
            var manager = new PyannoteCommunityModelManager(
                new StubAssetProvisioningService(paths),
                paths,
                architectureResolver: () => Architecture.X64);

            Assert.Throws<DirectoryNotFoundException>(manager.EnsureInstalled);
        }
        finally
        {
            DeleteDirectory(assetsPath);
        }
    }

    [Fact]
    public void EnsureInstalled_Throws_WhenModelPayloadIsIncomplete()
    {
        string assetsPath = CreateTempDirectory();
        AppDataPathProvider paths = new(localAppDataPath: assetsPath);

        try
        {
            CreatePyannoteAssets(paths, Architecture.X64);
            File.Delete(Path.Combine(paths.PyannoteAssetsPath, "speaker-diarization-community-1", "config.yaml"));
            var manager = new PyannoteCommunityModelManager(
                new StubAssetProvisioningService(paths),
                paths,
                architectureResolver: () => Architecture.X64);

            FileNotFoundException ex = Assert.Throws<FileNotFoundException>(manager.EnsureInstalled);
            Assert.Contains("config.yaml", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(assetsPath);
        }
    }

    [Fact]
    public void EnsureInstalled_Throws_WhenPythonRuntimePayloadIsIncomplete()
    {
        string assetsPath = CreateTempDirectory();
        AppDataPathProvider paths = new(localAppDataPath: assetsPath);

        try
        {
            CreatePyannoteAssets(paths, Architecture.X64);
            Directory.Delete(Path.Combine(paths.PythonRuntimesPath, "win-x64", "Lib", "site-packages", "torchaudio"), recursive: true);
            var manager = new PyannoteCommunityModelManager(
                new StubAssetProvisioningService(paths),
                paths,
                architectureResolver: () => Architecture.X64);

            DirectoryNotFoundException ex = Assert.Throws<DirectoryNotFoundException>(manager.EnsureInstalled);
            Assert.Contains("torchaudio", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(assetsPath);
        }
    }

    [Fact]
    public void RuntimePaths_SelectX64Runtime()
    {
        string assetsPath = CreateTempDirectory();
        AppDataPathProvider paths = new(localAppDataPath: assetsPath);

        try
        {
            CreatePyannoteAssets(paths, Architecture.X64);

            var manager = new PyannoteCommunityModelManager(
                new StubAssetProvisioningService(paths),
                paths,
                architectureResolver: () => Architecture.X64);

            Assert.Equal(Path.Combine(paths.PythonRuntimesPath, "win-x64"), manager.RuntimeDirectoryPath);
            Assert.Equal(Path.Combine(paths.PythonRuntimesPath, "win-x64", "python.exe"), manager.PythonExecutablePath);
            manager.EnsureInstalled();
        }
        finally
        {
            DeleteDirectory(assetsPath);
        }
    }

    [Fact]
    public void RuntimeDiagnostics_IncludeInstalledNativeSearchDirectories()
    {
        string assetsPath = CreateTempDirectory();
        AppDataPathProvider paths = new(localAppDataPath: assetsPath);

        try
        {
            CreatePyannoteAssets(paths, Architecture.X64);

            var manager = new PyannoteCommunityModelManager(
                new StubAssetProvisioningService(paths),
                paths,
                architectureResolver: () => Architecture.X64);

            IReadOnlyList<string> nativeSearchDirectories = manager.GetRuntimeNativeSearchDirectories();

            Assert.Contains(Path.Combine(paths.PythonRuntimesPath, "win-x64", "Lib", "site-packages", "torch", "lib"), nativeSearchDirectories);
            Assert.Contains(Path.Combine(paths.PythonRuntimesPath, "win-x64", "Lib", "site-packages", "torch", "bin"), nativeSearchDirectories);
            Assert.Contains(Path.Combine(paths.PythonRuntimesPath, "win-x64", "Lib", "site-packages", "torchaudio", "lib"), nativeSearchDirectories);
            string diagnostics = manager.DescribeExecutionContext();
            Assert.Contains($"runtimeDir='{Path.Combine(paths.PythonRuntimesPath, "win-x64")}'", diagnostics, StringComparison.Ordinal);
            Assert.Contains("nativeSearchDirs=", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(assetsPath);
        }
    }

    [Fact]
    public void PrepareInstalledRuntime_CopiesNativeCompatibilityDllsToRuntimeRoot()
    {
        string assetsPath = CreateTempDirectory();
        AppDataPathProvider paths = new(localAppDataPath: assetsPath);

        try
        {
            CreatePyannoteAssets(paths, Architecture.X64);
            var manager = new PyannoteCommunityModelManager(
                new StubAssetProvisioningService(paths),
                paths,
                architectureResolver: () => Architecture.X64);

            manager.PrepareInstalledRuntime();

            Assert.True(File.Exists(Path.Combine(manager.RuntimeDirectoryPath, "msvcp140.dll")));
            Assert.True(File.Exists(Path.Combine(manager.RuntimeDirectoryPath, "vcomp140.dll")));
        }
        finally
        {
            DeleteDirectory(assetsPath);
        }
    }

    [Fact]
    public void EnsureExecutionReady_WhenPackaged_WritesRunnerScriptToLocalStateTemp()
    {
        string assetsPath = CreateTempDirectory();
        AppDataPathProvider paths = new(
            localAppDataPath: assetsPath,
            packageFamilyName: "JustinTagardaSoftware.AudioScript_test");

        try
        {
            CreatePyannoteAssets(paths, Architecture.X64);

            var manager = new PyannoteCommunityModelManager(
                new StubAssetProvisioningService(paths),
                paths,
                architectureResolver: () => Architecture.X64);

            manager.EnsureExecutionReady();

            string expectedPath = Path.Combine(
                paths.TempPath,
                "pyannote-community-runner",
                "run_community_diarization.py");

            Assert.Equal(expectedPath, manager.RunnerScriptPath);
            Assert.True(File.Exists(expectedPath));
            Assert.DoesNotContain(
                Path.Combine("Program Files", "WindowsApps"),
                manager.RunnerScriptPath,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectory(assetsPath);
        }
    }

    [Fact]
    public void EnsureInstalled_ThrowsPlatformNotSupported_OnNonX64()
    {
        string assetsPath = CreateTempDirectory();
        AppDataPathProvider paths = new(localAppDataPath: assetsPath);

        try
        {
            var manager = new PyannoteCommunityModelManager(
                new StubAssetProvisioningService(paths),
                paths,
                architectureResolver: () => Architecture.X86);

            Assert.Throws<PlatformNotSupportedException>(manager.EnsureInstalled);
        }
        finally
        {
            DeleteDirectory(assetsPath);
        }
    }

    [Fact]
    public async Task DiarizeAudioFileAsync_UsesInstalledPythonAndMapsSpeakers()
    {
        string assetsPath = CreateTempDirectory();
        string audioPath = CreateSilentWaveFile(TimeSpan.FromSeconds(2));
        AppDataPathProvider paths = new(localAppDataPath: assetsPath);

        try
        {
            CreatePyannoteAssets(paths, Architecture.X64);
            var manager = new PyannoteCommunityModelManager(
                new StubAssetProvisioningService(paths),
                paths,
                architectureResolver: () => Architecture.X64);
            var runner = new StubPyannoteCommunityProcessRunner(
                """[{"speaker":"SPEAKER_00","start":0.1,"end":1.2},{"speaker":"custom","start":1.3,"end":2.0}]""");
            using var logs = new ProcessLogService();
            var engine = new PyannoteCommunityDiarizationEngine(
                new AudioStandardizer(),
                manager,
                logs,
                runner);

            IReadOnlyList<SpeakerDiarizationTurn> turns = await engine.DiarizeAudioFileAsync(
                audioPath,
                CancellationToken.None);

            Assert.Equal(new[] { "speaker_1", "speaker_2" }, turns.Select(turn => turn.Speaker).ToArray());
            Assert.Equal(manager.PythonExecutablePath, runner.PythonExecutablePath);
            Assert.Equal(manager.RunnerScriptPath, runner.RunnerScriptPath);
            Assert.Equal(manager.ModelDirectoryPath, runner.ModelDirectoryPath);
            Assert.True(runner.WasAudioFileAvailableAtRun);
        }
        finally
        {
            File.Delete(audioPath);
            DeleteDirectory(assetsPath);
        }
    }

    [Fact]
    public async Task DiarizeAudioFileAsync_LogsHeartbeatStages()
    {
        string assetsPath = CreateTempDirectory();
        string audioPath = CreateSilentWaveFile(TimeSpan.FromSeconds(2));
        string logsPath = CreateTempDirectory();
        AppDataPathProvider paths = new(localAppDataPath: assetsPath);

        try
        {
            CreatePyannoteAssets(paths, Architecture.X64);
            var manager = new PyannoteCommunityModelManager(
                new StubAssetProvisioningService(paths),
                paths,
                architectureResolver: () => Architecture.X64);
            var runner = new StubPyannoteCommunityProcessRunner(
                """[{"speaker":"SPEAKER_00","start":0.1,"end":1.2}]""",
                ["runner_started", "model_loaded", "inference_started", "inference_finished", "completed"]);
            using var logs = new ProcessLogService(logsPath);
            var engine = new PyannoteCommunityDiarizationEngine(
                new AudioStandardizer(),
                manager,
                logs,
                runner);

            await engine.DiarizeAudioFileAsync(audioPath, CancellationToken.None);

            string logText = File.ReadAllText(logs.LogFilePath);
            Assert.Contains("Pyannote runner started.", logText, StringComparison.Ordinal);
            Assert.Contains("Pyannote model loaded.", logText, StringComparison.Ordinal);
            Assert.Contains("Pyannote inference started.", logText, StringComparison.Ordinal);
            Assert.Contains("Pyannote inference finished.", logText, StringComparison.Ordinal);
            Assert.Contains("Pyannote runner completed.", logText, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(audioPath);
            DeleteDirectory(assetsPath);
            DeleteDirectory(logsPath);
        }
    }

    private static void CreatePyannoteAssets(AppDataPathProvider paths, Architecture architecture)
    {
        CreateRunnerScript(paths);
        CreateModelDirectory(paths);
        CreatePythonRuntime(paths, "win-x64");
    }

    private static void CreateModelDirectory(AppDataPathProvider paths)
    {
        string modelPath = Path.Combine(paths.PyannoteAssetsPath, "speaker-diarization-community-1");
        Directory.CreateDirectory(Path.Combine(modelPath, "segmentation"));
        Directory.CreateDirectory(Path.Combine(modelPath, "embedding"));
        Directory.CreateDirectory(Path.Combine(modelPath, "plda"));
        File.WriteAllText(Path.Combine(modelPath, "config.yaml"), "pipeline: community-1");
        File.WriteAllText(Path.Combine(modelPath, "segmentation", "pytorch_model.bin"), string.Empty);
        File.WriteAllText(Path.Combine(modelPath, "embedding", "pytorch_model.bin"), string.Empty);
        File.WriteAllText(Path.Combine(modelPath, "plda", "plda.npz"), string.Empty);
        File.WriteAllText(Path.Combine(modelPath, "plda", "xvec_transform.npz"), string.Empty);
    }

    private static void CreateRunnerScript(AppDataPathProvider paths)
    {
        string runnerPath = Path.Combine(paths.PyannoteAssetsPath, "run_community_diarization.py");
        Directory.CreateDirectory(Path.GetDirectoryName(runnerPath)!);
        File.WriteAllText(runnerPath, "print('stub')");
    }

    private static void CreatePythonRuntime(AppDataPathProvider paths, string runtimeDirectoryName)
    {
        string runtimePath = Path.Combine(paths.PythonRuntimesPath, runtimeDirectoryName);
        Directory.CreateDirectory(Path.Combine(runtimePath, "Lib", "site-packages", "torch"));
        Directory.CreateDirectory(Path.Combine(runtimePath, "Lib", "site-packages", "torch", "lib"));
        Directory.CreateDirectory(Path.Combine(runtimePath, "Lib", "site-packages", "torch", "bin"));
        Directory.CreateDirectory(Path.Combine(runtimePath, "Lib", "site-packages", "torchaudio"));
        Directory.CreateDirectory(Path.Combine(runtimePath, "Lib", "site-packages", "torchaudio", "lib"));
        Directory.CreateDirectory(Path.Combine(runtimePath, "Lib", "site-packages", "pyannote", "audio"));
        Directory.CreateDirectory(Path.Combine(runtimePath, "Lib", "site-packages", "numpy.libs"));
        Directory.CreateDirectory(Path.Combine(runtimePath, "Lib", "site-packages", "scipy.libs"));
        Directory.CreateDirectory(Path.Combine(runtimePath, "Lib", "site-packages", "sklearn", ".libs"));
        Directory.CreateDirectory(Path.Combine(runtimePath, "Lib", "site-packages", "pandas.libs"));
        string[] requiredFiles =
        [
            "python.exe",
            "python3.dll",
            "python312.dll",
            "vcruntime140.dll",
            "vcruntime140_1.dll",
            "libcrypto-3.dll",
            "libssl-3.dll",
            "Lib\\site-packages\\torch\\lib\\c10.dll",
            "Lib\\site-packages\\torch\\lib\\libiomp5md.dll",
            "Lib\\site-packages\\torch\\lib\\shm.dll",
            "Lib\\site-packages\\torch\\lib\\torch.dll",
            "Lib\\site-packages\\torch\\lib\\torch_cpu.dll",
            "Lib\\site-packages\\torch\\lib\\torch_global_deps.dll",
            "Lib\\site-packages\\torch\\lib\\torch_python.dll",
            "Lib\\site-packages\\torch\\lib\\uv.dll",
            "Lib\\site-packages\\numpy.libs\\libscipy_openblas64_-63c857e738469261263c764a36be9436.dll",
            "Lib\\site-packages\\numpy.libs\\msvcp140-a4c2229bdc2a2a630acdc095b4d86008.dll",
            "Lib\\site-packages\\scipy.libs\\libscipy_openblas-64eda39e79589aedb16f58e5547eb599.dll",
            "Lib\\site-packages\\sklearn\\.libs\\msvcp140.dll",
            "Lib\\site-packages\\sklearn\\.libs\\vcomp140.dll",
            "Lib\\site-packages\\pandas.libs\\msvcp140-a4c2229bdc2a2a630acdc095b4d86008.dll",
            "Lib\\site-packages\\torchaudio\\lib\\libtorchaudio.pyd",
            "Lib\\site-packages\\torchaudio\\lib\\_torchaudio.pyd",
        ];
        foreach (string relativePath in requiredFiles)
        {
            string filePath = Path.Combine(runtimePath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, string.Empty);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "assets", $"AudioScript-pyannote-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        Directory.Delete(path, recursive: true);
    }

    private static string CreateSilentWaveFile(TimeSpan duration)
    {
        string path = Path.Combine(Path.GetTempPath(), $"AudioScript-pyannote-smoke-{Guid.NewGuid():N}.wav");
        int sampleRate = 16000;
        short channels = 1;
        short bitsPerSample = 16;
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        int byteRate = sampleRate * blockAlign;
        long dataBytes = (long)Math.Ceiling(duration.TotalSeconds * byteRate);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write((int)(36 + dataBytes));
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
        writer.Write((int)dataBytes);
        stream.SetLength(44 + dataBytes);

        return path;
    }

    private sealed class StubPyannoteCommunityProcessRunner : IPyannoteCommunityProcessRunner
    {
        private readonly string _standardOutput;
        private readonly IReadOnlyList<string> _standardErrorLines;

        public StubPyannoteCommunityProcessRunner(string standardOutput, IReadOnlyList<string>? standardErrorLines = null)
        {
            _standardOutput = standardOutput;
            _standardErrorLines = standardErrorLines ?? Array.Empty<string>();
        }

        public string? PythonExecutablePath { get; private set; }

        public string? RunnerScriptPath { get; private set; }

        public string? ModelDirectoryPath { get; private set; }

        public string? AudioFilePath { get; private set; }

        public bool WasAudioFileAvailableAtRun { get; private set; }

        public Task<PyannoteCommunityProcessResult> RunAsync(
            string pythonExecutablePath,
            string runnerScriptPath,
            string modelDirectoryPath,
            string audioFilePath,
            Action<string>? onStandardErrorLine,
            CancellationToken cancellationToken)
        {
            PythonExecutablePath = pythonExecutablePath;
            RunnerScriptPath = runnerScriptPath;
            ModelDirectoryPath = modelDirectoryPath;
            AudioFilePath = audioFilePath;
            WasAudioFileAvailableAtRun = File.Exists(audioFilePath);
            foreach (string line in _standardErrorLines)
            {
                onStandardErrorLine?.Invoke(line);
            }

            return Task.FromResult(new PyannoteCommunityProcessResult(0, _standardOutput, string.Join(Environment.NewLine, _standardErrorLines)));
        }
    }

    private sealed class StubAssetProvisioningService : IAssetProvisioningService
    {
        private readonly AppDataPathProvider _paths;

        public StubAssetProvisioningService(AppDataPathProvider paths)
        {
            _paths = paths;
        }

        public IReadOnlyList<ProvisionedAssetDescriptor> GetManifestAssets()
        {
            return Array.Empty<ProvisionedAssetDescriptor>();
        }

        public AssetProvisioningStatus GetStatus(string assetId)
        {
            return new AssetProvisioningStatus(assetId, assetId, AssetProvisioningState.Ready, string.Empty);
        }

        public string ResolveInstallPath(string assetId)
        {
            return assetId switch
            {
                PyannoteCommunityModelManager.PyannoteModelAssetId => Path.Combine(_paths.PyannoteAssetsPath, "speaker-diarization-community-1"),
                PyannoteCommunityModelManager.PyannotePythonX64AssetId => Path.Combine(_paths.PythonRuntimesPath, "win-x64"),
                _ => string.Empty,
            };
        }

        public bool IsInstalled(string assetId)
        {
            return true;
        }

        public Task InstallAssetAsync(string assetId, IProgress<AssetProvisioningProgress>? progress, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task RemoveAssetAsync(string assetId, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
