namespace AudioScript.Abstractions;

public sealed record TranscriptionResult(
    string Text,
    string Model,
    DateTimeOffset CreatedAt,
    TimeSpan? Duration,
    IReadOnlyList<TranscriptionTokenLogprob> TokenLogprobs,
    IReadOnlyList<LowConfidenceToken> LowConfidenceTokens,
    IReadOnlyList<TranscriptionTimedLine>? TimedLines = null
);



