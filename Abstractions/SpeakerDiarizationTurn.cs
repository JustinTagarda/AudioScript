namespace AudioScript.Abstractions;

public sealed record SpeakerDiarizationTurn(
    string Speaker,
    TimeSpan StartOffset,
    TimeSpan EndOffset
);

