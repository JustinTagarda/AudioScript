namespace AudioTranscript.ViewModels;

public sealed class ProcessLogEntryViewModel {
    public ProcessLogEntryViewModel(string time, string message) {
        Time = time;
        Message = message;
    }

    public string Time { get; }

    public string Message { get; }
}
