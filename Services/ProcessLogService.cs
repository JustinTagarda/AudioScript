namespace AudioScript.Services;

public sealed class ProcessLogService {
    public event EventHandler<string>? LogEmitted;

    public void Log(string source, string message) {
        if (string.IsNullOrWhiteSpace(message)) {
            return;
        }

        string trimmedSource = source?.Trim() ?? string.Empty;
        string trimmedMessage = message.Trim();

        string payload = string.IsNullOrWhiteSpace(trimmedSource)
            ? trimmedMessage
            : $"[{trimmedSource}] {trimmedMessage}";

        LogEmitted?.Invoke(this, payload);
    }
}



