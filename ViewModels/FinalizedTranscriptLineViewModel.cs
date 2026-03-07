using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AudioTranscript.ViewModels;

public sealed class FinalizedTranscriptLineViewModel : INotifyPropertyChanged {
    private string _text;

    public FinalizedTranscriptLineViewModel(string timeline, string text) {
        Timeline = timeline?.Trim() ?? string.Empty;
        _text = text ?? string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Timeline { get; }

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
