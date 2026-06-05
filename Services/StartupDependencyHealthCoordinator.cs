namespace AudioScript.Services;

public sealed class StartupDependencyHealthCoordinator : IStartupDependencyHealthCoordinator
{
    private readonly StartupAssetProvisioningCoordinator _assetCoordinator;
    private readonly IPythonDependencyRepairService _pythonDependencyRepairService;
    private readonly ProcessLogService _processLogService;

    public StartupDependencyHealthCoordinator(
        StartupAssetProvisioningCoordinator assetCoordinator,
        IPythonDependencyRepairService pythonDependencyRepairService,
        ProcessLogService processLogService)
    {
        _assetCoordinator = assetCoordinator;
        _pythonDependencyRepairService = pythonDependencyRepairService;
        _processLogService = processLogService;
    }

    public async Task<StartupDependencyHealthResult> RunAsync(
        IProgress<StartupDependencyHealthProgress>? progress,
        CancellationToken cancellationToken)
    {
        var allFailed = new List<DependencyHealthItem>();
        var allAttempts = new List<DependencyRepairAttempt>();

        IReadOnlyList<ProvisionedAssetDescriptor> requiredAssets = _assetCoordinator.GetRequiredAssetsForStartupDisplay();
        foreach (ProvisionedAssetDescriptor asset in requiredAssets)
        {
            AssetProvisioningStatus status = _assetCoordinator.GetAssetStatus(asset.Id);
            DependencyHealthStatus initialStatus = status.State == AssetProvisioningState.Ready
                ? DependencyHealthStatus.Completed
                : DependencyHealthStatus.Pending;
            progress?.Report(new StartupDependencyHealthProgress(
                asset.Id,
                asset.DisplayName,
                DependencyHealthCategory.Asset,
                initialStatus,
                status.Message ?? status.State.ToString(),
                initialStatus == DependencyHealthStatus.Completed ? 100 : 0,
                0,
                0));
        }

        var assetProgress = new Progress<AssetProvisioningProgress>(asset =>
        {
            DependencyHealthStatus mappedStatus = MapAssetStatus(asset.Status);
            progress?.Report(new StartupDependencyHealthProgress(
                asset.AssetId,
                asset.DisplayName,
                DependencyHealthCategory.Asset,
                mappedStatus,
                asset.Status,
                Math.Clamp(asset.Percent, 0, 100),
                0,
                0));
        });

        StartupProvisioningResult assetResult = await _assetCoordinator
            .ProvisionRequiredAssetsAsync(assetProgress, cancellationToken)
            .ConfigureAwait(false);
        if (assetResult.FailedAssetCount > 0)
        {
            allFailed.AddRange(assetResult.Failures.Select(failure => new DependencyHealthItem(
                failure.AssetId,
                failure.DisplayName,
                DependencyHealthCategory.Asset,
                DependencyHealthStatus.Failed,
                failure.Reason,
                "Some transcription or speaker features may be unavailable.",
                [])));
        }

        try
        {
            PythonDependencyRepairResult pythonResult = await _pythonDependencyRepairService
                .ValidateAndRepairAsync(progress, cancellationToken)
                .ConfigureAwait(false);
            allAttempts.AddRange(pythonResult.Attempts);
            allFailed.AddRange(pythonResult.Items.Where(item => item.Status == DependencyHealthStatus.Failed));
        }
        catch (Exception ex)
        {
            _processLogService.LogException("StartupDependency", "python_dependency_check_failed", ex);
            allFailed.Add(new DependencyHealthItem(
                "pyannote-python-runtime",
                "Pyannote Python runtime",
                DependencyHealthCategory.PythonModule,
                DependencyHealthStatus.Failed,
                ex.Message,
                "Speaker diarization will be unavailable.",
                []));
        }

        bool succeeded = allFailed.Count == 0;
        bool degraded = !succeeded;

        _processLogService.Log(
            "StartupDependency",
            $"startup_dependency_health completed; succeeded={succeeded}; degraded={degraded}; failedCount={allFailed.Count}; attempts={allAttempts.Count}");

        return new StartupDependencyHealthResult(
            succeeded,
            degraded,
            allFailed,
            allAttempts);
    }

    private static DependencyHealthStatus MapAssetStatus(string status)
    {
        string normalized = status?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Contains("retry"))
        {
            return DependencyHealthStatus.Retrying;
        }

        if (normalized.Contains("download"))
        {
            return DependencyHealthStatus.Downloading;
        }

        if (normalized.Contains("install") || normalized.Contains("copied") || normalized.Contains("preparing"))
        {
            return DependencyHealthStatus.Installing;
        }

        if (normalized.Contains("ready"))
        {
            return DependencyHealthStatus.Completed;
        }

        if (normalized.Contains("fail"))
        {
            return DependencyHealthStatus.Failed;
        }

        return DependencyHealthStatus.Checking;
    }
}
