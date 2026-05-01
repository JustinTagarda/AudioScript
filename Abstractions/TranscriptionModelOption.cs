namespace AudioScript.Abstractions;

public sealed record TranscriptionModelOption(
    string Id,
    string DisplayName,
    bool SupportsFileTranscription = true,
    bool SupportsPlaybackTranscription = true,
    bool SupportsSpeakerDiarization = true,
    bool IsLocal = false
);



