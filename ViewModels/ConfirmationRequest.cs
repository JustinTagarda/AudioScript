namespace AudioTranscript.ViewModels;

public sealed class ConfirmationRequest {
    public ConfirmationRequest(
        string title,
        string message,
        string confirmButtonText,
        string cancelButtonText) {
        Title = title?.Trim() ?? string.Empty;
        Message = message?.Trim() ?? string.Empty;
        ConfirmButtonText = confirmButtonText?.Trim() ?? string.Empty;
        CancelButtonText = cancelButtonText?.Trim() ?? string.Empty;
    }

    public string Title { get; }

    public string Message { get; }

    public string ConfirmButtonText { get; }

    public string CancelButtonText { get; }

    public bool IsConfirmed { get; set; }
}
