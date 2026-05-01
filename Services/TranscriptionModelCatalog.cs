using AudioScript.Abstractions;

namespace AudioScript.Services;

public static class TranscriptionModelCatalog
{
    public const string ManualTranscription = "manual-transcription";
    public const string WhisperSmall = "whisper-small";
    public const string WhisperMedium = "whisper-medium";
    public const string WhisperLargeV3 = "whisper-large-v3";
    public const string WhisperLargeV3Turbo = "whisper-large-v3-turbo";

    private static readonly TranscriptionModelOption ManualModel = new(
        Id: ManualTranscription,
        DisplayName: "No AI assist: Manual transcription",
        SupportsFileTranscription: false,
        SupportsPlaybackTranscription: false,
        SupportsSpeakerDiarization: false);

    private static readonly TranscriptionModelOption LocalWhisperSmallModel = new(
        Id: WhisperSmall,
        DisplayName: "Whisper small",
        IsLocal: true);

    private static readonly TranscriptionModelOption LocalWhisperMediumModel = new(
        Id: WhisperMedium,
        DisplayName: "Whisper medium",
        IsLocal: true);

    private static readonly TranscriptionModelOption LocalWhisperLargeModel = new(
        Id: WhisperLargeV3,
        DisplayName: "Whisper large-v3",
        IsLocal: true);

    private static readonly TranscriptionModelOption LocalWhisperLargeTurboModel = new(
        Id: WhisperLargeV3Turbo,
        DisplayName: "Whisper large-v3-turbo",
        IsLocal: true);

    private static readonly IReadOnlyList<TranscriptionModelOption> LocalWhisperModelOptions = new[] {
        LocalWhisperSmallModel,
        LocalWhisperMediumModel,
        LocalWhisperLargeModel,
        LocalWhisperLargeTurboModel,
    };

    private static readonly IReadOnlyList<TranscriptionModelOption> AvailableModels =
        LocalWhisperModelOptions.Concat(new[] { ManualModel }).ToArray();

    public static IReadOnlyList<TranscriptionModelOption> Models => AvailableModels;

    public static IReadOnlyList<TranscriptionModelOption> LocalWhisperModels => LocalWhisperModelOptions;

    public static bool IsRecognizedTranscriptionEngine(string model)
    {
        return AvailableModels.Any(option => string.Equals(option.Id, model?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static bool SupportsFileTranscription(string model)
    {
        return Find(model)?.SupportsFileTranscription == true;
    }

    public static bool SupportsPlaybackTranscription(string model)
    {
        return Find(model)?.SupportsPlaybackTranscription == true;
    }

    public static bool SupportsSpeakerDiarization(string model)
    {
        return Find(model)?.SupportsSpeakerDiarization == true;
    }

    public static bool IsLocalWhisper(string model)
    {
        string trimmed = model?.Trim() ?? string.Empty;
        return string.Equals(WhisperSmall, trimmed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(WhisperMedium, trimmed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(WhisperLargeV3, trimmed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(WhisperLargeV3Turbo, trimmed, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsManualOnly(string model)
    {
        return string.Equals(ManualTranscription, model?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool UsesAiAssist(string model)
    {
        return IsLocalWhisper(model);
    }

    public static TranscriptionModelOption? Find(string model)
    {
        string trimmed = model?.Trim() ?? string.Empty;
        return AvailableModels.FirstOrDefault(option =>
            string.Equals(option.Id, trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
