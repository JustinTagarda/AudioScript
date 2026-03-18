using System.Reflection;

namespace VoxTranscriber.Services;

public static class ApplicationDeploymentInfo {
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Metadata = new(LoadMetadata);

    public static string PackId => GetValue("VelopackPackId");

    public static string PackTitle => GetValue("VelopackPackTitle");

    public static string PackAuthors => GetValue("VelopackPackAuthors");

    public static string MainExe => GetValue("VelopackMainExe");

    public static string ReleaseChannel => GetValue("VelopackReleaseChannel");

    public static string ReleaseRepoUrl => GetValue("VelopackReleaseRepoUrl");

    public static Version CurrentVersion => typeof(ApplicationDeploymentInfo).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);

    public static bool HasConfiguredReleaseRepo {
        get {
            if (string.IsNullOrWhiteSpace(ReleaseRepoUrl)) {
                return false;
            }

            if (ReleaseRepoUrl.Contains("REPLACE_ME", StringComparison.OrdinalIgnoreCase)) {
                return false;
            }

            return Uri.TryCreate(ReleaseRepoUrl, UriKind.Absolute, out Uri? uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
        }
    }

    private static IReadOnlyDictionary<string, string> LoadMetadata() {
        return typeof(ApplicationDeploymentInfo).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .GroupBy(attribute => attribute.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Value ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
    }

    private static string GetValue(string key) {
        return Metadata.Value.TryGetValue(key, out string? value)
            ? value
            : string.Empty;
    }
}


