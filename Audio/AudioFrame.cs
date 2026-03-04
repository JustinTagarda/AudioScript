using NAudio.Wave;

namespace AudioTranscript.Audio;

public sealed record AudioFrame(
    byte[] Pcm16KhzMono,
    WaveFormat Format,
    DateTimeOffset CapturedAt
);