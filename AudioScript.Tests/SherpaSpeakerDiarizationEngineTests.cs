using System.Text;
using AudioScript.Abstractions;
using AudioScript.Audio;
using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class SherpaSpeakerDiarizationEngineTests {
    [Fact]
    public async Task DiarizeAudioFileAsync_RunsWithBundledModels() {
        string audioPath = CreateSilentWaveFile(TimeSpan.FromSeconds(2));

        try {
            using var logs = new ProcessLogService();
            var engine = new SherpaSpeakerDiarizationEngine(
                new AudioStandardizer(),
                new SherpaDiarizationModelManager(),
                logs);

            IReadOnlyList<SpeakerDiarizationTurn> turns = await engine.DiarizeAudioFileAsync(
                audioPath,
                CancellationToken.None);

            Assert.NotNull(turns);
        }
        finally {
            File.Delete(audioPath);
        }
    }

    private static string CreateSilentWaveFile(TimeSpan duration) {
        string path = Path.Combine(Path.GetTempPath(), $"AudioScript-sherpa-smoke-{Guid.NewGuid():N}.wav");
        int sampleRate = 16000;
        short channels = 1;
        short bitsPerSample = 16;
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        int byteRate = sampleRate * blockAlign;
        long dataBytes = (long)Math.Ceiling(duration.TotalSeconds * byteRate);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write((int)(36 + dataBytes));
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
        writer.Write((int)dataBytes);
        stream.SetLength(44 + dataBytes);

        return path;
    }
}
