namespace AudioScript.Abstractions;

public sealed record SpeakerDiarizationSegment(
    string Speaker,
    string Text,
    TimeSpan StartOffset,
    TimeSpan? EndOffset = null
);

