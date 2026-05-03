using AudioScript.Audio;
using NAudio.Wave;
using Xunit;

namespace AudioScript.Tests;

public sealed class AutomaticGainAudioCaptureServiceTests
{
    [Fact]
    public void AutomaticGain_BoostsQuietPcm()
    {
        var processor = new Pcm16AutomaticGainProcessor(
            new LiveAudioGainOptions(IsAutomaticGainEnabled: true, ManualGainLevel: 0.5));
        byte[] quietSpeech = CreatePcmToneChunk(100, peakLevel: 0.003);

        AudioGainProcessingResult result = processor.Process(quietSpeech, AudioFormatConstants.EngineWaveFormat);

        Assert.True(result.GainMultiplier > 1);
        Assert.True(result.OutputPeak > result.InputPeak);
        Assert.True(result.OutputPeak >= 0.015);
    }

    [Fact]
    public void AutomaticGain_LeavesDigitalSilenceSilent()
    {
        var processor = new Pcm16AutomaticGainProcessor(
            new LiveAudioGainOptions(IsAutomaticGainEnabled: true, ManualGainLevel: 0.5));
        byte[] silence = new byte[AudioFormatConstants.EngineWaveFormat.AverageBytesPerSecond / 10];

        AudioGainProcessingResult result = processor.Process(silence, AudioFormatConstants.EngineWaveFormat);

        Assert.Equal(0, result.InputPeak);
        Assert.Equal(0, result.OutputPeak);
    }

    [Fact]
    public void ManualGain_UsesSliderMappedMultiplier()
    {
        var processor = new Pcm16AutomaticGainProcessor(
            new LiveAudioGainOptions(IsAutomaticGainEnabled: false, ManualGainLevel: 1));
        byte[] quietSpeech = CreatePcmToneChunk(100, peakLevel: 0.005);

        AudioGainProcessingResult result = processor.Process(quietSpeech, AudioFormatConstants.EngineWaveFormat);

        Assert.Equal(64, result.GainMultiplier, precision: 6);
        Assert.True(result.OutputPeak > 0.2);
    }

    [Fact]
    public void CaptureWrapper_EmitsProcessedFrameWithGainMetadata()
    {
        var inner = new FakeLoopbackCaptureService();
        using var capture = new AutomaticGainAudioCaptureService(
            inner,
            new LiveAudioGainOptions(IsAutomaticGainEnabled: true, ManualGainLevel: 0.5));
        LoopbackAudioFrameEventArgs? captured = null;
        capture.AudioFrameCaptured += (_, frame) => captured = frame;

        capture.StartCapture();
        inner.EmitFrame(CreatePcmToneChunk(100, peakLevel: 0.003), AudioFormatConstants.EngineWaveFormat);

        Assert.NotNull(captured);
        Assert.True(captured.AppliedGain > 1);
        Assert.True(captured.AutomaticGainApplied);
    }

    private static byte[] CreatePcmToneChunk(int durationMilliseconds, double peakLevel)
    {
        WaveFormat format = AudioFormatConstants.EngineWaveFormat;
        int sampleCount = Math.Max((int)(format.SampleRate * (durationMilliseconds / 1000d)), 1);
        byte[] buffer = new byte[sampleCount * format.BlockAlign];
        double clampedPeak = Math.Clamp(peakLevel, 0, 1);

        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            double phase = (2 * Math.PI * 440 * sampleIndex) / format.SampleRate;
            short sample = (short)Math.Round(Math.Sin(phase) * clampedPeak * short.MaxValue);
            int byteIndex = sampleIndex * format.BlockAlign;
            buffer[byteIndex] = (byte)(sample & 0xFF);
            buffer[byteIndex + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return buffer;
    }

    private sealed class FakeLoopbackCaptureService : IAudioLoopbackCaptureService
    {
        public event EventHandler<LoopbackAudioFrameEventArgs>? AudioFrameCaptured;

        public event EventHandler<Exception>? CaptureFaulted;

        public bool IsCapturing { get; private set; }

        public WaveFormat? CaptureFormat { get; private set; }

        public void StartCapture()
        {
            IsCapturing = true;
        }

        public void StopCapture()
        {
            IsCapturing = false;
        }

        public void Dispose()
        {
            StopCapture();
        }

        public void EmitFrame(byte[] buffer, WaveFormat waveFormat)
        {
            CaptureFormat = waveFormat;
            AudioFrameCaptured?.Invoke(this, new LoopbackAudioFrameEventArgs(buffer, waveFormat));
        }

        public void EmitFault(Exception ex)
        {
            CaptureFaulted?.Invoke(this, ex);
        }
    }
}
