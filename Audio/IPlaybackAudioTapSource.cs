using NAudio.Wave;

namespace VoxTranscribe.Audio;

public interface IPlaybackAudioTapSource {
    event EventHandler<PlaybackAudioFrameEventArgs>? PlaybackAudioFrameProduced;

    event EventHandler<Exception>? PlaybackAudioFaulted;

    WaveFormat? PlaybackAudioFormat { get; }
}


