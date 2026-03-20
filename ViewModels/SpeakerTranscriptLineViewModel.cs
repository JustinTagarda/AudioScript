using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VoxTranscriber.ViewModels;

public sealed class SpeakerTranscriptLineViewModel : INotifyPropertyChanged {
    private string _speakerLabel;
    private string _text;

    public SpeakerTranscriptLineViewModel(
        TimeSpan startOffset,
        TimeSpan? endOffset,
        string speakerLabel,
        string text) {
        StartOffset = startOffset < TimeSpan.Zero ? TimeSpan.Zero : startOffset;
        EndOffset = endOffset is null
            ? null
            : endOffset.Value < StartOffset
                ? StartOffset
                : endOffset.Value;
        _speakerLabel = string.IsNullOrWhiteSpace(speakerLabel)
            ? "Speaker"
            : speakerLabel.Trim();
        _text = text ?? string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TimeSpan StartOffset { get; }

    public TimeSpan? EndOffset { get; }

    public string Timeline => StartOffset.ToString(@"hh\:mm\:ss");

    public string SpeakerLabel {
        get => _speakerLabel;
        set {
            string normalized = string.IsNullOrWhiteSpace(value)
                ? "Speaker"
                : value.Trim();

            if (string.Equals(_speakerLabel, normalized, StringComparison.Ordinal)) {
                return;
            }

            _speakerLabel = normalized;
            OnPropertyChanged();
        }
    }

    public string Text {
        get => _text;
        set {
            string normalized = value ?? string.Empty;

            if (string.Equals(_text, normalized, StringComparison.Ordinal)) {
                return;
            }

            _text = normalized;
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
