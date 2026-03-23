using System.IO;
using NAudio.Wave;

namespace AudioScript.Audio;

public sealed class WaveClipExtractor {
    public string ExtractTemporaryWaveFile(
        string sourceWavePath,
        TimeSpan start,
        TimeSpan end,
        string fileLabel) {
        if (string.IsNullOrWhiteSpace(sourceWavePath)) {
            throw new ArgumentException("Source wave path is required.", nameof(sourceWavePath));
        }

        string fullPath = Path.GetFullPath(sourceWavePath.Trim());
        if (!File.Exists(fullPath)) {
            throw new FileNotFoundException("Source wave file was not found.", fullPath);
        }

        TimeSpan normalizedStart = start < TimeSpan.Zero ? TimeSpan.Zero : start;
        TimeSpan normalizedEnd = end < normalizedStart ? normalizedStart : end;
        if (normalizedEnd <= normalizedStart) {
            throw new InvalidOperationException("Audio clip start time must be earlier than the end time.");
        }

        string tempPath = Path.Combine(
            Path.GetTempPath(),
            $"AudioScript-{SanitizeLabel(fileLabel)}-{Guid.NewGuid():N}.wav");

        using var reader = new WaveFileReader(fullPath);
        long startPosition = ResolveBytePosition(reader.WaveFormat, normalizedStart, reader.Length);
        long endPosition = ResolveBytePosition(reader.WaveFormat, normalizedEnd, reader.Length);

        if (endPosition <= startPosition) {
            throw new InvalidOperationException("Audio clip range produced an empty wave slice.");
        }

        reader.Position = startPosition;
        using var writer = new WaveFileWriter(tempPath, reader.WaveFormat);

        byte[] buffer = new byte[81920];
        while (reader.Position < endPosition) {
            int bytesToRead = (int)Math.Min(buffer.Length, endPosition - reader.Position);
            int bytesRead = reader.Read(buffer, 0, bytesToRead);
            if (bytesRead <= 0) {
                break;
            }

            writer.Write(buffer, 0, bytesRead);
        }

        return tempPath;
    }

    private static long ResolveBytePosition(WaveFormat format, TimeSpan offset, long maxLength) {
        long rawPosition = (long)Math.Round(
            offset.TotalSeconds * format.AverageBytesPerSecond,
            MidpointRounding.AwayFromZero);
        rawPosition = Math.Clamp(rawPosition, 0, maxLength);

        int remainder = (int)(rawPosition % format.BlockAlign);
        if (remainder != 0) {
            rawPosition -= remainder;
        }

        return Math.Clamp(rawPosition, 0, maxLength);
    }

    private static string SanitizeLabel(string fileLabel) {
        if (string.IsNullOrWhiteSpace(fileLabel)) {
            return "clip";
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        char[] sanitized = fileLabel
            .Trim()
            .Select(character => invalidChars.Contains(character) ? '-' : character)
            .ToArray();
        string value = new string(sanitized);
        return string.IsNullOrWhiteSpace(value) ? "clip" : value;
    }
}

