namespace AudioTranscript.Engines;

public sealed class OpenAiOptions {
    public string ApiKey { get; set; } =
        Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;

    public Uri Endpoint { get; set; } = new("https://api.openai.com/v1/audio/transcriptions");

    public int TimeoutSeconds { get; set; } = 120;
}
