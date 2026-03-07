using AudioTranscript.Services;
using Xunit;

namespace AudioTranscript.Tests;

public sealed class AppPreferencesStoreTests {
    [Fact]
    public void Load_ReturnsDefault_WhenSettingsFileDoesNotExist() {
        string rootPath = CreateTempDirectory();
        string settingsPath = Path.Combine(rootPath, "app-preferences.json");

        try {
            var store = new AppPreferencesStore(settingsPath);

            AppPreferencesSnapshot snapshot = store.Load();

            Assert.False(snapshot.CopyFinalizedWithTimeline);
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

            store.Save(copyFinalizedWithTimeline: true);
            AppPreferencesSnapshot snapshot = store.Load();

            Assert.True(snapshot.CopyFinalizedWithTimeline);
            Assert.Empty(Directory.EnumerateFiles(rootPath, "*.tmp", SearchOption.AllDirectories));
        }
        finally {
            DeleteDirectory(rootPath);
        }
    }

    private static string CreateTempDirectory() {
        string path = Path.Combine(Path.GetTempPath(), $"audiotranscript-app-prefs-tests-{Guid.NewGuid():N}");
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
