using System.Windows;

namespace AudioScript;

public partial class UpdateRequiredDialogWindow : Window {
    public UpdateRequiredDialogWindow(string message) {
        InitializeComponent();
        UpdateMessageText.Text = message;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) {
        DialogResult = true;
    }
}



