namespace AudioScript.Services;

public enum WhisperModelVariant
{
    Small,
    Medium,
    LargeV3,
    LargeV3Turbo,
}

public sealed record WhisperEngineModelDefinition(
    string Id,
    string DisplayName,
    string FileName,
    string SizeText,
    string Description,
    string Benefits,
    string Notes,
    WhisperModelVariant? GgmlType,
    long? ExpectedBytes,
    bool IsBundled,
    bool IsFixedInstalled);
