using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioScript.Abstractions;

namespace AudioScript.ViewModels;

public sealed class TranscriptModeOptionViewModel : INotifyPropertyChanged {
    private readonly Action<TranscriptModeOptionViewModel>? _selectionRequested;
    private bool _isSelected;

    public TranscriptModeOptionViewModel(
        TranscriptGenerationMode mode,
        string displayName,
        string description,
        Action<TranscriptModeOptionViewModel>? selectionRequested = null) {
        Mode = mode;
        DisplayName = displayName;
        Description = description;
        _selectionRequested = selectionRequested;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TranscriptGenerationMode Mode { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public bool IsSelected {
        get => _isSelected;
        set {
            if (_isSelected == value) {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();

            if (_isSelected) {
                _selectionRequested?.Invoke(this);
            }
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

