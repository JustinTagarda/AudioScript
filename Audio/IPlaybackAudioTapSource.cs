using NAudio.Wave;

namespace VoxTranscriber.Audio;

public interface IPlaybackAudioTapSource {
    event EventHandler<PlaybackAudioFrameEventArgs>? PlaybackAudioFrameProduced;

    event EventHandler<Exception>? PlaybackAudioFaulted;

    WaveFormat? PlaybackAudioFormat { get; }
}


