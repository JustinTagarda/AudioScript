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

    public string ModelDirectoryPath => _assetProvisioningService.ResolveInstallPath(PyannoteModelAssetId);

    public string RunnerScriptPath => Path.Combine(ModelDirectoryPath, "run_community_diarization.py");

    public string RuntimeDirectoryPath => _assetProvisioningService.ResolveInstallPath(PyannotePythonX64AssetId);

    public string PythonExecutablePath => Path.Combine(RuntimeDirectoryPath, "python.exe");

    public bool IsSupportedOnCurrentArchitecture => _architectureResolver() == Architecture.X64;

    public void EnsureInstalled()
    {
        EnsureSupportedArchitecture();

        if (!Directory.Exists(ModelDirectoryPath))
        {
            throw new DirectoryNotFoundException(
                $"Pyannote Community-1 model is missing from the AudioScript installation. Reinstall AudioScript from Microsoft Store. Path: {ModelDirectoryPath}");
        }

        EnsureRunnerScriptExists();

        if (!Directory.Exists(RuntimeDirectoryPath))
        {
            throw new DirectoryNotFoundException(
                $"Pyannote Python runtime is missing from the AudioScript installation. Reinstall AudioScript from Microsoft Store. Path: {RuntimeDirectoryPath}");
        }

        if (!File.Exists(PythonExecutablePath))
        {
            throw new FileNotFoundException(
                "Pyannote Python executable is missing from the AudioScript installation. Reinstall AudioScript from Microsoft Store.",
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
        await Task.Yield();
        EnsureSupportedArchitecture();
        EnsureInstalled();
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

    private const string RunnerScriptContent = """
import json
import sys
import wave

import numpy as np
import torch
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
