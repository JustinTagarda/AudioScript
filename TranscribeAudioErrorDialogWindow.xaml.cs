using System.Windows;

namespace AudioScript;

public partial class TranscribeAudioErrorDialogWindow : Window
{
    public TranscribeAudioErrorDialogWindow(string message)
    {
        InitializeComponent();
        ErrorMessageText.Text = message;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
