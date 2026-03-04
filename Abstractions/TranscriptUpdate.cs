namespace AudioTranscript.Abstractions;

public sealed record TranscriptUpdate(
    string Text,
    bool IsFinal,
    DateTimeOffset CreatedAt,
    TimeSpan? SegmentStart = null,
    TimeSpan? SegmentEnd = null,
    string? Speaker = null,
    string? Language = null
);