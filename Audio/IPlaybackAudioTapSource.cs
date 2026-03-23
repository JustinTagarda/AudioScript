using NAudio.Wave;

namespace AudioScript.Audio;

public interface IPlaybackAudioTapSource {
    event EventHandler<PlaybackAudioFrameEventArgs>? PlaybackAudioFrameProduced;

    event EventHandler<Exception>? PlaybackAudioFaulted;

    WaveFormat? PlaybackAudioFormat { get; }
}



