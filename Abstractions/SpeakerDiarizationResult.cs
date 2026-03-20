namespace VoxTranscriber.Abstractions;

public sealed record SpeakerDiarizationResult(
    string Text,
    string Model,
    DateTimeOffset CreatedAt,
    TimeSpan? Duration,
    IReadOnlyList<SpeakerDiarizationSegment> Segments
);
