using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AudioScript.ViewModels;

public sealed class FinalizedTranscriptLineViewModel : INotifyPropertyChanged {
    private TimeSpan? _startOffset;
    private TimeSpan? _endOffset;
    private string _speakerLabel;
    private string _text;
    private bool _isPlaybackTimelineMatch;
    private bool _areRowActionsVisible;
    private bool _isPlaybackEditTranscribing;
    private double _playbackEditProgressPercent;
    private bool _isPlaybackEditProgressIndeterminate;
    private bool _isManuallyReviewed;
    private bool _isTranscriptionPartial;
    private string _speakerLabelSource;
    private int? _diarizationRevision;
    private int? _lastDiarizedChunkIndex;
    private bool _isProvisional;

    public FinalizedTranscriptLineViewModel(
        TimeSpan? startOffset,
        TimeSpan? endOffset,
        bool isTimestampEstimated,
        string text,
        string speakerLabel = "",
        bool isManuallyReviewed = false,
        bool isTranscriptionPartial = false,
        string speakerLabelSource = "",
        int? diarizationRevision = null,
        int? lastDiarizedChunkIndex = null,
        bool isProvisional = false) {
        _startOffset = startOffset;
        _endOffset = endOffset;
        IsTimestampEstimated = isTimestampEstimated;
        _speakerLabel = speakerLabel?.Trim() ?? string.Empty;
        _text = text ?? string.Empty;
        _isManuallyReviewed = isManuallyReviewed;
        _isTranscriptionPartial = isTranscriptionPartial;
        _speakerLabelSource = speakerLabelSource?.Trim() ?? string.Empty;
        _diarizationRevision = diarizationRevision;
        _lastDiarizedChunkIndex = lastDiarizedChunkIndex;
        _isProvisional = isProvisional;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TimeSpan? StartOffset => _startOffset;

    public TimeSpan? EndOffset => _endOffset;

    public bool IsTimestampEstimated { get; }

    public string SpeakerLabel {
        get => _speakerLabel;
        set {
            string normalized = value?.Trim() ?? string.Empty;

            if (string.Equals(_speakerLabel, normalized, StringComparison.Ordinal)) {
                return;
            }

            _speakerLabel = normalized;
            OnPropertyChanged();
        }
    }

    public string Timeline {
        get => _startOffset is null
            ? string.Empty
            : FormatTimeline(_startOffset.Value);
    }

    public void SetTimelineOffsets(TimeSpan? startOffset, TimeSpan? endOffset) {
        bool startChanged = _startOffset != startOffset;
        bool endChanged = _endOffset != endOffset;

        if (!startChanged && !endChanged) {
            return;
        }

        _startOffset = startOffset;
        _endOffset = endOffset;

        if (startChanged) {
            OnPropertyChanged(nameof(StartOffset));
            OnPropertyChanged(nameof(Timeline));
        }

        if (endChanged) {
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

    public bool IsTranscriptionPartial {
        get => _isTranscriptionPartial;
        set {
            if (_isTranscriptionPartial == value) {
                return;
            }

            _isTranscriptionPartial = value;
            OnPropertyChanged();
        }
    }

    public string SpeakerLabelSource {
        get => _speakerLabelSource;
        set {
            string normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(_speakerLabelSource, normalized, StringComparison.Ordinal)) {
                return;
            }

            _speakerLabelSource = normalized;
            OnPropertyChanged();
        }
    }

    public int? DiarizationRevision {
        get => _diarizationRevision;
        set {
            if (_diarizationRevision == value) {
                return;
            }

            _diarizationRevision = value;
            OnPropertyChanged();
        }
    }

    public int? LastDiarizedChunkIndex {
        get => _lastDiarizedChunkIndex;
        set {
            if (_lastDiarizedChunkIndex == value) {
                return;
            }

            _lastDiarizedChunkIndex = value;
            OnPropertyChanged();
        }
    }

    public bool IsProvisional {
        get => _isProvisional;
        set {
            if (_isProvisional == value) {
                return;
            }

            _isProvisional = value;
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



