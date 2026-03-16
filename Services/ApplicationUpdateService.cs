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
    public async Task CheckForUpdatesAsync(CancellationToken cancellationToken) {
        if (_updateManager is null) {
            return;
        }

        if (!_updateManager.IsInstalled) {
            Log("Skipping update check because this copy is not installed by Velopack.");
            return;
        }

        if (_pendingUpdate is not null) {
            Log("Skipping update check because an update is already staged for shutdown.");
            return;
        }

        Log($"Checking for updates from {ApplicationDeploymentInfo.ReleaseRepoUrl}.");

        UpdateInfo? update = await _updateManager.CheckForUpdatesAsync();
        if (cancellationToken.IsCancellationRequested) {
            return;
        }

        if (update is null) {
            Log("No updates are available.");
            SetFooterStatus(BuildInstalledVersionStatus());
            return;
        }

        Log($"Update {update.TargetFullRelease.Version} is available. Downloading in the background.");
        SetFooterStatus(BuildDownloadStatus(update.TargetFullRelease.Version.ToString(), 0));

        int lastLoggedProgress = -10;
        await _updateManager.DownloadUpdatesAsync(
            update,
            progress => {
                SetFooterStatus(BuildDownloadStatus(update.TargetFullRelease.Version.ToString(), progress));
                if (progress < 100 && progress < lastLoggedProgress + 10) {
                    return;
                }

                lastLoggedProgress = progress;
                Log($"Download progress: {progress}%");
            },
            cancellationToken);

        _pendingUpdate = _updateManager.UpdatePendingRestart;

        if (_pendingUpdate is not null) {
            Log($"Update {_pendingUpdate.Version} is ready and will be applied when the app exits.");
            SetFooterStatus(BuildReadyOnExitStatus(_pendingUpdate.Version.ToString()));
        }
        else {
            Log("Update download completed, but no pending update was registered.");
            SetFooterStatus(BuildInstalledVersionStatus());
        }
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
