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

    public OpenAiSettingsStore(string? settingsFilePath = null) {
        if (!string.IsNullOrWhiteSpace(settingsFilePath)) {
            _settingsFilePath = settingsFilePath;
            return;
        }

        string appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoxTranscribe");
        _settingsFilePath = Path.Combine(appDataDirectory, "openai-settings.json");
    }

    public OpenAiSettingsSnapshot Load() {
        string apiKey = string.Empty;

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
            // Fall back to empty when reading fails.
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

    public void Clear() {
        try {
            if (File.Exists(_settingsFilePath)) {
                File.Delete(_settingsFilePath);
            }
        }
        catch {
            // Keep settings UI responsive if cleanup fails.
        }

        // Remove environment-level key sources so restart cannot repopulate from OPENAI_API_KEY.
        ClearOpenAiApiKeyEnvironmentVariables();
    }

    private static void ClearOpenAiApiKeyEnvironmentVariables() {
        const string keyName = "OPENAI_API_KEY";

        try {
            Environment.SetEnvironmentVariable(keyName, null, EnvironmentVariableTarget.Process);
        }
        catch {
            // Ignore process-scoped cleanup failures.
        }

        try {
            Environment.SetEnvironmentVariable(keyName, null, EnvironmentVariableTarget.User);
        }
        catch {
            // Ignore user-scoped cleanup failures.
        }

        try {
            Environment.SetEnvironmentVariable(keyName, null, EnvironmentVariableTarget.Machine);
        }
        catch {
            // Ignore machine-scoped cleanup failures (may require elevation).
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


