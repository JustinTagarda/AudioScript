using System.IO;
using System.Runtime.InteropServices;

namespace AudioScript.Services;

public sealed class PyannoteCommunityModelManager
{
    private const string ModelRelativePath = "pyannote/speaker-diarization-community-1";
    private const string RunnerScriptRelativePath = "pyannote/run_community_diarization.py";

    private readonly string _assetsDirectoryPath;
    private readonly Func<Architecture> _architectureResolver;

    public PyannoteCommunityModelManager(
        string? assetsDirectoryPath = null,
        Func<Architecture>? architectureResolver = null)
    {
        _assetsDirectoryPath = string.IsNullOrWhiteSpace(assetsDirectoryPath)
            ? Path.Combine(AppContext.BaseDirectory, "assets")
            : Path.GetFullPath(assetsDirectoryPath);
        _architectureResolver = architectureResolver ?? (() => RuntimeInformation.ProcessArchitecture);
    }

    public string ModelDirectoryPath => Path.Combine(_assetsDirectoryPath, ModelRelativePath);

    public string RunnerScriptPath => Path.Combine(_assetsDirectoryPath, RunnerScriptRelativePath);

    public string RuntimeDirectoryPath => Path.Combine(_assetsDirectoryPath, "python", ResolveRuntimeDirectoryName());

    public string PythonExecutablePath => Path.Combine(RuntimeDirectoryPath, "python.exe");

    public void EnsureInstalled()
    {
        if (!Directory.Exists(ModelDirectoryPath))
        {
            throw new DirectoryNotFoundException(
                $"Bundled pyannote Community-1 model was not found. Reinstall or repair AudioScript. Path: {ModelDirectoryPath}");
        }

        if (!File.Exists(RunnerScriptPath))
        {
            throw new FileNotFoundException(
                "Bundled pyannote diarization runner was not found. Reinstall or repair AudioScript.",
                RunnerScriptPath);
        }

        if (!Directory.Exists(RuntimeDirectoryPath))
        {
            throw new DirectoryNotFoundException(
                $"Bundled pyannote Python runtime was not found. Reinstall or repair AudioScript. Path: {RuntimeDirectoryPath}");
        }

        if (!File.Exists(PythonExecutablePath))
        {
            throw new FileNotFoundException(
                "Bundled pyannote Python executable was not found. Reinstall or repair AudioScript.",
                PythonExecutablePath);
        }
    }

    private string ResolveRuntimeDirectoryName()
    {
        return _architectureResolver() switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            Architecture.X86 => throw new PlatformNotSupportedException("Pyannote Community-1 requires a 64-bit AudioScript build."),
            Architecture.Arm => throw new PlatformNotSupportedException("Pyannote Community-1 requires a 64-bit AudioScript build."),
            Architecture.Wasm => throw new PlatformNotSupportedException("Pyannote Community-1 is not supported on WebAssembly."),
            Architecture.S390x => throw new PlatformNotSupportedException("Pyannote Community-1 is not supported on s390x."),
            Architecture.Ppc64le => throw new PlatformNotSupportedException("Pyannote Community-1 is not supported on ppc64le."),
            _ => throw new PlatformNotSupportedException($"Unsupported process architecture '{_architectureResolver()}'."),
        };
    }
}
