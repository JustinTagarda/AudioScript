using System.Text.RegularExpressions;
using AudioTranscript.Abstractions;

namespace AudioTranscript.Services;

public sealed class TranscriptionSegmentMerger {
    private static readonly TimeSpan DuplicateStartTolerance = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DuplicateEndTolerance = TimeSpan.FromSeconds(1.5);

    public IReadOnlyList<TranscriptionTimedLine> Merge(IEnumerable<ChunkTranscriptionSegment> segments) {
        ArgumentNullException.ThrowIfNull(segments);

        var merged = new List<CanonicalSegment>();

        foreach (ChunkTranscriptionSegment segment in segments
                     .Where(IsUsable)
                     .OrderBy(item => item.AbsoluteStartOffset)
                     .ThenBy(item => item.AbsoluteEndOffset)
                     .ThenBy(item => item.ChunkIndex)) {
            int duplicateIndex = FindDuplicateIndex(merged, segment);

            if (duplicateIndex >= 0) {
                merged[duplicateIndex] = ChooseCanonical(merged[duplicateIndex], segment);
                continue;
            }

            merged.Add(new CanonicalSegment(
                ChunkIndex: segment.ChunkIndex,
                Text: segment.Text.Trim(),
                StartOffset: segment.AbsoluteStartOffset,
                EndOffset: segment.AbsoluteEndOffset));
        }

        return merged
            .OrderBy(item => item.StartOffset)
            .ThenBy(item => item.EndOffset)
            .Select(item => new TranscriptionTimedLine(
                Text: item.Text,
                StartOffset: item.StartOffset,
                EndOffset: item.EndOffset,
                IsTimestampEstimated: false))
            .ToArray();
    }

    private static int FindDuplicateIndex(IReadOnlyList<CanonicalSegment> merged, ChunkTranscriptionSegment candidate) {
        string normalizedCandidateText = NormalizeText(candidate.Text);

        for (int index = merged.Count - 1; index >= 0; index--) {
            CanonicalSegment existing = merged[index];

            if (candidate.AbsoluteStartOffset - existing.EndOffset > DuplicateEndTolerance) {
                break;
            }

            if (!LooksLikeDuplicate(existing, candidate, normalizedCandidateText)) {
                continue;
            }

            return index;
        }

        return -1;
    }

    private static bool LooksLikeDuplicate(
        CanonicalSegment existing,
        ChunkTranscriptionSegment candidate,
        string normalizedCandidateText) {
        string normalizedExistingText = NormalizeText(existing.Text);

        if (string.IsNullOrWhiteSpace(normalizedExistingText)
            || string.IsNullOrWhiteSpace(normalizedCandidateText)) {
            return false;
        }

        bool textMatches =
            string.Equals(normalizedExistingText, normalizedCandidateText, StringComparison.Ordinal)
            || ContainsLongerEquivalent(normalizedExistingText, normalizedCandidateText)
            || ContainsLongerEquivalent(normalizedCandidateText, normalizedExistingText);

        if (!textMatches) {
            return false;
        }

        TimeSpan overlap = Min(existing.EndOffset, candidate.AbsoluteEndOffset)
            - Max(existing.StartOffset, candidate.AbsoluteStartOffset);

        if (overlap > TimeSpan.Zero) {
            return true;
        }

        bool startsNearEachOther =
            Abs(existing.StartOffset - candidate.AbsoluteStartOffset) <= DuplicateStartTolerance;
        bool endsNearEachOther =
            Abs(existing.EndOffset - candidate.AbsoluteEndOffset) <= DuplicateEndTolerance;

        return startsNearEachOther && endsNearEachOther;
    }

    private static CanonicalSegment ChooseCanonical(CanonicalSegment existing, ChunkTranscriptionSegment candidate) {
        string normalizedExistingText = NormalizeText(existing.Text);
        string normalizedCandidateText = NormalizeText(candidate.Text);

        bool candidateContainsExisting = ContainsLongerEquivalent(normalizedCandidateText, normalizedExistingText);
        bool existingContainsCandidate = ContainsLongerEquivalent(normalizedExistingText, normalizedCandidateText);

        if (candidateContainsExisting && !existingContainsCandidate) {
            return new CanonicalSegment(
                ChunkIndex: candidate.ChunkIndex,
                Text: candidate.Text.Trim(),
                StartOffset: candidate.AbsoluteStartOffset,
                EndOffset: candidate.AbsoluteEndOffset);
        }

        if (existingContainsCandidate && !candidateContainsExisting) {
            return existing;
        }

        return existing;
    }

    private static bool IsUsable(ChunkTranscriptionSegment segment) {
        return !string.IsNullOrWhiteSpace(segment.Text)
            && segment.AbsoluteEndOffset >= segment.AbsoluteStartOffset;
    }

    private static string NormalizeText(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return string.Empty;
        }

        string lower = text.Trim().ToLowerInvariant();
        lower = Regex.Replace(lower, @"\s+", " ");
        lower = Regex.Replace(lower, @"[^\p{L}\p{N}\s]", string.Empty);
        return lower.Trim();
    }

    private static bool ContainsLongerEquivalent(string left, string right) {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) {
            return false;
        }

        return left.Length >= 12
            && right.Length >= 12
            && left.Contains(right, StringComparison.Ordinal);
    }

    private static TimeSpan Abs(TimeSpan value) => value < TimeSpan.Zero ? value.Negate() : value;

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;

    private sealed record CanonicalSegment(
        int ChunkIndex,
        string Text,
        TimeSpan StartOffset,
        TimeSpan EndOffset
    );
}

public sealed record ChunkTranscriptionSegment(
    int ChunkIndex,
    TimeSpan ChunkStartOffset,
    TimeSpan LocalStartOffset,
    TimeSpan LocalEndOffset,
    string Text
) {
    public TimeSpan AbsoluteStartOffset => ChunkStartOffset + LocalStartOffset;

    public TimeSpan AbsoluteEndOffset => ChunkStartOffset + LocalEndOffset;
}
