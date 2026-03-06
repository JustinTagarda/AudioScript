namespace AudioTranscript.Abstractions;

[Flags]
public enum EngineCapability {
    None = 0,
    Diarization = 1 << 0,
    Timestamps = 1 << 1,
    Punctuation = 1 << 2,
    LanguageAutoDetect = 1 << 3,
}
