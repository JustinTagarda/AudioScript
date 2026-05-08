using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class ProcessLogServiceTests
{
    [Fact]
    public void Log_WritesEachMultilineEntryWithDiagnosticPrefix()
    {
        string rootPath = CreateTempDirectory();

        try
        {
            using var logs = new ProcessLogService(Path.Combine(rootPath, "logs"));

            logs.Log("UnitTest", "first line" + Environment.NewLine + "second line");

            string logText = File.ReadAllText(logs.LogFilePath);
            Assert.Contains("[UnitTest] first line", logText, StringComparison.Ordinal);
            Assert.Contains("[CONT] second line", logText, StringComparison.Ordinal);
            Assert.Contains("[PID ", logText, StringComparison.Ordinal);
            Assert.Contains("[v", logText, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void LogEnvironmentSnapshot_WritesProcessAndPlatformDetails()
    {
        string rootPath = CreateTempDirectory();

        try
        {
            using var logs = new ProcessLogService(Path.Combine(rootPath, "logs"));

            logs.LogEnvironmentSnapshot("Environment", "C:\\Temp\\AudioScript", isPackaged: true);

            string logText = File.ReadAllText(logs.LogFilePath);
            Assert.Contains("[Environment] Process='", logText, StringComparison.Ordinal);
            Assert.Contains("framework='", logText, StringComparison.Ordinal);
            Assert.Contains("app data root='C:\\Temp\\AudioScript', packaged=True", logText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"AudioScript-process-log-tests-{Guid.NewGuid():N}");
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
