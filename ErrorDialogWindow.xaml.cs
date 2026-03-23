using System.Windows;

namespace AudioScript;

public partial class ErrorDialogWindow : Window {
    public ErrorDialogWindow(string message) {
        InitializeComponent();
        ErrorMessageText.Text = message;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) {
        DialogResult = true;
    }
}



