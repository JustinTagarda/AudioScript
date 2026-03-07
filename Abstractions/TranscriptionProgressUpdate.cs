namespace AudioTranscript.Abstractions;

public sealed record TranscriptionProgressUpdate(
    string StatusMessage,
    bool IsLargeFile = false,
    int? ChunkIndex = null,
    int? TotalChunks = null
);
