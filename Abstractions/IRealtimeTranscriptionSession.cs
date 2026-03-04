namespace AudioTranscript.Abstractions;

public interface IRealtimeTranscriptionSession : IAsyncDisposable {
    event EventHandler<TranscriptUpdate>? UpdateReceived;

    Task StartAsync(CancellationToken cancellationToken);

    Task PushAudioAsync(ReadOnlyMemory<byte> pcm16KhzMono, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}