using System.Windows;

namespace AudioScript;

public partial class ConfirmationDialogWindow : Window {
    public ConfirmationDialogWindow(
        string title,
        string message,
        string confirmButtonText,
        string cancelButtonText) {
        InitializeComponent();
        Title = title;
        DialogTitleText.Text = title;
        DialogMessageText.Text = message;
        ProceedButton.Content = confirmButtonText;
        CancelButton.Content = cancelButtonText;
    }

    public ConfirmationDialogWindow(
        string title,
        string heading,
        string message,
        string confirmButtonText,
        string cancelButtonText) {
        InitializeComponent();
        Title = title;
        DialogTitleText.Text = heading;
        DialogMessageText.Text = message;
        ProceedButton.Content = confirmButtonText;
        CancelButton.Content = cancelButtonText;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) {
        DialogResult = false;
    }

    private void ProceedButton_Click(object sender, RoutedEventArgs e) {
        DialogResult = true;
    }
}



