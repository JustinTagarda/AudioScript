using System.IO;
using System.Text.Json;

namespace VoxTranscribe.Services;

public sealed class AppPreferencesStore {
    private static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
    };

    private readonly string _settingsFilePath;

    public AppPreferencesStore(string? settingsFilePath = null) {
        if (!string.IsNullOrWhiteSpace(settingsFilePath)) {
            _settingsFilePath = Path.GetFullPath(settingsFilePath);
            return;
        }

        string appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoxTranscribe");
        _settingsFilePath = Path.Combine(appDataDirectory, "app-preferences.json");
    }

    public AppPreferencesSnapshot Load() {
        if (!File.Exists(_settingsFilePath)) {
            return new AppPreferencesSnapshot(
                CopyFinalizedWithTimeline: false,
                AutoTranscribeWithAi: false);
        }

        try {
            string json = File.ReadAllText(_settingsFilePath);
            PersistedAppPreferences? persisted = JsonSerializer.Deserialize<PersistedAppPreferences>(json, JsonOptions);

            if (persisted is null) {
                return new AppPreferencesSnapshot(
                    CopyFinalizedWithTimeline: false,
                    AutoTranscribeWithAi: false);
            }

            return new AppPreferencesSnapshot(
                CopyFinalizedWithTimeline: persisted.CopyFinalizedWithTimeline,
                AutoTranscribeWithAi: persisted.AutoTranscribeWithAi);
        }
        catch {
            return new AppPreferencesSnapshot(
                CopyFinalizedWithTimeline: false,
                AutoTranscribeWithAi: false);
        }
    }

    public void Save(AppPreferencesSnapshot snapshot) {
        try {
            string directory = Path.GetDirectoryName(_settingsFilePath)!;
            Directory.CreateDirectory(directory);

            var persisted = new PersistedAppPreferences {
                CopyFinalizedWithTimeline = snapshot.CopyFinalizedWithTimeline,
                AutoTranscribeWithAi = snapshot.AutoTranscribeWithAi,
            };

            string json = JsonSerializer.Serialize(persisted, JsonOptions);
            WriteAllTextAtomic(_settingsFilePath, json);
        }
        catch {
            // Keep the UI responsive if preference persistence fails.
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

    private sealed class PersistedAppPreferences {
        public bool CopyFinalizedWithTimeline { get; init; }

        public bool AutoTranscribeWithAi { get; init; }
    }
}

public sealed record AppPreferencesSnapshot(
    bool CopyFinalizedWithTimeline,
    bool AutoTranscribeWithAi);


