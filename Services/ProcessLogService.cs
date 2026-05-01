using System.IO;
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
    private readonly object _sync = new();
    private readonly string _logsDirectoryPath;
    private readonly string _logFilePath;
    private bool _disposed;

    public ProcessLogService(string? logsDirectoryPath = null)
    {
        _logsDirectoryPath = string.IsNullOrWhiteSpace(logsDirectoryPath)
            ? AppDataPathProvider.Create().LogsPath
            : Path.GetFullPath(logsDirectoryPath);

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
        string payload = string.IsNullOrWhiteSpace(trimmedSource)
            ? trimmedMessage
            : $"[{trimmedSource}] {trimmedMessage}";

        string filePayload =
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} " +
            $"[{level.ToString().ToUpperInvariant()}] " +
            $"[T{Environment.CurrentManagedThreadId}] " +
            $"{payload}";

        WriteToFile(filePayload);
        LogEmitted?.Invoke(this, payload);
    }

    public void LogException(string source, string message, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string composedMessage = string.IsNullOrWhiteSpace(message)
            ? exception.ToString()
            : $"{message}{Environment.NewLine}{exception}";
        Log(source, composedMessage, ProcessLogLevel.Error);
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private void WriteToFile(string line)
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
                File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Never let logging failures break app behavior.
            }
        }
    }
}
