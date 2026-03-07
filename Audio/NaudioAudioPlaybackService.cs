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

        var reader = new AudioFileReader(fullPath);
        var output = new WaveOutEvent();
        output.Init(reader);
        output.PlaybackStopped += OnPlaybackStopped;

        WaveOutEvent? previousOutput;
        AudioFileReader? previousReader;

        lock (_sync) {
            previousOutput = _output;
            previousReader = _reader;
            _reader = reader;
            _output = output;
            _loadedFilePath = fullPath;
        }

        DisposePlaybackCore(previousOutput, previousReader);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UnloadFile() {
        WaveOutEvent? output;
        AudioFileReader? reader;

        lock (_sync) {
            output = _output;
            reader = _reader;
            _output = null;
            _reader = null;
            _loadedFilePath = null;
        }

        DisposePlaybackCore(output, reader);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Play() {
        WaveOutEvent? output;
        AudioFileReader? reader;

        lock (_sync) {
            output = _output;
            reader = _reader;

            if (output is null || reader is null) {
                throw new InvalidOperationException("No audio file is loaded.");
            }

            if (reader.Position >= reader.Length) {
                reader.Position = 0;
            }
        }

        output.Play();
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause() {
        WaveOutEvent? output;

        lock (_sync) {
            output = _output;
        }

        if (output is null) {
            return;
        }

        if (output.PlaybackState == PlaybackState.Playing) {
            output.Pause();
        }

        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop() {
        WaveOutEvent? output;
        AudioFileReader? reader;

        lock (_sync) {
            output = _output;
            reader = _reader;
        }

        if (output is null || reader is null) {
            return;
        }

        output.Stop();

        lock (_sync) {
            if (!ReferenceEquals(reader, _reader)) {
                return;
            }

            reader.Position = 0;
        }

        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Seek(TimeSpan position) {
        AudioFileReader? reader;
        TimeSpan clamped = position;

        lock (_sync) {
            reader = _reader;

            if (reader is null) {
                return;
            }

            TimeSpan duration = reader.TotalTime;

            if (clamped < TimeSpan.Zero) {
                clamped = TimeSpan.Zero;
            }
            else if (duration > TimeSpan.Zero && clamped > duration) {
                clamped = duration;
            }
        }

        lock (_sync) {
            if (!ReferenceEquals(reader, _reader)) {
                return;
            }

            reader.CurrentTime = clamped;
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

    private void DisposePlaybackCore(WaveOutEvent? output, AudioFileReader? reader) {
        if (output is not null) {
            output.PlaybackStopped -= OnPlaybackStopped;
        }

        output?.Dispose();
        reader?.Dispose();
    }
}
