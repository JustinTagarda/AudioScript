using System.IO;
using NAudio.Wave;

namespace VoxTranscriber.Audio;

public sealed class AudioStandardizer {
    public string ConvertFileToEngineWav(string sourceFilePath) {
        if (!File.Exists(sourceFilePath)) {
            throw new FileNotFoundException("Audio file not found.", sourceFilePath);
        }

        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"VoxTranscriber-{Guid.NewGuid():N}.wav");

        using var reader = new AudioFileReader(sourceFilePath);
        using var resampler = new MediaFoundationResampler(reader, AudioFormatConstants.EngineWaveFormat) {
            ResamplerQuality = 60,
        };

        WaveFileWriter.CreateWaveFile(tempPath, resampler);
        return tempPath;
    }

    public byte[] ConvertPcmBytesToEngineWav(byte[] pcmAudioBytes, WaveFormat sourceFormat) {
        ArgumentNullException.ThrowIfNull(pcmAudioBytes);
        ArgumentNullException.ThrowIfNull(sourceFormat);

        if (pcmAudioBytes.Length == 0) {
            throw new InvalidOperationException("Playback audio chunk was empty.");
        }

        using var rawStream = new RawSourceWaveStream(
            new MemoryStream(pcmAudioBytes, writable: false),
            sourceFormat);
        using var outputStream = new MemoryStream();

        if (WaveFormatsMatch(sourceFormat, AudioFormatConstants.EngineWaveFormat)) {
            WaveFileWriter.WriteWavFileToStream(outputStream, rawStream);
            return outputStream.ToArray();
        }

        using var resampler = new MediaFoundationResampler(rawStream, AudioFormatConstants.EngineWaveFormat) {
            ResamplerQuality = 60,
        };

        WaveFileWriter.WriteWavFileToStream(outputStream, resampler);
        return outputStream.ToArray();
    }

    private static bool WaveFormatsMatch(WaveFormat left, WaveFormat right) {
        return left.Encoding == right.Encoding
            && left.SampleRate == right.SampleRate
            && left.BitsPerSample == right.BitsPerSample
            && left.Channels == right.Channels
            && left.BlockAlign == right.BlockAlign
            && left.AverageBytesPerSecond == right.AverageBytesPerSecond;
    }
}


