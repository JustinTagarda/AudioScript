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
    public void EnsureInstalled_Succeeds_WhenBundledAssetsExist()
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
    public async Task DiarizeAudioFileAsync_UsesBundledPythonAndMapsSpeakers()
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
        Directory.CreateDirectory(modelPath);
        File.WriteAllText(Path.Combine(modelPath, "config.yaml"), "pipeline: community-1");
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
        Directory.CreateDirectory(runtimePath);
        File.WriteAllText(Path.Combine(runtimePath, "python.exe"), string.Empty);
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
