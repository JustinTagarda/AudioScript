using System.Text.Json;
using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class OfficialSourceBootstrapManifestTests
{
    [Fact]
    public void AssetManifest_IncludesOfficialSourceBootstrapSection()
    {
        string manifestPath = Path.Combine(ResolveRepoRoot(), "assets", "bootstrap", "asset-manifest.json");
        string json = File.ReadAllText(manifestPath);

        AssetProvisioningManifest? manifest = JsonSerializer.Deserialize<AssetProvisioningManifest>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            });

        Assert.NotNull(manifest);
        Assert.NotNull(manifest!.Bootstrap);
        Assert.Equal("3.12.10", manifest.Bootstrap!.RuntimeVersion);
        Assert.Contains(manifest.Bootstrap.Sources, source => source.Id == "python-embeddable-x64");
        Assert.Contains(manifest.Bootstrap.Sources, source => source.Id == "pip");
        Assert.Contains(manifest.Bootstrap.Sources, source => source.Id == "setuptools");
        Assert.Contains(manifest.Bootstrap.Sources, source => source.Id == "pyannote-community-model");
        Assert.DoesNotContain(manifest.Bootstrap.Sources, source => source.SourceUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(manifest.Bootstrap.Sources, source => source.SourceUrl.Contains("python.org", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(manifest.Bootstrap.Sources, source => source.SourceUrl.Contains("huggingface.co", StringComparison.OrdinalIgnoreCase));

        var runtimeAsset = Assert.Single(manifest.Assets.Where(asset => asset.Id == "pyannote-python-x64"));
        Assert.True(string.IsNullOrWhiteSpace(runtimeAsset.DownloadUri));
        Assert.Empty(runtimeAsset.DownloadSources);
    }

    private static string ResolveRepoRoot()
    {
        string current = AppContext.BaseDirectory;
        DirectoryInfo? directory = new(current);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AudioScript.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
