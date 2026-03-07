namespace AudioTranscript.Audio;

public interface IAudioPlaybackService : IDisposable {
    event EventHandler? PlaybackStateChanged;

    string? LoadedFilePath { get; }

    bool IsLoaded { get; }

    bool IsPlaying { get; }

    TimeSpan Duration { get; }

    TimeSpan Position { get; }

    void LoadFile(string filePath);

    void UnloadFile();

    void Play();

    void Pause();

    void Stop();

    void Seek(TimeSpan position);
}
