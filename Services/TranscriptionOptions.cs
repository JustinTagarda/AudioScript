namespace AudioScript.Services;

public sealed class TranscriptionOptions
{
    public const string DefaultPrompt =
        "This audio is a classroom recording captured on a mobile phone. The spoken language is mostly Cebuano with some English code-switching. Transcribe exactly what was spoken. Do not translate. Preserve the original language used by each speaker. Keep filler words and hesitations when audible. Use Cebuano spellings when the speech is Cebuano. If a word is unclear because of overlapping background speech or noise, keep the closest phonetic transcription instead of translating or rewriting it.";

    public string Prompt { get; set; } = DefaultPrompt;

    public string PlaybackLanguageHint { get; set; } = "ceb";
}
