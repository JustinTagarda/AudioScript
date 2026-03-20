namespace VoxTranscriber.Services;

public sealed class OpenAiTranscriptionOptions {
    public const string DefaultPrompt =
        "This audio is a classroom recording captured on a mobile phone. The spoken language is mostly Cebuano with some English code-switching. Transcribe exactly what was spoken. Do not translate. Preserve the original language used by each speaker. Keep filler words and hesitations when audible. Use Cebuano spellings when the speech is Cebuano. If a word is unclear because of overlapping background speech or noise, keep the closest phonetic transcription instead of translating or rewriting it.";

    public string ApiKey { get; set; } =
        Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;

    public Uri Endpoint { get; set; } = new("https://api.openai.com/v1/audio/transcriptions");

    public int TimeoutSeconds { get; set; } = 180;

    public int SpeakerDiarizationTimeoutSeconds { get; set; } = 600;

    public string Prompt { get; set; } = DefaultPrompt;

    public string PlaybackLanguageHint { get; set; } = "ceb";

    public string DiarizationChunkingStrategy { get; set; } = "auto";

    public double LowConfidenceLogprobThreshold { get; set; } = -1.0;
}


