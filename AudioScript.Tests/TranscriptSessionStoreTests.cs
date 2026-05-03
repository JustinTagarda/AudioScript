using System.Text;
using System.Text.Json;
using AudioScript.Abstractions;
using AudioScript.Audio;
using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class TranscriptSessionStoreTests {
    [Fact]
    public void ImportAudioFile_SaveLoadRoundTrip_RestoresTranscriptData() {
        string rootPath = CreateTempDirectory();
        string audioPath = CreateSilentWaveFile(16000);

        try {
            var store = new TranscriptSessionStore(rootPath);

            TranscriptSessionLoadResult imported = store.ImportAudioFile(audioPath);
            imported.Document.Transcript.ModelId = TranscriptionModelCatalog.WhisperSmall;
            imported.Document.Transcript.FinalText = "hello world";
            imported.Document.Transcript.Lines = new List<TranscriptSessionLineDocument> {
                new() {
                    Text = "hello world",
                    StartSeconds = 1.25,
                    EndSeconds = 2.75,
                    IsTimestampEstimated = false,
                },
            };

            store.Save(imported.Document);

            TranscriptSessionLoadResult reloaded = store.LoadSession(imported.Document.SessionId);

            Assert.True(reloaded.AudioAvailable);
            Assert.NotNull(reloaded.AudioFilePath);
            Assert.Equal("hello world", reloaded.Document.Transcript.FinalText);
            Assert.Equal(TranscriptionModelCatalog.WhisperSmall, reloaded.Document.Transcript.ModelId);
            Assert.Single(reloaded.Document.Transcript.Lines);
            Assert.Equal(1.25, reloaded.Document.Transcript.Lines[0].StartSeconds);
            Assert.Equal(2.75, reloaded.Document.Transcript.Lines[0].EndSeconds);
            Assert.False(reloaded.Document.Transcript.Lines[0].IsTimestampEstimated);
        }
        finally {
            DeleteDirectory(rootPath);
            File.Delete(audioPath);
        }
    }

    [Fact]
    public void Save_WritesAtomically_AndLeavesNoTempFiles() {
        string rootPath = CreateTempDirectory();
        string audioPath = CreateSilentWaveFile(16000);

        try {
            var store = new TranscriptSessionStore(rootPath);
            TranscriptSessionLoadResult imported = store.ImportAudioFile(audioPath);

            imported.Document.Transcript.FinalText = "first";
            store.Save(imported.Document);

            imported.Document.Transcript.FinalText = "second";
            store.Save(imported.Document);

            TranscriptSessionLoadResult reloaded = store.LoadSession(imported.Document.SessionId);

            Assert.Equal("second", reloaded.Document.Transcript.FinalText);
            Assert.Empty(Directory.EnumerateFiles(rootPath, "*.tmp", SearchOption.AllDirectories));
        }
        finally {
            DeleteDirectory(rootPath);
            File.Delete(audioPath);
        }
    }

    [Fact]
    public void SaveLoadRoundTrip_PreservesEmptyTranscriptLines() {
        string rootPath = CreateTempDirectory();
        string audioPath = CreateSilentWaveFile(16000);

        try {
            var store = new TranscriptSessionStore(rootPath);
            TranscriptSessionLoadResult imported = store.ImportAudioFile(audioPath);

            imported.Document.Transcript.FinalText = string.Empty;
            imported.Document.Transcript.Lines = new List<TranscriptSessionLineDocument> {
                new() {
                    Text = string.Empty,
                    StartSeconds = 0,
                    EndSeconds = 10,
                    IsTimestampEstimated = true,
                },
            };

            store.Save(imported.Document);

            TranscriptSessionLoadResult reloaded = store.LoadSession(imported.Document.SessionId);

            Assert.Single(reloaded.Document.Transcript.Lines);
            Assert.Equal(string.Empty, reloaded.Document.Transcript.Lines[0].Text);
            Assert.Equal(0, reloaded.Document.Transcript.Lines[0].StartSeconds);
            Assert.Equal(10, reloaded.Document.Transcript.Lines[0].EndSeconds);
            Assert.True(reloaded.Document.Transcript.Lines[0].IsTimestampEstimated);
        }
        finally {
            DeleteDirectory(rootPath);
            File.Delete(audioPath);
        }
    }

    [Fact]
    public void SaveLoadRoundTrip_PreservesTranscriptSpeakerLabels() {
        string rootPath = CreateTempDirectory();
        string audioPath = CreateSilentWaveFile(16000);

        try {
            var store = new TranscriptSessionStore(rootPath);
            TranscriptSessionLoadResult imported = store.ImportAudioFile(audioPath);

            imported.Document.Transcript.ModelId = TranscriptionModelCatalog.WhisperSmall;
            imported.Document.Transcript.FinalText = "Speaker 1: hello";
            imported.Document.Transcript.Lines = new List<TranscriptSessionLineDocument> {
                new() {
                    Text = "hello",
                    SpeakerLabel = "Speaker 1",
                    StartSeconds = 1,
                    EndSeconds = 2.5,
                },
            };
            imported.Document.Editing.SelectedTranscriptViewIndex = 0;

            store.Save(imported.Document);

            TranscriptSessionLoadResult reloaded = store.LoadSession(imported.Document.SessionId);

            Assert.Equal(TranscriptionModelCatalog.WhisperSmall, reloaded.Document.Transcript.ModelId);
            Assert.Equal("Speaker 1: hello", reloaded.Document.Transcript.FinalText);
            Assert.Single(reloaded.Document.Transcript.Lines);
            Assert.Equal("Speaker 1", reloaded.Document.Transcript.Lines[0].SpeakerLabel);
            Assert.Equal(1, reloaded.Document.Transcript.Lines[0].StartSeconds);
            Assert.Equal(2.5, reloaded.Document.Transcript.Lines[0].EndSeconds);
            Assert.Equal(0, reloaded.Document.Editing.SelectedTranscriptViewIndex);
        }
        finally {
            DeleteDirectory(rootPath);
            File.Delete(audioPath);
        }
    }

    [Fact]
    public void SaveLoadRoundTrip_PreservesSpeakerDiarizationJobStateAndRowMetadata() {
        string rootPath = CreateTempDirectory();
        string audioPath = CreateSilentWaveFile(16000);

        try {
            var store = new TranscriptSessionStore(rootPath);
            TranscriptSessionLoadResult imported = store.ImportAudioFile(audioPath);

            imported.Document.Transcript.Lines = new List<TranscriptSessionLineDocument> {
                new() {
                    Text = "hello",
                    SpeakerLabel = "Speaker 1",
                    SpeakerLabelSource = SpeakerLabelSources.DiarizationPartial,
                    DiarizationRevision = 3,
                    LastDiarizedChunkIndex = 2,
                    StartSeconds = 1,
                    EndSeconds = 2,
                },
            };
            imported.Document.Transcript.SpeakerDiarizationJob = new SpeakerDiarizationJobDocument {
                Status = SpeakerDiarizationJobStatuses.Failed,
                Engine = "pyannote-community-1",
                JobVersion = 1,
                AudioFingerprint = "audio",
                TranscriptFingerprint = "transcript",
                ChunkDurationSeconds = 300,
                OverlapDurationSeconds = 30,
                TotalChunks = 4,
                LastCompletedChunkIndex = 1,
                StartedUtc = DateTimeOffset.Parse("2026-05-02T01:00:00Z"),
                LastUpdatedUtc = DateTimeOffset.Parse("2026-05-02T01:05:00Z"),
                LastError = "failed",
                Revision = 3,
                NextSpeakerIndex = 4,
                SpeakerMappings = [
                    new SpeakerDiarizationSpeakerMapDocument {
                        ChunkSpeakerKey = "1:speaker_1",
                        GlobalSpeakerLabel = "Speaker 2",
                    },
                ],
            };

            store.Save(imported.Document);

            TranscriptSessionLoadResult reloaded = store.LoadSession(imported.Document.SessionId);

            Assert.Single(reloaded.Document.Transcript.Lines);
            Assert.Equal(SpeakerLabelSources.DiarizationPartial, reloaded.Document.Transcript.Lines[0].SpeakerLabelSource);
            Assert.Equal(3, reloaded.Document.Transcript.Lines[0].DiarizationRevision);
            Assert.Equal(2, reloaded.Document.Transcript.Lines[0].LastDiarizedChunkIndex);
            Assert.Equal(SpeakerDiarizationJobStatuses.Failed, reloaded.Document.Transcript.SpeakerDiarizationJob.Status);
            Assert.Equal(1, reloaded.Document.Transcript.SpeakerDiarizationJob.LastCompletedChunkIndex);
            Assert.Equal(4, reloaded.Document.Transcript.SpeakerDiarizationJob.TotalChunks);
            Assert.Single(reloaded.Document.Transcript.SpeakerDiarizationJob.SpeakerMappings);
            Assert.Equal("Speaker 2", reloaded.Document.Transcript.SpeakerDiarizationJob.SpeakerMappings[0].GlobalSpeakerLabel);
        }
        finally {
            DeleteDirectory(rootPath);
            File.Delete(audioPath);
        }
    }

    [Fact]
    public void ImportAudioFile_ReusesExistingSessionForSameAudio() {
        string rootPath = CreateTempDirectory();
        string audioPath = CreateSilentWaveFile(16000);

        try {
            var store = new TranscriptSessionStore(rootPath);

            TranscriptSessionLoadResult first = store.ImportAudioFile(audioPath);
            TranscriptSessionLoadResult second = store.ImportAudioFile(audioPath);

            string sessionDirectory = Path.Combine(rootPath, first.Document.SessionId);
            string audioDirectory = Path.Combine(sessionDirectory, "audio");

            Assert.Equal(first.Document.SessionId, second.Document.SessionId);
            Assert.Single(Directory.EnumerateFiles(audioDirectory));
        }
        finally {
            DeleteDirectory(rootPath);
            File.Delete(audioPath);
        }
    }

    [Fact]
    public void ListRecentSessions_ReturnsMostRecentlyCreatedFirst() {
        string rootPath = CreateTempDirectory();
        string firstAudioPath = CreateSilentWaveFile(16000);
        string secondAudioPath = CreateSilentWaveFile(32000);

        try {
            var store = new TranscriptSessionStore(rootPath);

            TranscriptSessionLoadResult first = store.ImportAudioFile(firstAudioPath);
            first.Document.CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
            TranscriptSessionLoadResult second = store.ImportAudioFile(secondAudioPath);

            store.Save(first.Document);
            second.Document.CreatedUtc = DateTimeOffset.UtcNow;
            store.Save(second.Document);

            IReadOnlyList<TranscriptSessionSummary> recent = store.ListRecentSessions();

            Assert.True(recent.Count >= 2);
            Assert.Equal(second.Document.SessionId, recent[0].SessionId);
            Assert.Equal(first.Document.SessionId, recent[1].SessionId);
        }
        finally {
            DeleteDirectory(rootPath);
            File.Delete(firstAudioPath);
            File.Delete(secondAudioPath);
        }
    }

    [Fact]
    public void RestoreAudioFile_RejectsMismatchedFingerprint() {
        string rootPath = CreateTempDirectory();
        string firstAudioPath = CreateSilentWaveFile(16000);
        string secondAudioPath = CreateSilentWaveFile(48000);

        try {
            var store = new TranscriptSessionStore(rootPath);
            TranscriptSessionLoadResult imported = store.ImportAudioFile(firstAudioPath);

            string storedAudioPath = imported.AudioFilePath!;
            File.Delete(storedAudioPath);

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                store.RestoreAudioFile(imported.Document.SessionId, secondAudioPath));

            Assert.Contains("does not match", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally {
            DeleteDirectory(rootPath);
            File.Delete(firstAudioPath);
            File.Delete(secondAudioPath);
        }
    }

    [Fact]
    public void DeleteSession_RemovesSessionDirectoryAndRecentEntry() {
        string rootPath = CreateTempDirectory();
        string audioPath = CreateSilentWaveFile(16000);

        try {
            var store = new TranscriptSessionStore(rootPath);
            TranscriptSessionLoadResult imported = store.ImportAudioFile(audioPath);
            string sessionDirectory = store.GetSessionDirectoryPath(imported.Document.SessionId);

            Assert.True(Directory.Exists(sessionDirectory));

            store.DeleteSession(imported.Document.SessionId);

            Assert.False(Directory.Exists(sessionDirectory));
            Assert.DoesNotContain(
                store.ListRecentSessions(),
                session => string.Equals(session.SessionId, imported.Document.SessionId, StringComparison.OrdinalIgnoreCase));
        }
        finally {
            DeleteDirectory(rootPath);
            File.Delete(audioPath);
        }
    }

    [Fact]
    public void Save_DoesNotPersistSelectedTranscriptMode() {
        string rootPath = CreateTempDirectory();
        string audioPath = CreateSilentWaveFile(16000);

        try {
            var store = new TranscriptSessionStore(rootPath);
            TranscriptSessionLoadResult imported = store.ImportAudioFile(audioPath);

            store.Save(imported.Document);

            string sessionPath = Path.Combine(rootPath, imported.Document.SessionId, "session.json");
            string json = File.ReadAllText(sessionPath);

            Assert.DoesNotContain("SelectedTranscriptMode", json, StringComparison.OrdinalIgnoreCase);
        }
        finally {
            DeleteDirectory(rootPath);
            File.Delete(audioPath);
        }
    }

    [Fact]
    public async Task LiveRecordingSession_WritesSegmentedManifestAndSessionMetadata() {
        string rootPath = CreateTempDirectory();

        try {
            var store = new TranscriptSessionStore(rootPath);
            TranscriptSessionLoadResult liveSession = store.CreateLiveSession("Live Test");
            LiveRecordingSessionCreateResult recordingResult = store.CreateLiveRecordingSession(
                liveSession.Document.SessionId,
                "Test Source",
                TimeSpan.FromMilliseconds(100));
            LiveRecordingSession recording = recordingResult.RecordingSession;

            Assert.Equal(AudioStorageKinds.LiveRecordingManifest, recordingResult.Audio.StorageKind);
            Assert.Equal(TranscriptSessionStore.LiveRecordingManifestRelativePath, recordingResult.Audio.StoredRelativePath);

            recording.Start();
            recording.WriteFrame(new LoopbackAudioFrameEventArgs(new byte[3200], StandardizingAudioCaptureService.StandardFormat));
            recording.WriteFrame(new LoopbackAudioFrameEventArgs(new byte[3200], StandardizingAudioCaptureService.StandardFormat));
            await recording.CompleteAsync();

            TranscriptSessionLoadResult updated = store.UpdateLiveRecordingMetadata(liveSession.Document.SessionId);

            Assert.True(updated.AudioAvailable);
            Assert.Equal(AudioStorageKinds.LiveRecordingManifest, updated.Document.Audio.StorageKind);
            Assert.Equal(TranscriptSessionStore.LiveRecordingManifestRelativePath, updated.Document.Audio.StoredRelativePath);
            Assert.True(updated.Document.Audio.FileSizeBytes > 0);
            Assert.True(updated.Document.Audio.DurationSeconds > 0);

            LiveRecordingManifest manifest = TranscriptSessionStore.LoadLiveRecordingManifest(updated.AudioFilePath!);
            Assert.Equal(LiveRecordingManifestStatuses.Completed, manifest.Status);
            Assert.True(manifest.Segments.Count >= 2);
            Assert.Empty(Directory.EnumerateFiles(rootPath, "*.tmp", SearchOption.AllDirectories));
        }
        finally {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void LoadSession_RepairsRecordingManifestStatusToInterrupted() {
        string rootPath = CreateTempDirectory();

        try {
            var store = new TranscriptSessionStore(rootPath);
            TranscriptSessionLoadResult liveSession = store.CreateLiveSession("Interrupted Live Test");
            string sessionDirectory = store.GetSessionDirectoryPath(liveSession.Document.SessionId);
            string liveDirectory = Path.Combine(sessionDirectory, "audio", "live");
            Directory.CreateDirectory(liveDirectory);
            string segmentPath = Path.Combine(liveDirectory, "segment-000001.wav");
            CreateSilentWaveFile(segmentPath, 3200);

            var manifest = new LiveRecordingManifest {
                Status = LiveRecordingManifestStatuses.Recording,
                InputSource = "Test Source",
                TotalDurationSeconds = 0.1,
                TotalFileSizeBytes = new FileInfo(segmentPath).Length,
                Segments = [
                    new LiveRecordingSegmentManifest {
                        RelativePath = "audio/live/segment-000001.wav",
                        StartSeconds = 0,
                        DurationSeconds = 0.1,
                        FileSizeBytes = new FileInfo(segmentPath).Length,
                    },
                ],
            };
            string manifestPath = Path.Combine(liveDirectory, "manifest.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            liveSession.Document.Audio.StorageKind = AudioStorageKinds.LiveRecordingManifest;
            liveSession.Document.Audio.StoredRelativePath = TranscriptSessionStore.LiveRecordingManifestRelativePath;
            store.Save(liveSession.Document);

            TranscriptSessionLoadResult loaded = store.LoadSession(liveSession.Document.SessionId);

            Assert.True(loaded.AudioAvailable);
            LiveRecordingManifest repaired = TranscriptSessionStore.LoadLiveRecordingManifest(manifestPath);
            Assert.Equal(LiveRecordingManifestStatuses.Interrupted, repaired.Status);
        }
        finally {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void LoadSession_RepairsBlankLiveRecordingManifestPathWhenManifestExists() {
        string rootPath = CreateTempDirectory();

        try {
            var store = new TranscriptSessionStore(rootPath);
            TranscriptSessionLoadResult liveSession = store.CreateLiveSession("Repair Blank Manifest Path Test");
            string sessionDirectory = store.GetSessionDirectoryPath(liveSession.Document.SessionId);
            string liveDirectory = Path.Combine(sessionDirectory, "audio", "live");
            Directory.CreateDirectory(liveDirectory);
            string segmentPath = Path.Combine(liveDirectory, "segment-000001.wav");
            CreateSilentWaveFile(segmentPath, 3200);

            var manifest = new LiveRecordingManifest {
                Status = LiveRecordingManifestStatuses.Completed,
                InputSource = "Test Source",
                TotalDurationSeconds = 0.1,
                TotalFileSizeBytes = new FileInfo(segmentPath).Length,
                Segments = [
                    new LiveRecordingSegmentManifest {
                        RelativePath = "audio/live/segment-000001.wav",
                        StartSeconds = 0,
                        DurationSeconds = 0.1,
                        FileSizeBytes = new FileInfo(segmentPath).Length,
                    },
                ],
            };
            string manifestPath = Path.Combine(liveDirectory, "manifest.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            liveSession.Document.Audio.StorageKind = AudioStorageKinds.LiveRecordingManifest;
            liveSession.Document.Audio.StoredRelativePath = string.Empty;
            store.Save(liveSession.Document);

            TranscriptSessionLoadResult loaded = store.LoadSession(liveSession.Document.SessionId);

            Assert.True(loaded.AudioAvailable);
            Assert.Equal(TranscriptSessionStore.LiveRecordingManifestRelativePath, loaded.Document.Audio.StoredRelativePath);
            Assert.Equal(Path.GetFullPath(manifestPath), Path.GetFullPath(loaded.AudioFilePath!));
        }
        finally {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void LoadSession_OldSchemaDefaultsToImportedAudioStorage() {
        string rootPath = CreateTempDirectory();
        string audioPath = CreateSilentWaveFile(16000);

        try {
            var store = new TranscriptSessionStore(rootPath);
            TranscriptSessionLoadResult imported = store.ImportAudioFile(audioPath);
            imported.Document.Audio.StorageKind = string.Empty;
            store.Save(imported.Document);

            TranscriptSessionLoadResult loaded = store.LoadSession(imported.Document.SessionId);

            Assert.Equal(AudioStorageKinds.ImportedFile, loaded.Document.Audio.StorageKind);
            Assert.True(loaded.AudioAvailable);
        }
        finally {
            DeleteDirectory(rootPath);
            File.Delete(audioPath);
        }
    }

    [Fact]
    public void SegmentedLiveRecordingWaveStream_SeeksAcrossSegments() {
        string rootPath = CreateTempDirectory();

        try {
            string sessionDirectory = Path.Combine(rootPath, "live-test");
            string liveDirectory = Path.Combine(sessionDirectory, "audio", "live");
            Directory.CreateDirectory(liveDirectory);
            string firstSegmentPath = Path.Combine(liveDirectory, "segment-000001.wav");
            string secondSegmentPath = Path.Combine(liveDirectory, "segment-000002.wav");
            CreateToneWaveFile(firstSegmentPath, sampleValue: 1000, sampleCount: 1600);
            CreateToneWaveFile(secondSegmentPath, sampleValue: 2000, sampleCount: 1600);

            var manifest = new LiveRecordingManifest {
                Status = LiveRecordingManifestStatuses.Completed,
                Segments = [
                    new LiveRecordingSegmentManifest {
                        RelativePath = "audio/live/segment-000001.wav",
                        StartSeconds = 0,
                        DurationSeconds = 0.1,
                        FileSizeBytes = new FileInfo(firstSegmentPath).Length,
                    },
                    new LiveRecordingSegmentManifest {
                        RelativePath = "audio/live/segment-000002.wav",
                        StartSeconds = 0.1,
                        DurationSeconds = 0.1,
                        FileSizeBytes = new FileInfo(secondSegmentPath).Length,
                    },
                ],
            };
            string manifestPath = Path.Combine(liveDirectory, "manifest.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));

            using var stream = new SegmentedLiveRecordingWaveStream(manifestPath);
            stream.CurrentTime = TimeSpan.FromSeconds(0.1);
            byte[] buffer = new byte[2];
            int read = stream.Read(buffer, 0, buffer.Length);

            Assert.Equal(2, read);
            Assert.Equal(2000, BitConverter.ToInt16(buffer, 0));
        }
        finally {
            DeleteDirectory(rootPath);
        }
    }

    private static string CreateTempDirectory() {
        string path = Path.Combine(Path.GetTempPath(), $"AudioScript-session-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateSilentWaveFile(long dataBytes) {
        string path = Path.Combine(Path.GetTempPath(), $"AudioScript-session-audio-{Guid.NewGuid():N}.wav");
        CreateSilentWaveFile(path, dataBytes);
        return path;
    }

    private static void CreateSilentWaveFile(string path, long dataBytes) {
        int sampleRate = 16000;
        short channels = 1;
        short bitsPerSample = 16;
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        int byteRate = sampleRate * blockAlign;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write((int)(36 + dataBytes));
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write((int)dataBytes);

        stream.SetLength(44 + dataBytes);
    }

    private static void CreateToneWaveFile(string path, short sampleValue, int sampleCount) {
        int sampleRate = 16000;
        short channels = 1;
        short bitsPerSample = 16;
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        int byteRate = sampleRate * blockAlign;
        int dataBytes = sampleCount * blockAlign;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        for (int index = 0; index < sampleCount; index++) {
            writer.Write(sampleValue);
        }
    }

    private static void DeleteDirectory(string path) {
        if (!Directory.Exists(path)) {
            return;
        }

        Directory.Delete(path, recursive: true);
    }
}



