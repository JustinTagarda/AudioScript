using AudioScript.Audio;
using NAudio.Wave;
using Xunit;

namespace AudioScript.Tests;

public sealed class PlaybackAudioCaptureServiceTests {
    [Fact]
    public void StartCapture_RelaysFramesFromPlaybackTap() {
        var tapSource = new FakePlaybackAudioTapSource();
        using var captureService = new PlaybackAudioCaptureService(tapSource);
        var frames = new List<LoopbackAudioFrameEventArgs>();

        captureService.AudioFrameCaptured += (_, frame) => frames.Add(frame);
        captureService.StartCapture();

        tapSource.EmitFrame(new byte[] { 1, 2, 3, 4 }, new WaveFormat(48000, 32, 2));

        Assert.Single(frames);
        Assert.Equal(4, frames[0].BytesRecorded);
        Assert.Equal(48000, frames[0].WaveFormat.SampleRate);
        Assert.Equal(32, frames[0].WaveFormat.BitsPerSample);
        Assert.Equal(2, frames[0].WaveFormat.Channels);
        Assert.True(captureService.IsCapturing);
        Assert.NotNull(captureService.CaptureFormat);
    }

    [Fact]
    public void StopCapture_StopsRelayingFrames() {
        var tapSource = new FakePlaybackAudioTapSource();
        using var captureService = new PlaybackAudioCaptureService(tapSource);
        int frameCount = 0;

        captureService.AudioFrameCaptured += (_, _) => frameCount++;
        captureService.StartCapture();
        tapSource.EmitFrame(new byte[] { 1, 2 }, new WaveFormat(44100, 16, 2));

        captureService.StopCapture();
        tapSource.EmitFrame(new byte[] { 3, 4 }, new WaveFormat(44100, 16, 2));

        Assert.Equal(1, frameCount);
        Assert.False(captureService.IsCapturing);
    }

    [Fact]
    public void StartCapture_RelaysPlaybackFaults() {
        var tapSource = new FakePlaybackAudioTapSource();
        using var captureService = new PlaybackAudioCaptureService(tapSource);
        Exception? capturedFault = null;

        captureService.CaptureFaulted += (_, ex) => capturedFault = ex;
        captureService.StartCapture();

        tapSource.EmitFault(new InvalidOperationException("tap fault"));

        Assert.NotNull(capturedFault);
        Assert.Equal("tap fault", capturedFault!.Message);
    }

    private sealed class FakePlaybackAudioTapSource : IPlaybackAudioTapSource {
        public event EventHandler<PlaybackAudioFrameEventArgs>? PlaybackAudioFrameProduced;
        public event EventHandler<Exception>? PlaybackAudioFaulted;

        public WaveFormat? PlaybackAudioFormat { get; private set; }

        public void EmitFrame(byte[] buffer, WaveFormat waveFormat) {
            PlaybackAudioFormat = waveFormat;
            PlaybackAudioFrameProduced?.Invoke(this, new PlaybackAudioFrameEventArgs(buffer, waveFormat));
        }

        public void EmitFault(Exception ex) {
            PlaybackAudioFaulted?.Invoke(this, ex);
        }
    }
}



