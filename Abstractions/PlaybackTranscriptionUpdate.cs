namespace VoxTranscribe.Abstractions;

public sealed record PlaybackTranscriptionUpdate(
    string SessionId,
    string Text,
    int? SequenceIndex = null
);


