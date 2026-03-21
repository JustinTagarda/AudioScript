using System.Globalization;
using System.Text.Json;
using VoxTranscribe.Abstractions;

namespace VoxTranscribe.Services;

public sealed class OpenAiSpeakerDiarizationResponseParser {
    public SpeakerDiarizationResult Parse(string responseBody, string model) {
        if (string.IsNullOrWhiteSpace(responseBody)) {
            throw new InvalidOperationException("OpenAI speaker diarization response was empty.");
        }

        JsonDocument document;
        try {
            document = JsonDocument.Parse(responseBody);
        }
        catch (JsonException ex) {
            throw new InvalidOperationException("OpenAI speaker diarization response contained malformed JSON.", ex);
        }

        using (document) {
            JsonElement root = document.RootElement;
            IReadOnlyList<SpeakerDiarizationSegment> segments = ExtractSegments(root);

            if (segments.Count == 0) {
                throw new InvalidOperationException("OpenAI speaker diarization response did not include any speaker segments.");
            }

            string text = ExtractText(root, segments);
            TimeSpan? duration = ExtractDuration(root);

            return new SpeakerDiarizationResult(
                Text: text,
                Model: model,
                CreatedAt: DateTimeOffset.UtcNow,
                Duration: duration,
                Segments: segments);
        }
    }

    private static string ExtractText(
        JsonElement root,
        IReadOnlyList<SpeakerDiarizationSegment> segments) {
        if (root.ValueKind == JsonValueKind.Object) {
            if (root.TryGetProperty("text", out JsonElement textNode)
                && textNode.ValueKind == JsonValueKind.String) {
                string text = textNode.GetString()?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text)) {
                    return text;
                }
            }

            if (root.TryGetProperty("output_text", out JsonElement outputTextNode)
                && outputTextNode.ValueKind == JsonValueKind.String) {
                string text = outputTextNode.GetString()?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text)) {
                    return text;
                }
            }
        }

        return string.Join(
            Environment.NewLine,
            segments.Select(segment => $"{segment.Speaker}: {segment.Text}".Trim()));
    }

    private static TimeSpan? ExtractDuration(JsonElement root) {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("duration", out JsonElement durationNode)) {
            return null;
        }

        if (durationNode.ValueKind == JsonValueKind.Number
            && durationNode.TryGetDouble(out double seconds)
            && seconds >= 0) {
            return TimeSpan.FromSeconds(seconds);
        }

        if (durationNode.ValueKind == JsonValueKind.String
            && double.TryParse(
                durationNode.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsedSeconds)
            && parsedSeconds >= 0) {
            return TimeSpan.FromSeconds(parsedSeconds);
        }

        return null;
    }

    private static IReadOnlyList<SpeakerDiarizationSegment> ExtractSegments(JsonElement root) {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("segments", out JsonElement segmentsNode)
            || segmentsNode.ValueKind != JsonValueKind.Array) {
            return Array.Empty<SpeakerDiarizationSegment>();
        }

        var segments = new List<SpeakerDiarizationSegment>();

        foreach (JsonElement segmentNode in segmentsNode.EnumerateArray()) {
            if (!TryExtractSegment(segmentNode, out SpeakerDiarizationSegment? segment) || segment is null) {
                continue;
            }

            segments.Add(segment);
        }

        return segments;
    }

    private static bool TryExtractSegment(JsonElement node, out SpeakerDiarizationSegment? segment) {
        segment = null;

        if (node.ValueKind != JsonValueKind.Object
            || !node.TryGetProperty("speaker", out JsonElement speakerNode)
            || speakerNode.ValueKind != JsonValueKind.String
            || !node.TryGetProperty("text", out JsonElement textNode)
            || textNode.ValueKind != JsonValueKind.String
            || !node.TryGetProperty("start", out JsonElement startNode)
            || !TryReadNonNegativeSeconds(startNode, out double startSeconds)) {
            return false;
        }

        string speaker = speakerNode.GetString()?.Trim() ?? string.Empty;
        string text = textNode.GetString()?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(speaker) || string.IsNullOrWhiteSpace(text)) {
            return false;
        }

        double? endSeconds = null;
        if (node.TryGetProperty("end", out JsonElement endNode)
            && TryReadNonNegativeSeconds(endNode, out double parsedEndSeconds)) {
            endSeconds = parsedEndSeconds;
        }

        TimeSpan startOffset = TimeSpan.FromSeconds(startSeconds);
        TimeSpan? endOffset = endSeconds is null
            ? null
            : TimeSpan.FromSeconds(Math.Max(endSeconds.Value, startSeconds));

        segment = new SpeakerDiarizationSegment(
            Speaker: speaker,
            Text: text,
            StartOffset: startOffset,
            EndOffset: endOffset);
        return true;
    }

    private static bool TryReadNonNegativeSeconds(JsonElement node, out double seconds) {
        seconds = 0;

        if (node.ValueKind == JsonValueKind.Number && node.TryGetDouble(out seconds)) {
            return seconds >= 0;
        }

        if (node.ValueKind == JsonValueKind.String
            && double.TryParse(
                node.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out seconds)) {
            return seconds >= 0;
        }

        return false;
    }
}
