using System.ComponentModel;
using System.Windows;

namespace AudioTranscript;

public partial class UpdateProgressWindow : Window {
    private bool _allowClose;

    public UpdateProgressWindow() {
        InitializeComponent();
    }

    public void ShowDownloading(string targetVersion, int progressPercent) {
        int clampedProgress = Math.Clamp(progressPercent, 0, 100);

        StatusTitleText.Text = "Downloading update";
        StatusMessageText.Text =
            $"AudioTranscript is downloading version {targetVersion}. Please wait while the update is prepared.";
        StatusProgressBar.IsIndeterminate = false;
        StatusProgressBar.Value = clampedProgress;
        ProgressText.Text = $"{clampedProgress}%";
    }

    public void ShowInstalling(string targetVersion) {
        StatusTitleText.Text = "Installing update";
        StatusMessageText.Text =
            $"AudioTranscript is finalizing version {targetVersion}. Please wait while the app prepares to restart.";
        StatusProgressBar.IsIndeterminate = true;
        ProgressText.Text = "Installing...";
    }

    public void ShowCompletedAndRestarting(string targetVersion) {
        StatusTitleText.Text = "Update completed";
        StatusMessageText.Text =
            $"Version {targetVersion} is ready. AudioTranscript will restart automatically.";
        StatusProgressBar.IsIndeterminate = false;
        StatusProgressBar.Value = 100;
        ProgressText.Text = "Restarting...";
    }

    public void AllowClose() {
        _allowClose = true;
    }

    protected override void OnClosing(CancelEventArgs e) {
        if (!_allowClose) {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }
}
