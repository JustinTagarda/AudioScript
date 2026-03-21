using System.Globalization;
using System.Text.Json;
using VoxTranscribe.Abstractions;

namespace VoxTranscribe.Services;

public sealed class OpenAiTranscriptionResponseParser {
    public TranscriptionResult Parse(
        string responseBody,
        string model,
        double lowConfidenceThreshold) {
        if (string.IsNullOrWhiteSpace(responseBody)) {
            throw new InvalidOperationException("OpenAI transcription response was empty.");
        }

        JsonDocument document;
        try {
            document = JsonDocument.Parse(responseBody);
        }
        catch (JsonException ex) {
            throw new InvalidOperationException("OpenAI transcription response contained malformed JSON.", ex);
        }

        using (document) {
            JsonElement root = document.RootElement;
            string text = ExtractText(root);

            if (string.IsNullOrWhiteSpace(text)) {
                throw new InvalidOperationException("OpenAI transcription response did not include transcript text.");
            }

            TimeSpan? duration = ExtractDuration(root);
            IReadOnlyList<TranscriptionTokenLogprob> tokenLogprobs = ExtractTokenLogprobs(root);
            IReadOnlyList<TranscriptionTimedLine> timedLines = ExtractTimedLines(root);
            IReadOnlyList<LowConfidenceToken> lowConfidenceTokens = tokenLogprobs
                .Where(item => item.Logprob <= lowConfidenceThreshold)
                .Select(item => new LowConfidenceToken(item.Token, item.Logprob, item.Index))
                .ToArray();

            return new TranscriptionResult(
                Text: text.Trim(),
                Model: model,
                CreatedAt: DateTimeOffset.UtcNow,
                Duration: duration,
                TokenLogprobs: tokenLogprobs,
                LowConfidenceTokens: lowConfidenceTokens,
                TimedLines: timedLines);
        }
    }

    private static string ExtractText(JsonElement root) {
        if (root.ValueKind == JsonValueKind.Object) {
            if (root.TryGetProperty("text", out JsonElement textNode)
                && textNode.ValueKind == JsonValueKind.String) {
                return textNode.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("output_text", out JsonElement outputTextNode)
                && outputTextNode.ValueKind == JsonValueKind.String) {
                return outputTextNode.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static TimeSpan? ExtractDuration(JsonElement root) {
        if (root.ValueKind != JsonValueKind.Object) {
            return null;
        }

        if (!root.TryGetProperty("duration", out JsonElement durationNode)) {
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

    private static IReadOnlyList<TranscriptionTokenLogprob> ExtractTokenLogprobs(JsonElement root) {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("logprobs", out JsonElement logprobsNode)) {
            return Array.Empty<TranscriptionTokenLogprob>();
        }

        var collected = new List<TranscriptionTokenLogprob>();
        CollectTokenLogprobs(logprobsNode, collected, nextIndex: 0);
        return collected;
    }

    private static int CollectTokenLogprobs(
        JsonElement node,
        ICollection<TranscriptionTokenLogprob> output,
        int nextIndex) {
        switch (node.ValueKind) {
            case JsonValueKind.Array:
                foreach (JsonElement child in node.EnumerateArray()) {
                    nextIndex = CollectTokenLogprobs(child, output, nextIndex);
                }

                return nextIndex;

            case JsonValueKind.Object:
                if (TryExtractTokenAndLogprob(node, out string token, out double logprob)) {
                    output.Add(new TranscriptionTokenLogprob(token, logprob, nextIndex));
                    nextIndex++;
                    return nextIndex;
                }

                if (TryExtractParallelTokenArrays(node, output, ref nextIndex)) {
                    return nextIndex;
                }

                foreach (JsonProperty property in node.EnumerateObject()) {
                    nextIndex = CollectTokenLogprobs(property.Value, output, nextIndex);
                }

                return nextIndex;

            default:
                return nextIndex;
        }
    }

    private static bool TryExtractTokenAndLogprob(
        JsonElement node,
        out string token,
        out double logprob) {
        token = string.Empty;
        logprob = 0;

        if (!node.TryGetProperty("token", out JsonElement tokenNode)
            || tokenNode.ValueKind != JsonValueKind.String
            || !node.TryGetProperty("logprob", out JsonElement logprobNode)
            || !TryReadLogprob(logprobNode, out logprob)) {
            return false;
        }

        token = tokenNode.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(token);
    }

    private static bool TryExtractParallelTokenArrays(
        JsonElement node,
        ICollection<TranscriptionTokenLogprob> output,
        ref int nextIndex) {
        if (!node.TryGetProperty("tokens", out JsonElement tokensNode)
            || !node.TryGetProperty("token_logprobs", out JsonElement logprobsNode)
            || tokensNode.ValueKind != JsonValueKind.Array
            || logprobsNode.ValueKind != JsonValueKind.Array) {
            return false;
        }

        JsonElement.ArrayEnumerator tokenEnumerator = tokensNode.EnumerateArray();
        JsonElement.ArrayEnumerator logprobEnumerator = logprobsNode.EnumerateArray();

        while (tokenEnumerator.MoveNext() && logprobEnumerator.MoveNext()) {
            JsonElement tokenNode = tokenEnumerator.Current;
            JsonElement logprobNode = logprobEnumerator.Current;

            if (tokenNode.ValueKind != JsonValueKind.String
                || !TryReadLogprob(logprobNode, out double logprob)) {
                continue;
            }

            string token = tokenNode.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(token)) {
                continue;
            }

            output.Add(new TranscriptionTokenLogprob(token, logprob, nextIndex));
            nextIndex++;
        }

        return true;
    }

    private static bool TryReadLogprob(JsonElement node, out double logprob) {
        logprob = 0;

        if (node.ValueKind == JsonValueKind.Number && node.TryGetDouble(out logprob)) {
            return true;
        }

        if (node.ValueKind == JsonValueKind.String
            && double.TryParse(
                node.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out logprob)) {
            return true;
        }

        return false;
    }

    private static IReadOnlyList<TranscriptionTimedLine> ExtractTimedLines(JsonElement root) {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("segments", out JsonElement segmentsNode)
            || segmentsNode.ValueKind != JsonValueKind.Array) {
            return Array.Empty<TranscriptionTimedLine>();
        }

        var lines = new List<TranscriptionTimedLine>();

        foreach (JsonElement segmentNode in segmentsNode.EnumerateArray()) {
            if (segmentNode.ValueKind != JsonValueKind.Object) {
                continue;
            }

            if (!TryExtractSegment(segmentNode, out TranscriptionTimedLine? line) || line is null) {
                continue;
            }

            lines.Add(line);
        }

        return lines;
    }

    private static bool TryExtractSegment(JsonElement node, out TranscriptionTimedLine? line) {
        line = null;

        if (!node.TryGetProperty("text", out JsonElement textNode)
            || textNode.ValueKind != JsonValueKind.String) {
            return false;
        }

        string text = textNode.GetString()?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) {
            return false;
        }

        if (!node.TryGetProperty("start", out JsonElement startNode)
            || !TryReadNonNegativeSeconds(startNode, out double startSeconds)) {
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

        line = new TranscriptionTimedLine(
            Text: text,
            StartOffset: startOffset,
            EndOffset: endOffset,
            IsTimestampEstimated: false);
        return true;
    }

    private static bool TryReadNonNegativeSeconds(JsonElement node, out double seconds) {
        seconds = 0;

        if (!TryReadLogprob(node, out seconds)) {
            return false;
        }

        if (seconds < 0) {
            seconds = 0;
        }

        return true;
    }
}


