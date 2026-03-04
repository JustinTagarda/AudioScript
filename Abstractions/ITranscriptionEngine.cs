namespace AudioTranscript.Abstractions;

public interface ITranscriptionEngine {
    string Id { get; }

    string DisplayName { get; }

    EngineCapability Capabilities { get; }

    Task<TranscriptUpdate> TranscribeFileAsync(
        string audioFilePath,
        TranscriptionRequest request,
        CancellationToken cancellationToken);

    IRealtimeTranscriptionSession CreateRealtimeSession(TranscriptionRequest request);
}