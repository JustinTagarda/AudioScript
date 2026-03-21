namespace VoxTranscribe.Abstractions;

public sealed record KnownSpeakerReference(
    string Name,
    string AudioFilePath
);
