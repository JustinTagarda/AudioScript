using System.IO;
using System.Text.Json;
using AudioScript.Abstractions;
using AudioScript.Audio;

namespace AudioScript.Services;

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

        _settingsFilePath = AppDataPathProvider.Create().SettingsFilePath;
    }

    public AppPreferencesSnapshot Load() {
        if (!File.Exists(_settingsFilePath)) {
            return new AppPreferencesSnapshot(
                CopyFinalizedWithTimeline: false,
                AutoTranscribeWithAi: false,
                ThemePreference: AppThemePreference.System,
                AutoPlayTimelineSelection: true,
                LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                LiveAudioDeviceNumber: -1,
                SelectedEngineId: TranscriptionModelCatalog.WhisperSmall,
                LiveAudioAutoGainEnabled: true,
                LiveAudioGainLevel: LiveAudioGainOptions.DefaultManualGainLevel);
        }

        try {
            string json = File.ReadAllText(_settingsFilePath);
            PersistedAppPreferences? persisted = JsonSerializer.Deserialize<PersistedAppPreferences>(json, JsonOptions);

            if (persisted is null) {
                return new AppPreferencesSnapshot(
                    CopyFinalizedWithTimeline: false,
                    AutoTranscribeWithAi: false,
                    ThemePreference: AppThemePreference.System,
                    AutoPlayTimelineSelection: true,
                    LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                    LiveAudioDeviceNumber: -1,
                    SelectedEngineId: TranscriptionModelCatalog.WhisperSmall,
                    LiveAudioAutoGainEnabled: true,
                    LiveAudioGainLevel: LiveAudioGainOptions.DefaultManualGainLevel);
            }

            return new AppPreferencesSnapshot(
                CopyFinalizedWithTimeline: persisted.CopyFinalizedWithTimeline,
                AutoTranscribeWithAi: persisted.AutoTranscribeWithAi,
                ThemePreference: ParseThemePreference(persisted.ThemePreference),
                AutoPlayTimelineSelection: persisted.AutoPlayTimelineSelection ?? true,
                LiveAudioSourceKind: ParseLiveAudioSourceKind(persisted.LiveAudioSourceKind),
                LiveAudioDeviceNumber: persisted.LiveAudioDeviceNumber ?? -1,
                SelectedEngineId: NormalizeSelectedEngineId(persisted.SelectedEngineId),
                LiveAudioAutoGainEnabled: true,
                LiveAudioGainLevel: LiveAudioGainOptions.DefaultManualGainLevel);
        }
        catch {
            return new AppPreferencesSnapshot(
                CopyFinalizedWithTimeline: false,
                AutoTranscribeWithAi: false,
                ThemePreference: AppThemePreference.System,
                AutoPlayTimelineSelection: true,
                LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                LiveAudioDeviceNumber: -1,
                SelectedEngineId: TranscriptionModelCatalog.WhisperSmall,
                LiveAudioAutoGainEnabled: true,
                LiveAudioGainLevel: LiveAudioGainOptions.DefaultManualGainLevel);
        }
    }

    public void Save(AppPreferencesSnapshot snapshot) {
        try {
            string directory = Path.GetDirectoryName(_settingsFilePath)!;
            Directory.CreateDirectory(directory);

            var persisted = new PersistedAppPreferences {
                CopyFinalizedWithTimeline = snapshot.CopyFinalizedWithTimeline,
                AutoTranscribeWithAi = snapshot.AutoTranscribeWithAi,
                ThemePreference = snapshot.ThemePreference.ToString(),
                AutoPlayTimelineSelection = snapshot.AutoPlayTimelineSelection,
                LiveAudioSourceKind = snapshot.LiveAudioSourceKind.ToString(),
                LiveAudioDeviceNumber = snapshot.LiveAudioDeviceNumber,
                SelectedEngineId = snapshot.SelectedEngineId,
                LiveAudioAutoGainEnabled = true,
                LiveAudioGainLevel = LiveAudioGainOptions.DefaultManualGainLevel,
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

    private static AppThemePreference ParseThemePreference(string? value) {
        return Enum.TryParse(value, ignoreCase: true, out AppThemePreference preference)
            ? preference
            : AppThemePreference.System;
    }

    private static LiveAudioSourceKind ParseLiveAudioSourceKind(string? value) {
        return Enum.TryParse(value, ignoreCase: true, out LiveAudioSourceKind kind)
            ? kind
            : LiveAudioSourceKind.DefaultPlayback;
    }

    private static string NormalizeSelectedEngineId(string? value) {
        string trimmed = value?.Trim() ?? string.Empty;
        if (string.Equals(trimmed, BuildLegacyMinimumWhisperId(), StringComparison.OrdinalIgnoreCase)) {
            return TranscriptionModelCatalog.WhisperSmall;
        }

        return TranscriptionModelCatalog.IsRecognizedTranscriptionEngine(trimmed)
            ? trimmed
            : TranscriptionModelCatalog.WhisperSmall;
    }

    private static string BuildLegacyMinimumWhisperId() {
        return string.Concat("whisper", "-", "base");
    }

    private sealed class PersistedAppPreferences {
        public bool CopyFinalizedWithTimeline { get; init; }

        public bool AutoTranscribeWithAi { get; init; }

        public string? ThemePreference { get; init; }

        public bool? AutoPlayTimelineSelection { get; init; }

        public string? LiveAudioSourceKind { get; init; }

        public int? LiveAudioDeviceNumber { get; init; }

        public string? SelectedEngineId { get; init; }

        public bool? LiveAudioAutoGainEnabled { get; init; }

        public double? LiveAudioGainLevel { get; init; }
    }
}

public sealed record AppPreferencesSnapshot(
    bool CopyFinalizedWithTimeline,
    bool AutoTranscribeWithAi,
    AppThemePreference ThemePreference,
    bool AutoPlayTimelineSelection,
    LiveAudioSourceKind LiveAudioSourceKind,
    int LiveAudioDeviceNumber,
    string SelectedEngineId = TranscriptionModelCatalog.WhisperSmall,
    bool LiveAudioAutoGainEnabled = true,
    double LiveAudioGainLevel = LiveAudioGainOptions.DefaultManualGainLevel);



