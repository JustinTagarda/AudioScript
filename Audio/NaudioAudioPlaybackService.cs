using System.IO;
using NAudio.Wave;

namespace AudioTranscript.Audio;

public sealed class NaudioAudioPlaybackService : IAudioPlaybackService {
    private readonly object _sync = new();
    private WaveOutEvent? _output;
    private AudioFileReader? _reader;
    private string? _loadedFilePath;

    public event EventHandler? PlaybackStateChanged;

    public string? LoadedFilePath {
        get {
            lock (_sync) {
                return _loadedFilePath;
            }
        }
    }

    public bool IsLoaded {
        get {
            lock (_sync) {
                return _reader is not null;
            }
        }
    }

    public bool IsPlaying {
        get {
            lock (_sync) {
                return _output?.PlaybackState == PlaybackState.Playing;
            }
        }
    }

    public TimeSpan Duration {
        get {
            lock (_sync) {
                return _reader?.TotalTime ?? TimeSpan.Zero;
            }
        }
    }

    public TimeSpan Position {
        get {
            lock (_sync) {
                return _reader?.CurrentTime ?? TimeSpan.Zero;
            }
        }
    }

    public void LoadFile(string filePath) {
        if (string.IsNullOrWhiteSpace(filePath)) {
            throw new ArgumentException("Audio file path is required.", nameof(filePath));
        }

        string fullPath = Path.GetFullPath(filePath.Trim());

        if (!File.Exists(fullPath)) {
            throw new FileNotFoundException("Audio file was not found.", fullPath);
        }

        lock (_sync) {
            DisposePlaybackCore();

            var reader = new AudioFileReader(fullPath);
            var output = new WaveOutEvent();
            output.Init(reader);
            output.PlaybackStopped += OnPlaybackStopped;

            _reader = reader;
            _output = output;
            _loadedFilePath = fullPath;
        }

        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UnloadFile() {
        lock (_sync) {
            DisposePlaybackCore();
            _loadedFilePath = null;
        }

        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Play() {
        lock (_sync) {
            if (_output is null || _reader is null) {
                throw new InvalidOperationException("No audio file is loaded.");
            }

            if (_reader.Position >= _reader.Length) {
                _reader.Position = 0;
            }

            _output.Play();
        }

        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause() {
        lock (_sync) {
            if (_output is null) {
                return;
            }

            if (_output.PlaybackState == PlaybackState.Playing) {
                _output.Pause();
            }
        }

        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop() {
        lock (_sync) {
            if (_output is null || _reader is null) {
                return;
            }

            _output.Stop();
            _reader.Position = 0;
        }

        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Seek(TimeSpan position) {
        lock (_sync) {
            if (_reader is null) {
                return;
            }

            TimeSpan duration = _reader.TotalTime;
            TimeSpan clamped = position;

            if (clamped < TimeSpan.Zero) {
                clamped = TimeSpan.Zero;
            }
            else if (duration > TimeSpan.Zero && clamped > duration) {
                clamped = duration;
            }

            _reader.CurrentTime = clamped;
        }

        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() {
        UnloadFile();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e) {
        lock (_sync) {
            if (_reader is not null && _reader.Position >= _reader.Length) {
                _reader.Position = 0;
            }
        }

        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DisposePlaybackCore() {
        if (_output is not null) {
            _output.PlaybackStopped -= OnPlaybackStopped;
            _output.Dispose();
            _output = null;
        }

        if (_reader is not null) {
            _reader.Dispose();
            _reader = null;
        }
    }
}
