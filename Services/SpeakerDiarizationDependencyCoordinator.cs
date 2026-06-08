namespace AudioScript.Services;

public enum SpeakerDiarizationDependencyState
{
    Ready,
    Missing,
    Corrupted,
    Unsupported,
    Failed,
    Canceled,
}

public sealed record SpeakerDiarizationDependencyStatus(
    SpeakerDiarizationDependencyState State,
    string Message)
{
    public bool IsReady => State == SpeakerDiarizationDependencyState.Ready;

    public bool CanInstall =>
        State is SpeakerDiarizationDependencyState.Missing
            or SpeakerDiarizationDependencyState.Corrupted;
}

public enum SpeakerDiarizationDependencyProgressPhase
{
    Checking,
    Downloading,
    Installing,
    Verifying,
    ValidatingExecution,
}

public sealed record SpeakerDiarizationDependencyProgress(
    SpeakerDiarizationDependencyProgressPhase Phase,
    string StatusMessage,
    string DetailMessage,
    double DownloadPercent,
    double InstallPercent);

public sealed record SpeakerDiarizationDependencyResult(
    SpeakerDiarizationDependencyState State,
    string Message)
{
    public bool Succeeded => State == SpeakerDiarizationDependencyState.Ready;

    public bool WasCanceled => State == SpeakerDiarizationDependencyState.Canceled;
}

public sealed class SpeakerDiarizationDependencyCoordinator
{
    private static readonly string[] AssetIds =
    [
        PyannoteCommunityModelManager.PyannotePythonX64AssetId,
        PyannoteCommunityModelManager.PyannoteModelAssetId,
    ];

    private readonly IAssetProvisioningService _assetProvisioningService;
    private readonly PyannoteCommunityModelManager _modelManager;
    private readonly IPythonDependencyRepairService _pythonDependencyRepairService;
    private readonly IPyannoteExecutionProbe _pyannoteExecutionProbe;
    private readonly IOfficialSourceBootstrapService _officialSourceBootstrapService;
    private readonly ProcessLogService _processLogService;

    public SpeakerDiarizationDependencyCoordinator(
        IAssetProvisioningService assetProvisioningService,
        PyannoteCommunityModelManager modelManager,
        IPythonDependencyRepairService pythonDependencyRepairService,
        IPyannoteExecutionProbe pyannoteExecutionProbe,
        ProcessLogService processLogService,
        IOfficialSourceBootstrapService? officialSourceBootstrapService = null)
    {
        _assetProvisioningService = assetProvisioningService;
        _modelManager = modelManager;
        _pythonDependencyRepairService = pythonDependencyRepairService;
        _pyannoteExecutionProbe = pyannoteExecutionProbe;
        _officialSourceBootstrapService = officialSourceBootstrapService ?? new LegacyOfficialSourceBootstrapService(assetProvisioningService);
        _processLogService = processLogService;
    }

    public async Task<SpeakerDiarizationDependencyStatus> CheckStatusAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        if (!_modelManager.IsSupportedOnCurrentArchitecture)
        {
            return new SpeakerDiarizationDependencyStatus(
                SpeakerDiarizationDependencyState.Unsupported,
                "Speaker diarization requires an x64 AudioScript build.");
        }

        foreach (string assetId in AssetIds)
        {
            AssetProvisioningStatus status = _assetProvisioningService.GetStatus(assetId);
            if (status.State == AssetProvisioningState.Unsupported)
            {
                return new SpeakerDiarizationDependencyStatus(
                    SpeakerDiarizationDependencyState.Unsupported,
                    status.Message ?? $"{status.DisplayName} is not supported on this architecture.");
            }

            if (status.State != AssetProvisioningState.Ready)
            {
                return new SpeakerDiarizationDependencyStatus(
                    SpeakerDiarizationDependencyState.Missing,
                    "Speaker detection components need to be installed.");
            }
        }

        try
        {
            _modelManager.PrepareInstalledRuntime();
            PythonDependencyRepairResult pythonResult = await _pythonDependencyRepairService
                .ValidateAndRepairAsync(progress: null, cancellationToken)
                .ConfigureAwait(false);
            if (pythonResult.Succeeded)
            {
                return new SpeakerDiarizationDependencyStatus(
                    SpeakerDiarizationDependencyState.Ready,
                    "Speaker detection components are ready.");
            }

            string failed = string.Join(
                ", ",
                pythonResult.Items
                    .Where(item => item.Status == DependencyHealthStatus.Failed)
                    .Select(item => item.DisplayName)
                    .Where(name => !string.IsNullOrWhiteSpace(name)));
            return new SpeakerDiarizationDependencyStatus(
                SpeakerDiarizationDependencyState.Corrupted,
                string.IsNullOrWhiteSpace(failed)
                    ? "Speaker detection components are corrupted."
                    : $"Speaker detection components are corrupted ({failed}).");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _processLogService.LogException(
                "SpeakerDiarizationDependencies",
                "Speaker diarization dependency health check failed.",
                ex);
            return new SpeakerDiarizationDependencyStatus(
                SpeakerDiarizationDependencyState.Corrupted,
                BuildDependencyHealthFailureMessage(ex));
        }
    }

    public async Task<SpeakerDiarizationDependencyResult> InstallOrRepairAsync(
        SpeakerDiarizationDependencyStatus currentStatus,
        IProgress<SpeakerDiarizationDependencyProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (currentStatus.IsReady)
        {
            return new SpeakerDiarizationDependencyResult(
                SpeakerDiarizationDependencyState.Ready,
                currentStatus.Message);
        }

        if (!currentStatus.CanInstall)
        {
            return new SpeakerDiarizationDependencyResult(
                currentStatus.State,
                currentStatus.Message);
        }

        try
        {
            progress?.Report(new SpeakerDiarizationDependencyProgress(
                SpeakerDiarizationDependencyProgressPhase.Checking,
                "Preparing speaker detection components.",
                currentStatus.Message,
                0,
                0));

            if (currentStatus.State == SpeakerDiarizationDependencyState.Corrupted)
            {
                await RemoveExistingAssetsAsync(cancellationToken).ConfigureAwait(false);
            }

            OfficialSourceBootstrapResult bootstrapResult = await _officialSourceBootstrapService
                .BootstrapAsync(
                    new Progress<AssetProvisioningProgress>(asset =>
                    {
                        progress?.Report(new SpeakerDiarizationDependencyProgress(
                            asset.Status.Contains("download", StringComparison.OrdinalIgnoreCase)
                                ? SpeakerDiarizationDependencyProgressPhase.Downloading
                                : SpeakerDiarizationDependencyProgressPhase.Installing,
                            $"Provisioning {asset.DisplayName}.",
                            asset.Status,
                            asset.Status.Contains("download", StringComparison.OrdinalIgnoreCase) ? asset.Percent : 100,
                            asset.Status.Contains("download", StringComparison.OrdinalIgnoreCase) ? 0 : asset.Percent));
                    }),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!bootstrapResult.Succeeded)
            {
                return new SpeakerDiarizationDependencyResult(
                    bootstrapResult.WasCanceled ? SpeakerDiarizationDependencyState.Canceled : SpeakerDiarizationDependencyState.Failed,
                    bootstrapResult.Message);
            }

            progress?.Report(new SpeakerDiarizationDependencyProgress(
                SpeakerDiarizationDependencyProgressPhase.Verifying,
                "Verifying speaker detection components.",
                "Checking the installed runtime.",
                100,
                100));

            SpeakerDiarizationDependencyStatus verified = await CheckStatusAsync(cancellationToken).ConfigureAwait(false);
            if (verified.IsReady)
            {
                return new SpeakerDiarizationDependencyResult(
                    SpeakerDiarizationDependencyState.Ready,
                    "Speaker detection components are ready.");
            }

            return new SpeakerDiarizationDependencyResult(
                SpeakerDiarizationDependencyState.Failed,
                verified.Message);
        }
        catch (OperationCanceledException)
        {
            _processLogService.Log(
                "SpeakerDiarizationDependencies",
                "Speaker diarization dependency installation was canceled.",
                ProcessLogLevel.Warning);
            return new SpeakerDiarizationDependencyResult(
                SpeakerDiarizationDependencyState.Canceled,
                "Speaker detection setup was canceled.");
        }
        catch (Exception ex)
        {
            _processLogService.LogException(
                "SpeakerDiarizationDependencies",
                "Speaker diarization dependency installation failed.",
                ex);
            return new SpeakerDiarizationDependencyResult(
                SpeakerDiarizationDependencyState.Failed,
                BuildInstallFailureMessage(ex));
        }
    }

    public async Task<SpeakerDiarizationDependencyResult> EnsureExecutionReadyAsync(
        IProgress<SpeakerDiarizationDependencyProgress>? progress,
        CancellationToken cancellationToken)
    {
        SpeakerDiarizationDependencyStatus status = await CheckStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.IsReady)
        {
            SpeakerDiarizationDependencyResult installResult = await InstallOrRepairAsync(
                status,
                progress,
                cancellationToken).ConfigureAwait(false);
            if (!installResult.Succeeded)
            {
                return installResult;
            }
        }

        try
        {
            PyannoteExecutionProbeResult probeResult = await ProbeExecutionAsync(progress, cancellationToken).ConfigureAwait(false);
            if (probeResult.Succeeded)
            {
                return new SpeakerDiarizationDependencyResult(
                    SpeakerDiarizationDependencyState.Ready,
                    "Speaker detection components are ready.");
            }

            _processLogService.Log(
                "SpeakerDiarizationDependencies",
                $"Speaker diarization execution probe failed; attempting one full repair. detail='{probeResult.Message}'",
                ProcessLogLevel.Warning);

            SpeakerDiarizationDependencyResult repairResult = await InstallOrRepairAsync(
                new SpeakerDiarizationDependencyStatus(
                    SpeakerDiarizationDependencyState.Corrupted,
                    BuildExecutionProbeFailureMessage(probeResult.Message)),
                progress,
                cancellationToken).ConfigureAwait(false);
            if (!repairResult.Succeeded)
            {
                return repairResult;
            }

            PyannoteExecutionProbeResult repairedProbeResult = await ProbeExecutionAsync(progress, cancellationToken).ConfigureAwait(false);
            if (repairedProbeResult.Succeeded)
            {
                return new SpeakerDiarizationDependencyResult(
                    SpeakerDiarizationDependencyState.Ready,
                    "Speaker detection components are ready.");
            }

            return new SpeakerDiarizationDependencyResult(
                SpeakerDiarizationDependencyState.Failed,
                BuildExecutionProbeFailureMessage(repairedProbeResult.Message));
        }
        catch (OperationCanceledException)
        {
            _processLogService.Log(
                "SpeakerDiarizationDependencies",
                "Speaker diarization runtime validation was canceled.",
                ProcessLogLevel.Warning);
            return new SpeakerDiarizationDependencyResult(
                SpeakerDiarizationDependencyState.Canceled,
                "Speaker detection setup was canceled.");
        }
        catch (Exception ex)
        {
            _processLogService.LogException(
                "SpeakerDiarizationDependencies",
                "Speaker diarization runtime validation failed.",
                ex);
            return new SpeakerDiarizationDependencyResult(
                SpeakerDiarizationDependencyState.Failed,
                BuildExecutionProbeFailureMessage(ex.Message));
        }
    }

    private async Task<PyannoteExecutionProbeResult> ProbeExecutionAsync(
        IProgress<SpeakerDiarizationDependencyProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new SpeakerDiarizationDependencyProgress(
            SpeakerDiarizationDependencyProgressPhase.ValidatingExecution,
            "Validating speaker detection runtime.",
            "Launching the installed pyannote runtime.",
            100,
            100));

        return await _pyannoteExecutionProbe
            .ProbeExecutionAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RemoveExistingAssetsAsync(CancellationToken cancellationToken)
    {
        foreach (string assetId in AssetIds)
        {
            await _assetProvisioningService.RemoveAssetAsync(assetId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string BuildInstallFailureMessage(Exception ex)
    {
        string detail = GetInnermostExceptionMessage(ex);
        if (string.IsNullOrWhiteSpace(detail))
        {
            return "Speaker detection components could not be installed.";
        }

        return $"Speaker detection components could not be installed. {detail}";
    }

    private static string BuildExecutionProbeFailureMessage(string detail)
    {
        string trimmed = detail?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "Speaker detection runtime validation failed. Close AudioScript and run Detect Speaker again. If the problem persists, reinstall AudioScript.";
        }

        if (trimmed.Contains("WinError 126", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("torch_python.dll", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("native DLL", StringComparison.OrdinalIgnoreCase))
        {
            return $"Speaker detection runtime validation failed because a native DLL could not be loaded. {trimmed}";
        }

        if (trimmed.Contains("import torch", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("torch", StringComparison.OrdinalIgnoreCase))
        {
            return $"Speaker detection runtime validation failed while loading torch. {trimmed}";
        }

        if (trimmed.Contains("pyannote.audio", StringComparison.OrdinalIgnoreCase))
        {
            return $"Speaker detection runtime validation failed while loading pyannote.audio. {trimmed}";
        }

        return $"Speaker detection runtime validation failed. {trimmed}";
    }

    private static string BuildDependencyHealthFailureMessage(Exception ex)
    {
        string detail = GetInnermostExceptionMessage(ex);
        if (string.IsNullOrWhiteSpace(detail))
        {
            return "Speaker detection components are missing or corrupted.";
        }

        return $"Speaker detection components are missing or corrupted. {detail}";
    }

    private static string GetInnermostExceptionMessage(Exception ex)
    {
        Exception current = ex;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current.Message.Trim();
    }

    private sealed class LegacyOfficialSourceBootstrapService : IOfficialSourceBootstrapService
    {
        private readonly IAssetProvisioningService _assetProvisioningService;

        public LegacyOfficialSourceBootstrapService(IAssetProvisioningService assetProvisioningService)
        {
            _assetProvisioningService = assetProvisioningService;
        }

        public async Task<OfficialSourceBootstrapResult> BootstrapAsync(
            IProgress<AssetProvisioningProgress>? progress,
            CancellationToken cancellationToken)
        {
            var steps = new List<OfficialSourceBootstrapStepResult>();
            foreach (string assetId in AssetIds)
            {
                AssetProvisioningStatus status = _assetProvisioningService.GetStatus(assetId);
                if (status.State == AssetProvisioningState.Ready)
                {
                    steps.Add(new OfficialSourceBootstrapStepResult(assetId, true, $"{status.DisplayName} already installed."));
                    continue;
                }

                var assetProgress = new Progress<AssetProvisioningProgress>(asset =>
                {
                    progress?.Report(asset);
                });

                try
                {
                    await _assetProvisioningService.InstallAssetAsync(assetId, assetProgress, cancellationToken).ConfigureAwait(false);
                    steps.Add(new OfficialSourceBootstrapStepResult(assetId, true, $"{status.DisplayName} installed."));
                }
                catch (OperationCanceledException)
                {
                    return new OfficialSourceBootstrapResult(false, true, steps, "Bootstrap canceled.");
                }
                catch (Exception ex)
                {
                    return new OfficialSourceBootstrapResult(false, false, steps, ex.Message);
                }
            }

            return new OfficialSourceBootstrapResult(true, false, steps, "Legacy bootstrap completed.");
        }
    }
}
