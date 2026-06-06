using System.ComponentModel;
using System.Windows;
using AudioScript.Services;

namespace AudioScript;

public partial class SpeakerDiarizationInstallWindow : Window
{
    private bool _allowClose;
    private bool _cancelRequested;

    public SpeakerDiarizationInstallWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public event EventHandler? CancelRequested;

    public void ApplyProgress(SpeakerDiarizationDependencyProgress progress)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ApplyProgress(progress)));
            return;
        }

        StatusText.Text = progress.StatusMessage;
        DetailText.Text = progress.DetailMessage;
        DownloadProgressBar.Value = Math.Clamp(progress.DownloadPercent, 0, 100);
        InstallProgressBar.Value = Math.Clamp(progress.InstallPercent, 0, 100);
        DownloadProgressBar.IsIndeterminate = progress.Phase == SpeakerDiarizationDependencyProgressPhase.Checking;
        InstallProgressBar.IsIndeterminate = progress.Phase == SpeakerDiarizationDependencyProgressPhase.Verifying;
    }

    public void CloseAfterOperation()
    {
        _allowClose = true;
        if (Dispatcher.CheckAccess())
        {
            CloseIfLoaded();
            return;
        }

        Dispatcher.BeginInvoke(new Action(CloseIfLoaded));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        RequestCancel();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        RequestCancel();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CloseIfLoaded();
    }

    private void CloseIfLoaded()
    {
        if (_allowClose && IsLoaded)
        {
            Close();
        }
    }

    private void RequestCancel()
    {
        if (_cancelRequested)
        {
            return;
        }

        _cancelRequested = true;
        CancelButton.IsEnabled = false;
        StatusText.Text = "Canceling speaker detection setup.";
        DetailText.Text = "Stopping the current operation.";
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }
}
