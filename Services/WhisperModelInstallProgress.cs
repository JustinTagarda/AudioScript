namespace AudioScript.Services;

public sealed record WhisperModelInstallProgress(
    string Status,
    long BytesReceived,
    long? TotalBytes,
    double Percent);

