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
    private readonly ProcessLogService _processLogService;

    public SpeakerDiarizationDependencyCoordinator(
        IAssetProvisioningService assetProvisioningService,
        PyannoteCommunityModelManager modelManager,
        IPythonDependencyRepairService pythonDependencyRepairService,
        ProcessLogService processLogService)
    {
        _assetProvisioningService = assetProvisioningService;
        _modelManager = modelManager;
        _pythonDependencyRepairService = pythonDependencyRepairService;
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
            _modelManager.ValidateInstalled();
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
                "Speaker detection components are missing or corrupted.");
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

            for (int i = 0; i < AssetIds.Length; i++)
            {
                string assetId = AssetIds[i];
                int assetIndex = i + 1;
                AssetProvisioningStatus status = _assetProvisioningService.GetStatus(assetId);
                progress?.Report(new SpeakerDiarizationDependencyProgress(
                    SpeakerDiarizationDependencyProgressPhase.Downloading,
                    $"Installing {status.DisplayName}.",
                    $"Component {assetIndex} of {AssetIds.Length}",
                    0,
                    i * 100d / AssetIds.Length));

                var assetProgress = new Progress<AssetProvisioningProgress>(asset =>
                    progress?.Report(MapAssetProgress(asset, assetIndex, AssetIds.Length)));
                await _assetProvisioningService
                    .InstallAssetAsync(assetId, assetProgress, cancellationToken)
                    .ConfigureAwait(false);
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
                "Speaker detection components could not be installed.");
        }
    }

    private async Task RemoveExistingAssetsAsync(CancellationToken cancellationToken)
    {
        foreach (string assetId in AssetIds)
        {
            await _assetProvisioningService.RemoveAssetAsync(assetId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static SpeakerDiarizationDependencyProgress MapAssetProgress(
        AssetProvisioningProgress progress,
        int assetIndex,
        int totalAssets)
    {
        bool downloading = progress.Status.Contains("download", StringComparison.OrdinalIgnoreCase)
            || progress.Status.Contains("prepar", StringComparison.OrdinalIgnoreCase);
        double completedAssetPercent = (assetIndex - 1) * 100d / totalAssets;
        double currentAssetWeightedPercent = Math.Clamp(progress.Percent, 0, 100) / totalAssets;
        double installPercent = Math.Clamp(completedAssetPercent + currentAssetWeightedPercent, 0, 100);
        double downloadPercent = downloading ? installPercent : 100;

        return new SpeakerDiarizationDependencyProgress(
            downloading
                ? SpeakerDiarizationDependencyProgressPhase.Downloading
                : SpeakerDiarizationDependencyProgressPhase.Installing,
            downloading
                ? "Downloading speaker detection components."
                : "Installing speaker detection components.",
            $"{progress.DisplayName}: {progress.Status}",
            downloadPercent,
            installPercent);
    }
}
