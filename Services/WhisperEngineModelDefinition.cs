using Whisper.net.Ggml;

namespace AudioScript.Services;

public sealed record WhisperEngineModelDefinition(
    string Id,
    string DisplayName,
    string FileName,
    string SizeText,
    string Description,
    string Benefits,
    string Notes,
    GgmlType? GgmlType,
    long? ExpectedBytes,
    bool IsBundled,
    bool IsFixedInstalled);

