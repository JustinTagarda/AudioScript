using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using AudioScript.Services;

namespace AudioScript;

public partial class SpeakerDiarizationInstallWindow : Window
{
    private readonly DispatcherTimer _elapsedTimer;
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    private bool _allowClose;
    private bool _cancelRequested;

    public SpeakerDiarizationInstallWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;

        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _elapsedTimer.Tick += (_, _) => UpdateElapsedText();
        UpdateElapsedText();
    }

    public event EventHandler? CancelRequested;

    public void ApplyProgress(SpeakerDiarizationDependencyProgress progress)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ApplyProgress(progress)));
            return;
        }

        StatusText.Text = GetUserFacingStatusMessage(progress);
        DetailText.Text = GetUserFacingDetailMessage(progress);
        DownloadProgressBar.Value = Math.Clamp(progress.DownloadPercent, 0, 100);
        InstallProgressBar.Value = Math.Clamp(progress.InstallPercent, 0, 100);
        UpdateElapsedText();
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
        _elapsedTimer.Start();
        CloseIfLoaded();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _elapsedTimer.Stop();
    }

    private void CloseIfLoaded()
    {
        if (_allowClose && IsLoaded)
        {
            Close();
        }
    }

    private void UpdateElapsedText()
    {
        TimeSpan elapsed = DateTimeOffset.UtcNow - _startedUtc;
        ElapsedText.Text = $"Elapsed {FormatElapsed(elapsed)}";
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        int totalMinutes = (int)elapsed.TotalMinutes;
        int seconds = elapsed.Seconds;
        return $"{totalMinutes:00}:{seconds:00}";
    }

    private static string GetUserFacingStatusMessage(SpeakerDiarizationDependencyProgress progress)
    {
        return progress.Phase switch
        {
            SpeakerDiarizationDependencyProgressPhase.Checking =>
                "Preparing speaker detection components.",
            SpeakerDiarizationDependencyProgressPhase.Downloading =>
                "Downloading required speaker detection components.",
            SpeakerDiarizationDependencyProgressPhase.Installing =>
                "Installing speaker detection components.",
            SpeakerDiarizationDependencyProgressPhase.Verifying =>
                "Verifying installed speaker detection components.",
            SpeakerDiarizationDependencyProgressPhase.ValidatingExecution =>
                "Validating speaker detection runtime.",
            _ => progress.StatusMessage,
        };
    }

    private static string GetUserFacingDetailMessage(SpeakerDiarizationDependencyProgress progress)
    {
        return progress.Phase switch
        {
            SpeakerDiarizationDependencyProgressPhase.Checking =>
                "Checking whether the required runtime is already installed.",
            SpeakerDiarizationDependencyProgressPhase.Downloading =>
                "Downloading files needed for speaker detection.",
            SpeakerDiarizationDependencyProgressPhase.Installing =>
                progress.DetailMessage,
            SpeakerDiarizationDependencyProgressPhase.Verifying =>
                "Confirming the installed runtime is ready to use.",
            SpeakerDiarizationDependencyProgressPhase.ValidatingExecution =>
                "Launching the installed runtime to confirm it works.",
            _ => progress.DetailMessage,
        };
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
