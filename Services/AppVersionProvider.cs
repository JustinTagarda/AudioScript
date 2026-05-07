using System.Reflection;
using Windows.ApplicationModel;

namespace AudioScript.Services;

public sealed class AppVersionProvider : IAppVersionProvider
{
    private readonly Lazy<AppVersionInfo> _versionInfo;

    public AppVersionProvider()
    {
        _versionInfo = new Lazy<AppVersionInfo>(ResolveVersionInfo);
    }

    public bool IsPackaged => _versionInfo.Value.IsPackaged;

    public string InstalledVersion => _versionInfo.Value.VersionText;

    public string DisplayVersionText => $"Version {InstalledVersion}";

    public static string FormatVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);

        if (version.Revision > 0)
        {
            return version.ToString(4);
        }

        if (version.Build >= 0)
        {
            return version.ToString(3);
        }

        return version.ToString(2);
    }

    private static AppVersionInfo ResolveVersionInfo()
    {
        try
        {
            PackageId packageId = Package.Current.Id;
            PackageVersion version = packageId.Version;
            return new AppVersionInfo(
                IsPackaged: true,
                VersionText: $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}");
        }
        catch
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            return new AppVersionInfo(IsPackaged: false, VersionText: FormatVersion(version));
        }
    }

    private sealed record AppVersionInfo(bool IsPackaged, string VersionText);
}

