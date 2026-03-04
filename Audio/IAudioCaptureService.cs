namespace AudioTranscript.Audio;

public interface IAudioCaptureService : IDisposable {
    event EventHandler<AudioFrame>? FrameCaptured;

    bool IsCapturing { get; }

    Task StartDefaultPlaybackAsync(CancellationToken cancellationToken);

    Task StopAsync();
}
