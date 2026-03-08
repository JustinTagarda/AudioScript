using System.IO;
using NAudio.Wave;

namespace AudioTranscript.Audio;

public sealed class NaudioAudioPlaybackService : IAudioPlaybackService, IPlaybackAudioTapSource {
    private readonly object _sync = new();
    private WaveOutEvent? _output;
    private AudioFileReader? _reader;
    private string? _loadedFilePath;
    private bool _isMuted;

    public event EventHandler? PlaybackStateChanged;
    public event EventHandler<PlaybackAudioFrameEventArgs>? PlaybackAudioFrameProduced;
    public event EventHandler<Exception>? PlaybackAudioFaulted;

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

    public WaveFormat? PlaybackAudioFormat {
        get {
            lock (_sync) {
                return _reader?.WaveFormat;
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

    public bool IsMuted {
        get {
            lock (_sync) {
                return _isMuted;
            }
        }
        set {
            WaveOutEvent? output;
            bool shouldApply;

            lock (_sync) {
                shouldApply = _isMuted != value;
                if (!shouldApply) {
                    return;
                }

                _isMuted = value;
                output = _output;
            }

            ApplyMuteState(output, value);
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
        var tappedProvider = new TappedWaveProvider(
            reader,
            OnPlaybackAudioFrameProduced,
            OnPlaybackAudioFaulted);
        var output = new WaveOutEvent();
        output.Init(tappedProvider);
        ApplyMuteState(output, IsMuted);
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

    private void OnPlaybackAudioFrameProduced(byte[] buffer, WaveFormat waveFormat) {
        PlaybackAudioFrameProduced?.Invoke(this, new PlaybackAudioFrameEventArgs(buffer, waveFormat));
    }

    private void OnPlaybackAudioFaulted(Exception ex) {
        PlaybackAudioFaulted?.Invoke(this, ex);
    }

    private static void ApplyMuteState(WaveOutEvent? output, bool isMuted) {
        if (output is null) {
            return;
        }

        output.Volume = isMuted ? 0f : 1f;
    }

    private sealed class TappedWaveProvider : IWaveProvider {
        private readonly IWaveProvider _source;
        private readonly Action<byte[], WaveFormat> _frameHandler;
        private readonly Action<Exception> _faultHandler;

        public TappedWaveProvider(
            IWaveProvider source,
            Action<byte[], WaveFormat> frameHandler,
            Action<Exception> faultHandler) {
            _source = source;
            _frameHandler = frameHandler;
            _faultHandler = faultHandler;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(byte[] buffer, int offset, int count) {
            try {
                int bytesRead = _source.Read(buffer, offset, count);
                if (bytesRead <= 0) {
                    return bytesRead;
                }

                byte[] copied = new byte[bytesRead];
                Buffer.BlockCopy(buffer, offset, copied, 0, bytesRead);
                _frameHandler(copied, WaveFormat);
                return bytesRead;
            }
            catch (Exception ex) {
                _faultHandler(ex);
                throw;
            }
        }
    }
}
