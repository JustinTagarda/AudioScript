using System.Globalization;
using System.IO;
using System.Text;
using NAudio.Wave;
using VoxTranscribe.Abstractions;
using VoxTranscribe.Audio;

namespace VoxTranscribe.Services;

public sealed class ChunkedSpeakerDiarizationService {
    private const long DirectUploadLimitBytes = 25_000_000;
    private const long ChunkUploadSafetyBytes = 24_000_000;
    private const int MaxKnownSpeakerReferences = 4;
    private const int MaxReferenceClipsPerSpeaker = 3;
    private static readonly TimeSpan DirectRequestMaxDuration = TimeSpan.FromSeconds(1400);
    private static readonly TimeSpan MinimumReferenceDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumReferenceDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ReferenceEdgePadding = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdealReferenceDuration = TimeSpan.FromSeconds(5);

    private readonly AudioStandardizer _audioStandardizer;
    private readonly SilenceIntervalDetector _silenceIntervalDetector;
    private readonly SilenceAwareChunkPlanner _chunkPlanner;
    private readonly WaveClipExtractor _waveClipExtractor;
    private readonly OpenAiSpeakerDiarizationService _requestService;
    private readonly ProcessLogService _processLogService;

    public ChunkedSpeakerDiarizationService(
        AudioStandardizer audioStandardizer,
        SilenceIntervalDetector silenceIntervalDetector,
        SilenceAwareChunkPlanner chunkPlanner,
        WaveClipExtractor waveClipExtractor,
        OpenAiSpeakerDiarizationService requestService,
        ProcessLogService processLogService) {
        _audioStandardizer = audioStandardizer;
        _silenceIntervalDetector = silenceIntervalDetector;
        _chunkPlanner = chunkPlanner;
        _waveClipExtractor = waveClipExtractor;
        _requestService = requestService;
        _processLogService = processLogService;
    }

    public async Task<SpeakerDiarizationResult> DiarizeAudioFileAsync(
        string audioFilePath,
        CancellationToken cancellationToken) {
        string fullPath = ValidateAudioFilePath(audioFilePath);
        var sourceInfo = new FileInfo(fullPath);
        TimeSpan sourceDuration = ResolveAudioDuration(fullPath);

        if (!RequiresChunking(sourceInfo.Length, sourceDuration)) {
            Log(
                $"Speaker diarization will use a single request for '{sourceInfo.Name}' " +
                $"({FormatDuration(sourceDuration)}, {sourceInfo.Length:N0} bytes).");
            return await _requestService.DiarizeAudioFileAsync(fullPath, cancellationToken);
        }

        Log(
            $"Speaker diarization will split '{sourceInfo.Name}' into chunked requests " +
            $"({FormatDuration(sourceDuration)}, {sourceInfo.Length:N0} bytes).");

        string standardizedWavePath = _audioStandardizer.ConvertFileToEngineWav(fullPath);
        var temporaryFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            standardizedWavePath,
        };

        try {
            IReadOnlyList<TimeSpanRange> silenceIntervals =
                _silenceIntervalDetector.DetectSilenceIntervals(standardizedWavePath);
            IReadOnlyList<DiarizationChunkPlan> chunkPlans =
                _chunkPlanner.PlanChunks(sourceDuration, silenceIntervals);

            Log(
                $"Prepared {chunkPlans.Count:N0} diarization chunk(s) " +
                $"from {silenceIntervals.Count:N0} detected silence interval(s).");

            var mergedSegments = new List<SpeakerDiarizationSegment>();
            var speakerProfiles = new Dictionary<string, SpeakerProfile>(StringComparer.OrdinalIgnoreCase);
            int nextSpeakerNumber = 1;
            DateTimeOffset createdAt = DateTimeOffset.UtcNow;

            for (int chunkIndex = 0; chunkIndex < chunkPlans.Count; chunkIndex++) {
                cancellationToken.ThrowIfCancellationRequested();

                DiarizationChunkPlan chunkPlan = chunkPlans[chunkIndex];
                string chunkFilePath = _waveClipExtractor.ExtractTemporaryWaveFile(
                    standardizedWavePath,
                    chunkPlan.RequestStart,
                    chunkPlan.RequestEnd,
                    $"diarize-chunk-{chunkPlan.Index + 1}");
                temporaryFiles.Add(chunkFilePath);

                IReadOnlyList<KnownSpeakerReference> knownSpeakerReferences =
                    BuildKnownSpeakerReferences(speakerProfiles);

                Log(
                    $"Submitting chunk {chunkIndex + 1}/{chunkPlans.Count} " +
                    $"[{FormatDuration(chunkPlan.RequestStart)} - {FormatDuration(chunkPlan.RequestEnd)}] " +
                    $"with {knownSpeakerReferences.Count:N0} known speaker reference(s).");

                SpeakerDiarizationResult chunkResult = await _requestService.DiarizeAudioFileAsync(
                    chunkFilePath,
                    knownSpeakerReferences,
                    cancellationToken);

                if (chunkIndex == 0) {
                    createdAt = chunkResult.CreatedAt;
                }

                IReadOnlyList<SpeakerDiarizationSegment> absoluteChunkSegments =
                    TranslateChunkToAbsoluteOffsets(chunkResult.Segments, chunkPlan.RequestStart);
                Dictionary<string, string> speakerMap = ResolveSpeakerMap(
                    absoluteChunkSegments,
                    chunkPlan,
                    mergedSegments,
                    speakerProfiles,
                    ref nextSpeakerNumber);
                IReadOnlyList<SpeakerDiarizationSegment> keepSegments = FilterAndMapSegments(
                    absoluteChunkSegments,
                    speakerMap,
                    chunkPlan,
                    isLastChunk: chunkIndex == chunkPlans.Count - 1);

                mergedSegments.AddRange(keepSegments);
                UpdateSpeakerProfiles(
                    standardizedWavePath,
                    absoluteChunkSegments,
                    speakerMap,
                    chunkPlan,
                    speakerProfiles,
                    temporaryFiles);

                Log(
                    $"Chunk {chunkIndex + 1}/{chunkPlans.Count} produced {absoluteChunkSegments.Count:N0} segment(s); " +
                    $"{keepSegments.Count:N0} kept after overlap merge.");
            }

            IReadOnlyList<SpeakerDiarizationSegment> orderedSegments = mergedSegments
                .OrderBy(segment => segment.StartOffset)
                .ToArray();

            return new SpeakerDiarizationResult(
                Text: BuildResultText(orderedSegments),
                Model: OpenAiTranscriptionModelCatalog.Gpt4oTranscribeDiarize,
                CreatedAt: createdAt,
                Duration: sourceDuration,
                Segments: orderedSegments);
        }
        finally {
            CleanupTemporaryFiles(temporaryFiles);
        }
    }

    public static SilenceAwareChunkPlannerOptions BuildRecommendedChunkPlannerOptions() {
        double maxSecondsByUploadSize =
            Math.Floor((ChunkUploadSafetyBytes - 44d) / AudioFormatConstants.EngineWaveFormat.AverageBytesPerSecond);
        double boundedMaxSeconds = Math.Min(maxSecondsByUploadSize, DirectRequestMaxDuration.TotalSeconds);
        TimeSpan maximumChunkDuration = TimeSpan.FromSeconds(Math.Max(420, boundedMaxSeconds));
        TimeSpan targetChunkDuration = maximumChunkDuration - TimeSpan.FromSeconds(90);
        if (targetChunkDuration < TimeSpan.FromMinutes(6)) {
            targetChunkDuration = maximumChunkDuration;
        }

        return new SilenceAwareChunkPlannerOptions(
            TargetChunkDuration: targetChunkDuration,
            MinimumChunkDuration: TimeSpan.FromMinutes(5),
            MaximumChunkDuration: maximumChunkDuration,
            OverlapDuration: TimeSpan.FromSeconds(10),
            SearchBeforePreferredSplit: TimeSpan.FromSeconds(90),
            SearchAfterPreferredSplit: TimeSpan.FromSeconds(30),
            MinimumSilenceDuration: TimeSpan.FromMilliseconds(450));
    }

    private void UpdateSpeakerProfiles(
        string standardizedWavePath,
        IReadOnlyList<SpeakerDiarizationSegment> absoluteSegments,
        IReadOnlyDictionary<string, string> speakerMap,
        DiarizationChunkPlan chunkPlan,
        IDictionary<string, SpeakerProfile> speakerProfiles,
        ISet<string> temporaryFiles) {
        IEnumerable<ReferenceCandidate> candidates = absoluteSegments
            .Select(segment => CreateReferenceCandidate(segment, speakerMap, chunkPlan))
            .Where(candidate => candidate is not null)
            .Cast<ReferenceCandidate>()
            .GroupBy(candidate => candidate.GlobalSpeakerId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(candidate => candidate.Score)
                .ThenByDescending(candidate => candidate.ClipRange.End)
                .First());

        foreach (ReferenceCandidate candidate in candidates) {
            if (!speakerProfiles.TryGetValue(candidate.GlobalSpeakerId, out SpeakerProfile? profile)) {
                profile = new SpeakerProfile(candidate.GlobalSpeakerId);
                speakerProfiles[candidate.GlobalSpeakerId] = profile;
            }

            string clipPath = _waveClipExtractor.ExtractTemporaryWaveFile(
                standardizedWavePath,
                candidate.ClipRange.Start,
                candidate.ClipRange.End,
                candidate.GlobalSpeakerId);
            temporaryFiles.Add(clipPath);

            profile.LastSeen = candidate.ClipRange.End;
            profile.ReferenceClips.Add(new SpeakerReferenceClip(
                AudioFilePath: clipPath,
                Score: candidate.Score,
                RecordedAt: candidate.ClipRange.End));

            if (profile.ReferenceClips.Count > MaxReferenceClipsPerSpeaker) {
                List<SpeakerReferenceClip> retained = profile.ReferenceClips
                    .OrderByDescending(reference => reference.Score)
                    .ThenByDescending(reference => reference.RecordedAt)
                    .Take(MaxReferenceClipsPerSpeaker)
                    .ToList();
                profile.ReferenceClips.Clear();
                profile.ReferenceClips.AddRange(retained);
            }
        }
    }

    private ReferenceCandidate? CreateReferenceCandidate(
        SpeakerDiarizationSegment segment,
        IReadOnlyDictionary<string, string> speakerMap,
        DiarizationChunkPlan chunkPlan) {
        string localSpeaker = NormalizeSpeakerKey(segment.Speaker);
        if (string.IsNullOrWhiteSpace(localSpeaker)
            || !speakerMap.TryGetValue(localSpeaker, out string? globalSpeakerId)
            || segment.EndOffset is null) {
            return null;
        }

        TimeSpan midpoint = ResolveMidpoint(segment);
        bool withinKeepRange = midpoint >= chunkPlan.KeepStart && midpoint <= chunkPlan.KeepEnd;
        if (!withinKeepRange) {
            return null;
        }

        TimeSpan start = segment.StartOffset;
        TimeSpan end = segment.EndOffset.Value;
        if (end <= start) {
            return null;
        }

        TimeSpan clipDuration = end - start;
        if (clipDuration < MinimumReferenceDuration) {
            return null;
        }

        if (clipDuration > MaximumReferenceDuration) {
            TimeSpan trim = clipDuration - MaximumReferenceDuration;
            start += TimeSpan.FromTicks(trim.Ticks / 2);
            end = start + MaximumReferenceDuration;
            clipDuration = end - start;
        }

        TimeSpan minimumStart = chunkPlan.RequestStart + ReferenceEdgePadding;
        TimeSpan maximumEnd = chunkPlan.RequestEnd - ReferenceEdgePadding;
        if (maximumEnd <= minimumStart) {
            return null;
        }

        if (start < minimumStart) {
            TimeSpan shift = minimumStart - start;
            start += shift;
            end += shift;
        }

        if (end > maximumEnd) {
            TimeSpan shift = end - maximumEnd;
            start -= shift;
            end -= shift;
        }

        if (start < chunkPlan.RequestStart || end > chunkPlan.RequestEnd || end <= start) {
            return null;
        }

        clipDuration = end - start;
        if (clipDuration < MinimumReferenceDuration) {
            return null;
        }

        double durationPenalty = Math.Abs((clipDuration - IdealReferenceDuration).TotalSeconds);
        double edgeDistanceSeconds = Math.Min(
            (start - chunkPlan.RequestStart).TotalSeconds,
            (chunkPlan.RequestEnd - end).TotalSeconds);
        double score = 100
            - (durationPenalty * 10)
            + Math.Min(edgeDistanceSeconds, 12)
            + Math.Min((segment.Text?.Length ?? 0) / 8d, 6);

        return new ReferenceCandidate(
            GlobalSpeakerId: globalSpeakerId,
            ClipRange: new TimeSpanRange(start, end),
            Score: score);
    }

    private IReadOnlyList<KnownSpeakerReference> BuildKnownSpeakerReferences(
        IReadOnlyDictionary<string, SpeakerProfile> speakerProfiles) {
        return speakerProfiles.Values
            .Where(profile => profile.ReferenceClips.Count > 0)
            .OrderByDescending(profile => profile.LastSeen)
            .Take(MaxKnownSpeakerReferences)
            .Select(profile => {
                SpeakerReferenceClip clip = profile.ReferenceClips
                    .OrderByDescending(reference => reference.Score)
                    .ThenByDescending(reference => reference.RecordedAt)
                    .First();

                return new KnownSpeakerReference(profile.GlobalSpeakerId, clip.AudioFilePath);
            })
            .ToArray();
    }

    private Dictionary<string, string> ResolveSpeakerMap(
        IReadOnlyList<SpeakerDiarizationSegment> absoluteSegments,
        DiarizationChunkPlan chunkPlan,
        IReadOnlyList<SpeakerDiarizationSegment> mergedSegments,
        IDictionary<string, SpeakerProfile> speakerProfiles,
        ref int nextSpeakerNumber) {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] localSpeakers = absoluteSegments
            .Select(segment => NormalizeSpeakerKey(segment.Speaker))
            .Where(speaker => !string.IsNullOrWhiteSpace(speaker))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (string localSpeaker in localSpeakers) {
            if (speakerProfiles.ContainsKey(localSpeaker)) {
                map[localSpeaker] = localSpeaker;
            }
        }

        foreach (KeyValuePair<(string LocalSpeaker, string GlobalSpeaker), int> entry in BuildOverlapScores(
                     absoluteSegments,
                     chunkPlan,
                     mergedSegments)
                 .OrderByDescending(entry => entry.Value)
                 .ThenBy(entry => entry.Key.LocalSpeaker, StringComparer.OrdinalIgnoreCase)
                 .ThenBy(entry => entry.Key.GlobalSpeaker, StringComparer.OrdinalIgnoreCase)) {
            (string LocalSpeaker, string GlobalSpeaker) key = entry.Key;
            int score = entry.Value;
            if (score < 4
                || map.ContainsKey(key.LocalSpeaker)
                || map.Values.Contains(key.GlobalSpeaker, StringComparer.OrdinalIgnoreCase)) {
                continue;
            }

            map[key.LocalSpeaker] = key.GlobalSpeaker;
        }

        foreach (string localSpeaker in localSpeakers) {
            if (map.ContainsKey(localSpeaker)) {
                continue;
            }

            string globalSpeakerId = $"speaker_{nextSpeakerNumber.ToString(CultureInfo.InvariantCulture)}";
            nextSpeakerNumber++;

            map[localSpeaker] = globalSpeakerId;
            speakerProfiles[globalSpeakerId] = new SpeakerProfile(globalSpeakerId);
        }

        foreach (string globalSpeakerId in map.Values.Distinct(StringComparer.OrdinalIgnoreCase)) {
            if (!speakerProfiles.ContainsKey(globalSpeakerId)) {
                speakerProfiles[globalSpeakerId] = new SpeakerProfile(globalSpeakerId);
            }
        }

        return map;
    }

    private Dictionary<(string LocalSpeaker, string GlobalSpeaker), int> BuildOverlapScores(
        IReadOnlyList<SpeakerDiarizationSegment> absoluteSegments,
        DiarizationChunkPlan chunkPlan,
        IReadOnlyList<SpeakerDiarizationSegment> mergedSegments) {
        var scores = new Dictionary<(string LocalSpeaker, string GlobalSpeaker), int>();

        if (chunkPlan.KeepStart <= chunkPlan.RequestStart || mergedSegments.Count == 0) {
            return scores;
        }

        TimeSpan overlapStart = chunkPlan.RequestStart;
        TimeSpan overlapEnd = chunkPlan.KeepStart;

        SpeakerDiarizationSegment[] previousOverlapSegments = mergedSegments
            .Where(segment => ResolveComparableEnd(segment) > overlapStart && segment.StartOffset < overlapEnd)
            .OrderBy(segment => segment.StartOffset)
            .ToArray();
        SpeakerDiarizationSegment[] currentOverlapSegments = absoluteSegments
            .Where(segment => ResolveComparableEnd(segment) > overlapStart && segment.StartOffset < overlapEnd)
            .OrderBy(segment => segment.StartOffset)
            .ToArray();

        foreach (SpeakerDiarizationSegment current in currentOverlapSegments) {
            string localSpeaker = NormalizeSpeakerKey(current.Speaker);
            if (string.IsNullOrWhiteSpace(localSpeaker)) {
                continue;
            }

            foreach (SpeakerDiarizationSegment previous in previousOverlapSegments) {
                int score = ScoreOverlapMatch(previous, current);
                if (score <= 0) {
                    continue;
                }

                (string LocalSpeaker, string GlobalSpeaker) key = (localSpeaker, previous.Speaker);
                if (scores.TryGetValue(key, out int existingScore)) {
                    scores[key] = existingScore + score;
                }
                else {
                    scores[key] = score;
                }
            }
        }

        return scores;
    }

    private static int ScoreOverlapMatch(
        SpeakerDiarizationSegment previous,
        SpeakerDiarizationSegment current) {
        string previousText = NormalizeText(previous.Text);
        string currentText = NormalizeText(current.Text);

        double overlapSeconds = ResolveOverlapSeconds(previous, current);
        double startDifferenceSeconds = Math.Abs((current.StartOffset - previous.StartOffset).TotalSeconds);
        double endDifferenceSeconds = Math.Abs((ResolveComparableEnd(current) - ResolveComparableEnd(previous)).TotalSeconds);

        int score = 0;
        if (overlapSeconds >= 0.25) {
            score += 3;
        }

        if (startDifferenceSeconds <= 1.5) {
            score += 2;
        }

        if (endDifferenceSeconds <= 1.5) {
            score += 1;
        }

        if (!string.IsNullOrWhiteSpace(previousText) && !string.IsNullOrWhiteSpace(currentText)) {
            if (string.Equals(previousText, currentText, StringComparison.Ordinal)) {
                score += 4;
            }
            else if (previousText.Contains(currentText, StringComparison.Ordinal)
                     || currentText.Contains(previousText, StringComparison.Ordinal)) {
                score += 2;
            }
            else if (CountSharedWords(previousText, currentText) >= 3) {
                score += 1;
            }
        }

        return score;
    }

    private static IReadOnlyList<SpeakerDiarizationSegment> FilterAndMapSegments(
        IReadOnlyList<SpeakerDiarizationSegment> absoluteSegments,
        IReadOnlyDictionary<string, string> speakerMap,
        DiarizationChunkPlan chunkPlan,
        bool isLastChunk) {
        var results = new List<SpeakerDiarizationSegment>();

        foreach (SpeakerDiarizationSegment absoluteSegment in absoluteSegments.OrderBy(segment => segment.StartOffset)) {
            TimeSpan midpoint = ResolveMidpoint(absoluteSegment);
            if (midpoint < chunkPlan.KeepStart) {
                continue;
            }

            if ((!isLastChunk && midpoint >= chunkPlan.KeepEnd)
                || (isLastChunk && midpoint > chunkPlan.KeepEnd)) {
                continue;
            }

            string localSpeaker = NormalizeSpeakerKey(absoluteSegment.Speaker);
            if (string.IsNullOrWhiteSpace(localSpeaker)
                || !speakerMap.TryGetValue(localSpeaker, out string? globalSpeakerId)) {
                continue;
            }

            results.Add(new SpeakerDiarizationSegment(
                Speaker: globalSpeakerId,
                Text: absoluteSegment.Text,
                StartOffset: absoluteSegment.StartOffset,
                EndOffset: absoluteSegment.EndOffset));
        }

        return results;
    }

    private static IReadOnlyList<SpeakerDiarizationSegment> TranslateChunkToAbsoluteOffsets(
        IReadOnlyList<SpeakerDiarizationSegment> chunkSegments,
        TimeSpan chunkRequestStart) {
        return chunkSegments
            .Select(segment => new SpeakerDiarizationSegment(
                Speaker: NormalizeSpeakerKey(segment.Speaker),
                Text: segment.Text?.Trim() ?? string.Empty,
                StartOffset: chunkRequestStart + segment.StartOffset,
                EndOffset: segment.EndOffset is null
                    ? null
                    : chunkRequestStart + segment.EndOffset.Value))
            .Where(segment => !string.IsNullOrWhiteSpace(segment.Speaker) && !string.IsNullOrWhiteSpace(segment.Text))
            .ToArray();
    }

    private static double ResolveOverlapSeconds(
        SpeakerDiarizationSegment left,
        SpeakerDiarizationSegment right) {
        TimeSpan overlapStart = left.StartOffset > right.StartOffset
            ? left.StartOffset
            : right.StartOffset;
        TimeSpan overlapEnd = ResolveComparableEnd(left) < ResolveComparableEnd(right)
            ? ResolveComparableEnd(left)
            : ResolveComparableEnd(right);

        return overlapEnd <= overlapStart
            ? 0
            : (overlapEnd - overlapStart).TotalSeconds;
    }

    private static int CountSharedWords(string left, string right) {
        string[] leftWords = left
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] rightWords = right
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (leftWords.Length == 0 || rightWords.Length == 0) {
            return 0;
        }

        var rightWordSet = new HashSet<string>(rightWords, StringComparer.Ordinal);
        int shared = 0;
        foreach (string word in leftWords.Distinct(StringComparer.Ordinal)) {
            if (rightWordSet.Contains(word)) {
                shared++;
            }
        }

        return shared;
    }

    private static TimeSpan ResolveMidpoint(SpeakerDiarizationSegment segment) {
        TimeSpan end = ResolveComparableEnd(segment);
        if (end <= segment.StartOffset) {
            return segment.StartOffset;
        }

        return segment.StartOffset + TimeSpan.FromTicks((end - segment.StartOffset).Ticks / 2);
    }

    private static TimeSpan ResolveComparableEnd(SpeakerDiarizationSegment segment) {
        return segment.EndOffset is null || segment.EndOffset <= segment.StartOffset
            ? segment.StartOffset
            : segment.EndOffset.Value;
    }

    private static string NormalizeSpeakerKey(string? speaker) {
        return string.IsNullOrWhiteSpace(speaker)
            ? string.Empty
            : speaker.Trim();
    }

    private static string NormalizeText(string? text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (char character in text.Trim().ToLowerInvariant()) {
            builder.Append(char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) ? character : ' ');
        }

        return string.Join(
            ' ',
            builder.ToString()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string BuildResultText(IReadOnlyList<SpeakerDiarizationSegment> orderedSegments) {
        return string.Join(
            Environment.NewLine,
            orderedSegments.Select(segment => $"{segment.Speaker}: {segment.Text}".Trim()));
    }

    private static TimeSpan ResolveAudioDuration(string audioFilePath) {
        using var reader = new AudioFileReader(audioFilePath);
        return reader.TotalTime;
    }

    private static bool RequiresChunking(long fileSizeBytes, TimeSpan duration) {
        return fileSizeBytes > DirectUploadLimitBytes || duration > DirectRequestMaxDuration;
    }

    private static string ValidateAudioFilePath(string audioFilePath) {
        if (string.IsNullOrWhiteSpace(audioFilePath)) {
            throw new ArgumentException("Audio file path is required.", nameof(audioFilePath));
        }

        string fullPath = Path.GetFullPath(audioFilePath.Trim());
        if (!File.Exists(fullPath)) {
            throw new FileNotFoundException("Audio file was not found.", fullPath);
        }

        return fullPath;
    }

    private void CleanupTemporaryFiles(IEnumerable<string> temporaryFiles) {
        foreach (string temporaryFile in temporaryFiles.Distinct(StringComparer.OrdinalIgnoreCase)) {
            try {
                if (File.Exists(temporaryFile)) {
                    File.Delete(temporaryFile);
                }
            }
            catch (Exception ex) {
                Log($"Temporary file cleanup skipped for '{Path.GetFileName(temporaryFile)}': {ex.Message}");
            }
        }
    }

    private void Log(string message) {
        _processLogService.Log("SpeakerDiarization", message);
    }

    private static string FormatDuration(TimeSpan duration) {
        return duration.TotalHours >= 1
            ? duration.ToString(@"hh\:mm\:ss")
            : duration.ToString(@"mm\:ss");
    }

    private sealed class SpeakerProfile {
        public SpeakerProfile(string globalSpeakerId) {
            GlobalSpeakerId = globalSpeakerId;
        }

        public string GlobalSpeakerId { get; }

        public List<SpeakerReferenceClip> ReferenceClips { get; } = [];

        public TimeSpan LastSeen { get; set; }
    }

    private sealed record SpeakerReferenceClip(
        string AudioFilePath,
        double Score,
        TimeSpan RecordedAt
    );

    private sealed record ReferenceCandidate(
        string GlobalSpeakerId,
        TimeSpanRange ClipRange,
        double Score
    );
}
