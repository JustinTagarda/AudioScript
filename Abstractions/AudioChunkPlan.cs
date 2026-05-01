namespace AudioScript.Abstractions;

public sealed record AudioChunkPlan(
    int Index,
    TimeSpan RequestStart,
    TimeSpan RequestEnd,
    TimeSpan KeepStart,
    TimeSpan KeepEnd
);
