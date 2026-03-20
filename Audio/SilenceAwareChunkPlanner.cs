using VoxTranscriber.Abstractions;

namespace VoxTranscriber.Audio;

public sealed record SilenceAwareChunkPlannerOptions(
    TimeSpan TargetChunkDuration,
    TimeSpan MinimumChunkDuration,
    TimeSpan MaximumChunkDuration,
    TimeSpan OverlapDuration,
    TimeSpan SearchBeforePreferredSplit,
    TimeSpan SearchAfterPreferredSplit,
    TimeSpan MinimumSilenceDuration
) {
    public static SilenceAwareChunkPlannerOptions Default { get; } = new(
        TargetChunkDuration: TimeSpan.FromMinutes(17),
        MinimumChunkDuration: TimeSpan.FromMinutes(8),
        MaximumChunkDuration: TimeSpan.FromMinutes(20),
        OverlapDuration: TimeSpan.FromSeconds(10),
        SearchBeforePreferredSplit: TimeSpan.FromSeconds(90),
        SearchAfterPreferredSplit: TimeSpan.FromSeconds(30),
        MinimumSilenceDuration: TimeSpan.FromMilliseconds(450));
}

public sealed class SilenceAwareChunkPlanner {
    private readonly SilenceAwareChunkPlannerOptions _options;

    public SilenceAwareChunkPlanner(SilenceAwareChunkPlannerOptions? options = null) {
        _options = options ?? SilenceAwareChunkPlannerOptions.Default;
    }

    public IReadOnlyList<DiarizationChunkPlan> PlanChunks(
        TimeSpan duration,
        IReadOnlyList<TimeSpanRange> silenceIntervals) {
        if (duration <= TimeSpan.Zero) {
            throw new InvalidOperationException("Audio duration must be greater than zero.");
        }

        var normalizedSilences = silenceIntervals
            .Select(interval => interval.Normalize())
            .Where(interval => interval.Duration >= _options.MinimumSilenceDuration)
            .OrderBy(interval => interval.Start)
            .ToArray();

        if (duration <= _options.MaximumChunkDuration) {
            return new[] {
                new DiarizationChunkPlan(
                    Index: 0,
                    RequestStart: TimeSpan.Zero,
                    RequestEnd: duration,
                    KeepStart: TimeSpan.Zero,
                    KeepEnd: duration),
            };
        }

        var chunks = new List<DiarizationChunkPlan>();
        TimeSpan keepStart = TimeSpan.Zero;
        int chunkIndex = 0;

        while (keepStart < duration) {
            TimeSpan keepEnd = ResolveKeepEnd(keepStart, duration, normalizedSilences);
            if (keepEnd <= keepStart) {
                keepEnd = keepStart + _options.MaximumChunkDuration;
            }

            if (keepEnd > duration) {
                keepEnd = duration;
            }

            TimeSpan requestStart = chunkIndex == 0
                ? keepStart
                : keepStart - _options.OverlapDuration;
            if (requestStart < TimeSpan.Zero) {
                requestStart = TimeSpan.Zero;
            }

            TimeSpan requestEnd = keepEnd < duration
                ? keepEnd + _options.OverlapDuration
                : duration;
            if (requestEnd > duration) {
                requestEnd = duration;
            }

            chunks.Add(new DiarizationChunkPlan(
                Index: chunkIndex,
                RequestStart: requestStart,
                RequestEnd: requestEnd,
                KeepStart: keepStart,
                KeepEnd: keepEnd));

            keepStart = keepEnd;
            chunkIndex++;
        }

        return chunks;
    }

    private TimeSpan ResolveKeepEnd(
        TimeSpan keepStart,
        TimeSpan duration,
        IReadOnlyList<TimeSpanRange> silences) {
        TimeSpan remaining = duration - keepStart;
        if (remaining <= _options.MaximumChunkDuration) {
            return duration;
        }

        TimeSpan preferred = keepStart + _options.TargetChunkDuration;
        TimeSpan minimum = keepStart + _options.MinimumChunkDuration;
        TimeSpan maximum = keepStart + _options.MaximumChunkDuration;

        if (minimum > duration) {
            minimum = duration;
        }

        if (maximum > duration) {
            maximum = duration;
        }

        TimeSpan searchStart = preferred - _options.SearchBeforePreferredSplit;
        TimeSpan searchEnd = preferred + _options.SearchAfterPreferredSplit;
        if (searchStart < minimum) {
            searchStart = minimum;
        }

        if (searchEnd > maximum) {
            searchEnd = maximum;
        }

        TimeSpan? bestMidpoint = FindNearestSilenceMidpoint(
            silences,
            preferred,
            searchStart,
            searchEnd);

        if (bestMidpoint is null) {
            bestMidpoint = FindNearestSilenceMidpoint(
                silences,
                preferred,
                minimum,
                maximum);
        }

        return bestMidpoint ?? maximum;
    }

    private static TimeSpan? FindNearestSilenceMidpoint(
        IReadOnlyList<TimeSpanRange> silences,
        TimeSpan preferred,
        TimeSpan searchStart,
        TimeSpan searchEnd) {
        TimeSpan? best = null;
        long bestDistance = long.MaxValue;

        foreach (TimeSpanRange silence in silences) {
            TimeSpan midpoint = silence.Midpoint;
            if (midpoint < searchStart || midpoint > searchEnd) {
                continue;
            }

            long distance = Math.Abs((midpoint - preferred).Ticks);
            if (distance < bestDistance) {
                bestDistance = distance;
                best = midpoint;
            }
        }

        return best;
    }
}
