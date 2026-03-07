namespace AudioTranscript.ViewModels;

public enum ToastNotificationType {
    Info,
    Success,
    Warning,
    Error,
}

public sealed record ToastNotification(
    string Title,
    string Message,
    ToastNotificationType Type = ToastNotificationType.Info
);
