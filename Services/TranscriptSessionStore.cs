using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using AudioScript.Abstractions;
using NAudio.Wave;

namespace AudioScript.Services;

public sealed class TranscriptSessionStore {
    public const int CurrentSchemaVersion = 3;
    public const string LiveSessionAudioName = "Live Transcription";
    public const string LiveRecordingManifestRelativePath = "audio/live/manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
    };

    private readonly string _sessionsRootPath;
    private readonly ProcessLogService? _processLogService;

    public TranscriptSessionStore(string? sessionsRootPath = null, ProcessLogService? processLogService = null) {
        _sessionsRootPath = string.IsNullOrWhiteSpace(sessionsRootPath)
            ? AppDataPathProvider.Create().SessionsPath
            : Path.GetFullPath(sessionsRootPath);
        _processLogService = processLogService;
    }

    public TranscriptSessionLoadResult ImportAudioFile(string sourceFilePath) {
        Log($"ImportAudioFile started. source='{sourceFilePath}'.");
        try {
            string fullPath = ValidateAudioFilePath(sourceFilePath);
            string fileHash = ComputeSha256(fullPath);
            string sessionId = BuildSessionId(fileHash);
            string sessionDirectoryPath = GetSessionDirectoryPath(sessionId);
            string sessionFilePath = GetSessionFilePath(sessionId);
            string originalFileName = Path.GetFileName(fullPath);
            string storedRelativePath = string.IsNullOrWhiteSpace(originalFileName)
                ? "audio/imported-audio"
                : Path.Combine("audio", SanitizeFileName(originalFileName));
            string storedAudioPath = Path.Combine(sessionDirectoryPath, storedRelativePath);
            var sourceInfo = new FileInfo(fullPath);

            Log(
                $"ImportAudioFile validated source. fullPath='{fullPath}', size={sourceInfo.Length:N0}, " +
                $"sessionId='{sessionId}', sessionFile='{sessionFilePath}'.");

            Directory.CreateDirectory(Path.Combine(sessionDirectoryPath, "audio"));

            TranscriptSessionDocument document = File.Exists(sessionFilePath)
                ? LoadDocument(sessionFilePath)
                : CreateNewDocument(sessionId, originalFileName);

            if (!string.IsNullOrWhiteSpace(document.Audio.StoredRelativePath)) {
                storedRelativePath = document.Audio.StoredRelativePath;
                storedAudioPath = Path.Combine(sessionDirectoryPath, storedRelativePath);
            }
            else {
                document.Audio.StorageKind = AudioStorageKinds.ImportedFile;
            }

            bool needsCopy = !File.Exists(storedAudioPath)
                || document.Audio.FileSizeBytes != sourceInfo.Length
                || !string.Equals(document.Audio.Sha256, fileHash, StringComparison.OrdinalIgnoreCase);

            Log(
                $"ImportAudioFile target prepared. storedAudioPath='{storedAudioPath}', needsCopy={needsCopy}.");

            if (needsCopy) {
                CopyFileAtomic(fullPath, storedAudioPath);
            }

            document.Audio = new TranscriptSessionAudioDocument {
                StorageKind = AudioStorageKinds.ImportedFile,
                StoredRelativePath = NormalizeRelativePath(storedRelativePath),
                OriginalFileName = originalFileName,
                FileSizeBytes = sourceInfo.Length,
                DurationSeconds = TryGetAudioDurationSeconds(storedAudioPath),
                Sha256 = fileHash,
            };
            document.DisplayName = Path.GetFileNameWithoutExtension(originalFileName);
            document.UpdatedUtc = DateTimeOffset.UtcNow;

            Save(document);
            Log($"ImportAudioFile completed. sessionId='{sessionId}', storedAudioPath='{storedAudioPath}'.");
            return LoadSession(sessionId);
        }
        catch (Exception ex) {
            _processLogService?.LogException("SessionStore", $"ImportAudioFile failed. source='{sourceFilePath}'.", ex);
            throw;
        }
    }

    public TranscriptSessionLoadResult CreateLiveSession(string? displayName = null) {
        Log($"CreateLiveSession started. displayName='{displayName}'.");
        string sessionId = $"live-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        TranscriptSessionDocument document = CreateNewDocument(sessionId, LiveSessionAudioName);
        document.DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? $"Live Transcription {DateTimeOffset.Now:yyyy-MM-dd HH-mm}"
            : displayName.Trim();
        document.Audio.OriginalFileName = LiveSessionAudioName;
        document.Audio.StorageKind = AudioStorageKinds.LiveRecordingManifest;
        document.Editing.SelectedTranscriptViewIndex = 0;

        Save(document);
        Log($"CreateLiveSession completed. sessionId='{sessionId}'.");
        return LoadSession(sessionId);
    }

    public LiveRecordingSessionCreateResult CreateLiveRecordingSession(
        string sessionId,
        string inputSource,
        TimeSpan? segmentDuration = null) {
        Log($"CreateLiveRecordingSession started. sessionId='{sessionId}', inputSource='{inputSource}'.");
        TranscriptSessionLoadResult loadResult = LoadSessionWithoutAudioValidation(sessionId);
        TranscriptSessionDocument document = loadResult.Document;
        string sessionDirectoryPath = GetSessionDirectoryPath(sessionId);
        string manifestPath = Path.Combine(sessionDirectoryPath, LiveRecordingManifestRelativePath);

        document.Audio.StorageKind = AudioStorageKinds.LiveRecordingManifest;
        document.Audio.StoredRelativePath = NormalizeRelativePath(LiveRecordingManifestRelativePath);
        document.Audio.OriginalFileName = LiveSessionAudioName;
        document.Audio.FileSizeBytes = 0;
        document.Audio.DurationSeconds = null;
        document.Audio.Sha256 = string.Empty;
        document.UpdatedUtc = DateTimeOffset.UtcNow;
        Save(document);

        var recordingSession = new LiveRecordingSession(
            manifestPath,
            LiveRecordingManifestRelativePath,
            inputSource,
            _processLogService,
            segmentDuration);

        return new LiveRecordingSessionCreateResult(
            recordingSession,
            CloneAudioDocument(document.Audio));
    }

    public TranscriptSessionLoadResult UpdateLiveRecordingMetadata(string sessionId) {
        Log($"UpdateLiveRecordingMetadata started. sessionId='{sessionId}'.");
        TranscriptSessionLoadResult loadResult = LoadSessionWithoutAudioValidation(sessionId);
        TranscriptSessionDocument document = loadResult.Document;
        if (!IsLiveRecordingManifest(document) || string.IsNullOrWhiteSpace(document.Audio.StoredRelativePath)) {
            return LoadSession(sessionId);
        }

        string manifestPath = Path.Combine(GetSessionDirectoryPath(sessionId), document.Audio.StoredRelativePath);
        if (!File.Exists(manifestPath)) {
            return LoadSession(sessionId);
        }

        LiveRecordingManifest manifest = RepairInterruptedLiveRecordingIfNeeded(manifestPath);
        document.Audio.FileSizeBytes = manifest.TotalFileSizeBytes;
        document.Audio.DurationSeconds = manifest.TotalDurationSeconds > 0 ? manifest.TotalDurationSeconds : null;
        document.Audio.Sha256 = string.Empty;
        document.UpdatedUtc = DateTimeOffset.UtcNow;
        Save(document);
        return LoadSession(sessionId);
    }

    public bool TryLoadExistingSessionForAudio(string sourceFilePath, out TranscriptSessionLoadResult? loadResult) {
        Log($"TryLoadExistingSessionForAudio started. source='{sourceFilePath}'.");
        string fullPath = ValidateAudioFilePath(sourceFilePath);
        string fileHash = ComputeSha256(fullPath);
        string sessionId = BuildSessionId(fileHash);
        string sessionFilePath = GetSessionFilePath(sessionId);

        if (!File.Exists(sessionFilePath)) {
            Log($"TryLoadExistingSessionForAudio found no session. sessionFile='{sessionFilePath}'.");
            loadResult = null;
            return false;
        }

        loadResult = LoadSession(sessionId);
        Log($"TryLoadExistingSessionForAudio loaded existing session '{sessionId}'.");
        return true;
    }

    public TranscriptSessionLoadResult LoadSession(string sessionId) {
        Log($"LoadSession started. sessionId='{sessionId}'.");
        if (string.IsNullOrWhiteSpace(sessionId)) {
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        }

        string sessionFilePath = GetSessionFilePath(sessionId);
        if (!File.Exists(sessionFilePath)) {
            throw new FileNotFoundException("Session file was not found.", sessionFilePath);
        }

        TranscriptSessionDocument document = LoadDocument(sessionFilePath);
        string? audioPath = ResolveStoredAudioPath(document);
        bool audioAvailable = false;
        string? audioIssueMessage = null;

        if (IsLiveRecordingManifest(document)) {
            if (string.IsNullOrWhiteSpace(document.Audio.StoredRelativePath)) {
                audioIssueMessage = null;
            }
            else if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath)) {
                audioIssueMessage = "The stored live recording manifest is missing.";
                audioPath = null;
            }
            else {
                LiveRecordingManifest manifest = RepairInterruptedLiveRecordingIfNeeded(audioPath);
                string sessionDirectoryPath = GetSessionDirectoryPath(document.SessionId);
                bool hasMissingSegment = manifest.Segments.Any(segment =>
                    string.IsNullOrWhiteSpace(segment.RelativePath)
                    || !File.Exists(Path.Combine(sessionDirectoryPath, segment.RelativePath)));
                if (manifest.Segments.Count == 0) {
                    audioIssueMessage = "This live session does not have recorded audio segments.";
                    audioPath = null;
                }
                else if (hasMissingSegment) {
                    audioIssueMessage = "One or more live recording audio segments are missing.";
                    audioPath = null;
                }
                else {
                    audioAvailable = true;
                }
            }
        }
        else if (string.IsNullOrWhiteSpace(audioPath)) {
            audioIssueMessage = "This session does not have a stored audio file path.";
        }
        else if (!File.Exists(audioPath)) {
            audioIssueMessage = "The stored session audio file is missing.";
            audioPath = null;
        }
        else if (!string.IsNullOrWhiteSpace(document.Audio.Sha256)) {
            string actualHash = ComputeSha256(audioPath);
            if (!string.Equals(actualHash, document.Audio.Sha256, StringComparison.OrdinalIgnoreCase)) {
                audioIssueMessage = "The stored session audio file appears to be corrupted or mismatched.";
                audioPath = null;
            }
            else {
                audioAvailable = true;
            }
        }
        else {
            audioAvailable = true;
        }

        TranscriptSessionLoadResult result = new TranscriptSessionLoadResult(
            Document: document,
            AudioFilePath: audioPath,
            AudioAvailable: audioAvailable,
            AudioIssueMessage: audioIssueMessage);
        Log(
            $"LoadSession completed. sessionId='{sessionId}', audioAvailable={audioAvailable}, " +
            $"audioPath='{audioPath ?? "(none)"}', audioIssue='{audioIssueMessage ?? "(none)"}'.");
        return result;
    }

    public TranscriptSessionLoadResult RestoreAudioFile(string sessionId, string replacementAudioPath) {
        Log($"RestoreAudioFile started. sessionId='{sessionId}', replacement='{replacementAudioPath}'.");
        TranscriptSessionLoadResult loadResult = LoadSessionWithoutAudioValidation(sessionId);
        TranscriptSessionDocument document = loadResult.Document;
        string fullReplacementPath = ValidateAudioFilePath(replacementAudioPath);
        string replacementHash = ComputeSha256(fullReplacementPath);

        if (!string.IsNullOrWhiteSpace(document.Audio.Sha256)
            && !string.Equals(document.Audio.Sha256, replacementHash, StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidOperationException(
                "The selected audio file does not match the stored session audio fingerprint.");
        }

        string relativePath = string.IsNullOrWhiteSpace(document.Audio.StoredRelativePath)
            ? Path.Combine("audio", SanitizeFileName(Path.GetFileName(fullReplacementPath)))
            : document.Audio.StoredRelativePath;
        string absolutePath = Path.Combine(GetSessionDirectoryPath(sessionId), relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        CopyFileAtomic(fullReplacementPath, absolutePath);

        var info = new FileInfo(fullReplacementPath);
        document.Audio = new TranscriptSessionAudioDocument {
            StorageKind = AudioStorageKinds.ImportedFile,
            StoredRelativePath = NormalizeRelativePath(relativePath),
            OriginalFileName = string.IsNullOrWhiteSpace(document.Audio.OriginalFileName)
                ? Path.GetFileName(fullReplacementPath)
                : document.Audio.OriginalFileName,
            FileSizeBytes = info.Length,
            DurationSeconds = TryGetAudioDurationSeconds(absolutePath),
            Sha256 = replacementHash,
        };
        document.UpdatedUtc = DateTimeOffset.UtcNow;

        Save(document);
        Log($"RestoreAudioFile completed. sessionId='{sessionId}', restoredPath='{absolutePath}'.");
        return LoadSession(sessionId);
    }

    public TranscriptSessionLoadResult ConvertLiveSessionToImportedAudio(
        string sessionId,
        string replacementAudioPath,
        string? originalFileName = null) {
        Log(
            $"ConvertLiveSessionToImportedAudio started. sessionId='{sessionId}', " +
            $"replacement='{replacementAudioPath}', originalFileName='{originalFileName}'.");
        TranscriptSessionLoadResult loadResult = LoadSessionWithoutAudioValidation(sessionId);
        TranscriptSessionDocument document = loadResult.Document;
        if (!IsLiveRecordingManifest(document)) {
            throw new InvalidOperationException("Only live recording sessions can be converted to imported audio.");
        }

        string fullReplacementPath = ValidateAudioFilePath(replacementAudioPath);
        EnsureReadableWaveFile(fullReplacementPath);
        string extension = Path.GetExtension(fullReplacementPath);
        if (string.IsNullOrWhiteSpace(extension)) {
            extension = ".wav";
        }

        string resolvedOriginalFileName = string.IsNullOrWhiteSpace(originalFileName)
            ? $"{LiveSessionAudioName}{extension}"
            : originalFileName.Trim();
        string storedRelativePath = Path.Combine("audio", SanitizeFileName(resolvedOriginalFileName));
        string absolutePath = Path.Combine(GetSessionDirectoryPath(sessionId), storedRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        CopyFileAtomic(fullReplacementPath, absolutePath);

        var info = new FileInfo(absolutePath);
        document.Audio = new TranscriptSessionAudioDocument {
            StorageKind = AudioStorageKinds.ImportedFile,
            StoredRelativePath = NormalizeRelativePath(storedRelativePath),
            OriginalFileName = resolvedOriginalFileName,
            FileSizeBytes = info.Length,
            DurationSeconds = TryGetAudioDurationSeconds(absolutePath),
            Sha256 = ComputeSha256(absolutePath),
        };
        document.UpdatedUtc = DateTimeOffset.UtcNow;

        Save(document);
        Log(
            $"ConvertLiveSessionToImportedAudio completed. sessionId='{sessionId}', " +
            $"storedPath='{absolutePath}'.");
        return LoadSession(sessionId);
    }

    public IReadOnlyList<TranscriptSessionSummary> ListRecentSessions(int? maxCount = null) {
        if (!Directory.Exists(_sessionsRootPath)) {
            return Array.Empty<TranscriptSessionSummary>();
        }

        var summaries = new List<TranscriptSessionSummary>();

        foreach (string sessionFilePath in Directory.EnumerateFiles(_sessionsRootPath, "session.json", SearchOption.AllDirectories)) {
            try {
                TranscriptSessionDocument document = LoadDocument(sessionFilePath);
                string? audioPath = ResolveStoredAudioPath(document);
                bool hasStoredAudio = !string.IsNullOrWhiteSpace(audioPath) && File.Exists(audioPath);

                summaries.Add(new TranscriptSessionSummary(
                    SessionId: document.SessionId,
                    DisplayName: GetEffectiveDisplayName(document),
                    CreatedUtc: document.CreatedUtc,
                    UpdatedUtc: document.UpdatedUtc,
                    OriginalFileName: document.Audio.OriginalFileName,
                    HasStoredAudio: hasStoredAudio,
                    SummaryText: BuildRecentSessionSummaryText(document)));
            }
            catch (Exception ex) {
                Log($"Skipped malformed session '{sessionFilePath}': {ex.Message}");
            }
        }

        IEnumerable<TranscriptSessionSummary> orderedSessions = summaries
            .OrderByDescending(item => item.CreatedUtc);

        if (maxCount is > 0) {
            orderedSessions = orderedSessions.Take(maxCount.Value);
        }

        return orderedSessions.ToArray();
    }

    public bool SessionDisplayNameExists(string displayName, string? excludeSessionId = null) {
        string normalizedDisplayName = displayName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedDisplayName)) {
            return false;
        }

        if (!Directory.Exists(_sessionsRootPath)) {
            return false;
        }

        foreach (string sessionFilePath in Directory.EnumerateFiles(_sessionsRootPath, "session.json", SearchOption.AllDirectories)) {
            try {
                TranscriptSessionDocument document = LoadDocument(sessionFilePath);
                if (!string.IsNullOrWhiteSpace(excludeSessionId)
                    && string.Equals(document.SessionId, excludeSessionId, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                if (string.Equals(
                        GetEffectiveDisplayName(document),
                        normalizedDisplayName,
                        StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }
            catch (Exception ex) {
                Log($"Skipped malformed session '{sessionFilePath}' while checking display name uniqueness: {ex.Message}");
            }
        }

        return false;
    }

    public string GetSessionDisplayName(string sessionId) {
        if (string.IsNullOrWhiteSpace(sessionId)) {
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        }

        TranscriptSessionDocument document = LoadDocument(GetSessionFilePath(sessionId));
        return GetEffectiveDisplayName(document);
    }

    internal static string BuildRecentSessionSummaryText(TranscriptSessionDocument document) {
        string transcriptionState = GetRecentSessionTranscriptionState(document.Transcript);
        string? speakerState = GetRecentSessionSpeakerState(document.Transcript);
        string durationText = GetRecentSessionDurationText(document.Audio.DurationSeconds);
        var summaryParts = new List<string> { transcriptionState };

        if (!string.IsNullOrWhiteSpace(speakerState)) {
            summaryParts.Add(speakerState);
        }

        summaryParts.Add($"Duration {durationText}");
        return string.Join(" | ", summaryParts);
    }

    private static string GetRecentSessionTranscriptionState(TranscriptSessionTranscriptDocument transcript) {
        TranscriptionJobDocument job = transcript.TranscriptionJob;

        if (string.Equals(job.Status, TranscriptionJobStatuses.Completed, StringComparison.OrdinalIgnoreCase)) {
            return "Transcribed";
        }

        if (string.Equals(job.Status, TranscriptionJobStatuses.NotStarted, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(transcript.FinalText)
            && transcript.Lines.Count == 0) {
            return "Empty";
        }

        if (IsRecentSessionIncompleteTranscriptionJob(job)
            && (job.LastCompletedChunkIndex >= 0
                || job.TotalChunks > 0
                || !string.IsNullOrWhiteSpace(transcript.FinalText)
                || transcript.Lines.Count > 0)) {
            return "Incomplete";
        }

        return string.IsNullOrWhiteSpace(transcript.FinalText) && transcript.Lines.Count == 0
            ? "Empty"
            : "Incomplete";
    }

    private static string? GetRecentSessionSpeakerState(TranscriptSessionTranscriptDocument transcript) {
        bool hasSpeakerColumnData = transcript.Lines.Any(line =>
            !string.IsNullOrWhiteSpace(line.SpeakerLabel)
            || !string.IsNullOrWhiteSpace(line.SpeakerLabelSource));

        return hasSpeakerColumnData
            ? "Speakers"
            : null;
    }

    private static string GetRecentSessionDurationText(double? durationSeconds) {
        TimeSpan duration = durationSeconds is > 0
            ? TimeSpan.FromSeconds(durationSeconds.Value)
            : TimeSpan.Zero;

        return duration.TotalHours >= 1
            ? duration.ToString(@"hh\:mm\:ss")
            : duration.ToString(@"mm\:ss");
    }

    private static bool IsRecentSessionIncompleteTranscriptionJob(TranscriptionJobDocument job) {
        return string.Equals(job.Status, TranscriptionJobStatuses.Running, StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.Status, TranscriptionJobStatuses.Paused, StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.Status, TranscriptionJobStatuses.Failed, StringComparison.OrdinalIgnoreCase);
    }

    public void RenameSessionDisplayName(string sessionId, string displayName) {
        Log($"RenameSessionDisplayName started. sessionId='{sessionId}', displayName='{displayName}'.");
        if (string.IsNullOrWhiteSpace(sessionId)) {
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        }

        string normalizedDisplayName = displayName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedDisplayName)) {
            throw new ArgumentException("Session display name is required.", nameof(displayName));
        }

        TranscriptSessionDocument document = LoadDocument(GetSessionFilePath(sessionId));
        document.DisplayName = normalizedDisplayName;
        document.UpdatedUtc = DateTimeOffset.UtcNow;
        Save(document);

        Log($"RenameSessionDisplayName completed. sessionId='{sessionId}', displayName='{normalizedDisplayName}'.");
    }

    public void Save(TranscriptSessionDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        if (string.IsNullOrWhiteSpace(document.SessionId)) {
            throw new InvalidOperationException("Session id is required.");
        }

        document.SchemaVersion = CurrentSchemaVersion;
        document.UpdatedUtc = document.UpdatedUtc == default
            ? DateTimeOffset.UtcNow
            : document.UpdatedUtc;

        string sessionFilePath = GetSessionFilePath(document.SessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(sessionFilePath)!);

        string json = JsonSerializer.Serialize(document, JsonOptions);
        WriteAllTextAtomic(sessionFilePath, json);
        Log($"Save completed. sessionId='{document.SessionId}', sessionFile='{sessionFilePath}'.");
    }

    public void DeleteSession(string sessionId) {
        Log($"DeleteSession started. sessionId='{sessionId}'.");
        if (string.IsNullOrWhiteSpace(sessionId)) {
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        }

        string sessionDirectoryPath = GetSessionDirectoryPath(sessionId);
        if (!Directory.Exists(sessionDirectoryPath)) {
            return;
        }

        Directory.Delete(sessionDirectoryPath, recursive: true);
        Log($"DeleteSession completed. sessionId='{sessionId}', directory='{sessionDirectoryPath}'.");
    }

    public bool ClearSessionStoredAudio(string sessionId) {
        Log($"ClearSessionStoredAudio started. sessionId='{sessionId}'.");
        if (string.IsNullOrWhiteSpace(sessionId)) {
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        }

        TranscriptSessionLoadResult loadResult = LoadSessionWithoutAudioValidation(sessionId);
        TranscriptSessionDocument document = loadResult.Document;

        string? storedAudioPath = ResolveStoredAudioPath(document);
        bool deletedAnyAudio = false;
        if (!string.IsNullOrWhiteSpace(storedAudioPath)) {
            if (File.Exists(storedAudioPath)) {
                File.Delete(storedAudioPath);
                deletedAnyAudio = true;
            }

            string? audioDirectoryPath = Path.GetDirectoryName(storedAudioPath);
            if (!string.IsNullOrWhiteSpace(audioDirectoryPath) && Directory.Exists(audioDirectoryPath)) {
                Directory.Delete(audioDirectoryPath, recursive: true);
                deletedAnyAudio = true;
            }
        }

        document.Audio.StoredRelativePath = string.Empty;
        document.Audio.FileSizeBytes = 0;
        document.Audio.DurationSeconds = null;
        document.Audio.Sha256 = string.Empty;
        document.UpdatedUtc = DateTimeOffset.UtcNow;
        Save(document);

        Log($"ClearSessionStoredAudio completed. sessionId='{sessionId}', deletedAnyAudio={deletedAnyAudio}.");
        return deletedAnyAudio;
    }

    public bool PruneLiveRecordingAudioAfterTime(string sessionId, double keepUntilSecondsInclusive) {
        Log($"PruneLiveRecordingAudioAfterTime started. sessionId='{sessionId}', keepUntilSecondsInclusive={keepUntilSecondsInclusive:N3}.");
        if (string.IsNullOrWhiteSpace(sessionId)) {
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        }

        TranscriptSessionLoadResult loadResult = LoadSessionWithoutAudioValidation(sessionId);
        TranscriptSessionDocument document = loadResult.Document;
        if (!IsLiveRecordingManifest(document) || string.IsNullOrWhiteSpace(document.Audio.StoredRelativePath)) {
            Log($"PruneLiveRecordingAudioAfterTime skipped. sessionId='{sessionId}', liveManifestNotConfigured=true.");
            return false;
        }

        string manifestPath = Path.Combine(GetSessionDirectoryPath(sessionId), document.Audio.StoredRelativePath);
        if (!File.Exists(manifestPath)) {
            Log($"PruneLiveRecordingAudioAfterTime skipped. sessionId='{sessionId}', manifestMissing=true.");
            return false;
        }

        double cutoff = Math.Max(0, keepUntilSecondsInclusive);
        const double epsilon = 0.000001d;
        LiveRecordingManifest manifest = LoadLiveRecordingManifest(manifestPath);
        string sessionDirectoryPath = GetSessionDirectoryPath(sessionId);

        List<LiveRecordingSegmentManifest> keptSegments = manifest.Segments
            .Where(segment => (segment.StartSeconds + segment.DurationSeconds) <= cutoff + epsilon)
            .OrderBy(segment => segment.StartSeconds)
            .ToList();
        HashSet<string> keptRelativePaths = keptSegments
            .Where(segment => !string.IsNullOrWhiteSpace(segment.RelativePath))
            .Select(segment => NormalizeRelativePath(segment.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool removedAny = false;
        foreach (LiveRecordingSegmentManifest segment in manifest.Segments) {
            string relativePath = NormalizeRelativePath(segment.RelativePath ?? string.Empty);
            if (string.IsNullOrWhiteSpace(relativePath) || keptRelativePaths.Contains(relativePath)) {
                continue;
            }

            string segmentPath = Path.Combine(sessionDirectoryPath, relativePath);
            if (File.Exists(segmentPath)) {
                File.Delete(segmentPath);
            }

            removedAny = true;
        }

        manifest.Segments = keptSegments;
        manifest.TotalDurationSeconds = keptSegments.Sum(segment => Math.Max(0, segment.DurationSeconds));
        manifest.TotalFileSizeBytes = keptSegments.Sum(segment => Math.Max(0, segment.FileSizeBytes));
        WriteAllTextAtomic(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));

        document.Audio.FileSizeBytes = manifest.TotalFileSizeBytes;
        document.Audio.DurationSeconds = manifest.TotalDurationSeconds > 0 ? manifest.TotalDurationSeconds : null;
        document.Audio.Sha256 = string.Empty;
        document.UpdatedUtc = DateTimeOffset.UtcNow;
        Save(document);

        Log(
            $"PruneLiveRecordingAudioAfterTime completed. sessionId='{sessionId}', removedAny={removedAny}, " +
            $"keptSegments={keptSegments.Count:N0}, totalDurationSeconds={manifest.TotalDurationSeconds:N3}.");
        return removedAny;
    }

    public string? ResolveStoredAudioPathForPlayback(TranscriptSessionDocument document) {
        return ResolveStoredAudioPath(document);
    }

    public string GetSessionDirectoryPath(string sessionId) {
        if (string.IsNullOrWhiteSpace(sessionId)) {
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        }

        return Path.Combine(_sessionsRootPath, sessionId.Trim());
    }

    private TranscriptSessionLoadResult LoadSessionWithoutAudioValidation(string sessionId) {
        string sessionFilePath = GetSessionFilePath(sessionId);
        if (!File.Exists(sessionFilePath)) {
            throw new FileNotFoundException("Session file was not found.", sessionFilePath);
        }

        TranscriptSessionDocument document = LoadDocument(sessionFilePath);
        return new TranscriptSessionLoadResult(
            Document: document,
            AudioFilePath: ResolveStoredAudioPath(document),
            AudioAvailable: false,
            AudioIssueMessage: null);
    }

    private string GetSessionFilePath(string sessionId) {
        return Path.Combine(GetSessionDirectoryPath(sessionId), "session.json");
    }

    private TranscriptSessionDocument LoadDocument(string sessionFilePath) {
        try {
            string json = File.ReadAllText(sessionFilePath);
            TranscriptSessionDocument? document = JsonSerializer.Deserialize<TranscriptSessionDocument>(json, JsonOptions);

            if (document is null) {
                throw new InvalidOperationException("Session file did not contain valid session data.");
            }

            if (document.SchemaVersion <= 0) {
                throw new InvalidOperationException("Session schema version is missing or invalid.");
            }

            if (document.SchemaVersion > CurrentSchemaVersion) {
                throw new InvalidOperationException(
                    $"Session schema version {document.SchemaVersion} is newer than this app supports.");
            }

            if (string.IsNullOrWhiteSpace(document.SessionId)) {
                throw new InvalidOperationException("Session data is missing its session id.");
            }

            document.Transcript ??= new TranscriptSessionTranscriptDocument();
            document.Transcript.Lines ??= new List<TranscriptSessionLineDocument>();
            document.Transcript.TranscriptionJob ??= new TranscriptionJobDocument();
            document.Transcript.SpeakerDiarizationJob ??= new SpeakerDiarizationJobDocument();
            document.Audio ??= new TranscriptSessionAudioDocument();
            if (string.IsNullOrWhiteSpace(document.Audio.StorageKind)) {
                document.Audio.StorageKind = string.IsNullOrWhiteSpace(document.Audio.StoredRelativePath)
                    && IsLiveSession(document)
                        ? AudioStorageKinds.LiveRecordingManifest
                        : AudioStorageKinds.ImportedFile;
            }
            document.Editing ??= new TranscriptSessionEditingDocument();
            RepairMissingLiveRecordingManifestPath(document);

            return document;
        }
        catch (InvalidOperationException) {
            throw;
        }
        catch (Exception ex) {
            throw new InvalidOperationException(
                $"Session file '{sessionFilePath}' could not be read: {ex.Message}",
                ex);
        }
    }

    private static TranscriptSessionDocument CreateNewDocument(string sessionId, string originalFileName) {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        return new TranscriptSessionDocument {
            SchemaVersion = CurrentSchemaVersion,
            SessionId = sessionId,
            DisplayName = Path.GetFileNameWithoutExtension(originalFileName),
            CreatedUtc = now,
            UpdatedUtc = now,
            Audio = new TranscriptSessionAudioDocument {
                OriginalFileName = originalFileName,
            },
            Transcript = new TranscriptSessionTranscriptDocument(),
            Editing = new TranscriptSessionEditingDocument(),
        };
    }

    private static bool IsLiveSession(TranscriptSessionDocument document) {
        return string.Equals(
            document.Audio?.OriginalFileName,
            LiveSessionAudioName,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLiveRecordingManifest(TranscriptSessionDocument document) {
        return string.Equals(
            document.Audio?.StorageKind,
            AudioStorageKinds.LiveRecordingManifest,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string GetEffectiveDisplayName(TranscriptSessionDocument document) {
        return string.IsNullOrWhiteSpace(document.DisplayName)
            ? document.Audio.OriginalFileName
            : document.DisplayName;
    }

    private void RepairMissingLiveRecordingManifestPath(TranscriptSessionDocument document) {
        if (!IsLiveRecordingManifest(document)
            || !string.IsNullOrWhiteSpace(document.Audio.StoredRelativePath)
            || string.IsNullOrWhiteSpace(document.SessionId)) {
            return;
        }

        string manifestPath = Path.Combine(GetSessionDirectoryPath(document.SessionId), LiveRecordingManifestRelativePath);
        if (!File.Exists(manifestPath)) {
            return;
        }

        document.Audio.StoredRelativePath = NormalizeRelativePath(LiveRecordingManifestRelativePath);
        document.Audio.OriginalFileName = string.IsNullOrWhiteSpace(document.Audio.OriginalFileName)
            ? LiveSessionAudioName
            : document.Audio.OriginalFileName;
        Log(
            $"Repaired missing live recording manifest path. sessionId='{document.SessionId}', " +
            $"relativePath='{document.Audio.StoredRelativePath}'.");
    }

    public static TranscriptSessionAudioDocument CloneAudioDocument(TranscriptSessionAudioDocument audio) {
        ArgumentNullException.ThrowIfNull(audio);

        return new TranscriptSessionAudioDocument {
            StorageKind = audio.StorageKind,
            StoredRelativePath = audio.StoredRelativePath,
            OriginalFileName = audio.OriginalFileName,
            FileSizeBytes = audio.FileSizeBytes,
            DurationSeconds = audio.DurationSeconds,
            Sha256 = audio.Sha256,
        };
    }

    private static string BuildSessionId(string fileHash) {
        if (string.IsNullOrWhiteSpace(fileHash)) {
            throw new InvalidOperationException("Audio fingerprint could not be computed.");
        }

        return fileHash.Trim().ToLowerInvariant();
    }

    private static string ValidateAudioFilePath(string sourceFilePath) {
        if (string.IsNullOrWhiteSpace(sourceFilePath)) {
            throw new ArgumentException("Audio file path is required.", nameof(sourceFilePath));
        }

        string fullPath = Path.GetFullPath(sourceFilePath.Trim());

        if (!File.Exists(fullPath)) {
            throw new FileNotFoundException("Audio file was not found.", fullPath);
        }

        using FileStream _ = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return fullPath;
    }

    private static string ComputeSha256(string filePath) {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    private double? TryGetAudioDurationSeconds(string filePath) {
        try {
            using var reader = new AudioFileReader(filePath);
            if (reader.TotalTime <= TimeSpan.Zero) {
                return null;
            }

            return reader.TotalTime.TotalSeconds;
        }
        catch (Exception ex) {
            Log($"Unable to read audio duration for '{filePath}': {ex.Message}");
            return null;
        }
    }

    private static void EnsureReadableWaveFile(string filePath) {
        try {
            using var reader = new WaveFileReader(filePath);
            _ = reader.TotalTime;
        }
        catch (Exception ex) {
            throw new InvalidOperationException(
                $"The replacement audio file '{filePath}' is not a valid WAV file.",
                ex);
        }
    }

    private LiveRecordingManifest RepairInterruptedLiveRecordingIfNeeded(string manifestPath) {
        LiveRecordingManifest manifest = LoadLiveRecordingManifest(manifestPath);
        if (!string.Equals(manifest.Status, LiveRecordingManifestStatuses.Recording, StringComparison.OrdinalIgnoreCase)) {
            return manifest;
        }

        manifest.Status = LiveRecordingManifestStatuses.Interrupted;
        manifest.EndedUtc ??= DateTimeOffset.UtcNow;
        manifest.ErrorMessage = string.IsNullOrWhiteSpace(manifest.ErrorMessage)
            ? "Recording ended unexpectedly."
            : manifest.ErrorMessage;
        manifest.TotalDurationSeconds = manifest.Segments.Sum(segment => segment.DurationSeconds);
        manifest.TotalFileSizeBytes = manifest.Segments.Sum(segment => segment.FileSizeBytes);
        WriteAllTextAtomic(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
        return manifest;
    }

    public static LiveRecordingManifest LoadLiveRecordingManifest(string manifestPath) {
        if (string.IsNullOrWhiteSpace(manifestPath)) {
            throw new ArgumentException("Manifest path is required.", nameof(manifestPath));
        }

        string json = File.ReadAllText(manifestPath);
        LiveRecordingManifest? manifest = JsonSerializer.Deserialize<LiveRecordingManifest>(json, JsonOptions);
        if (manifest is null) {
            throw new InvalidOperationException("Live recording manifest did not contain valid data.");
        }

        manifest.Segments ??= new List<LiveRecordingSegmentManifest>();
        return manifest;
    }

    private static void CopyFileAtomic(string sourcePath, string targetPath) {
        string directory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(directory);

        string tempPath = Path.Combine(directory, $"{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        File.Copy(sourcePath, tempPath, overwrite: true);

        try {
            if (File.Exists(targetPath)) {
                File.Replace(tempPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else {
                File.Move(tempPath, targetPath);
            }
        }
        finally {
            if (File.Exists(tempPath)) {
                File.Delete(tempPath);
            }
        }
    }

    private static void WriteAllTextAtomic(string targetPath, string content) {
        string directory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(directory);

        string tempPath = Path.Combine(directory, $"{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, content);

        try {
            if (File.Exists(targetPath)) {
                File.Replace(tempPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else {
                File.Move(tempPath, targetPath);
            }
        }
        finally {
            if (File.Exists(tempPath)) {
                File.Delete(tempPath);
            }
        }
    }

    private string? ResolveStoredAudioPath(TranscriptSessionDocument document) {
        if (document.Audio is null || string.IsNullOrWhiteSpace(document.Audio.StoredRelativePath)) {
            return null;
        }

        return Path.Combine(GetSessionDirectoryPath(document.SessionId), document.Audio.StoredRelativePath);
    }

    private static string NormalizeRelativePath(string relativePath) {
        return relativePath.Replace('\\', '/');
    }

    private static string SanitizeFileName(string fileName) {
        string sanitized = Path.GetFileName(fileName);

        foreach (char invalid in Path.GetInvalidFileNameChars()) {
            sanitized = sanitized.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(sanitized)
            ? "audio-file"
            : sanitized;
    }

    private void Log(string message) {
        _processLogService?.Log("SessionStore", message);
    }
}

public sealed class TranscriptSessionDocument {
    public int SchemaVersion { get; set; } = TranscriptSessionStore.CurrentSchemaVersion;

    public string SessionId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public TranscriptSessionAudioDocument Audio { get; set; } = new();

    public TranscriptSessionTranscriptDocument Transcript { get; set; } = new();

    public TranscriptSessionEditingDocument Editing { get; set; } = new();
}

public sealed class TranscriptSessionAudioDocument {
    public string StorageKind { get; set; } = AudioStorageKinds.ImportedFile;

    public string StoredRelativePath { get; set; } = string.Empty;

    public string OriginalFileName { get; set; } = string.Empty;

    public long FileSizeBytes { get; set; }

    public double? DurationSeconds { get; set; }

    public string Sha256 { get; set; } = string.Empty;
}

public sealed class TranscriptSessionTranscriptDocument {
    public string FinalText { get; set; } = string.Empty;

    public string ModelId { get; set; } = string.Empty;

    public DateTimeOffset? LastTranscribedUtc { get; set; }

    public List<TranscriptSessionLineDocument> Lines { get; set; } = new();

    public TranscriptionJobDocument TranscriptionJob { get; set; } = new();

    public SpeakerDiarizationJobDocument SpeakerDiarizationJob { get; set; } = new();
}

public sealed class TranscriptSessionLineDocument {
    public string Text { get; set; } = string.Empty;

    public string SpeakerLabel { get; set; } = string.Empty;

    public double? StartSeconds { get; set; }

    public double? EndSeconds { get; set; }

    public bool IsTimestampEstimated { get; set; }

    public bool IsManuallyReviewed { get; set; }

    public bool IsTranscriptionPartial { get; set; }

    public string SpeakerLabelSource { get; set; } = string.Empty;

    public int? DiarizationRevision { get; set; }

    public int? LastDiarizedChunkIndex { get; set; }
}

public sealed class TranscriptionJobDocument {
    public string Status { get; set; } = TranscriptionJobStatuses.NotStarted;

    public string Engine { get; set; } = string.Empty;

    public int JobVersion { get; set; }

    public string AudioFingerprint { get; set; } = string.Empty;

    public int TotalChunks { get; set; }

    public int LastCompletedChunkIndex { get; set; } = -1;

    public DateTimeOffset? StartedUtc { get; set; }

    public DateTimeOffset? LastUpdatedUtc { get; set; }

    public DateTimeOffset? CompletedUtc { get; set; }

    public string LastError { get; set; } = string.Empty;
}

public static class TranscriptionJobStatuses {
    public const string NotStarted = "not_started";
    public const string Running = "running";
    public const string Paused = "paused";
    public const string Failed = "failed";
    public const string Completed = "completed";
}

public sealed class SpeakerDiarizationJobDocument {
    public string Status { get; set; } = SpeakerDiarizationJobStatuses.NotStarted;

    public string Engine { get; set; } = string.Empty;

    public int JobVersion { get; set; }

    public string AudioFingerprint { get; set; } = string.Empty;

    public string TranscriptFingerprint { get; set; } = string.Empty;

    public double ChunkDurationSeconds { get; set; }

    public double OverlapDurationSeconds { get; set; }

    public int TotalChunks { get; set; }

    public int LastCompletedChunkIndex { get; set; } = -1;

    public DateTimeOffset? StartedUtc { get; set; }

    public DateTimeOffset? LastUpdatedUtc { get; set; }

    public DateTimeOffset? CompletedUtc { get; set; }

    public string LastError { get; set; } = string.Empty;

    public int Revision { get; set; }

    public int NextSpeakerIndex { get; set; } = 1;

    public List<SpeakerDiarizationSpeakerMapDocument> SpeakerMappings { get; set; } = new();
}

public sealed class SpeakerDiarizationSpeakerMapDocument {
    public string ChunkSpeakerKey { get; set; } = string.Empty;

    public string GlobalSpeakerLabel { get; set; } = string.Empty;
}

public static class SpeakerDiarizationJobStatuses {
    public const string NotStarted = "not_started";
    public const string Running = "running";
    public const string Canceled = "canceled";
    public const string Failed = "failed";
    public const string Completed = "completed";
}

public static class SpeakerLabelSources {
    public const string Manual = "manual";
    public const string Heuristic = "heuristic";
    public const string DiarizationPartial = "diarization_partial";
    public const string DiarizationFinal = "diarization_final";
}

public sealed class TranscriptSessionEditingDocument {
    public int? SelectedRowIndex { get; set; }

    public int SelectedTranscriptViewIndex { get; set; }
}

public sealed record TranscriptSessionSummary(
    string SessionId,
    string DisplayName,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string OriginalFileName,
    bool HasStoredAudio,
    string SummaryText,
    bool IsLoaded = false
);

public sealed record TranscriptSessionLoadResult(
    TranscriptSessionDocument Document,
    string? AudioFilePath,
    bool AudioAvailable,
    string? AudioIssueMessage
);

public sealed record LiveRecordingSessionCreateResult(
    LiveRecordingSession RecordingSession,
    TranscriptSessionAudioDocument Audio
);



