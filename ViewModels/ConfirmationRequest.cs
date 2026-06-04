namespace AudioScript.ViewModels;

public sealed class ConfirmationRequest {
    public ConfirmationRequest(
        string title,
        string message,
        string confirmButtonText,
        string cancelButtonText)
        : this(title, title, message, confirmButtonText, cancelButtonText) {
    }

    public ConfirmationRequest(
        string title,
        string heading,
        string message,
        string confirmButtonText,
        string cancelButtonText) {
        Title = title?.Trim() ?? string.Empty;
        Heading = string.IsNullOrWhiteSpace(heading) ? Title : heading.Trim();
        Message = message?.Trim() ?? string.Empty;
        ConfirmButtonText = confirmButtonText?.Trim() ?? string.Empty;
        CancelButtonText = cancelButtonText?.Trim() ?? string.Empty;
    }

    public string Title { get; }

    public string Heading { get; }

    public string Message { get; }

    public string ConfirmButtonText { get; }

    public string CancelButtonText { get; }

    public bool IsConfirmed { get; set; }
}



