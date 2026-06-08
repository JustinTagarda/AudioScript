using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace AudioScript.Services;

public sealed class PyannoteCommunityModelManager
{
    public const string PyannoteModelAssetId = "pyannote-community-model";
    public const string PyannotePythonX64AssetId = "pyannote-python-x64";
    private static readonly string[] RequiredModelDirectoryRelativePaths =
    [
        "segmentation",
        "embedding",
        "plda",
    ];

    private static readonly string[] RequiredModelFileRelativePaths =
    [
        "config.yaml",
        "segmentation\\pytorch_model.bin",
        "embedding\\pytorch_model.bin",
        "plda\\plda.npz",
        "plda\\xvec_transform.npz",
    ];

    private static readonly string[] RequiredRuntimeDirectoryRelativePaths =
    [
        "Lib\\site-packages\\torch",
        "Lib\\site-packages\\torch\\lib",
        "Lib\\site-packages\\torch\\bin",
        "Lib\\site-packages\\torchaudio",
        "Lib\\site-packages\\torchaudio\\lib",
        "Lib\\site-packages\\pyannote\\audio",
        "Lib\\site-packages\\numpy.libs",
        "Lib\\site-packages\\scipy.libs",
        "Lib\\site-packages\\sklearn\\.libs",
        "Lib\\site-packages\\pandas.libs",
    ];

    private static readonly string[] RequiredRuntimeFileRelativePaths =
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

    private readonly AppDataPathProvider _paths;
    private readonly IAssetProvisioningService _assetProvisioningService;
    private readonly Func<Architecture> _architectureResolver;

    public PyannoteCommunityModelManager(
        IAssetProvisioningService assetProvisioningService,
        AppDataPathProvider? paths = null,
        Func<Architecture>? architectureResolver = null)
    {
        _assetProvisioningService = assetProvisioningService;
        _paths = paths ?? AppDataPathProvider.Create();
        _architectureResolver = architectureResolver ?? (() => RuntimeInformation.ProcessArchitecture);
    }

    public string ModelDirectoryPath => _assetProvisioningService.ResolveInstallPath(PyannoteModelAssetId);

    public string RunnerScriptPath => _paths.IsPackaged
        ? Path.Combine(_paths.TempPath, "pyannote-community-runner", "run_community_diarization.py")
        : Path.Combine(ModelDirectoryPath, "run_community_diarization.py");

    public string RuntimeDirectoryPath => _assetProvisioningService.ResolveInstallPath(PyannotePythonX64AssetId);

    public string PythonExecutablePath => Path.Combine(RuntimeDirectoryPath, "python.exe");

    public bool IsSupportedOnCurrentArchitecture => _architectureResolver() == Architecture.X64;

    public IReadOnlyList<string> GetRuntimeNativeSearchDirectories()
    {
        string runtimeRoot = RuntimeDirectoryPath;
        var directories = new List<string>
        {
            runtimeRoot,
            Path.Combine(runtimeRoot, "Lib", "site-packages", "torch", "lib"),
            Path.Combine(runtimeRoot, "Lib", "site-packages", "torch", "bin"),
            Path.Combine(runtimeRoot, "Lib", "site-packages", "torchaudio", "lib"),
            Path.Combine(runtimeRoot, "Lib", "site-packages", "numpy.libs"),
            Path.Combine(runtimeRoot, "Lib", "site-packages", "scipy.libs"),
            Path.Combine(runtimeRoot, "Lib", "site-packages", "sklearn", ".libs"),
            Path.Combine(runtimeRoot, "Lib", "site-packages", "pandas.libs"),
            Path.Combine(runtimeRoot, "Library", "bin"),
        };

        return directories
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string DescribeExecutionContext()
    {
        IReadOnlyList<string> nativeSearchDirectories = GetRuntimeNativeSearchDirectories();
        return
            $"architecture={_architectureResolver()}, supported={IsSupportedOnCurrentArchitecture}, " +
            $"runtimeDir='{RuntimeDirectoryPath}', pythonExe='{PythonExecutablePath}', runnerScript='{RunnerScriptPath}', " +
            $"modelDir='{ModelDirectoryPath}', nativeSearchDirs='{string.Join(" | ", nativeSearchDirectories)}'";
    }

    public void EnsureInstalled()
    {
        ValidateInstalled();
    }

    public void ValidateInstalled()
    {
        EnsureSupportedArchitecture();

        if (!Directory.Exists(ModelDirectoryPath))
        {
            throw new DirectoryNotFoundException(
                $"Pyannote Community-1 model is not installed. Run Detect Speaker to download it. Path: {ModelDirectoryPath}");
        }

        EnsureCriticalDirectoriesExist(
            rootPath: ModelDirectoryPath,
            relativePaths: RequiredModelDirectoryRelativePaths,
            missingPrefix: "Pyannote Community-1 model payload is incomplete or corrupted.");

        EnsureCriticalFilesExist(
            rootPath: ModelDirectoryPath,
            relativePaths: RequiredModelFileRelativePaths,
            missingPrefix: "Pyannote Community-1 model payload is incomplete or corrupted.");

        if (!Directory.Exists(RuntimeDirectoryPath))
        {
            throw new DirectoryNotFoundException(
                $"Pyannote Python runtime is not installed. Run Detect Speaker to download it. Path: {RuntimeDirectoryPath}");
        }

        EnsureCriticalDirectoriesExist(
            rootPath: RuntimeDirectoryPath,
            relativePaths: RequiredRuntimeDirectoryRelativePaths,
            missingPrefix: "Pyannote Python runtime payload is incomplete or corrupted.");

        EnsureCriticalFilesExist(
            rootPath: RuntimeDirectoryPath,
            relativePaths: RequiredRuntimeFileRelativePaths,
            missingPrefix: "Pyannote Python runtime payload is incomplete or corrupted.");
    }

    public void EnsureExecutionReady()
    {
        PrepareInstalledRuntime();
        EnsureRunnerScriptExists();
    }

    public void PrepareInstalledRuntime()
    {
        ValidateInstalled();
        EnsureNativeRuntimeCompatibilityFiles();
    }

    public bool IsInstalled()
    {
        if (!IsSupportedOnCurrentArchitecture)
        {
            return false;
        }

        return _assetProvisioningService.IsInstalled(PyannoteModelAssetId)
            && _assetProvisioningService.IsInstalled(PyannotePythonX64AssetId);
    }

    public async Task EnsureProvisionedAsync(
        IProgress<AssetProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        EnsureExecutionReady();
    }

    private void EnsureRunnerScriptExists()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RunnerScriptPath)!);
        string existing = File.Exists(RunnerScriptPath)
            ? File.ReadAllText(RunnerScriptPath)
            : string.Empty;
        if (!string.Equals(existing, RunnerScriptContent, StringComparison.Ordinal))
        {
            File.WriteAllText(RunnerScriptPath, RunnerScriptContent);
        }
    }

    private void EnsureNativeRuntimeCompatibilityFiles()
    {
        CopyRuntimeFileIfNeeded(
            "Lib\\site-packages\\sklearn\\.libs\\msvcp140.dll",
            "msvcp140.dll");
        CopyRuntimeFileIfNeeded(
            "Lib\\site-packages\\sklearn\\.libs\\vcomp140.dll",
            "vcomp140.dll");
    }

    private void CopyRuntimeFileIfNeeded(string sourceRelativePath, string destinationRelativePath)
    {
        string sourcePath = Path.Combine(RuntimeDirectoryPath, sourceRelativePath);
        string destinationPath = Path.Combine(RuntimeDirectoryPath, destinationRelativePath);
        if (File.Exists(destinationPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath, overwrite: false);
    }

    private static void EnsureCriticalDirectoriesExist(
        string rootPath,
        IReadOnlyList<string> relativePaths,
        string missingPrefix)
    {
        foreach (string relativePath in relativePaths)
        {
            string fullPath = Path.Combine(rootPath, relativePath);
            if (Directory.Exists(fullPath))
            {
                continue;
            }

            throw new DirectoryNotFoundException(
                $"{missingPrefix} Missing directory '{relativePath}'. Run Detect Speaker again to restore the runtime. Path: {fullPath}");
        }
    }

    private static void EnsureCriticalFilesExist(
        string rootPath,
        IReadOnlyList<string> relativePaths,
        string missingPrefix)
    {
        foreach (string relativePath in relativePaths)
        {
            string fullPath = Path.Combine(rootPath, relativePath);
            if (File.Exists(fullPath))
            {
                continue;
            }

            throw new FileNotFoundException(
                $"{missingPrefix} Missing file '{relativePath}'. Run Detect Speaker again to restore the runtime.",
                fullPath);
        }
    }

    private const string RunnerScriptContent = """
import json
import os
import sys
import wave

import numpy as np

print("runner_started", file=sys.stderr, flush=True)

if len(sys.argv) != 3:
    print("[]")
    sys.exit(2)

model_dir = sys.argv[1]
audio_path = sys.argv[2]

def _configure_native_dll_search_paths():
    runtime_root = os.path.dirname(sys.executable)
    directories = [
        runtime_root,
        os.path.join(runtime_root, "Lib", "site-packages", "torch", "lib"),
        os.path.join(runtime_root, "Lib", "site-packages", "torch", "bin"),
        os.path.join(runtime_root, "Lib", "site-packages", "torchaudio", "lib"),
        os.path.join(runtime_root, "Lib", "site-packages", "numpy.libs"),
        os.path.join(runtime_root, "Lib", "site-packages", "scipy.libs"),
        os.path.join(runtime_root, "Lib", "site-packages", "sklearn", ".libs"),
        os.path.join(runtime_root, "Lib", "site-packages", "pandas.libs"),
        os.path.join(runtime_root, "Library", "bin"),
    ]
    configured = []
    handles = []
    seen = set()
    for directory in directories:
        normalized = os.path.normpath(directory)
        if not os.path.isdir(normalized) or normalized in seen:
            continue
        seen.add(normalized)
        configured.append(normalized)
        os.environ["PATH"] = normalized + os.pathsep + os.environ.get("PATH", "")
        if hasattr(os, "add_dll_directory"):
            handles.append(os.add_dll_directory(normalized))
    return configured, handles

configured_dll_dirs, dll_directory_handles = _configure_native_dll_search_paths()
print("runtime_bootstrap", file=sys.stderr, flush=True)
print("dll_search_paths=" + "|".join(configured_dll_dirs), file=sys.stderr, flush=True)

from pyannote.audio import Pipeline
import torch

print("model_loading", file=sys.stderr, flush=True)
pipeline = Pipeline.from_pretrained(model_dir)
if torch.cuda.is_available():
    pipeline = pipeline.to(torch.device("cuda"))
print("model_loaded", file=sys.stderr, flush=True)

print("waveform_loading", file=sys.stderr, flush=True)
# Prefer a direct WAV loader to avoid runtime torchcodec/ffmpeg dependency
# issues inside torchaudio on Windows embedded runtimes.
with wave.open(audio_path, "rb") as wav_file:
    channels = wav_file.getnchannels()
    sample_rate = wav_file.getframerate()
    sample_width = wav_file.getsampwidth()
    frame_count = wav_file.getnframes()
    raw = wav_file.readframes(frame_count)

if sample_width == 1:
    data = np.frombuffer(raw, dtype=np.uint8).astype(np.float32)
    data = (data - 128.0) / 128.0
elif sample_width == 2:
    data = np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0
elif sample_width == 4:
    data = np.frombuffer(raw, dtype=np.int32).astype(np.float32) / 2147483648.0
else:
    raise RuntimeError(f"Unsupported WAV sample width: {sample_width} byte(s).")

if channels <= 0:
    raise RuntimeError("Invalid WAV channel count.")

data = data.reshape(-1, channels).T
waveform = torch.from_numpy(data)
print("waveform_loaded", file=sys.stderr, flush=True)

print("inference_started", file=sys.stderr, flush=True)
diarization = pipeline({"waveform": waveform, "sample_rate": sample_rate})
print("inference_finished", file=sys.stderr, flush=True)

print("serializing_turns", file=sys.stderr, flush=True)
turns = []
annotation = diarization
if hasattr(diarization, "speaker_diarization"):
    annotation = diarization.speaker_diarization

for segment, _, speaker in annotation.itertracks(yield_label=True):
    turns.append({
        "speaker": str(speaker),
        "start": float(segment.start),
        "end": float(segment.end),
    })

print(json.dumps(turns))
print("completed", file=sys.stderr, flush=True)
""";

    private string ResolveRuntimeDirectoryName()
    {
        return _architectureResolver() switch
        {
            Architecture.X64 => "win-x64",
            Architecture.X86 => throw new PlatformNotSupportedException("Speaker diarization requires an x64 AudioScript build."),
            Architecture.Arm => throw new PlatformNotSupportedException("Speaker diarization requires an x64 AudioScript build."),
            Architecture.Arm64 => throw new PlatformNotSupportedException("Speaker diarization requires an x64 AudioScript build."),
            Architecture.Wasm => throw new PlatformNotSupportedException("Pyannote Community-1 is not supported on WebAssembly."),
            Architecture.S390x => throw new PlatformNotSupportedException("Pyannote Community-1 is not supported on s390x."),
            Architecture.Ppc64le => throw new PlatformNotSupportedException("Pyannote Community-1 is not supported on ppc64le."),
            _ => throw new PlatformNotSupportedException($"Unsupported process architecture '{_architectureResolver()}'."),
        };
    }

    private void EnsureSupportedArchitecture()
    {
        if (!IsSupportedOnCurrentArchitecture)
        {
            throw new PlatformNotSupportedException("Speaker diarization requires an x64 AudioScript build.");
        }
    }

}
