using System.Diagnostics;
using System.Text;

namespace AudioScript.Services;

public sealed class PythonDependencyRepairService : IPythonDependencyRepairService
{
    private static readonly string[] RequiredModules = ["torch", "torchaudio", "pyannote.audio"];

    private readonly PyannoteCommunityModelManager _modelManager;
    private readonly ProcessLogService _processLogService;

    public PythonDependencyRepairService(
        PyannoteCommunityModelManager modelManager,
        ProcessLogService processLogService)
    {
        _modelManager = modelManager;
        _processLogService = processLogService;
    }

    public async Task<PythonDependencyRepairResult> ValidateAndRepairAsync(
        IProgress<StartupDependencyHealthProgress>? progress,
        CancellationToken cancellationToken)
    {
        var attempts = new List<DependencyRepairAttempt>();

        if (!_modelManager.IsSupportedOnCurrentArchitecture)
        {
            return new PythonDependencyRepairResult(
                false,
                [
                    BuildFailureItem(
                        "pyannote-python-runtime",
                        "Pyannote Python runtime",
                        "Speaker diarization requires an x64 AudioScript build.",
                        "Reinstall AudioScript from Microsoft Store with the supported x64 package.",
                        attempts)
                ],
                attempts);
        }

        _modelManager.PrepareInstalledRuntime();
        ProbeResult probe = await ProbeMissingModulesAsync(cancellationToken).ConfigureAwait(false);
        attempts.Add(new DependencyRepairAttempt(
            "python:probe",
            1,
            probe.Success,
            probe.ExitCode,
            Truncate(probe.ErrorMessage)));

        if (!probe.Success)
        {
            return new PythonDependencyRepairResult(
                false,
                [
                    BuildFailureItem(
                        "pyannote-python-runtime",
                        "Pyannote Python runtime",
                        "Installed Python dependency probe failed.",
                        "Run Detect Speaker again to repair the speaker detection runtime.",
                        attempts)
                ],
                attempts);
        }

        var items = new List<DependencyHealthItem>();
        foreach (string module in RequiredModules)
        {
            bool missing = probe.MissingModules.Contains(module, StringComparer.OrdinalIgnoreCase);
            progress?.Report(new StartupDependencyHealthProgress(
                module,
                module,
                DependencyHealthCategory.PythonModule,
                missing ? DependencyHealthStatus.Failed : DependencyHealthStatus.Completed,
                missing ? "Installed module is unavailable." : "Ready",
                missing ? 0 : 100,
                1,
                1));

            items.Add(missing
                ? BuildFailureItem(
                    module,
                    module,
                    "Installed Python module is unavailable.",
                    "Run Detect Speaker again to repair the speaker detection runtime.",
                    attempts)
                : new DependencyHealthItem(
                    module,
                    module,
                    DependencyHealthCategory.PythonModule,
                    DependencyHealthStatus.Completed,
                    "Ready",
                    string.Empty,
                    attempts));
        }

        bool succeeded = items.All(item => item.Status == DependencyHealthStatus.Completed);
        return new PythonDependencyRepairResult(succeeded, items, attempts);
    }

    private async Task<ProbeResult> ProbeMissingModulesAsync(CancellationToken cancellationToken)
    {
        string modulesLiteral = string.Join(",", RequiredModules.Select(module => $"'{module}'"));
        string script = string.Join(
            "\n",
            "import importlib.util",
            $"mods=[{modulesLiteral}]",
            "missing=[]",
            "for m in mods:",
            "    try:",
            "        spec=importlib.util.find_spec(m)",
            "        if spec is None:",
            "            missing.append(m)",
            "    except ModuleNotFoundError:",
            "        missing.append(m)",
            "print('|'.join(missing))");
        (int exitCode, string stdout, string stderr) = await RunPythonAsync($"-c \"{script}\"", cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            _processLogService.Log(
                "StartupDependency",
                $"python_dependency_probe_failed exitCode={exitCode} stderr='{Truncate(stderr)}'");
            return new ProbeResult(false, [], exitCode, stderr);
        }

        string output = (stdout ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(output))
        {
            return new ProbeResult(true, [], exitCode, string.Empty);
        }

        List<string> missing = output
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        return new ProbeResult(true, missing, exitCode, string.Empty);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunPythonAsync(
        string arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _modelManager.PythonExecutablePath,
            Arguments = arguments,
            WorkingDirectory = _modelManager.RuntimeDirectoryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return (
            process.ExitCode,
            await stdoutTask,
            await stderrTask.ConfigureAwait(false));
    }

    private static DependencyHealthItem BuildFailureItem(
        string id,
        string displayName,
        string message,
        string impact,
        IReadOnlyList<DependencyRepairAttempt> attempts)
    {
        return new DependencyHealthItem(
            id,
            displayName,
            DependencyHealthCategory.PythonModule,
            DependencyHealthStatus.Failed,
            message,
            impact,
            attempts);
    }

    private static string Truncate(string? text, int maxLength = 400)
    {
        string value = text?.Trim() ?? string.Empty;
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }

    private sealed record ProbeResult(
        bool Success,
        IReadOnlyList<string> MissingModules,
        int ExitCode,
        string ErrorMessage);
}
