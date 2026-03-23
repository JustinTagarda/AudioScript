using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using NAudio.Wave;

namespace AudioScript.Services;

public sealed class TranscriptSessionStore {
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
    };

    private readonly string _sessionsRootPath;
    private readonly ProcessLogService? _processLogService;

    public TranscriptSessionStore(string? sessionsRootPath = null, ProcessLogService? processLogService = null) {
        _sessionsRootPath = string.IsNullOrWhiteSpace(sessionsRootPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AudioScript",
                "Sessions")
            : Path.GetFullPath(sessionsRootPath);
        _processLogService = processLogService;
    }

    public TranscriptSessionLoadResult ImportAudioFile(string sourceFilePath) {
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

        Directory.CreateDirectory(Path.Combine(sessionDirectoryPath, "audio"));

        TranscriptSessionDocument document = File.Exists(sessionFilePath)
            ? LoadDocument(sessionFilePath)
            : CreateNewDocument(sessionId, originalFileName);

        if (!string.IsNullOrWhiteSpace(document.Audio.StoredRelativePath)) {
            storedRelativePath = document.Audio.StoredRelativePath;
            storedAudioPath = Path.Combine(sessionDirectoryPath, storedRelativePath);
        }

        bool needsCopy = !File.Exists(storedAudioPath)
            || document.Audio.FileSizeBytes != sourceInfo.Length
            || !string.Equals(document.Audio.Sha256, fileHash, StringComparison.OrdinalIgnoreCase);

        if (needsCopy) {
            CopyFileAtomic(fullPath, storedAudioPath);
        }

        document.Audio = new TranscriptSessionAudioDocument {
            StoredRelativePath = NormalizeRelativePath(storedRelativePath),
            OriginalFileName = originalFileName,
            FileSizeBytes = sourceInfo.Length,
            DurationSeconds = TryGetAudioDurationSeconds(storedAudioPath),
            Sha256 = fileHash,
        };
        document.DisplayName = Path.GetFileNameWithoutExtension(originalFileName);
        document.UpdatedUtc = DateTimeOffset.UtcNow;

        Save(document);
        return LoadSession(sessionId);
    }

    public bool TryLoadExistingSessionForAudio(string sourceFilePath, out TranscriptSessionLoadResult? loadResult) {
        string fullPath = ValidateAudioFilePath(sourceFilePath);
        string fileHash = ComputeSha256(fullPath);
        string sessionId = BuildSessionId(fileHash);
        string sessionFilePath = GetSessionFilePath(sessionId);

        if (!File.Exists(sessionFilePath)) {
            loadResult = null;
            return false;
        }

        loadResult = LoadSession(sessionId);
        return true;
    }

    public TranscriptSessionLoadResult LoadSession(string sessionId) {
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

        if (string.IsNullOrWhiteSpace(audioPath)) {
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

        return new TranscriptSessionLoadResult(
            Document: document,
            AudioFilePath: audioPath,
            AudioAvailable: audioAvailable,
            AudioIssueMessage: audioIssueMessage);
    }

    public TranscriptSessionLoadResult RestoreAudioFile(string sessionId, string replacementAudioPath) {
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
        return LoadSession(sessionId);
    }

    public IReadOnlyList<TranscriptSessionSummary> ListRecentSessions(int maxCount = 12) {
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
                    DisplayName: string.IsNullOrWhiteSpace(document.DisplayName)
                        ? document.Audio.OriginalFileName
                        : document.DisplayName,
                    UpdatedUtc: document.UpdatedUtc,
                    OriginalFileName: document.Audio.OriginalFileName,
                    HasStoredAudio: hasStoredAudio));
            }
            catch (Exception ex) {
                Log($"Skipped malformed session '{sessionFilePath}': {ex.Message}");
            }
        }

        return summaries
            .OrderByDescending(item => item.UpdatedUtc)
            .Take(Math.Max(maxCount, 1))
            .ToArray();
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
    }

    public void DeleteSession(string sessionId) {
        if (string.IsNullOrWhiteSpace(sessionId)) {
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        }

        string sessionDirectoryPath = GetSessionDirectoryPath(sessionId);
        if (!Directory.Exists(sessionDirectoryPath)) {
            return;
        }

        Directory.Delete(sessionDirectoryPath, recursive: true);
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
            document.SpeakerTranscript ??= new TranscriptSessionTranscriptDocument();
            document.SpeakerTranscript.Lines ??= new List<TranscriptSessionLineDocument>();
            document.Audio ??= new TranscriptSessionAudioDocument();
            document.Editing ??= new TranscriptSessionEditingDocument();

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
            SpeakerTranscript = new TranscriptSessionTranscriptDocument(),
            Editing = new TranscriptSessionEditingDocument(),
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

    public TranscriptSessionTranscriptDocument SpeakerTranscript { get; set; } = new();

    public TranscriptSessionEditingDocument Editing { get; set; } = new();
}

public sealed class TranscriptSessionAudioDocument {
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
}

public sealed class TranscriptSessionLineDocument {
    public string Text { get; set; } = string.Empty;

    public string SpeakerLabel { get; set; } = string.Empty;

    public double? StartSeconds { get; set; }

    public double? EndSeconds { get; set; }

    public bool IsTimestampEstimated { get; set; }

    public bool IsManuallyReviewed { get; set; }
}

public sealed class TranscriptSessionEditingDocument {
    public int? SelectedRowIndex { get; set; }

    public string SelectedTranscriptMode { get; set; } = string.Empty;

    public int SelectedTranscriptViewIndex { get; set; }
}

public sealed record TranscriptSessionSummary(
    string SessionId,
    string DisplayName,
    DateTimeOffset UpdatedUtc,
    string OriginalFileName,
    bool HasStoredAudio
);

public sealed record TranscriptSessionLoadResult(
    TranscriptSessionDocument Document,
    string? AudioFilePath,
    bool AudioAvailable,
    string? AudioIssueMessage
);



