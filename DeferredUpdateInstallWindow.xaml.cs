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

    public void CloseAfterOperation()
    {
        _allowClose = true;
        Close();
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
            AppUpdateState.Checking => "Preparing update",
            AppUpdateState.UpdateAvailable => "Update available",
            AppUpdateState.Downloading => "Downloading update",
            AppUpdateState.Installing => "Applying update",
            AppUpdateState.Failed => "Update failed",
            _ => "Applying update",
        };

        StatusText.Text = !string.IsNullOrWhiteSpace(snapshot.StatusMessage)
            ? snapshot.StatusMessage
            : snapshot.State == AppUpdateState.Idle
                ? "Preparing update"
                : snapshot.StageText;

        bool showProgress = snapshot.IsProgressVisible
            || snapshot.State is AppUpdateState.Checking
                or AppUpdateState.Downloading
                or AppUpdateState.Installing;
        UpdateProgressBar.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressBar.IsIndeterminate = snapshot.State is AppUpdateState.Checking;
        UpdateProgressBar.Value = Math.Clamp(snapshot.ProgressValue * 100, 0, 100);
        ProgressText.Text = snapshot.State switch
        {
            AppUpdateState.Checking => "Revalidating the update before closing.",
            AppUpdateState.UpdateAvailable => "An update is ready to install.",
            AppUpdateState.Downloading => "Downloading package updates.",
            AppUpdateState.Installing => "Installing package updates.",
            AppUpdateState.Failed => "The update could not be installed.",
            _ when snapshot.IsProgressVisible => "Working on the update.",
            _ => "Waiting to start...",
        };
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _appUpdateService.SnapshotChanged -= OnSnapshotChanged;
        Closed -= OnClosed;
    }
}
