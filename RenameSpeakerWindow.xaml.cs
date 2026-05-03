using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AudioScript;

public partial class RenameSpeakerWindow : Window
{
    private readonly string _fromSpeaker;

    public RenameSpeakerWindow(string fromSpeaker)
    {
        _fromSpeaker = fromSpeaker?.Trim() ?? string.Empty;
        InitializeComponent();
        FromSpeakerText.Text = _fromSpeaker;
        ToSpeakerTextBox.Text = _fromSpeaker;
        ToSpeakerTextBox.SelectAll();
        Loaded += OnLoaded;
        UpdateRenameButtonState();
    }

    public string ToSpeaker => ToSpeakerTextBox.Text?.Trim() ?? string.Empty;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ToSpeakerTextBox.Focus();
        Keyboard.Focus(ToSpeakerTextBox);
    }

    private void ToSpeakerTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateRenameButtonState();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void RenameButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRename())
        {
            return;
        }

        DialogResult = true;
    }

    private void UpdateRenameButtonState()
    {
        if (RenameButton is not null)
        {
            RenameButton.IsEnabled = CanRename();
        }
    }

    private bool CanRename()
    {
        string toSpeaker = ToSpeaker;
        return !string.IsNullOrWhiteSpace(_fromSpeaker)
            && !string.IsNullOrWhiteSpace(toSpeaker)
            && !string.Equals(_fromSpeaker, toSpeaker, StringComparison.Ordinal);
    }
}
