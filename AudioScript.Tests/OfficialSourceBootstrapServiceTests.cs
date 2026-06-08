using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class OfficialSourceBootstrapServiceTests
{
    [Fact]
    public void EnsureEmbeddedRuntimeSitePackagesAreImportable_AddsSitePackagesToPthFile()
    {
        string rootPath = CreateTempDirectory();
        try
        {
            string runtimeRoot = Path.Combine(rootPath, "python", "win-x64");
            Directory.CreateDirectory(runtimeRoot);
            File.WriteAllText(
                Path.Combine(runtimeRoot, "python312._pth"),
                string.Join(Environment.NewLine, new[]
                {
                    "python312.zip",
                    ".",
                    string.Empty,
                    "# Uncomment to run site.main() automatically",
                    "import site",
                }));

            OfficialSourceBootstrapService.EnsureEmbeddedRuntimeSitePackagesAreImportable(runtimeRoot);

            string updated = File.ReadAllText(Path.Combine(runtimeRoot, "python312._pth"));
            Assert.Contains("Lib\\site-packages", updated, StringComparison.Ordinal);
            Assert.Contains("import site", updated, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"AudioScript-official-bootstrap-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

}
