using System.IO;
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
}
