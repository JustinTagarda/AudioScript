using System.Text.Json.Serialization;

namespace AudioScript.Services;

public enum ProvisioningInstallKind
{
    File,
    Directory
}

public enum ProvisioningInstallRoot
{
    Models,
    Pyannote,
    Python,
    Tools
}

public enum AssetDeliveryMode
{
    ProvisionedOptional,
    PackagedRequired,
}

public enum AssetProvisioningState
{
    Missing,
    Ready,
    Unsupported,
    Unconfigured
}

public sealed record AssetProvisioningProgress(
    string AssetId,
    string DisplayName,
    string Status,
    long BytesReceived,
    long? TotalBytes,
    double Percent);

public sealed record AssetProvisioningStatus(
    string AssetId,
    string DisplayName,
    AssetProvisioningState State,
    string InstallPath,
    string? Message = null);

public sealed record ProvisionedAssetDescriptor
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("downloadUri")]
    public string? DownloadUri { get; init; }

    [JsonPropertyName("downloadSources")]
    public string[] DownloadSources { get; init; } = Array.Empty<string>();

    [JsonPropertyName("developmentSourceRelativePath")]
    public string? DevelopmentSourceRelativePath { get; init; }

    [JsonPropertyName("packagedSourceRelativePath")]
    public string? PackagedSourceRelativePath { get; init; }

    [JsonPropertyName("deliveryMode")]
    public AssetDeliveryMode DeliveryMode { get; init; } = AssetDeliveryMode.ProvisionedOptional;

    [JsonPropertyName("installKind")]
    public ProvisioningInstallKind InstallKind { get; init; }

    [JsonPropertyName("installRoot")]
    public ProvisioningInstallRoot InstallRoot { get; init; }

    [JsonPropertyName("installRelativePath")]
    public string InstallRelativePath { get; init; } = string.Empty;

    [JsonPropertyName("expectedBytes")]
    public long? ExpectedBytes { get; init; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    [JsonPropertyName("supportedArchitectures")]
    public string[] SupportedArchitectures { get; init; } = Array.Empty<string>();

    [JsonPropertyName("required")]
    public bool Required { get; init; }

    [JsonPropertyName("releaseRequired")]
    public bool ReleaseRequired { get; init; }

    [JsonPropertyName("minimumDownloadSources")]
    public int? MinimumDownloadSources { get; init; }

    public bool IsPackagedRequired =>
        DeliveryMode == AssetDeliveryMode.PackagedRequired;
}

public sealed record AssetProvisioningManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("assets")]
    public ProvisionedAssetDescriptor[] Assets { get; init; } = Array.Empty<ProvisionedAssetDescriptor>();
}

internal sealed class ProvisionedAssetStateDocument
{
    public int SchemaVersion { get; set; } = 1;

    public List<ProvisionedAssetStateItem> Assets { get; set; } = new();
}

internal sealed class ProvisionedAssetStateItem
{
    public string AssetId { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string InstallPath { get; set; } = string.Empty;

    public DateTimeOffset InstalledAtUtc { get; set; }
}
