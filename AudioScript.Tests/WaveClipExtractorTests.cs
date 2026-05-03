using System.Text;
using AudioScript.Audio;
using NAudio.Wave;
using Xunit;

namespace AudioScript.Tests;

public sealed class WaveClipExtractorTests
{
    [Fact]
    public void ExtractTemporaryWaveFile_WithLeadingSilence_PrependsSilenceBeforeRequestedAudio()
    {
        string sourcePath = CreateConstantWaveFile();
        string clipPath = string.Empty;

        try
        {
            var extractor = new WaveClipExtractor();

            clipPath = extractor.ExtractTemporaryWaveFile(
                sourcePath,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(500),
                "leading-silence-test",
                TimeSpan.FromMilliseconds(500));

            using var reader = new WaveFileReader(clipPath);
            byte[] data = new byte[(int)reader.Length];
            int bytesRead = reader.Read(data, 0, data.Length);

            int halfSecondBytes = reader.WaveFormat.AverageBytesPerSecond / 2;
            Assert.Equal(halfSecondBytes * 2, bytesRead);
            Assert.All(data.Take(halfSecondBytes), value => Assert.Equal(0, value));
            Assert.Contains(data.Skip(halfSecondBytes), value => value != 0);
        }
        finally
        {
            File.Delete(sourcePath);
            if (!string.IsNullOrWhiteSpace(clipPath))
            {
                File.Delete(clipPath);
            }
        }
    }

    private static string CreateConstantWaveFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"AudioScript-wave-clip-tests-{Guid.NewGuid():N}.wav");
        int sampleRate = 8000;
        short channels = 1;
        short bitsPerSample = 16;
        short bytesPerSample = (short)(bitsPerSample / 8);
        short blockAlign = (short)(channels * bytesPerSample);
        int byteRate = sampleRate * blockAlign;
        int sampleCount = sampleRate;
        int dataLength = sampleCount * blockAlign;

        using var writer = new BinaryWriter(File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None), Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        for (int index = 0; index < sampleCount; index++)
        {
            writer.Write((short)1000);
        }

        return path;
    }
}
