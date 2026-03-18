using VoxTranscriber.Services;
using Xunit;

namespace VoxTranscriber.Tests;

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
                AutoTranscribeWithAi: false));
            AppPreferencesSnapshot snapshot = store.Load();

            Assert.True(snapshot.CopyFinalizedWithTimeline);
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
                AutoTranscribeWithAi: true));
            AppPreferencesSnapshot snapshot = store.Load();

            Assert.False(snapshot.CopyFinalizedWithTimeline);
            Assert.True(snapshot.AutoTranscribeWithAi);
            Assert.Empty(Directory.EnumerateFiles(rootPath, "*.tmp", SearchOption.AllDirectories));
        }
        finally {
            DeleteDirectory(rootPath);
        }
    }

    private static string CreateTempDirectory() {
        string path = Path.Combine(Path.GetTempPath(), $"VoxTranscriber-app-prefs-tests-{Guid.NewGuid():N}");
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


