using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace AudioScript.Services;

public enum ProcessLogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public sealed class ProcessLogService : IDisposable
{
    private static readonly string AssemblyVersion = GetAssemblyVersion();
    private readonly object _sync = new();
    private readonly string _logsDirectoryPath;
    private readonly string _logFilePath;
    private readonly string _runStateFilePath;
    private readonly int _processId;
    private readonly string _processName;
    private readonly string _machineName;
    private readonly string _userName;
    private readonly string _frameworkDescription;
    private readonly string _osDescription;
    private readonly Architecture _processArchitecture;
    private bool _disposed;
    private string _lastCrashContext = "startup";
    private DateTimeOffset _runStartedAtUtc;
    private DateTimeOffset _lastHeartbeatUtc;

    public ProcessLogService(string? logsDirectoryPath = null)
    {
        _logsDirectoryPath = string.IsNullOrWhiteSpace(logsDirectoryPath)
            ? AppDataPathProvider.Create().LogsPath
            : Path.GetFullPath(logsDirectoryPath);

        using Process process = Process.GetCurrentProcess();
        _processId = process.Id;
        _processName = process.ProcessName;
        _machineName = Environment.MachineName;
        _userName = Environment.UserName;
        _frameworkDescription = RuntimeInformation.FrameworkDescription;
        _osDescription = RuntimeInformation.OSDescription;
        _processArchitecture = RuntimeInformation.ProcessArchitecture;

        Directory.CreateDirectory(_logsDirectoryPath);
        _logFilePath = Path.Combine(
            _logsDirectoryPath,
            $"audioscript-{DateTime.Now:yyyyMMdd}.log");
        _runStateFilePath = Path.Combine(_logsDirectoryPath, "audioscript-runstate.json");

        TryLogPreviousUncleanRun();
        _runStartedAtUtc = DateTimeOffset.UtcNow;
        _lastHeartbeatUtc = _runStartedAtUtc;
        PersistRunState(cleanExit: false, currentContext: _lastCrashContext, extra: null);
    }

    public event EventHandler<string>? LogEmitted;

    public string LogFilePath => _logFilePath;

    public IReadOnlyList<string> BuildLiveTranscriptionTimingSummaryLines(TimeSpan window)
    {
        if (window <= TimeSpan.Zero)
        {
            return ["Live timing summary unavailable: invalid window."];
        }

        if (!File.Exists(_logFilePath))
        {
            return ["Live timing summary unavailable: log file does not exist yet."];
        }

        DateTimeOffset cutoff = DateTimeOffset.Now - window;
        var metrics = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        int processedLines = 0;

        try
        {
            foreach (string rawLine in File.ReadLines(_logFilePath))
            {
                if (!TryParseLogTimestamp(rawLine, out DateTimeOffset timestamp) || timestamp < cutoff)
                {
                    continue;
                }

                if (!(rawLine.Contains("[LiveSegmentTranscription]", StringComparison.Ordinal)
                      || rawLine.Contains("[WhisperLocal]", StringComparison.Ordinal)))
                {
                    continue;
                }

                processedLines++;
                ExtractMetric(rawLine, "waitBufferedMs", metrics);
                ExtractMetric(rawLine, "requestQueueWaitMs", metrics);
                ExtractMetric(rawLine, "endToStartMs", metrics);
                ExtractMetric(rawLine, "waitMs", metrics);
                ExtractMetric(rawLine, "transcribeMs", metrics);
                ExtractMetric(rawLine, "bufferWaitMs", metrics);
                ExtractMetric(rawLine, "e2eMs", metrics);
            }
        }
        catch (Exception ex)
        {
            return [$"Live timing summary unavailable: {ex.GetType().Name}: {ex.Message}"];
        }

        if (processedLines == 0 || metrics.Count == 0)
        {
            return [$"Live timing summary: no telemetry samples in the last {window.TotalMinutes:F0} minute(s)."];
        }

        var lines = new List<string>
        {
            $"Live timing summary ({window.TotalMinutes:F0}m window): telemetryLines={processedLines:N0}, metrics={metrics.Count:N0}.",
        };

        foreach ((string metricName, List<double> samples) in metrics.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (samples.Count == 0)
            {
                continue;
            }

            samples.Sort();
            double p50 = Percentile(samples, 0.50);
            double p95 = Percentile(samples, 0.95);
            double avg = samples.Average();
            double max = samples[^1];
            lines.Add(
                $"metric {metricName}: count={samples.Count:N0}, p50={p50:F0}ms, p95={p95:F0}ms, avg={avg:F0}ms, max={max:F0}ms.");
        }

        return lines;
    }

    public void Log(string source, string message)
    {
        Log(source, message, ProcessLogLevel.Info);
    }

    public void Log(string source, string message, ProcessLogLevel level)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        string trimmedSource = source?.Trim() ?? string.Empty;
        string trimmedMessage = message.Trim();
        string payloadPrefix = string.IsNullOrWhiteSpace(trimmedSource)
            ? trimmedMessage
            : $"[{trimmedSource}] {trimmedMessage}";

        string filePayload = BuildLogPrefix(level);

        WriteToFile(filePayload, payloadPrefix);
        TouchHeartbeat();
        LogEmitted?.Invoke(this, payloadPrefix);
    }

    public void UpdateCrashContext(string context, string? extra = null)
    {
        string normalized = string.IsNullOrWhiteSpace(context) ? "unknown" : context.Trim();
        _lastCrashContext = normalized;
        PersistRunState(cleanExit: false, currentContext: normalized, extra: extra);
    }

    public void LogException(string source, string message, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string composedMessage = string.IsNullOrWhiteSpace(message)
            ? exception.ToString()
            : $"{message}{Environment.NewLine}{exception}";
        Log(source, composedMessage, ProcessLogLevel.Error);
    }

    public void LogEnvironmentSnapshot(string source, string? appDataRootPath = null, bool? isPackaged = null)
    {
        Log(
            source,
            $"Process='{_processName}', pid={_processId}, version={AssemblyVersion}, framework='{_frameworkDescription}', os='{_osDescription}', " +
            $"processArchitecture={_processArchitecture}, machine='{_machineName}', user='{_userName}', cwd='{Environment.CurrentDirectory}', " +
            $"baseDir='{AppContext.BaseDirectory}', commandLine='{Environment.CommandLine}'",
            ProcessLogLevel.Info);

        if (!string.IsNullOrWhiteSpace(appDataRootPath) || isPackaged.HasValue)
        {
            Log(
                source,
                $"App data root='{appDataRootPath ?? "<unknown>"}', packaged={isPackaged?.ToString() ?? "unknown"}",
                ProcessLogLevel.Info);
        }

        LogProxySnapshot(source);
    }

    public void Dispose()
    {
        PersistRunState(cleanExit: true, currentContext: _lastCrashContext, extra: "dispose");
        _disposed = true;
    }

    private void WriteToFile(string prefix, string message)
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(_logsDirectoryPath);
                string normalizedMessage = message.ReplaceLineEndings(Environment.NewLine);
                string[] lines = normalizedMessage.Split(new[] { Environment.NewLine }, StringSplitOptions.None);

                using var stream = new FileStream(
                    _logFilePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                for (int i = 0; i < lines.Length; i++)
                {
                    string linePrefix = i == 0
                        ? prefix
                        : $"{prefix} [CONT]";
                    writer.WriteLine($"{linePrefix} {lines[i]}");
                }
            }
            catch
            {
                // Never let logging failures break app behavior.
            }
        }
    }

    private string BuildLogPrefix(ProcessLogLevel level)
    {
        return $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} " +
               $"[{level.ToString().ToUpperInvariant()}] " +
               $"[PID {_processId}] " +
               $"[T{Environment.CurrentManagedThreadId}] " +
               $"[v{AssemblyVersion}]";
    }

    private void LogProxySnapshot(string source)
    {
        try
        {
            Uri probeUri = new("https://assets.audioscript.app/");
            IWebProxy proxy = HttpClient.DefaultProxy;
            bool bypassed = proxy.IsBypassed(probeUri);
            Uri? proxyUri = proxy.GetProxy(probeUri);
            string proxyDescription = bypassed || proxyUri is null || proxyUri == probeUri
                ? "direct"
                : FormatProxyUri(proxyUri);

            Log(
                source,
                $"HTTP proxy snapshot for '{probeUri.Host}': mode='{proxyDescription}', bypassed={bypassed}.",
                ProcessLogLevel.Info);
        }
        catch
        {
            // Proxy inspection is diagnostic only.
        }
    }

    private static string FormatProxyUri(Uri proxyUri)
    {
        return $"{proxyUri.Scheme}://{proxyUri.Host}:{proxyUri.Port}";
    }

    private static bool TryParseLogTimestamp(string logLine, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (string.IsNullOrWhiteSpace(logLine))
        {
            return false;
        }

        int levelIndex = logLine.IndexOf(" [", StringComparison.Ordinal);
        if (levelIndex <= 0)
        {
            return false;
        }

        string candidate = logLine[..levelIndex];
        return DateTimeOffset.TryParseExact(
            candidate,
            "yyyy-MM-dd HH:mm:ss.fff zzz",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out timestamp);
    }

    private static void ExtractMetric(
        string logLine,
        string metricName,
        IDictionary<string, List<double>> metrics)
    {
        string marker = metricName + "=";
        int markerIndex = logLine.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return;
        }

        int valueStart = markerIndex + marker.Length;
        int valueEnd = valueStart;
        while (valueEnd < logLine.Length && char.IsDigit(logLine[valueEnd]))
        {
            valueEnd++;
        }

        if (valueEnd <= valueStart)
        {
            return;
        }

        string valueSlice = logLine[valueStart..valueEnd];
        if (!double.TryParse(valueSlice, NumberStyles.Integer, CultureInfo.InvariantCulture, out double parsed))
        {
            return;
        }

        if (!metrics.TryGetValue(metricName, out List<double>? sampleList))
        {
            sampleList = [];
            metrics[metricName] = sampleList;
        }

        sampleList.Add(parsed);
    }

    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        double clamped = Math.Clamp(percentile, 0, 1);
        double position = (sortedValues.Count - 1) * clamped;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sortedValues[lower];
        }

        double weight = position - lower;
        return sortedValues[lower] + (sortedValues[upper] - sortedValues[lower]) * weight;
    }

    private static string GetAssemblyVersion()
    {
        Assembly? entryAssembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        AssemblyName? assemblyName = entryAssembly?.GetName();
        Version? version = assemblyName?.Version;
        return version?.ToString() ?? "unknown";
    }

    private void TouchHeartbeat()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if ((now - _lastHeartbeatUtc) < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _lastHeartbeatUtc = now;
        PersistRunState(cleanExit: false, currentContext: _lastCrashContext, extra: null);
    }

    private void TryLogPreviousUncleanRun()
    {
        try
        {
            if (!File.Exists(_runStateFilePath))
            {
                return;
            }

            string json = File.ReadAllText(_runStateFilePath);
            RunStateSnapshot? snapshot = JsonSerializer.Deserialize<RunStateSnapshot>(json);
            if (snapshot is null || snapshot.CleanExit)
            {
                return;
            }

            string message =
                $"Previous run ended unexpectedly. previousPid={snapshot.Pid}, " +
                $"startedUtc='{snapshot.StartedUtc:O}', lastHeartbeatUtc='{snapshot.LastHeartbeatUtc:O}', " +
                $"lastContext='{snapshot.CurrentContext ?? "unknown"}', extra='{snapshot.Extra ?? "none"}'.";
            Log("CrashDiagnostics", message, ProcessLogLevel.Warning);
        }
        catch
        {
            // Crash-state parsing is diagnostic only.
        }
    }

    private void PersistRunState(bool cleanExit, string currentContext, string? extra)
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(_logsDirectoryPath);
                var snapshot = new RunStateSnapshot(
                    Pid: _processId,
                    StartedUtc: _runStartedAtUtc == default ? DateTimeOffset.UtcNow : _runStartedAtUtc,
                    LastHeartbeatUtc: DateTimeOffset.UtcNow,
                    CurrentContext: currentContext,
                    Extra: extra,
                    CleanExit: cleanExit);
                string json = JsonSerializer.Serialize(snapshot);
                File.WriteAllText(_runStateFilePath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch
            {
                // Never let diagnostics failures affect app behavior.
            }
        }
    }

    private sealed record RunStateSnapshot(
        int Pid,
        DateTimeOffset StartedUtc,
        DateTimeOffset LastHeartbeatUtc,
        string? CurrentContext,
        string? Extra,
        bool CleanExit);
}
