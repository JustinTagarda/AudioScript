using AudioScript.Abstractions;
using AudioScript.Audio;
using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class AppPreferencesStoreTests {
    [Fact]
    public void Load_ReturnsDefault_WhenSettingsFileDoesNotExist() {
        string rootPath = CreateTempDirectory();
        string settingsPath = Path.Combine(rootPath, "app-preferences.json");

        try {
            var store = new AppPreferencesStore(settingsPath);

            AppPreferencesSnapshot snapshot = store.Load();

            Assert.False(snapshot.CopyFinalizedWithTimeline);
            Assert.False(snapshot.AutoTranscribeWithAi);
            Assert.Equal(AppThemePreference.System, snapshot.ThemePreference);
            Assert.True(snapshot.AutoPlayTimelineSelection);
            Assert.Equal(TranscriptionModelCatalog.WhisperSmall, snapshot.SelectedEngineId);
        }
        finally {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void Save_AndLoad_RoundTripCopyWithTimelinePreference() {
        string rootPath = CreateTempDirectory();
        string settingsPath = Path.Combine(rootPath, "app-preferences.json");

        try {
            var store = new AppPreferencesStore(settingsPath);

            store.Save(new AppPreferencesSnapshot(
                CopyFinalizedWithTimeline: true,
                AutoTranscribeWithAi: false,
                ThemePreference: AppThemePreference.System,
                AutoPlayTimelineSelection: true,
                SelectedTranscriptMode: TranscriptGenerationMode.TranscribeAudio.ToString(),
                LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                LiveAudioDeviceNumber: -1,
                SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));
            AppPreferencesSnapshot snapshot = store.Load();

            Assert.True(snapshot.CopyFinalizedWithTimeline);
            Assert.Equal(AppThemePreference.System, snapshot.ThemePreference);
            Assert.Equal(TranscriptionModelCatalog.WhisperSmall, snapshot.SelectedEngineId);
            Assert.Empty(Directory.EnumerateFiles(rootPath, "*.tmp", SearchOption.AllDirectories));
        }
        finally {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void Save_AndLoad_RoundTripAutoTranscribePreference() {
        string rootPath = CreateTempDirectory();
        string settingsPath = Path.Combine(rootPath, "app-preferences.json");

        try {
            var store = new AppPreferencesStore(settingsPath);

            store.Save(new AppPreferencesSnapshot(
                CopyFinalizedWithTimeline: false,
                AutoTranscribeWithAi: true,
                ThemePreference: AppThemePreference.System,
                AutoPlayTimelineSelection: true,
                SelectedTranscriptMode: TranscriptGenerationMode.TranscribeAudio.ToString(),
                LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                LiveAudioDeviceNumber: -1));
            AppPreferencesSnapshot snapshot = store.Load();

            Assert.False(snapshot.CopyFinalizedWithTimeline);
            Assert.True(snapshot.AutoTranscribeWithAi);
            Assert.Empty(Directory.EnumerateFiles(rootPath, "*.tmp", SearchOption.AllDirectories));
        }
        finally {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void Load_MapsLegacyMinimumWhisperPreferenceToSmall() {
        string rootPath = CreateTempDirectory();
        string settingsPath = Path.Combine(rootPath, "app-preferences.json");

        try {
            Directory.CreateDirectory(rootPath);
            string legacyMinimumModelId = string.Concat("whisper", "-", "base");
            File.WriteAllText(
                settingsPath,
                $$"""
                {
                  "SelectedEngineId": "{{legacyMinimumModelId}}",
                  "SelectedTranscriptMode": "TranscribeAudio",
                  "LiveAudioSourceKind": "DefaultPlayback",
                  "LiveAudioDeviceNumber": -1
                }
                """);
            var store = new AppPreferencesStore(settingsPath);

            AppPreferencesSnapshot snapshot = store.Load();

            Assert.Equal(TranscriptionModelCatalog.WhisperSmall, snapshot.SelectedEngineId);
        }
        finally {
            DeleteDirectory(rootPath);
        }
    }


    [Fact]
    public void Save_AndLoad_RoundTripThemePreference() {
        string rootPath = CreateTempDirectory();
        string settingsPath = Path.Combine(rootPath, "app-preferences.json");

        try {
            var store = new AppPreferencesStore(settingsPath);

            store.Save(new AppPreferencesSnapshot(
                CopyFinalizedWithTimeline: false,
                AutoTranscribeWithAi: false,
                ThemePreference: AppThemePreference.Dark,
                AutoPlayTimelineSelection: false,
                SelectedTranscriptMode: TranscriptGenerationMode.TranscribeAudio.ToString(),
                LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                LiveAudioDeviceNumber: -1));
            AppPreferencesSnapshot snapshot = store.Load();

            Assert.Equal(AppThemePreference.Dark, snapshot.ThemePreference);
            Assert.False(snapshot.AutoPlayTimelineSelection);
            Assert.Empty(Directory.EnumerateFiles(rootPath, "*.tmp", SearchOption.AllDirectories));
        }
        finally {
            DeleteDirectory(rootPath);
        }
    }

    private static string CreateTempDirectory() {
        string path = Path.Combine(Path.GetTempPath(), $"AudioScript-app-prefs-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path) {
        if (!Directory.Exists(path)) {
            return;
        }

        Directory.Delete(path, recursive: true);
    }
}



