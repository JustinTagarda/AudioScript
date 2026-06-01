namespace AudioScript.Abstractions;

public sealed record AudioTranscriptionRequestOptions(
    bool SuppressPrompt = false,
    bool IsEngineWaveInput = false);
