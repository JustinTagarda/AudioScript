using NAudio.Wave;

namespace AudioScript.Audio;

public sealed class Pcm16AutomaticGainProcessor
{
    private const double TargetRmsLevel = 0.08;
    private const double TargetPeakLevel = 0.9;
    private const double SilenceRmsLevel = 0.00003;
    private const double MinimumAutomaticGain = 0.125;
    private const double MaximumAutomaticGain = 64;
    private const double GainIncreaseSmoothing = 0.30;
    private const double GainDecreaseSmoothing = 0.10;

    private readonly LiveAudioGainOptions _options;
    private double _currentGain;

    public Pcm16AutomaticGainProcessor(LiveAudioGainOptions? options = null)
    {
        _options = (options ?? LiveAudioGainOptions.Default).Validate();
        _currentGain = LiveAudioGainOptions.ManualGainLevelToMultiplier(_options.ManualGainLevel);
    }

    public AudioGainProcessingResult Process(byte[] buffer, WaveFormat waveFormat)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(waveFormat);
        EnsurePcm16(waveFormat);

        if (buffer.Length == 0)
        {
            return new AudioGainProcessingResult(
                buffer,
                InputPeak: 0,
                OutputPeak: 0,
                GainMultiplier: _currentGain,
                IsAutomaticGainEnabled: _options.IsAutomaticGainEnabled);
        }

        AudioLevelSnapshot inputLevel = MeasurePcm16(buffer);
        double gain = ResolveGain(inputLevel);
        byte[] processed = ApplyGain(buffer, gain, out double outputPeak);

        return new AudioGainProcessingResult(
            processed,
            inputLevel.Peak,
            outputPeak,
            gain,
            _options.IsAutomaticGainEnabled);
    }

    private double ResolveGain(AudioLevelSnapshot inputLevel)
    {
        if (!_options.IsAutomaticGainEnabled)
        {
            _currentGain = LiveAudioGainOptions.ManualGainLevelToMultiplier(_options.ManualGainLevel);
            return _currentGain;
        }

        double baseGain = LiveAudioGainOptions.ManualGainLevelToMultiplier(LiveAudioGainOptions.DefaultManualGainLevel);
        if (inputLevel.Rms <= SilenceRmsLevel || inputLevel.Peak <= 0)
        {
            _currentGain += (baseGain - _currentGain) * GainDecreaseSmoothing;
            return _currentGain;
        }

        double desiredGain = TargetRmsLevel / inputLevel.Rms;
        desiredGain = Math.Min(desiredGain, TargetPeakLevel / inputLevel.Peak);
        desiredGain = Math.Clamp(desiredGain, MinimumAutomaticGain, MaximumAutomaticGain);

        double smoothing = desiredGain > _currentGain
            ? GainIncreaseSmoothing
            : GainDecreaseSmoothing;
        _currentGain += (desiredGain - _currentGain) * smoothing;
        if (!double.IsFinite(_currentGain) || _currentGain <= 0)
        {
            _currentGain = baseGain;
        }

        return _currentGain;
    }

    private static AudioLevelSnapshot MeasurePcm16(byte[] buffer)
    {
        int length = buffer.Length - (buffer.Length % 2);
        int sampleCount = length / 2;
        if (sampleCount == 0)
        {
            return new AudioLevelSnapshot(0, 0);
        }

        long peakMagnitude = 0;
        double sumSquares = 0;

        for (int index = 0; index < length; index += 2)
        {
            short sample = BitConverter.ToInt16(buffer, index);
            int magnitude = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
            peakMagnitude = Math.Max(peakMagnitude, magnitude);

            double normalized = sample / (double)short.MaxValue;
            sumSquares += normalized * normalized;
        }

        return new AudioLevelSnapshot(
            Peak: peakMagnitude / (double)short.MaxValue,
            Rms: Math.Sqrt(sumSquares / sampleCount));
    }

    private static byte[] ApplyGain(byte[] buffer, double gain, out double outputPeak)
    {
        byte[] output = new byte[buffer.Length];
        Buffer.BlockCopy(buffer, 0, output, 0, buffer.Length);

        int length = buffer.Length - (buffer.Length % 2);
        double peak = 0;
        for (int index = 0; index < length; index += 2)
        {
            short sample = BitConverter.ToInt16(buffer, index);
            double normalized = sample / (double)short.MaxValue;
            double limited = ApplySoftLimiter(normalized * gain);
            short processed = ToPcm16(limited);
            output[index] = (byte)(processed & 0xFF);
            output[index + 1] = (byte)((processed >> 8) & 0xFF);
            peak = Math.Max(peak, Math.Abs(limited));
        }

        outputPeak = Math.Clamp(peak, 0, 1);
        return output;
    }

    private static double ApplySoftLimiter(double value)
    {
        double magnitude = Math.Abs(value);
        if (magnitude <= 0.95)
        {
            return value;
        }

        double limited = 0.95 + (0.05 * Math.Tanh((magnitude - 0.95) / 0.05));
        return Math.CopySign(Math.Min(limited, 1), value);
    }

    private static short ToPcm16(double value)
    {
        double clamped = Math.Clamp(value, -1, 1);
        return (short)Math.Round(clamped * short.MaxValue);
    }

    private static void EnsurePcm16(WaveFormat waveFormat)
    {
        if (waveFormat.Encoding != WaveFormatEncoding.Pcm || waveFormat.BitsPerSample != 16)
        {
            throw new InvalidOperationException("Automatic gain requires 16-bit PCM audio.");
        }
    }

    private sealed record AudioLevelSnapshot(double Peak, double Rms);
}
