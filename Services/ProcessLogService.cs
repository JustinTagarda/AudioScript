using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

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
    private readonly int _processId;
    private readonly string _processName;
    private readonly string _machineName;
    private readonly string _userName;
    private readonly string _frameworkDescription;
    private readonly string _osDescription;
    private readonly Architecture _processArchitecture;
    private bool _disposed;

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
    }

    public event EventHandler<string>? LogEmitted;

    public string LogFilePath => _logFilePath;

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
        LogEmitted?.Invoke(this, payloadPrefix);
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

    private static string GetAssemblyVersion()
    {
        Assembly? entryAssembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        AssemblyName? assemblyName = entryAssembly?.GetName();
        Version? version = assemblyName?.Version;
        return version?.ToString() ?? "unknown";
    }
}
