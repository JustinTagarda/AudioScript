namespace AudioScript.Services;

public sealed record WhisperModelUninstallResult(
    string ModelId,
    string DisplayName,
    string ModelPath,
    long DeletedBytes,
    bool WasDeleted);
