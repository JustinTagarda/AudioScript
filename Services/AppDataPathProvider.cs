using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AudioScript.Services;

public sealed class AppDataPathProvider
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    public AppDataPathProvider(string? localAppDataPath = null, string? packageFamilyName = null)
    {
        string packagedAssetsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "assets", "prebuilt"));
        string localAppData = string.IsNullOrWhiteSpace(localAppDataPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Path.GetFullPath(localAppDataPath);
        string? normalizedPackageFamilyName = string.IsNullOrWhiteSpace(packageFamilyName)
            ? null
            : packageFamilyName.Trim();

        LegacyRootPath = Path.Combine(localAppData, "AudioScript");
        IsPackaged = !string.IsNullOrWhiteSpace(normalizedPackageFamilyName);
        RootPath = IsPackaged
            ? Path.Combine(localAppData, "Packages", normalizedPackageFamilyName!, "LocalState")
            : LegacyRootPath;
        ModelsPath = Path.Combine(RootPath, "Models");
        ProvisioningPath = Path.Combine(RootPath, "Provisioning");
        ProvisionedAssetsPath = Path.Combine(RootPath, "Assets");
        PyannoteAssetsPath = Path.Combine(ProvisionedAssetsPath, "Pyannote");
        PythonRuntimesPath = Path.Combine(ProvisionedAssetsPath, "Python");
        ToolsPath = Path.Combine(ProvisionedAssetsPath, "Tools");
        SessionsPath = Path.Combine(RootPath, "Sessions");
        LogsPath = Path.Combine(RootPath, "Logs");
        TempPath = Path.Combine(RootPath, "Temp");
        SettingsPath = Path.Combine(RootPath, "Settings");
        SettingsFilePath = Path.Combine(SettingsPath, "app-preferences.json");
        PackagedAssetsPath = packagedAssetsRoot;
        PackagedModelsPath = Path.Combine(packagedAssetsRoot, "models");
        PackagedPyannotePath = Path.Combine(packagedAssetsRoot, "pyannote");
        PackagedPythonPath = Path.Combine(packagedAssetsRoot, "python");
        PackagedToolsPath = Path.Combine(packagedAssetsRoot, "tools");
    }

    public string RootPath { get; }

    public string ModelsPath { get; }

    public string ProvisioningPath { get; }

    public string ProvisionedAssetsPath { get; }

    public string PyannoteAssetsPath { get; }

    public string PythonRuntimesPath { get; }

    public string ToolsPath { get; }

    public string SessionsPath { get; }

    public string LogsPath { get; }

    public string TempPath { get; }

    public string SettingsPath { get; }

    public string SettingsFilePath { get; }

    public string PackagedAssetsPath { get; }

    public string PackagedModelsPath { get; }

    public string PackagedPyannotePath { get; }

    public string PackagedPythonPath { get; }

    public string PackagedToolsPath { get; }

    public bool IsPackaged { get; }

    public string LegacyRootPath { get; }

    public static AppDataPathProvider Create()
    {
        return new AppDataPathProvider(packageFamilyName: TryGetCurrentPackageFamilyName());
    }

    private static string? TryGetCurrentPackageFamilyName()
    {
        int length = 0;
        int result = GetCurrentPackageFamilyName(ref length, null);
        if (result == AppModelErrorNoPackage)
        {
            return null;
        }

        if (result != ErrorInsufficientBuffer || length <= 0)
        {
            return null;
        }

        var builder = new StringBuilder(length);
        result = GetCurrentPackageFamilyName(ref length, builder);
        return result == 0
            ? builder.ToString()
            : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetCurrentPackageFamilyName(ref int packageFamilyNameLength, StringBuilder? packageFamilyName);
}
