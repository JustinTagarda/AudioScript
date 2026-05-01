using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class AppDataPathProviderTests {
    [Fact]
    public void Constructor_WithoutPackageFamilyName_UsesLegacyAudioScriptRoot() {
        string localAppData = CreateTempDirectory();

        try {
            var provider = new AppDataPathProvider(localAppData);

            Assert.False(provider.IsPackaged);
            Assert.Equal(Path.Combine(localAppData, "AudioScript"), provider.RootPath);
            Assert.Equal(Path.Combine(localAppData, "AudioScript", "Models"), provider.ModelsPath);
            Assert.Equal(Path.Combine(localAppData, "AudioScript", "Sessions"), provider.SessionsPath);
            Assert.Equal(Path.Combine(localAppData, "AudioScript", "Logs"), provider.LogsPath);
            Assert.Equal(Path.Combine(localAppData, "AudioScript", "Temp"), provider.TempPath);
            Assert.Equal(Path.Combine(localAppData, "AudioScript", "Settings"), provider.SettingsPath);
        }
        finally {
            DeleteDirectory(localAppData);
        }
    }

    [Fact]
    public void Constructor_WithPackageFamilyName_UsesPackageLocalStateRoot() {
        string localAppData = CreateTempDirectory();

        try {
            var provider = new AppDataPathProvider(localAppData, "JustinTagardaSoftware.AudioScript_abc123");

            Assert.True(provider.IsPackaged);
            Assert.Equal(
                Path.Combine(localAppData, "Packages", "JustinTagardaSoftware.AudioScript_abc123", "LocalState"),
                provider.RootPath);
            Assert.Equal(Path.Combine(localAppData, "AudioScript"), provider.LegacyRootPath);
        }
        finally {
            DeleteDirectory(localAppData);
        }
    }

    private static string CreateTempDirectory() {
        string path = Path.Combine(Path.GetTempPath(), $"AudioScript-app-data-path-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path) {
        if (Directory.Exists(path)) {
            Directory.Delete(path, recursive: true);
        }
    }
}
