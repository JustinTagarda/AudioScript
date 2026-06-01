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

    [Fact]
    public void BuildLiveTranscriptionTimingSummaryLines_AggregatesRecentTelemetry()
    {
        string rootPath = CreateTempDirectory();

        try
        {
            using var logs = new ProcessLogService(Path.Combine(rootPath, "logs"));

            logs.Log("LiveSegmentTranscription", "[request-queued] waitBufferedMs=120 requestQueueWaitMs=15 endToStartMs=135");
            logs.Log("LiveSegmentTranscription", "[worker-picked] requestQueueWaitMs=20 endToStartMs=140");
            logs.Log("LiveSegmentTranscription", "[emit-complete] transcribeMs=410 bufferWaitMs=9 e2eMs=560");
            logs.Log("WhisperLocal", "[semaphore-acquired] waitMs=4 availableAfterWait=1");

            IReadOnlyList<string> summary = logs.BuildLiveTranscriptionTimingSummaryLines(TimeSpan.FromMinutes(15));
            string summaryText = string.Join(Environment.NewLine, summary);

            Assert.Contains("Live timing summary (15m window)", summaryText, StringComparison.Ordinal);
            Assert.Contains("metric requestQueueWaitMs: count=2", summaryText, StringComparison.Ordinal);
            Assert.Contains("metric transcribeMs: count=1", summaryText, StringComparison.Ordinal);
            Assert.Contains("metric waitMs: count=1", summaryText, StringComparison.Ordinal);
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
