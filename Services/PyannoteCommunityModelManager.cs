using System.IO;
using System.Runtime.InteropServices;

namespace AudioScript.Services;

public sealed class PyannoteCommunityModelManager
{
    public const string PyannoteModelAssetId = "pyannote-community-model";
    public const string PyannotePythonX64AssetId = "pyannote-python-x64";

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

    public string ModelDirectoryPath => Path.Combine(_paths.PyannoteAssetsPath, "speaker-diarization-community-1");

    public string RunnerScriptPath => Path.Combine(_paths.PyannoteAssetsPath, "run_community_diarization.py");

    public string RuntimeDirectoryPath => Path.Combine(_paths.PythonRuntimesPath, ResolveRuntimeDirectoryName());

    public string PythonExecutablePath => Path.Combine(RuntimeDirectoryPath, "python.exe");

    public bool IsSupportedOnCurrentArchitecture => _architectureResolver() == Architecture.X64;

    public void EnsureInstalled()
    {
        EnsureSupportedArchitecture();

        if (!Directory.Exists(ModelDirectoryPath))
        {
            throw new DirectoryNotFoundException(
                $"Pyannote Community-1 model is not installed. Download the speaker detection files and try again. Path: {ModelDirectoryPath}");
        }

        if (!File.Exists(RunnerScriptPath))
        {
            EnsureRunnerScriptExists();
        }

        if (!Directory.Exists(RuntimeDirectoryPath))
        {
            throw new DirectoryNotFoundException(
                $"Pyannote Python runtime is not installed. Download the speaker detection files and try again. Path: {RuntimeDirectoryPath}");
        }

        if (!File.Exists(PythonExecutablePath))
        {
            throw new FileNotFoundException(
                "Pyannote Python executable is not installed. Download the speaker detection files and try again.",
                PythonExecutablePath);
        }
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
        EnsureSupportedArchitecture();
        await InstallAssetAsync(PyannoteModelAssetId, progress, cancellationToken);
        await InstallAssetAsync(PyannotePythonX64AssetId, progress, cancellationToken);
        EnsureRunnerScriptExists();
    }

    private void EnsureRunnerScriptExists()
    {
        if (File.Exists(RunnerScriptPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(RunnerScriptPath)!);
        File.WriteAllText(RunnerScriptPath, RunnerScriptContent);
    }

    private const string RunnerScriptContent = """
import json
import sys

import torch
import torchaudio
from pyannote.audio import Pipeline

print("runner_started", file=sys.stderr, flush=True)

if len(sys.argv) != 3:
    print("[]")
    sys.exit(2)

model_dir = sys.argv[1]
audio_path = sys.argv[2]

print("model_loading", file=sys.stderr, flush=True)
pipeline = Pipeline.from_pretrained(model_dir)
if torch.cuda.is_available():
    pipeline = pipeline.to(torch.device("cuda"))
print("model_loaded", file=sys.stderr, flush=True)

print("waveform_loading", file=sys.stderr, flush=True)
waveform, sample_rate = torchaudio.load(audio_path)
print("waveform_loaded", file=sys.stderr, flush=True)

print("inference_started", file=sys.stderr, flush=True)
diarization = pipeline({"waveform": waveform, "sample_rate": sample_rate})
print("inference_finished", file=sys.stderr, flush=True)

print("serializing_turns", file=sys.stderr, flush=True)
turns = []
for segment, _, speaker in diarization.itertracks(yield_label=True):
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

    private async Task InstallAssetAsync(
        string assetId,
        IProgress<AssetProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        AssetProvisioningStatus status = _assetProvisioningService.GetStatus(assetId);
        if (status.State == AssetProvisioningState.Ready)
        {
            return;
        }

        await _assetProvisioningService.InstallAssetAsync(assetId, progress, cancellationToken);
    }
}
