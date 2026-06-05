using System.ComponentModel;
using System.Windows;
using AudioScript.Services;

namespace AudioScript;

public partial class DeferredUpdateInstallWindow : Window
{
    private readonly IAppUpdateService _appUpdateService;
    private bool _allowClose;

    public DeferredUpdateInstallWindow(IAppUpdateService appUpdateService)
    {
        _appUpdateService = appUpdateService ?? throw new ArgumentNullException(nameof(appUpdateService));
        InitializeComponent();
        _appUpdateService.SnapshotChanged += OnSnapshotChanged;
        Closed += OnClosed;
        ApplySnapshot(_appUpdateService.CurrentSnapshot);
    }

    public bool IsDismissalAllowed => _allowClose;

    public void CloseAfterOperation()
    {
        _allowClose = true;
        if (IsLoaded)
        {
            Close();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
    }

    private void OnSnapshotChanged(object? sender, AppUpdateSnapshot snapshot)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplySnapshot(snapshot);
            return;
        }

        Dispatcher.BeginInvoke(new Action(() => ApplySnapshot(snapshot)));
    }

    private void ApplySnapshot(AppUpdateSnapshot snapshot)
    {
        TitleText.Text = snapshot.State switch
        {
            AppUpdateState.Checking => "Preparing",
            AppUpdateState.UpdateAvailable => "Preparing",
            AppUpdateState.Downloading => "Downloading",
            AppUpdateState.Installing => "Installing",
            AppUpdateState.Completed => "Completed",
            AppUpdateState.Failed => "Update failed",
            AppUpdateState.Idle => "Completed",
            _ => "Applying update",
        };

        StatusText.Text = !string.IsNullOrWhiteSpace(snapshot.StatusMessage)
            ? snapshot.StatusMessage
            : snapshot.State == AppUpdateState.Idle
                ? "Completed"
                : snapshot.StageText;

        bool showProgress = snapshot.IsProgressVisible
            || snapshot.State is AppUpdateState.Checking
                or AppUpdateState.Downloading
                or AppUpdateState.Installing;
        UpdateProgressBar.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressBar.IsIndeterminate = false;
        UpdateProgressBar.Value = Math.Clamp(snapshot.ProgressValue * 100, 0, 100);
        ProgressText.Text = snapshot.State switch
        {
            AppUpdateState.Checking => "Preparing update request.",
            AppUpdateState.UpdateAvailable => "Waiting for permission.",
            AppUpdateState.Downloading => "Downloading package updates.",
            AppUpdateState.Installing => "Installing package updates.",
            AppUpdateState.Completed => "Update completed.",
            AppUpdateState.Idle => "Update completed.",
            AppUpdateState.Failed => "The update could not be installed.",
            _ when snapshot.IsProgressVisible => "Working on the update.",
            _ => "Waiting to start...",
        };
        PackageDetailText.Text = snapshot.PackageDetailText ?? string.Empty;
        GuidanceText.Text = snapshot.ResultGuidanceText ?? string.Empty;
        _allowClose = snapshot.State is AppUpdateState.Completed or AppUpdateState.Failed or AppUpdateState.Idle;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _appUpdateService.SnapshotChanged -= OnSnapshotChanged;
        Closed -= OnClosed;
    }
}
