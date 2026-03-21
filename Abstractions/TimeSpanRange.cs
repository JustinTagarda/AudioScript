namespace VoxTranscribe.Abstractions;

public sealed record TimeSpanRange(
    TimeSpan Start,
    TimeSpan End
) {
    public TimeSpanRange Normalize() {
        TimeSpan normalizedStart = Start < TimeSpan.Zero ? TimeSpan.Zero : Start;
        TimeSpan normalizedEnd = End < normalizedStart ? normalizedStart : End;
        return new TimeSpanRange(normalizedStart, normalizedEnd);
    }

    public TimeSpan Duration => End - Start;

    public TimeSpan Midpoint => Start + TimeSpan.FromTicks(Duration.Ticks / 2);
}
