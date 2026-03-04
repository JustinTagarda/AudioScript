namespace AudioTranscript.Abstractions;

public sealed record TranscriptionRequest(
    string? LanguageHint,
    bool IncludeTimestamps = true
);