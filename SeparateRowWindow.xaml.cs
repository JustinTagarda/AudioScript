using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace AudioScript;

public partial class SeparateRowWindow : Window, INotifyPropertyChanged
{
    private readonly string _originalText;
    private int _splitIndex;
    private string _firstRowText = string.Empty;
    private string _secondRowText = string.Empty;
    private string _validationErrorText = string.Empty;

    public SeparateRowWindow(string originalText, int initialSplitIndex)
    {
        _originalText = originalText ?? string.Empty;
        _splitIndex = Math.Clamp(initialSplitIndex, 0, _originalText.Length);
        UpdateSplitTextAndValidation();
        InitializeComponent();
        DataContext = this;
        Loaded += OnLoaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string OriginalText => _originalText;

    public int SplitIndex
    {
        get => _splitIndex;
        private set
        {
            int normalized = Math.Clamp(value, 0, _originalText.Length);
            if (_splitIndex == normalized)
            {
                return;
            }

            _splitIndex = normalized;
            OnPropertyChanged();
        }
    }

    public string FirstRowText
    {
        get => _firstRowText;
        private set
        {
            if (string.Equals(_firstRowText, value, StringComparison.Ordinal))
            {
                return;
            }

            _firstRowText = value;
            OnPropertyChanged();
        }
    }

    public string SecondRowText
    {
        get => _secondRowText;
        private set
        {
            if (string.Equals(_secondRowText, value, StringComparison.Ordinal))
            {
                return;
            }

            _secondRowText = value;
            OnPropertyChanged();
        }
    }

    public string ValidationErrorText
    {
        get => _validationErrorText;
        private set
        {
            string normalized = value ?? string.Empty;
            if (string.Equals(_validationErrorText, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _validationErrorText = normalized;
            OnPropertyChanged();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        OriginalTextBox.Focus();
        OriginalTextBox.CaretIndex = SplitIndex;
        OriginalTextBox.SelectionLength = 0;
    }

    private void OriginalTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox)
        {
            return;
        }

        SplitIndex = textBox.CaretIndex;
        UpdateSplitTextAndValidation();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateSplitTextAndValidation();
        if (!string.IsNullOrEmpty(ValidationErrorText))
        {
            return;
        }

        DialogResult = true;
    }

    private void UpdateSplitTextAndValidation()
    {
        (string firstText, string secondText) = MainWindow.SplitRowTextAtIndex(_originalText, SplitIndex);
        FirstRowText = firstText;
        SecondRowText = secondText;

        if (!MainWindow.TryValidateSeparateRowTextSplit(_originalText, SplitIndex, out string error))
        {
            ValidationErrorText = error;
            return;
        }

        ValidationErrorText = string.Empty;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
