namespace AudioScript.Abstractions;

public sealed record TranscriptionTokenLogprob(
    string Token,
    double Logprob,
    int? Index
);

public sealed record LowConfidenceToken(
    string Token,
    double Logprob,
    int? Index
);



