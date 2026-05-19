using System.Threading;
using System.Windows;
using AudioScript.ViewModels;

namespace AudioScript;

public partial class StartupProvisioningWindow : Window
{
    private bool _allowClose;

    public StartupProvisioningWindow()
    {
        InitializeComponent();
    }

    public CancellationTokenSource? CancellationTokenSource { get; set; }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        ConfirmCancelAndExit();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        if (DataContext is StartupProvisioningWindowViewModel viewModel && viewModel.ShowCancelButton)
        {
            e.Cancel = true;
            ConfirmCancelAndExit();
            return;
        }

        base.OnClosing(e);
    }

    public void CloseWithResult(bool wasSuccessful)
    {
        _allowClose = true;
        DialogResult = wasSuccessful;
        Close();
    }

    private void ConfirmCancelAndExit()
    {
        MessageBoxResult result = System.Windows.MessageBox.Show(
            this,
            "If you continue, startup asset installation will be canceled and AudioScript will exit.",
            "Cancel startup initialization",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        CancellationTokenSource?.Cancel();
    }
}
