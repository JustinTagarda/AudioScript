namespace AudioScript.Services.Store;

public sealed class AppVersionService : IAppVersionService
{
    private readonly IAppVersionProvider _versionProvider;

    public AppVersionService(IAppVersionProvider versionProvider)
    {
        _versionProvider = versionProvider ?? throw new ArgumentNullException(nameof(versionProvider));
    }

    public bool IsPackaged => _versionProvider.IsPackaged;

    public string VersionText => $"v{NormalizeFourPartVersion(_versionProvider.InstalledVersion)}";

    private static string NormalizeFourPartVersion(string versionText)
    {
        if (!Version.TryParse(versionText, out Version? version))
        {
            return "0.0.0.0";
        }

        int major = Math.Max(0, version.Major);
        int minor = Math.Max(0, version.Minor);
        int build = Math.Max(0, version.Build);
        int revision = Math.Max(0, version.Revision);
        return $"{major}.{minor}.{build}.{revision}";
    }
}
