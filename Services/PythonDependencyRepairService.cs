using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;

namespace AudioScript.Services;

public sealed class PythonDependencyRepairService : IPythonDependencyRepairService
{
    private const string PytorchCpuIndexUrl = "https://download.pytorch.org/whl/cpu";
    private const string PytorchStableIndexUrl = "https://download.pytorch.org/whl";
    private const string PypiSimpleIndexUrl = "https://pypi.org/simple";

    private static readonly string[] RequiredModules = ["torch", "torchaudio", "pyannote.audio"];
    private static readonly string[] RequiredPackages = ["torch", "torchaudio", "pyannote.audio"];
    private static readonly HttpClient BootstrapHttpClient = new();

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
            DependencyHealthItem unsupportedItem = BuildFailureItem(
                "pyannote-python-runtime",
                "Pyannote Python runtime",
                "Speaker diarization requires x64 architecture.",
                "Real speaker diarization will be unavailable.",
                []);
            return new PythonDependencyRepairResult(false, [unsupportedItem], attempts);
        }

        EnsureEmbeddedSitePackagesEnabled(attempts);

        ProbeResult initialProbe = await ProbeMissingModulesAsync(cancellationToken);
        if (!initialProbe.Success)
        {
            attempts.Add(new DependencyRepairAttempt(
                "probe:initial",
                1,
                false,
                initialProbe.ExitCode,
                Truncate(initialProbe.ErrorMessage)));
            return new PythonDependencyRepairResult(false,
            [
                BuildFailureItem(
                    "pyannote-python-runtime",
                    "Pyannote Python runtime",
                    "Python dependency probe failed.",
                    "Real speaker diarization will be unavailable.",
                    attempts)
            ], attempts);
        }

        List<string> missingModules = initialProbe.MissingModules;
        if (missingModules.Count == 0)
        {
            var readyItems = RequiredModules
                .Select(module => new DependencyHealthItem(
                    module,
                    module,
                    DependencyHealthCategory.PythonModule,
                    DependencyHealthStatus.Completed,
                    "Ready",
                    string.Empty,
                    []))
                .ToArray();
            return new PythonDependencyRepairResult(true, readyItems, attempts);
        }

        foreach (string module in missingModules)
        {
            progress?.Report(new StartupDependencyHealthProgress(
                module,
                module,
                DependencyHealthCategory.PythonModule,
                DependencyHealthStatus.Checking,
                "Missing module detected.",
                0,
                0,
                0));
        }

        bool pipReady = await EnsurePipAsync(attempts, cancellationToken);
        if (!pipReady)
        {
            pipReady = await BootstrapPipAsync(attempts, progress, cancellationToken);
        }

        string pythonExe = _modelManager.PythonExecutablePath;
        string[] missingSnapshot = missingModules.ToArray();
        bool repaired = false;
        if (pipReady)
        {
            repaired = await TryInstallTierLocalWheelsAsync(pythonExe, missingSnapshot, attempts, progress, cancellationToken);
        }
        else
        {
            attempts.Add(new DependencyRepairAttempt(
                "python:pip-bootstrap",
                1,
                false,
                null,
                "Pip bootstrap failed; pip-based repair tiers skipped."));
            _processLogService.Log(
                "StartupDependency",
                "dependency_repair strategy='python:pip-bootstrap' attempt=1 success=False exitCode=none reason='Pip bootstrap failed; pip-based repair tiers skipped.'");
        }

        if (!repaired && pipReady)
        {
            repaired = await TryInstallTierOnlineDefaultAsync(pythonExe, missingSnapshot, attempts, progress, cancellationToken);
        }

        if (!repaired && pipReady)
        {
            repaired = await TryInstallTierCleanReinstallAsync(pythonExe, attempts, progress, cancellationToken);
        }

        if (!repaired && pipReady)
        {
            repaired = await TryInstallTierMirrorAsync(pythonExe, missingSnapshot, attempts, progress, cancellationToken);
        }

        ProbeResult finalProbe = await ProbeMissingModulesAsync(cancellationToken);
        List<string> unresolved = finalProbe.Success ? finalProbe.MissingModules : RequiredModules.ToList();
        var items = new List<DependencyHealthItem>();
        foreach (string module in RequiredModules)
        {
            bool isMissing = unresolved.Contains(module, StringComparer.OrdinalIgnoreCase);
            items.Add(isMissing
                ? BuildFailureItem(
                    module,
                    module,
                    "Module could not be installed automatically.",
                    "Real speaker diarization will be unavailable.",
                    attempts.ToArray())
                : new DependencyHealthItem(
                    module,
                    module,
                    DependencyHealthCategory.PythonModule,
                    DependencyHealthStatus.Completed,
                    "Ready",
                    string.Empty,
                    attempts.ToArray()));
        }

        bool success = items.All(item => item.Status == DependencyHealthStatus.Completed);
        return new PythonDependencyRepairResult(success, items, attempts);
    }

    private async Task<bool> EnsurePipAsync(List<DependencyRepairAttempt> attempts, CancellationToken cancellationToken)
    {
        (int exitCode, string stdout, string stderr) = await RunPythonAsync("-m ensurepip --upgrade", cancellationToken);
        bool success = exitCode == 0;
        string detail = Truncate(success ? stdout : stderr);
        attempts.Add(new DependencyRepairAttempt("python:ensurepip", 1, success, exitCode, detail));
        LogRepair("python:ensurepip", 1, success, exitCode, detail);
        if (!success)
        {
            return false;
        }

        (int pipExitCode, string pipStdout, string pipStderr) = await RunPythonAsync("-m pip --version", cancellationToken);
        bool pipReady = pipExitCode == 0;
        string pipDetail = Truncate(pipReady ? pipStdout : pipStderr);
        attempts.Add(new DependencyRepairAttempt("python:pip-check", 1, pipReady, pipExitCode, pipDetail));
        LogRepair("python:pip-check", 1, pipReady, pipExitCode, pipDetail);
        return pipReady;
    }

    private async Task<bool> BootstrapPipAsync(
        List<DependencyRepairAttempt> attempts,
        IProgress<StartupDependencyHealthProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new StartupDependencyHealthProgress(
            "python-runtime",
            "Python runtime",
            DependencyHealthCategory.PythonModule,
            DependencyHealthStatus.Retrying,
            "Bootstrapping pip for Python runtime.",
            40,
            1,
            1));

        string runtimeDir = _modelManager.RuntimeDirectoryPath;
        string getPipPath = Path.Combine(runtimeDir, "get-pip.py");
        string[] bootstrapUrls =
        [
            Environment.GetEnvironmentVariable("AUDIOSCRIPT_PYTHON_GET_PIP_URL")?.Trim() ?? string.Empty,
            "https://bootstrap.pypa.io/get-pip.py"
        ];

        foreach (string url in bootstrapUrls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string script = await BootstrapHttpClient.GetStringAsync(url, cancellationToken);
                await File.WriteAllTextAsync(getPipPath, script, Encoding.UTF8, cancellationToken);
                attempts.Add(new DependencyRepairAttempt("python:get-pip-download", 1, true, 0, Truncate(url)));
                LogRepair("python:get-pip-download", 1, true, 0, url);

                (int installExitCode, string installStdout, string installStderr) = await RunPythonAsync($"\"{getPipPath}\"", cancellationToken);
                bool installSuccess = installExitCode == 0;
                string installDetail = Truncate(installSuccess ? installStdout : installStderr);
                attempts.Add(new DependencyRepairAttempt("python:get-pip-install", 1, installSuccess, installExitCode, installDetail));
                LogRepair("python:get-pip-install", 1, installSuccess, installExitCode, installDetail);
                if (!installSuccess)
                {
                    continue;
                }

                (int pipExitCode, string pipStdout, string pipStderr) = await RunPythonAsync("-m pip --version", cancellationToken);
                bool pipReady = pipExitCode == 0;
                string pipDetail = Truncate(pipReady ? pipStdout : pipStderr);
                attempts.Add(new DependencyRepairAttempt("python:pip-check", 2, pipReady, pipExitCode, pipDetail));
                LogRepair("python:pip-check", 2, pipReady, pipExitCode, pipDetail);
                if (pipReady)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                string detail = Truncate(ex.Message);
                attempts.Add(new DependencyRepairAttempt("python:get-pip-download", 1, false, null, detail));
                _processLogService.LogException("StartupDependency", "python_get_pip_download_failed", ex);
            }
        }

        return false;
    }

    private async Task<bool> TryInstallTierLocalWheelsAsync(
        string pythonExe,
        string[] missingModules,
        List<DependencyRepairAttempt> attempts,
        IProgress<StartupDependencyHealthProgress>? progress,
        CancellationToken cancellationToken)
    {
        string[] candidateDirs =
        [
            Path.Combine(AppContext.BaseDirectory, "assets", "python", "wheels", "win-x64"),
            Path.Combine(AppContext.BaseDirectory, "assets", "wheels", "win-x64"),
            Path.Combine(Path.GetDirectoryName(pythonExe) ?? string.Empty, "wheels")
        ];

        string? wheelDir = candidateDirs.FirstOrDefault(path => Directory.Exists(path) && Directory.EnumerateFiles(path, "*.whl").Any());
        if (string.IsNullOrWhiteSpace(wheelDir))
        {
            attempts.Add(new DependencyRepairAttempt("tier1:local-wheels", 1, false, null, "No local wheel cache found."));
            _processLogService.Log(
                "StartupDependency",
                "dependency_repair strategy='tier1:local-wheels' attempt=1 success=False exitCode=none reason='No local wheel cache found.'");
            return false;
        }

        progress?.Report(new StartupDependencyHealthProgress(
            "python-modules",
            "Python modules",
            DependencyHealthCategory.PythonModule,
            DependencyHealthStatus.Installing,
            "Installing from local wheel cache.",
            45,
            1,
            1));

        string joinedModules = string.Join(" ", missingModules);
        string args = $"-m pip install --no-index --find-links \"{wheelDir}\" {joinedModules}";
        (int exitCode, string stdout, string stderr) = await RunPythonAsync(args, cancellationToken);
        bool success = exitCode == 0;
        string detail = Truncate(success ? stdout : stderr);
        attempts.Add(new DependencyRepairAttempt("tier1:local-wheels", 1, success, exitCode, detail));
        LogRepair("tier1:local-wheels", 1, success, exitCode, detail);
        return success;
    }

    private async Task<bool> TryInstallTierOnlineDefaultAsync(
        string pythonExe,
        string[] missingModules,
        List<DependencyRepairAttempt> attempts,
        IProgress<StartupDependencyHealthProgress>? progress,
        CancellationToken cancellationToken)
    {
        string joinedModules = string.Join(" ", missingModules);
        const int maxAttempts = 2;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            progress?.Report(new StartupDependencyHealthProgress(
                "python-modules",
                "Python modules",
                DependencyHealthCategory.PythonModule,
                attempt == 1 ? DependencyHealthStatus.Installing : DependencyHealthStatus.Retrying,
                "Installing from primary online source.",
                60 + (attempt - 1) * 10,
                attempt,
                maxAttempts));

            string args =
                $"-m pip install --index-url {PytorchCpuIndexUrl} --extra-index-url {PypiSimpleIndexUrl} --extra-index-url {PytorchStableIndexUrl} {joinedModules}";
            (int exitCode, string stdout, string stderr) = await RunPythonAsync(args, cancellationToken);
            bool success = exitCode == 0;
            string detail = Truncate(success ? stdout : stderr);
            attempts.Add(new DependencyRepairAttempt("tier2:online-default", attempt, success, exitCode, detail));
            LogRepair("tier2:online-default", attempt, success, exitCode, detail);
            if (success)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> TryInstallTierCleanReinstallAsync(
        string pythonExe,
        List<DependencyRepairAttempt> attempts,
        IProgress<StartupDependencyHealthProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new StartupDependencyHealthProgress(
            "python-modules",
            "Python modules",
            DependencyHealthCategory.PythonModule,
            DependencyHealthStatus.Retrying,
            "Repairing with clean reinstall.",
            75,
            1,
            1));

        string uninstallTargets = "pyannote pyannote.audio torch torchaudio";
        (int uninstallExitCode, _, string uninstallStderr) = await RunPythonAsync(
            $"-m pip uninstall -y {uninstallTargets}",
            cancellationToken);
        attempts.Add(new DependencyRepairAttempt(
            "tier2.5:clean-uninstall",
            1,
            uninstallExitCode == 0,
            uninstallExitCode,
            Truncate(uninstallStderr)));
        LogRepair("tier2.5:clean-uninstall", 1, uninstallExitCode == 0, uninstallExitCode, Truncate(uninstallStderr));

        string reinstallTargets = string.Join(" ", RequiredPackages);
        (int installExitCode, string installStdout, string installStderr) = await RunPythonAsync(
            $"-m pip install --upgrade --index-url {PytorchCpuIndexUrl} --extra-index-url {PypiSimpleIndexUrl} --extra-index-url {PytorchStableIndexUrl} {reinstallTargets}",
            cancellationToken);
        bool installSuccess = installExitCode == 0;
        attempts.Add(new DependencyRepairAttempt(
            "tier2.5:clean-reinstall",
            1,
            installSuccess,
            installExitCode,
            Truncate(installSuccess ? installStdout : installStderr)));
        LogRepair("tier2.5:clean-reinstall", 1, installSuccess, installExitCode, Truncate(installSuccess ? installStdout : installStderr));
        return installSuccess;
    }

    private async Task<bool> TryInstallTierMirrorAsync(
        string pythonExe,
        string[] missingModules,
        List<DependencyRepairAttempt> attempts,
        IProgress<StartupDependencyHealthProgress>? progress,
        CancellationToken cancellationToken)
    {
        string joinedModules = string.Join(" ", missingModules);
        List<string> mirrors = ResolveMirrorIndexes();
        int maxAttempts = Math.Min(3, mirrors.Count);
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            string mirror = mirrors[attempt - 1];
            progress?.Report(new StartupDependencyHealthProgress(
                "python-modules",
                "Python modules",
                DependencyHealthCategory.PythonModule,
                DependencyHealthStatus.Retrying,
                $"Installing from mirror source ({mirror}).",
                80 + (attempt - 1) * 8,
                attempt,
                maxAttempts));

            string args =
                $"-m pip install --index-url {mirror} --extra-index-url {PytorchCpuIndexUrl} --extra-index-url {PytorchStableIndexUrl} {joinedModules}";
            (int exitCode, string stdout, string stderr) = await RunPythonAsync(args, cancellationToken);
            bool success = exitCode == 0;
            string detail = Truncate(success ? stdout : stderr);
            attempts.Add(new DependencyRepairAttempt("tier3:mirror", attempt, success, exitCode, detail));
            LogRepair("tier3:mirror", attempt, success, exitCode, detail);
            if (success)
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> ResolveMirrorIndexes()
    {
        var mirrors = new List<string>();
        string? envMirrors = Environment.GetEnvironmentVariable("AUDIOSCRIPT_PYTHON_MIRROR_INDEX_URLS");
        if (!string.IsNullOrWhiteSpace(envMirrors))
        {
            mirrors.AddRange(envMirrors
                .Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        string? legacyMirror = Environment.GetEnvironmentVariable("AUDIOSCRIPT_PYTHON_MIRROR_INDEX_URL");
        if (!string.IsNullOrWhiteSpace(legacyMirror))
        {
            mirrors.Add(legacyMirror.Trim());
        }

        mirrors.Add(PypiSimpleIndexUrl);
        mirrors.Add("https://pypi.python.org/simple");
        return mirrors
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<ProbeResult> ProbeMissingModulesAsync(CancellationToken cancellationToken)
    {
        _modelManager.EnsureInstalled();

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
        (int exitCode, string stdout, string stderr) = await RunPythonAsync($"-c \"{script}\"", cancellationToken);
        if (exitCode != 0)
        {
            return new ProbeResult(
                false,
                [],
                exitCode,
                $"Python dependency probe failed. {Truncate(stderr)}");
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

    private void EnsureEmbeddedSitePackagesEnabled(List<DependencyRepairAttempt> attempts)
    {
        try
        {
            string runtimeDir = _modelManager.RuntimeDirectoryPath;
            if (!Directory.Exists(runtimeDir))
            {
                attempts.Add(new DependencyRepairAttempt(
                    "python:enable-site",
                    1,
                    false,
                    null,
                    "Runtime directory not found."));
                return;
            }

            string? pthPath = Directory
                .EnumerateFiles(runtimeDir, "python*._pth", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(pthPath))
            {
                attempts.Add(new DependencyRepairAttempt(
                    "python:enable-site",
                    1,
                    false,
                    null,
                    "Embedded python _pth file not found."));
                return;
            }

            byte[] rawBytes = File.ReadAllBytes(pthPath);
            bool hasUtf8Bom = rawBytes.Length >= 3
                && rawBytes[0] == 0xEF
                && rawBytes[1] == 0xBB
                && rawBytes[2] == 0xBF;
            string content = hasUtf8Bom
                ? Encoding.UTF8.GetString(rawBytes, 3, rawBytes.Length - 3)
                : Encoding.UTF8.GetString(rawBytes);
            string normalizedContent = content.Replace("\uFEFF", string.Empty, StringComparison.Ordinal);
            bool siteEnabled = content
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Any(line => string.Equals(line.Trim(), "import site", StringComparison.Ordinal));
            bool hadBom = hasUtf8Bom || !string.Equals(content, normalizedContent, StringComparison.Ordinal);
            if (siteEnabled)
            {
                if (hadBom)
                {
                    File.WriteAllText(pthPath, normalizedContent, new UTF8Encoding(false));
                }
                attempts.Add(new DependencyRepairAttempt(
                    "python:enable-site",
                    1,
                    true,
                    0,
                    hadBom
                        ? "Site import already enabled; removed BOM from _pth."
                        : "Site import already enabled."));
                LogRepair(
                    "python:enable-site",
                    1,
                    true,
                    0,
                    hadBom
                        ? "Site import already enabled; removed BOM from _pth."
                        : "Site import already enabled.");
                return;
            }

            string updated = normalizedContent.Replace("#import site", "import site", StringComparison.Ordinal);
            if (updated == normalizedContent)
            {
                updated = normalizedContent.TrimEnd() + Environment.NewLine + "import site" + Environment.NewLine;
            }

            File.WriteAllText(pthPath, updated, new UTF8Encoding(false));
            attempts.Add(new DependencyRepairAttempt(
                "python:enable-site",
                1,
                true,
                0,
                Path.GetFileName(pthPath)));
            LogRepair("python:enable-site", 1, true, 0, Path.GetFileName(pthPath));
        }
        catch (Exception ex)
        {
            string detail = Truncate(ex.Message);
            attempts.Add(new DependencyRepairAttempt(
                "python:enable-site",
                1,
                false,
                null,
                detail));
            _processLogService.LogException("StartupDependency", "python_enable_site_failed", ex);
        }
    }

    private async Task<(int exitCode, string stdout, string stderr)> RunPythonAsync(string arguments, CancellationToken cancellationToken)
    {
        TimeSpan timeout = arguments.Contains("-m pip install", StringComparison.Ordinal)
            || arguments.Contains("get-pip.py", StringComparison.Ordinal)
            ? TimeSpan.FromMinutes(8)
            : TimeSpan.FromMinutes(2);
        var startInfo = new ProcessStartInfo
        {
            FileName = _modelManager.PythonExecutablePath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _modelManager.RuntimeDirectoryPath,
        };

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start bundled Python runtime.");
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore best-effort kill failures and return timeout result.
            }

            return (-1, await stdoutTask, $"Process timed out after {timeout.TotalMinutes:0.#} minute(s).");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private DependencyHealthItem BuildFailureItem(
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

    private void LogRepair(string strategy, int attempt, bool success, int exitCode, string? reason = null)
    {
        string reasonText = string.IsNullOrWhiteSpace(reason)
            ? "none"
            : reason.Replace("'", "\"");
        _processLogService.Log(
            "StartupDependency",
            $"dependency_repair strategy='{strategy}' attempt={attempt} success={success} exitCode={exitCode} reason='{reasonText}'");
    }

    private static string Truncate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string trimmed = text.Trim();
        return trimmed.Length <= 400 ? trimmed : trimmed[..400];
    }

    private sealed record ProbeResult(
        bool Success,
        List<string> MissingModules,
        int? ExitCode,
        string ErrorMessage);
}
