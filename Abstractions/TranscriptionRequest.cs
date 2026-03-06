namespace AudioTranscript.Abstractions;

public sealed record TranscriptionRequest(
    bool IncludeTimestamps = true,
    bool IncludePunctuation = true,
    bool EnableDiarization = true,
    string Language = "auto",
    string Prompt = ""
);
