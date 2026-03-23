namespace AudioScript.Abstractions;

public sealed record DiarizationChunkPlan(
    int Index,
    TimeSpan RequestStart,
    TimeSpan RequestEnd,
    TimeSpan KeepStart,
    TimeSpan KeepEnd
) {
    public TimeSpan RequestDuration => RequestEnd - RequestStart;

    public TimeSpan KeepDuration => KeepEnd - KeepStart;
}

