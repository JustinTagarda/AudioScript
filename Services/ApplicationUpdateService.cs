using Velopack;
using Velopack.Sources;

namespace AudioTranscript.Services;

public sealed class ApplicationUpdateService {
    private readonly ProcessLogService _processLogService;
    private readonly UpdateManager? _updateManager;
    private VelopackAsset? _pendingUpdate;
    private string _footerStatusText;

    public ApplicationUpdateService(ProcessLogService processLogService) {
        _processLogService = processLogService ?? throw new ArgumentNullException(nameof(processLogService));
        _footerStatusText = BuildInstalledVersionStatus();

        if (!ApplicationDeploymentInfo.HasConfiguredReleaseRepo) {
            Log("Skipping Velopack configuration because the public release repository URL is not set.");
            return;
        }

        var source = new GithubSource(
            repoUrl: ApplicationDeploymentInfo.ReleaseRepoUrl,
            accessToken: string.Empty,
            prerelease: false);

        _updateManager = new UpdateManager(source);
        _pendingUpdate = _updateManager.UpdatePendingRestart;

        if (_pendingUpdate is not null) {
            Log($"Found a previously-downloaded update for version {_pendingUpdate.Version}.");
            SetFooterStatus(BuildReadyOnExitStatus(_pendingUpdate.Version.ToString()));
        }
    }

    public event EventHandler? StatusChanged;

    public string FooterStatusText => _footerStatusText;

    public async Task<ApplicationExitUpdateCheckResult> CheckForUpdateOnExitAsync(CancellationToken cancellationToken) {
        if (_updateManager is null) {
            return ApplicationExitUpdateCheckResult.None;
        }

        if (!_updateManager.IsInstalled) {
            Log("Skipping update check because this copy is not installed by Velopack.");
            return ApplicationExitUpdateCheckResult.None;
        }

        if (_pendingUpdate is not null) {
            Log($"Using previously-downloaded update {_pendingUpdate.Version} during shutdown.");
            return new ApplicationExitUpdateCheckResult(
                Update: null,
                TargetVersion: _pendingUpdate.Version.ToString(),
                HasPendingUpdate: true);
        }

        Log($"Checking for updates from {ApplicationDeploymentInfo.ReleaseRepoUrl} before exit.");

        UpdateInfo? update = await _updateManager.CheckForUpdatesAsync();
        if (cancellationToken.IsCancellationRequested) {
            return ApplicationExitUpdateCheckResult.None;
        }

        if (update is null) {
            Log("No updates are available.");
            SetFooterStatus(BuildInstalledVersionStatus());
            return ApplicationExitUpdateCheckResult.None;
        }

        string targetVersion = update.TargetFullRelease.Version.ToString();
        Log($"Update {targetVersion} is available for installation during shutdown.");

        return new ApplicationExitUpdateCheckResult(
            Update: update,
            TargetVersion: targetVersion,
            HasPendingUpdate: false);
    }

    public async Task<bool> DownloadUpdateOnExitAsync(
        UpdateInfo update,
        IProgress<int>? progress,
        CancellationToken cancellationToken) {
        if (_updateManager is null) {
            return false;
        }

        string targetVersion = update.TargetFullRelease.Version.ToString();
        Log($"Downloading update {targetVersion} before exit.");
        SetFooterStatus(BuildDownloadStatus(targetVersion, 0));
        progress?.Report(0);

        int lastLoggedProgress = -10;
        await _updateManager.DownloadUpdatesAsync(
            update,
            progressValue => {
                SetFooterStatus(BuildDownloadStatus(targetVersion, progressValue));
                progress?.Report(progressValue);

                if (progressValue < 100 && progressValue < lastLoggedProgress + 10) {
                    return;
                }

                lastLoggedProgress = progressValue;
                Log($"Download progress: {progressValue}%");
            },
            cancellationToken);

        _pendingUpdate = _updateManager.UpdatePendingRestart;

        if (_pendingUpdate is not null) {
            Log($"Update {_pendingUpdate.Version} is ready and will be installed during shutdown.");
            SetFooterStatus(BuildReadyOnExitStatus(_pendingUpdate.Version.ToString()));
            progress?.Report(100);
            return true;
        }

        Log("Update download completed, but no pending update was registered.");
        SetFooterStatus(BuildInstalledVersionStatus());
        return false;
    }

    public async Task ApplyPendingUpdatesOnExitAsync() {
        if (_updateManager is null || _pendingUpdate is null) {
            return;
        }

        VelopackAsset updateToApply = _pendingUpdate;
        _pendingUpdate = null;

        Log($"Applying staged update {updateToApply.Version} during shutdown.");
        SetFooterStatus(BuildApplyingStatus(updateToApply.Version.ToString()));
        await _updateManager.WaitExitThenApplyUpdatesAsync(
            updateToApply,
            silent: true,
            restart: false,
            restartArgs: Array.Empty<string>());
    }

    private void Log(string message) {
        _processLogService.Log("AutoUpdate", message);
    }

    private void SetFooterStatus(string value) {
        if (string.Equals(_footerStatusText, value, StringComparison.Ordinal)) {
            return;
        }

        _footerStatusText = value;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string BuildInstalledVersionStatus() {
        return $"Version {FormatCurrentVersion(ApplicationDeploymentInfo.CurrentVersion)}";
    }

    private static string BuildDownloadStatus(string targetVersion, int progress) {
        int clampedProgress = Math.Clamp(progress, 0, 100);
        return $"{BuildInstalledVersionStatus()} · Updating to {targetVersion} {clampedProgress}%";
    }

    private static string BuildReadyOnExitStatus(string targetVersion) {
        return $"{BuildInstalledVersionStatus()} · Update {targetVersion} ready on exit";
    }

    private static string BuildApplyingStatus(string targetVersion) {
        return $"{BuildInstalledVersionStatus()} · Installing {targetVersion}...";
    }

    private static string FormatCurrentVersion(Version version) {
        if (version.Revision > 0) {
            return version.ToString(4);
        }

        if (version.Build >= 0) {
            return version.ToString(3);
        }

        return version.ToString(2);
    }
}

public sealed record ApplicationExitUpdateCheckResult(
    UpdateInfo? Update,
    string TargetVersion,
    bool HasPendingUpdate) {
    public static ApplicationExitUpdateCheckResult None { get; } =
        new(Update: null, TargetVersion: string.Empty, HasPendingUpdate: false);

    public bool ShouldInstallOnExit => HasPendingUpdate || Update is not null;
}
