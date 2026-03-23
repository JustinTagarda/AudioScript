namespace AudioScript.Abstractions;

public sealed record TranscriptionTimedLine(
    string Text,
    TimeSpan StartOffset,
    TimeSpan? EndOffset = null,
    bool IsTimestampEstimated = false
);



