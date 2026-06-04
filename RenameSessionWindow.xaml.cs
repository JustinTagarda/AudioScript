using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace AudioScript;

public partial class RenameSessionWindow : Window, INotifyPropertyChanged
{
    private readonly string _originalSessionName;
    private readonly Func<string, string?>? _validateRename;
    private string _sessionName = string.Empty;
    private string _validationErrorText = string.Empty;

    public RenameSessionWindow(
        string currentSessionName,
        Func<string, string?>? validateRename = null)
    {
        _originalSessionName = NormalizeName(currentSessionName);
        _validateRename = validateRename;
        InitializeComponent();
        DataContext = this;
        SessionName = _originalSessionName;
        Loaded += OnLoaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SessionName
    {
        get => _sessionName;
        set
        {
            string normalized = NormalizeName(value);
            if (string.Equals(_sessionName, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _sessionName = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSaveRename));

            if (!string.IsNullOrWhiteSpace(ValidationErrorText))
            {
                ValidationErrorText = string.Empty;
            }
        }
    }

    public string ValidationErrorText
    {
        get => _validationErrorText;
        private set
        {
            string normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(_validationErrorText, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _validationErrorText = normalized;
            OnPropertyChanged();
        }
    }

    public bool CanSaveRename =>
        !string.IsNullOrWhiteSpace(SessionName)
        && !string.Equals(_originalSessionName, SessionName, StringComparison.OrdinalIgnoreCase);

    public string RenamedSessionName => SessionName;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SessionNameTextBox.Focus();
        Keyboard.Focus(SessionNameTextBox);
        SessionNameTextBox.SelectAll();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        string proposedName = SessionName;
        if (string.IsNullOrWhiteSpace(proposedName))
        {
            ValidationErrorText = "Enter a session name.";
            return;
        }

        if (string.Equals(_originalSessionName, proposedName, StringComparison.OrdinalIgnoreCase))
        {
            ValidationErrorText = "The session name is unchanged.";
            return;
        }

        try
        {
            string? validationError = _validateRename?.Invoke(proposedName);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                ValidationErrorText = validationError;
                return;
            }
        }
        catch (Exception ex)
        {
            ValidationErrorText = $"Unable to validate session name: {ex.Message}";
            return;
        }

        DialogResult = true;
    }

    private static string NormalizeName(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
