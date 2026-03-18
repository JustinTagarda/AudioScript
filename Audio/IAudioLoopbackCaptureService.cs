using NAudio.Wave;

namespace VoxTranscriber.Audio;

public interface IAudioLoopbackCaptureService : IDisposable {
    event EventHandler<LoopbackAudioFrameEventArgs>? AudioFrameCaptured;

    event EventHandler<Exception>? CaptureFaulted;

    bool IsCapturing { get; }

    WaveFormat? CaptureFormat { get; }

    void StartCapture();

    void StopCapture();
}


