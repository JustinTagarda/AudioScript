using Velopack;
using Velopack.Sources;

namespace AudioTranscript.Services;

public sealed class ApplicationUpdateService {
    private readonly ProcessLogService _processLogService;
    private readonly UpdateManager? _updateManager;
    private VelopackAsset? _pendingUpdate;

    public ApplicationUpdateService(ProcessLogService processLogService) {
        _processLogService = processLogService ?? throw new ArgumentNullException(nameof(processLogService));

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
        }
    }

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
            return;
        }

        Log($"Update {update.TargetFullRelease.Version} is available. Downloading in the background.");

        int lastLoggedProgress = -10;
        await _updateManager.DownloadUpdatesAsync(
            update,
            progress => {
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
        }
        else {
            Log("Update download completed, but no pending update was registered.");
        }
    }

    public async Task ApplyPendingUpdatesOnExitAsync() {
        if (_updateManager is null || _pendingUpdate is null) {
            return;
        }

        VelopackAsset updateToApply = _pendingUpdate;
        _pendingUpdate = null;

        Log($"Applying staged update {updateToApply.Version} during shutdown.");
        await _updateManager.WaitExitThenApplyUpdatesAsync(
            updateToApply,
            silent: true,
            restart: false,
            restartArgs: Array.Empty<string>());
    }

    private void Log(string message) {
        _processLogService.Log("AutoUpdate", message);
    }
}
