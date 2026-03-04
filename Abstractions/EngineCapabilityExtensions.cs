namespace AudioTranscript.Abstractions;

public static class EngineCapabilityExtensions {
    public static IReadOnlyList<string> ToLabels(this EngineCapability capabilities) {
        var labels = new List<string>();

        if (capabilities.HasFlag(EngineCapability.Diarization)) {
            labels.Add("Diarization");
        }

        if (capabilities.HasFlag(EngineCapability.Timestamps)) {
            labels.Add("Timestamps");
        }

        if (capabilities.HasFlag(EngineCapability.Punctuation)) {
            labels.Add("Punctuation");
        }

        if (capabilities.HasFlag(EngineCapability.LanguageAutoDetect)) {
            labels.Add("Language Auto-Detect");
        }

        if (capabilities.HasFlag(EngineCapability.Offline)) {
            labels.Add("Offline");
        }

        return labels;
    }
}