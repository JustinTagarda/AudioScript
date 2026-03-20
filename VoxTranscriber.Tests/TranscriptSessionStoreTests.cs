using System.Text;
using VoxTranscriber.Abstractions;
using VoxTranscriber.Services;
using Xunit;

namespace VoxTranscriber.Tests;

public sealed class TranscriptSessionStoreTests {
    [Fact]
    public void ImportAudioFile_SaveLoadRoundTrip_RestoresTranscriptData() {
        string rootPath = CreateTempDirectory();
        string audioPath = CreateSilentWaveFile(16000);

        try {
            var store = new TranscriptSessionStore(rootPath);

            TranscriptSessionLoadResult imported = store.ImportAudioFile(audioPath);
            imported.Document.Transcript.ModelId = OpenAiTranscriptionModelCatalog.Gpt4oTranscribe;
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
            Assert.Equal(OpenAiTranscriptionModelCatalog.Gpt4oTranscribe, reloaded.Document.Transcript.ModelId);
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
    public void SaveLoadRoundTrip_PreservesSpeakerTranscriptAndEditingState() {
        string rootPath = CreateTempDirectory();
        string audioPath = CreateSilentWaveFile(16000);

        try {
            var store = new TranscriptSessionStore(rootPath);
            TranscriptSessionLoadResult imported = store.ImportAudioFile(audioPath);

            imported.Document.SpeakerTranscript.ModelId = OpenAiTranscriptionModelCatalog.Gpt4oTranscribeDiarize;
            imported.Document.SpeakerTranscript.FinalText = "00:00:01 Speaker 1: hello";
            imported.Document.SpeakerTranscript.Lines = new List<TranscriptSessionLineDocument> {
                new() {
                    Text = "hello",
                    SpeakerLabel = "Speaker 1",
                    StartSeconds = 1,
                    EndSeconds = 2.5,
                },
            };
            imported.Document.Editing.SelectedTranscriptMode = TranscriptGenerationMode.SpeakerDiarization.ToString();
            imported.Document.Editing.SelectedTranscriptViewIndex = 1;

            store.Save(imported.Document);

            TranscriptSessionLoadResult reloaded = store.LoadSession(imported.Document.SessionId);

            Assert.Equal(OpenAiTranscriptionModelCatalog.Gpt4oTranscribeDiarize, reloaded.Document.SpeakerTranscript.ModelId);
            Assert.Equal("00:00:01 Speaker 1: hello", reloaded.Document.SpeakerTranscript.FinalText);
            Assert.Single(reloaded.Document.SpeakerTranscript.Lines);
            Assert.Equal("Speaker 1", reloaded.Document.SpeakerTranscript.Lines[0].SpeakerLabel);
            Assert.Equal(1, reloaded.Document.SpeakerTranscript.Lines[0].StartSeconds);
            Assert.Equal(2.5, reloaded.Document.SpeakerTranscript.Lines[0].EndSeconds);
            Assert.Equal(TranscriptGenerationMode.SpeakerDiarization.ToString(), reloaded.Document.Editing.SelectedTranscriptMode);
            Assert.Equal(1, reloaded.Document.Editing.SelectedTranscriptViewIndex);
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
    public void ListRecentSessions_ReturnsMostRecentlyUpdatedFirst() {
        string rootPath = CreateTempDirectory();
        string firstAudioPath = CreateSilentWaveFile(16000);
        string secondAudioPath = CreateSilentWaveFile(32000);

        try {
            var store = new TranscriptSessionStore(rootPath);

            TranscriptSessionLoadResult first = store.ImportAudioFile(firstAudioPath);
            TranscriptSessionLoadResult second = store.ImportAudioFile(secondAudioPath);

            first.Document.UpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
            store.Save(first.Document);
            second.Document.UpdatedUtc = DateTimeOffset.UtcNow;
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

    private static string CreateTempDirectory() {
        string path = Path.Combine(Path.GetTempPath(), $"VoxTranscriber-session-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateSilentWaveFile(long dataBytes) {
        string path = Path.Combine(Path.GetTempPath(), $"VoxTranscriber-session-audio-{Guid.NewGuid():N}.wav");
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
        return path;
    }

    private static void DeleteDirectory(string path) {
        if (!Directory.Exists(path)) {
            return;
        }

        Directory.Delete(path, recursive: true);
    }
}


