using VoxTranscriber.Abstractions;

namespace VoxTranscriber.Services;

public static class OpenAiTranscriptionModelCatalog {
    public const string ManualTranscription = "manual-transcription";
    public const string Gpt4oTranscribe = "gpt-4o-transcribe";
    public const string Gpt4oMiniTranscribe = "gpt-4o-mini-transcribe";
    public const string Gpt4oTranscribeDiarize = "gpt-4o-transcribe-diarize";

    private static readonly TranscriptionModelOption ManualModel = new(
        Id: ManualTranscription,
        DisplayName: "No AI assist: Manual transcription");

    private static readonly IReadOnlyList<TranscriptionModelOption> OnlineModels = new[] {
        new TranscriptionModelOption(
            Id: Gpt4oTranscribe,
            DisplayName: "Online: OpenAI gpt-4o-transcribe"),
        new TranscriptionModelOption(
            Id: Gpt4oMiniTranscribe,
            DisplayName: "Online: OpenAI gpt-4o-mini-transcribe"),
    };

    private static readonly IReadOnlyList<TranscriptionModelOption> AvailableModels =
        OnlineModels.Concat(new[] { ManualModel }).ToArray();

    public static IReadOnlyList<TranscriptionModelOption> Models => AvailableModels;

    public static bool IsSupported(string model) {
        return OnlineModels.Any(option => string.Equals(option.Id, model, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsManualOnly(string model) {
        return string.Equals(ManualTranscription, model?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool UsesAiAssist(string model) {
        return IsSupported(model);
    }
}


