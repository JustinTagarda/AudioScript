using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AudioTranscript.ViewModels;

public sealed class FinalizedTranscriptLineViewModel : INotifyPropertyChanged {
    private TimeSpan? _startOffset;
    private TimeSpan? _endOffset;
    private string _text;
    private bool _isPlaybackTimelineMatch;
    private bool _areRowActionsVisible;
    private bool _isPlaybackEditTranscribing;
    private double _playbackEditProgressPercent;
    private bool _isPlaybackEditProgressIndeterminate;
    private bool _isManuallyReviewed;

    public FinalizedTranscriptLineViewModel(
        TimeSpan? startOffset,
        TimeSpan? endOffset,
        bool isTimestampEstimated,
        string text,
        bool isManuallyReviewed = false) {
        _startOffset = startOffset;
        _endOffset = endOffset;
        IsTimestampEstimated = isTimestampEstimated;
        _text = text ?? string.Empty;
        _isManuallyReviewed = isManuallyReviewed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TimeSpan? StartOffset => _startOffset;

    public TimeSpan? EndOffset => _endOffset;

    public bool IsTimestampEstimated { get; }

    public string Timeline {
        get => _startOffset is null
            ? string.Empty
            : FormatTimeline(_startOffset.Value);
        set {
            if (!TryParseTimeline(value, out TimeSpan parsed)) {
                OnPropertyChanged(nameof(Timeline));
                return;
            }

            TimeSpan? previousStart = _startOffset;
            TimeSpan? previousEnd = _endOffset;
            if (previousStart == parsed) {
                OnPropertyChanged(nameof(Timeline));
                return;
            }

            _startOffset = parsed;

            if (previousStart is not null
                && previousEnd is not null
                && previousEnd.Value >= previousStart.Value) {
                TimeSpan duration = previousEnd.Value - previousStart.Value;
                _endOffset = parsed + duration;
            }
            else if (_endOffset is not null && _endOffset.Value < parsed) {
                _endOffset = parsed;
            }

            OnPropertyChanged(nameof(Timeline));
            OnPropertyChanged(nameof(StartOffset));
            OnPropertyChanged(nameof(EndOffset));
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

    public bool IsPlaybackTimelineMatch {
        get => _isPlaybackTimelineMatch;
        set {
            if (_isPlaybackTimelineMatch == value) {
                return;
            }

            _isPlaybackTimelineMatch = value;
            OnPropertyChanged();
        }
    }

    public bool AreRowActionsVisible {
        get => _areRowActionsVisible;
        set {
            if (_areRowActionsVisible == value) {
                return;
            }

            _areRowActionsVisible = value;
            OnPropertyChanged();
        }
    }

    public bool IsPlaybackEditTranscribing {
        get => _isPlaybackEditTranscribing;
        set {
            if (_isPlaybackEditTranscribing == value) {
                return;
            }

            _isPlaybackEditTranscribing = value;
            OnPropertyChanged();
        }
    }

    public double PlaybackEditProgressPercent {
        get => _playbackEditProgressPercent;
        set {
            double normalized = double.IsFinite(value)
                ? Math.Clamp(value, 0, 100)
                : 0;

            if (Math.Abs(_playbackEditProgressPercent - normalized) < 0.001d) {
                return;
            }

            _playbackEditProgressPercent = normalized;
            OnPropertyChanged();
        }
    }

    public bool IsPlaybackEditProgressIndeterminate {
        get => _isPlaybackEditProgressIndeterminate;
        set {
            if (_isPlaybackEditProgressIndeterminate == value) {
                return;
            }

            _isPlaybackEditProgressIndeterminate = value;
            OnPropertyChanged();
        }
    }

    public bool IsManuallyReviewed {
        get => _isManuallyReviewed;
        set {
            if (_isManuallyReviewed == value) {
                return;
            }

            _isManuallyReviewed = value;
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public static bool TryParseTimeline(string? value, out TimeSpan offset) {
        offset = TimeSpan.Zero;

        if (!TryNormalizeTimeline(value, out string normalized)) {
            return false;
        }

        int minutes = ((normalized[0] - '0') * 10) + (normalized[1] - '0');
        int seconds = ((normalized[3] - '0') * 10) + (normalized[4] - '0');
        offset = new TimeSpan(0, minutes, seconds);
        return true;
    }

    public static bool TryNormalizeTimeline(string? value, out string normalized) {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(value)) {
            return false;
        }

        string trimmed = value.Trim();
        if (trimmed.Length != 5
            || trimmed[2] != ':'
            || !char.IsAsciiDigit(trimmed[0])
            || !char.IsAsciiDigit(trimmed[1])
            || !char.IsAsciiDigit(trimmed[3])
            || !char.IsAsciiDigit(trimmed[4])) {
            return false;
        }

        int seconds = ((trimmed[3] - '0') * 10) + (trimmed[4] - '0');
        if (seconds > 59) {
            return false;
        }

        normalized = trimmed;
        return true;
    }

    private static string FormatTimeline(TimeSpan offset) {
        if (offset < TimeSpan.Zero) {
            offset = TimeSpan.Zero;
        }

        int totalMinutes = (int)offset.TotalMinutes;
        return $"{totalMinutes:00}:{offset.Seconds:00}";
    }
}
