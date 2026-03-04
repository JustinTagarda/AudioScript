using System.IO;
using System.Buffers;
using NAudio.Wave;

namespace AudioTranscript.Audio;

public sealed class AudioStandardizer {
    public string ConvertFileToEngineWav(string sourceFilePath) {
        if (!File.Exists(sourceFilePath)) {
            throw new FileNotFoundException("Audio file not found.", sourceFilePath);
        }

        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"audiotranscript-{Guid.NewGuid():N}.wav");

        using var reader = new AudioFileReader(sourceFilePath);
        using var resampler = new MediaFoundationResampler(reader, AudioFormatConstants.EngineWaveFormat) {
            ResamplerQuality = 60,
        };

        WaveFileWriter.CreateWaveFile(tempPath, resampler);
        return tempPath;
    }

    public byte[] NormalizeBuffer(ReadOnlySpan<byte> buffer, WaveFormat sourceFormat) {
        if (sourceFormat.Encoding == WaveFormatEncoding.Pcm
            && sourceFormat.SampleRate == AudioFormatConstants.EngineWaveFormat.SampleRate
            && sourceFormat.BitsPerSample == AudioFormatConstants.EngineWaveFormat.BitsPerSample
            && sourceFormat.Channels == AudioFormatConstants.EngineWaveFormat.Channels) {
            return buffer.ToArray();
        }

        var sourceBytes = buffer.ToArray();

        using var sourceStream = new MemoryStream(sourceBytes, writable: false);
        using var rawSource = new RawSourceWaveStream(sourceStream, sourceFormat);
        using var resampler = new MediaFoundationResampler(rawSource, AudioFormatConstants.EngineWaveFormat) {
            ResamplerQuality = 60,
        };
        using var targetStream = new MemoryStream();

        var scratch = ArrayPool<byte>.Shared.Rent(4096);

        try {
            int read;
            while ((read = resampler.Read(scratch, 0, scratch.Length)) > 0) {
                targetStream.Write(scratch, 0, read);
            }
        }
        finally {
            ArrayPool<byte>.Shared.Return(scratch);
        }

        return targetStream.ToArray();
    }
}
