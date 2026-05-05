using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace AudioScript;

public partial class SeparateRowWindow : Window, INotifyPropertyChanged
{
    private readonly TimeSpan _rowStartOffset;
    private readonly TimeSpan _rowEndOffset;
    private readonly TimeSpan _minSplitOffset;
    private readonly TimeSpan _maxSplitOffset;
    private TimeSpan _splitOffset;
    private string _firstRowText;
    private string _secondRowText;
    private string _validationErrorText = string.Empty;

    public SeparateRowWindow(
        TimeSpan rowStartOffset,
        TimeSpan rowEndOffset,
        TimeSpan initialSplitOffset,
        string firstRowText,
        string secondRowText)
    {
        _rowStartOffset = rowStartOffset;
        _rowEndOffset = rowEndOffset;
        _minSplitOffset = _rowStartOffset + TimeSpan.FromSeconds(1);
        _maxSplitOffset = _rowEndOffset - TimeSpan.FromSeconds(1);
        _splitOffset = ClampSplitOffset(initialSplitOffset);
        _firstRowText = firstRowText ?? string.Empty;
        _secondRowText = secondRowText ?? string.Empty;
        InitializeComponent();
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string FirstRowStartText => FormatTimeline(_rowStartOffset);

    public string SecondRowEndText => FormatTimeline(_rowEndOffset);

    public TimeSpan MinSplitOffset => _minSplitOffset;

    public TimeSpan MaxSplitOffset => _maxSplitOffset;

    public TimeSpan SplitOffset
    {
        get => _splitOffset;
        set => SetSplitOffset(value);
    }

    public string FirstRowText
    {
        get => _firstRowText;
        set
        {
            string normalized = value ?? string.Empty;
            if (string.Equals(_firstRowText, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _firstRowText = normalized;
            OnPropertyChanged();
        }
    }

    public string SecondRowText
    {
        get => _secondRowText;
        set
        {
            string normalized = value ?? string.Empty;
            if (string.Equals(_secondRowText, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _secondRowText = normalized;
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

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!MainWindow.TryValidateSeparateRowInput(
            _rowStartOffset,
            _rowEndOffset,
            _splitOffset,
            FirstRowText,
            SecondRowText,
            out string error))
        {
            ValidationErrorText = error;
            return;
        }

        ValidationErrorText = string.Empty;
        DialogResult = true;
    }

    private void SetSplitOffset(TimeSpan value)
    {
        TimeSpan clamped = ClampSplitOffset(value);
        if (_splitOffset == clamped)
        {
            return;
        }

        _splitOffset = clamped;
        OnPropertyChanged(nameof(SplitOffset));
    }

    private TimeSpan ClampSplitOffset(TimeSpan value)
    {
        if (value < _minSplitOffset)
        {
            return _minSplitOffset;
        }

        if (value > _maxSplitOffset)
        {
            return _maxSplitOffset;
        }

        return value;
    }

    private static string FormatTimeline(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        int totalMinutes = (int)value.TotalMinutes;
        return $"{totalMinutes:00}:{value.Seconds:00}";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
