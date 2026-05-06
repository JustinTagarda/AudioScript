using System.Windows;
using AudioScript.Services;

namespace AudioScript;

public partial class ExportFormatDialogWindow : Window
{
    public ExportFormatDialogWindow()
    {
        InitializeComponent();
    }

    public TranscriptDocumentFormat? SelectedFormat { get; private set; }

    private void OptionTabDelimited_Click(object sender, RoutedEventArgs e)
    {
        SelectedFormat = TranscriptDocumentFormat.TabDelimited;
        DialogResult = true;
    }

    private void OptionInterviewLayout_Click(object sender, RoutedEventArgs e)
    {
        SelectedFormat = TranscriptDocumentFormat.InterviewLayout;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
