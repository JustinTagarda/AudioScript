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

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is StartupProvisioningWindowViewModel viewModel && viewModel.WasSuccessful)
        {
            DialogResult = true;
        }
        else
        {
            DialogResult = false;
        }

        _allowClose = true;
        Close();
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

        if (DataContext is StartupProvisioningWindowViewModel completedViewModel)
        {
            _allowClose = true;
            DialogResult = completedViewModel.WasSuccessful;
        }

        base.OnClosing(e);
    }

    private void ConfirmCancelAndExit()
    {
        MessageBoxResult result = System.Windows.MessageBox.Show(
            this,
            "If you continue, the startup asset installation will be canceled and AudioScript will exit.",
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
