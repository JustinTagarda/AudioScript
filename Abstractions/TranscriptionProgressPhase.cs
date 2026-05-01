namespace AudioScript.Abstractions;

public enum TranscriptionProgressPhase
{
    PreparingAudio,
    Chunking,
    TranscribingChunk,
    RunningSpeakerDiarization,
    MergingSpeakerLabels,
    MergingResults,
    Completed,
    Canceling,
}
