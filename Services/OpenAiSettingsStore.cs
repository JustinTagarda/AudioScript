using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoxTranscribe.Services;

public sealed class OpenAiSettingsStore {
    private static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
    };

    private readonly string _settingsFilePath;

    public OpenAiSettingsStore() {
        string appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoxTranscribe");
        _settingsFilePath = Path.Combine(appDataDirectory, "openai-settings.json");
    }

    public OpenAiSettingsSnapshot Load(string fallbackApiKey) {
        string apiKey = fallbackApiKey ?? string.Empty;

        if (!File.Exists(_settingsFilePath)) {
            return new OpenAiSettingsSnapshot(apiKey);
        }

        try {
            string json = File.ReadAllText(_settingsFilePath);
            PersistedOpenAiSettings? persisted = JsonSerializer.Deserialize<PersistedOpenAiSettings>(json, JsonOptions);

            if (persisted is null) {
                return new OpenAiSettingsSnapshot(apiKey);
            }

            string decryptedApiKey = Unprotect(persisted.ApiKeyCipherText);
            if (!string.IsNullOrWhiteSpace(decryptedApiKey)) {
                apiKey = decryptedApiKey;
            }

        }
        catch {
            // Fall back to environment/default values when reading fails.
        }

        return new OpenAiSettingsSnapshot(apiKey);
    }

    public void Save(string apiKey) {
        try {
            string directory = Path.GetDirectoryName(_settingsFilePath)!;
            Directory.CreateDirectory(directory);

            var persisted = new PersistedOpenAiSettings {
                ApiKeyCipherText = Protect(apiKey),
            };

            string json = JsonSerializer.Serialize(persisted, JsonOptions);
            File.WriteAllText(_settingsFilePath, json);
        }
        catch {
            // Keep settings UI responsive if persistence fails.
        }
    }

    private static string Protect(string value) {
        if (string.IsNullOrWhiteSpace(value)) {
            return string.Empty;
        }

        byte[] plainBytes = Encoding.UTF8.GetBytes(value.Trim());
        byte[] protectedBytes = ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string Unprotect(string cipherText) {
        if (string.IsNullOrWhiteSpace(cipherText)) {
            return string.Empty;
        }

        try {
            byte[] protectedBytes = Convert.FromBase64String(cipherText);
            byte[] plainBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch {
            return string.Empty;
        }
    }

    private sealed class PersistedOpenAiSettings {
        public string ApiKeyCipherText { get; init; } = string.Empty;
    }
}

public sealed record OpenAiSettingsSnapshot(string ApiKey);


