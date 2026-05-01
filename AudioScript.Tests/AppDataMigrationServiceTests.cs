using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class AppDataMigrationServiceTests {
    [Fact]
    public void MigrateLegacyData_MovesOptionalModelsIntoPackageLocalState() {
        string localAppData = CreateTempDirectory();

        try {
            var provider = new AppDataPathProvider(localAppData, "JustinTagardaSoftware.AudioScript_abc123");
            Directory.CreateDirectory(Path.Combine(provider.LegacyRootPath, "Models"));
            string legacyModelPath = Path.Combine(provider.LegacyRootPath, "Models", "ggml-medium.bin");
            File.WriteAllBytes(legacyModelPath, [1, 2, 3]);

            using var logs = new ProcessLogService(Path.Combine(localAppData, "logs"));
            var modelManager = new WhisperModelManager(logs, provider.ModelsPath, Path.Combine(localAppData, "bundled"));
            var migration = new AppDataMigrationService(provider, logs);

            AppDataMigrationResult result = migration.MigrateLegacyData(modelManager.Models);

            Assert.Equal(1, result.MigratedFileCount);
            Assert.False(result.IsPartialFailure);
            Assert.False(File.Exists(legacyModelPath));
            Assert.True(File.Exists(Path.Combine(provider.ModelsPath, "ggml-medium.bin")));
        }
        finally {
            DeleteDirectory(localAppData);
        }
    }

    [Fact]
    public void MigrateLegacyData_DoesNotOverwriteExistingDestinationModel() {
        string localAppData = CreateTempDirectory();

        try {
            var provider = new AppDataPathProvider(localAppData, "JustinTagardaSoftware.AudioScript_abc123");
            Directory.CreateDirectory(Path.Combine(provider.LegacyRootPath, "Models"));
            Directory.CreateDirectory(provider.ModelsPath);
            string legacyModelPath = Path.Combine(provider.LegacyRootPath, "Models", "ggml-medium.bin");
            string destinationModelPath = Path.Combine(provider.ModelsPath, "ggml-medium.bin");
            File.WriteAllBytes(legacyModelPath, [1]);
            File.WriteAllBytes(destinationModelPath, [2]);

            using var logs = new ProcessLogService(Path.Combine(localAppData, "logs"));
            var modelManager = new WhisperModelManager(logs, provider.ModelsPath, Path.Combine(localAppData, "bundled"));
            var migration = new AppDataMigrationService(provider, logs);

            AppDataMigrationResult result = migration.MigrateLegacyData(modelManager.Models);

            Assert.Equal(0, result.MigratedFileCount);
            Assert.Equal(1, result.SkippedFileCount);
            Assert.True(File.Exists(legacyModelPath));
            Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(destinationModelPath));
        }
        finally {
            DeleteDirectory(localAppData);
        }
    }

    [Fact]
    public void MigrateLegacyData_CopiesPreferencesAndSessionsConservatively() {
        string localAppData = CreateTempDirectory();

        try {
            var provider = new AppDataPathProvider(localAppData, "JustinTagardaSoftware.AudioScript_abc123");
            Directory.CreateDirectory(provider.LegacyRootPath);
            Directory.CreateDirectory(Path.Combine(provider.LegacyRootPath, "Sessions", "session-1"));
            File.WriteAllText(Path.Combine(provider.LegacyRootPath, "app-preferences.json"), "{}");
            File.WriteAllText(Path.Combine(provider.LegacyRootPath, "Sessions", "session-1", "session.json"), "{}");

            using var logs = new ProcessLogService(Path.Combine(localAppData, "logs"));
            var modelManager = new WhisperModelManager(logs, provider.ModelsPath, Path.Combine(localAppData, "bundled"));
            var migration = new AppDataMigrationService(provider, logs);

            AppDataMigrationResult result = migration.MigrateLegacyData(modelManager.Models);

            Assert.True(result.MigratedFileCount >= 2);
            Assert.True(File.Exists(Path.Combine(provider.LegacyRootPath, "app-preferences.json")));
            Assert.True(File.Exists(provider.SettingsFilePath));
            Assert.True(File.Exists(Path.Combine(provider.SessionsPath, "session-1", "session.json")));
        }
        finally {
            DeleteDirectory(localAppData);
        }
    }

    private static string CreateTempDirectory() {
        string path = Path.Combine(Path.GetTempPath(), $"AudioScript-app-data-migration-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path) {
        if (Directory.Exists(path)) {
            Directory.Delete(path, recursive: true);
        }
    }
}
