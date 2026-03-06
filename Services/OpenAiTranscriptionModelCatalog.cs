using AudioTranscript.Abstractions;

namespace AudioTranscript.Services;

public static class OpenAiTranscriptionModelCatalog {
    public const string Gpt4oTranscribe = "gpt-4o-transcribe";
    public const string Gpt4oMiniTranscribe = "gpt-4o-mini-transcribe";

    private static readonly IReadOnlyList<TranscriptionModelOption> AllModels = new[] {
        new TranscriptionModelOption(
            Id: Gpt4oTranscribe,
            DisplayName: "Online: OpenAI gpt-4o-transcribe"),
        new TranscriptionModelOption(
            Id: Gpt4oMiniTranscribe,
            DisplayName: "Online: OpenAI gpt-4o-mini-transcribe"),
    };

    public static IReadOnlyList<TranscriptionModelOption> Models => AllModels;

    public static bool IsSupported(string model) {
        return AllModels.Any(option => string.Equals(option.Id, model, StringComparison.OrdinalIgnoreCase));
    }
}
