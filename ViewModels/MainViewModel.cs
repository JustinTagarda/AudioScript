using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioTranscript.Abstractions;
using AudioTranscript.Engines;
using AudioTranscript.Services;

namespace AudioTranscript.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable {
    private const string FixedTranscriptionLanguage = "auto";

    private readonly LiveTranscriptionCoordinator _liveCoordinator;
    private readonly OpenAiOptions _openAiOptions;
    private readonly OpenAiSettingsStore _openAiSettingsStore;
    private readonly OpenAiApiKeyValidationService _openAiApiKeyValidationService;
    private readonly ProcessLogService _processLogService;
    private readonly WhisperProvisioningService _whisperProvisioningService;
    private readonly SynchronizationContext _uiContext;

    private EngineOptionViewModel? _selectedEngine;
    private string _interimText = "Interim transcript appears here while the model is revising...";
    private string _finalizedText = string.Empty;
    private string _statusMessage = "Ready.";
    private string _openAiApiKey;
    private bool _isBusy;
    private bool _isLiveRunning;
    private bool _isFileTranscribing;

    public MainViewModel(
        TranscriptionEngineRegistry engineRegistry,
        LiveTranscriptionCoordinator liveCoordinator,
        OpenAiOptions openAiOptions,
        OpenAiSettingsStore openAiSettingsStore,
        OpenAiApiKeyValidationService openAiApiKeyValidationService,
        ProcessLogService processLogService,
        WhisperProvisioningService whisperProvisioningService) {
        _liveCoordinator = liveCoordinator;
        _openAiOptions = openAiOptions;
        _openAiSettingsStore = openAiSettingsStore;
        _openAiApiKeyValidationService = openAiApiKeyValidationService;
        _processLogService = processLogService;
        _whisperProvisioningService = whisperProvisioningService;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();

        _openAiApiKey = _openAiOptions.ApiKey;

        Engines = new ObservableCollection<EngineOptionViewModel>(
            engineRegistry.Engines.Select(engine => new EngineOptionViewModel(engine)));
        ProcessLogs = new ObservableCollection<ProcessLogEntryViewModel>();

        TranscribeFileCommand = new AsyncRelayCommand(TranscribeFileAsync, CanTranscribeFile);
        StartLiveCommand = new AsyncRelayCommand(StartLiveAsync, CanStartLive);
        StopLiveCommand = new AsyncRelayCommand(StopLiveAsync, CanStopLive);
        ClearCommand = new AsyncRelayCommand(ClearAsync);

        _liveCoordinator.UpdateReceived += OnUpdateReceived;
        _liveCoordinator.StatusChanged += OnStatusChanged;
        _processLogService.LogEmitted += OnProcessLogEmitted;

        EngineOptionViewModel? initialEngine = Engines.FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(_openAiApiKey)) {
            initialEngine = Engines.FirstOrDefault(engine =>
                string.Equals(engine.Engine.Id, "openai_gpt4o_transcribe", StringComparison.OrdinalIgnoreCase))
                ?? Engines.FirstOrDefault(engine => IsOpenAiEngineId(engine.Engine.Id))
                ?? initialEngine;
        }

        SelectedEngine = initialEngine;

        AppendLogCore("Application initialized.");
        AppendLogCore($"Loaded {Engines.Count} engine(s).");

        if (!string.IsNullOrWhiteSpace(_openAiApiKey)) {
            AppendLogCore($"OpenAI API key loaded ({MaskApiKey(_openAiApiKey)}).");
        }
        else {
            AppendLogCore("OpenAI API key is not configured.");
        }

        IReadOnlyList<string> onlineEngines = Engines
            .Where(engine => IsOpenAiEngineId(engine.Engine.Id))
            .Select(engine => engine.DisplayName)
            .ToArray();

        if (onlineEngines.Count > 0) {
            AppendLogCore($"Available OpenAI engines: {string.Join(", ", onlineEngines)}.");
        }

        if (!string.IsNullOrWhiteSpace(_openAiApiKey)
            && SelectedEngine is not null
            && IsOpenAiEngineId(SelectedEngine.Engine.Id)) {
            AppendLogCore("Startup selection: online OpenAI engine selected because API key is present.");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? ErrorOccurred;

    public ObservableCollection<EngineOptionViewModel> Engines { get; }
    public ObservableCollection<ProcessLogEntryViewModel> ProcessLogs { get; }

    public AsyncRelayCommand TranscribeFileCommand { get; }

    public AsyncRelayCommand StartLiveCommand { get; }

    public AsyncRelayCommand StopLiveCommand { get; }

    public AsyncRelayCommand ClearCommand { get; }

    public EngineOptionViewModel? SelectedEngine {
        get => _selectedEngine;
        set {
            if (!SetProperty(ref _selectedEngine, value)) {
                return;
            }

            NotifyPropertyChanged(nameof(IsOpenAiEngineSelected));
            RefreshCommandStates();

            if (value is null) {
                AppendLog("Selected engine cleared.");
            }
            else {
                AppendLog($"Selected engine: {value.DisplayName} (id: {value.Engine.Id}).");
            }
        }
    }

    public bool IsOpenAiEngineSelected =>
        SelectedEngine is not null
        && IsOpenAiEngineId(SelectedEngine.Engine.Id);

    public string InterimText {
        get => _interimText;
        set => SetProperty(ref _interimText, value);
    }

    public string FinalizedText {
        get => _finalizedText;
        set => SetProperty(ref _finalizedText, value);
    }

    public string StatusMessage {
        get => _statusMessage;
        set {
            if (SetProperty(ref _statusMessage, value)) {
                AppendLog($"Status updated: {value}");
            }
        }
    }

    public bool IsBusy {
        get => _isBusy;
        private set {
            if (!SetProperty(ref _isBusy, value)) {
                return;
            }

            AppendLog($"Busy state: {(value ? "ON" : "OFF")}.");
            RefreshCommandStates();
        }
    }

    public bool IsLiveRunning {
        get => _isLiveRunning;
        private set {
            if (!SetProperty(ref _isLiveRunning, value)) {
                return;
            }

            AppendLog($"Live state: {(value ? "RUNNING" : "STOPPED")}.");
            RefreshCommandStates();
        }
    }

    public bool IsFileTranscribing {
        get => _isFileTranscribing;
        private set => SetProperty(ref _isFileTranscribing, value);
    }

    public string OpenAiApiKey {
        get => _openAiApiKey;
        set {
            if (!SetProperty(ref _openAiApiKey, value)) {
                return;
            }

            _openAiOptions.ApiKey = value.Trim();
            _openAiSettingsStore.Save(_openAiOptions.ApiKey);
            AppendLog($"OpenAI API key updated ({MaskApiKey(_openAiOptions.ApiKey)}).");
            RefreshCommandStates();
        }
    }

    public async ValueTask DisposeAsync() {
        AppendLog("Disposing transcription resources...");
        _liveCoordinator.UpdateReceived -= OnUpdateReceived;
        _liveCoordinator.StatusChanged -= OnStatusChanged;
        _processLogService.LogEmitted -= OnProcessLogEmitted;
        await _liveCoordinator.DisposeAsync();
        AppendLog("Disposed transcription resources.");
    }

    public async Task<OpenAiApiKeyValidationResult> ValidateOpenAiApiKeyAsync(string apiKey, CancellationToken cancellationToken) {
        AppendLog("Validating OpenAI API key with OpenAI service...");
        OpenAiApiKeyValidationResult result = await _openAiApiKeyValidationService.ValidateAsync(apiKey, cancellationToken);

        if (result.IsValid) {
            AppendLog("OpenAI API key validation succeeded.");
        }
        else {
            AppendLog($"OpenAI API key validation failed: {result.Message}");
        }

        return result;
    }

    public void ApplyOpenAiSettings(string apiKey) {
        AppendLog("Applying OpenAI settings.");
        OpenAiApiKey = apiKey;
        AppendLog("OpenAI settings applied.");
    }

    private async Task TranscribeFileAsync() {
        AppendLog("Command requested: Transcribe File.");

        if (SelectedEngine is null) {
            AppendLog("Transcribe aborted: no engine selected.");
            return;
        }

        if (!await EnsureSelectedEngineConfiguredAsync()) {
            AppendLog("Transcribe aborted: selected engine is not ready.");
            return;
        }

        AppendLog("Opening file picker for transcription.");
        var dialog = new Microsoft.Win32.OpenFileDialog {
            Title = "Select Audio File",
            Filter = "Audio Files|*.wav;*.mp3;*.flac;*.aac;*.m4a;*.ogg;*.wma;*.mp4|All Files|*.*",
            Multiselect = false,
        };

        bool? openResult = dialog.ShowDialog();

        if (openResult != true) {
            AppendLog("Transcribe canceled: user did not select a file.");
            return;
        }

        AppendLog($"Selected file: {dialog.FileName}");
        try {
            long fileSize = new System.IO.FileInfo(dialog.FileName).Length;
            AppendLog($"Selected file size: {fileSize:N0} bytes.");
        }
        catch {
            AppendLog("Unable to read selected file size.");
        }

        IsBusy = true;
        IsFileTranscribing = true;
        StatusMessage = $"Transcribing file using {SelectedEngine.DisplayName}...";
        AppendLog($"Submitting file transcription with {SelectedEngine.DisplayName}.");

        try {
            TranscriptUpdate update = await SelectedEngine.Engine.TranscribeFileAsync(
                dialog.FileName,
                BuildRequest(),
                CancellationToken.None);

            AppendFinal(update.Text);
            InterimText = string.Empty;
            StatusMessage = "File transcription completed.";
            AppendLog($"File transcription completed. Received {update.Text.Length:N0} characters.");
        }
        catch (Exception ex) {
            RaiseError($"File transcription failed: {ex.Message}");
        }
        finally {
            IsFileTranscribing = false;
            IsBusy = false;
            AppendLog("Command finished: Transcribe File.");
        }
    }

    private async Task StartLiveAsync() {
        AppendLog("Command requested: Start Live.");

        if (SelectedEngine is null || IsLiveRunning) {
            AppendLog("Start Live ignored: no engine selected or live already running.");
            return;
        }

        if (!await EnsureSelectedEngineConfiguredAsync()) {
            AppendLog("Start Live aborted: selected engine is not ready.");
            return;
        }

        IsBusy = true;
        AppendLog($"Starting live transcription with {SelectedEngine.DisplayName}.");

        try {
            await _liveCoordinator.StartAsync(
                SelectedEngine.Engine,
                BuildRequest(),
                CancellationToken.None);

            IsLiveRunning = true;
            InterimText = "Monitoring default system playback...";
            AppendLog("Live transcription started; monitoring default system playback.");
        }
        catch (Exception ex) {
            RaiseError($"Unable to start live transcription: {ex.Message}");
        }
        finally {
            IsBusy = false;
            AppendLog("Command finished: Start Live.");
        }
    }

    private async Task StopLiveAsync() {
        AppendLog("Command requested: Stop Live.");

        if (!IsLiveRunning) {
            AppendLog("Stop Live ignored: live transcription is not running.");
            return;
        }

        IsBusy = true;
        AppendLog("Stopping live transcription.");

        try {
            await _liveCoordinator.StopAsync(CancellationToken.None);
            IsLiveRunning = false;
            InterimText = string.Empty;
            AppendLog("Live transcription stopped.");
        }
        catch (Exception ex) {
            RaiseError($"Unable to stop live transcription: {ex.Message}");
        }
        finally {
            IsBusy = false;
            AppendLog("Command finished: Stop Live.");
        }
    }

    private Task ClearAsync() {
        InterimText = string.Empty;
        FinalizedText = string.Empty;
        ProcessLogs.Clear();
        _statusMessage = "Transcript and logs cleared.";
        NotifyPropertyChanged(nameof(StatusMessage));
        return Task.CompletedTask;
    }

    private bool CanTranscribeFile() {
        return SelectedEngine is not null && !IsBusy && !IsLiveRunning;
    }

    private bool CanStartLive() {
        return SelectedEngine is not null && !IsBusy && !IsLiveRunning;
    }

    private bool CanStopLive() {
        return IsLiveRunning && !IsBusy;
    }

    private void RefreshCommandStates() {
        TranscribeFileCommand.RaiseCanExecuteChanged();
        StartLiveCommand.RaiseCanExecuteChanged();
        StopLiveCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
    }

    private async Task<bool> EnsureSelectedEngineConfiguredAsync() {
        if (SelectedEngine is null) {
            AppendLog("Engine configuration check failed: no selected engine.");
            return false;
        }

        if (string.Equals(SelectedEngine.Engine.Id, "whisper_cpp", StringComparison.OrdinalIgnoreCase)) {
            AppendLog("Preparing local whisper.cpp engine assets.");
            StatusMessage = "Preparing local whisper.cpp engine...";
            WhisperProvisioningResult provisioning = await _whisperProvisioningService.EnsureReadyAsync(CancellationToken.None);

            if (!provisioning.IsReady) {
                RaiseError(provisioning.Message);
                AppendLog($"whisper.cpp provisioning failed: {provisioning.Message}");
                return false;
            }

            StatusMessage = provisioning.Message;
            AppendLog($"whisper.cpp provisioning result: {provisioning.Message}");
        }
        else if (IsOpenAiEngineId(SelectedEngine.Engine.Id)) {
            if (string.IsNullOrWhiteSpace(OpenAiApiKey)) {
                RaiseError("OpenAI API key is required for the online engine.");
                AppendLog("OpenAI engine blocked: API key missing.");
                return false;
            }

            AppendLog("OpenAI engine configuration verified.");
        }

        return true;
    }

    private TranscriptionRequest BuildRequest() {
        var request = new TranscriptionRequest(
            IncludeTimestamps: true,
            IncludePunctuation: true,
            EnableDiarization: true,
            Language: FixedTranscriptionLanguage);

        AppendLog(
            $"Transcription request: timestamps={request.IncludeTimestamps}, punctuation={request.IncludePunctuation}, diarization={request.EnableDiarization}, language='{request.Language}'.");

        return request;
    }

    private void AppendFinal(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return;
        }

        string trimmed = text.Trim();

        FinalizedText = string.IsNullOrWhiteSpace(FinalizedText)
            ? trimmed
            : $"{FinalizedText}{Environment.NewLine}{trimmed}";

        AppendLog($"Finalized text appended ({trimmed.Length:N0} chars): {TrimForLog(trimmed)}");
    }

    private void OnUpdateReceived(object? sender, TranscriptUpdate update) {
        _uiContext.Post(_ => {
            if (update.IsFinal) {
                AppendLogCore($"Realtime final update received: {TrimForLog(update.Text)}");
                AppendFinal(update.Text);
                InterimText = string.Empty;
                return;
            }

            InterimText = update.Text;
            AppendLogCore($"Realtime interim update: {TrimForLog(update.Text)}");
        }, null);
    }

    private void OnStatusChanged(object? sender, string status) {
        _uiContext.Post(_ => {
            StatusMessage = status;
            AppendLogCore($"Coordinator status: {status}");

            if (LooksLikeErrorStatus(status)) {
                RaiseError(status);
            }
        }, null);
    }

    private void OnProcessLogEmitted(object? sender, string message) {
        _uiContext.Post(_ => AppendLogCore(message), null);
    }

    private void RaiseError(string message) {
        if (string.IsNullOrWhiteSpace(message)) {
            return;
        }

        AppendLog($"ERROR: {message}");
        _uiContext.Post(_ => ErrorOccurred?.Invoke(this, message), null);
    }

    private static bool LooksLikeErrorStatus(string status) {
        return status.Contains("error", StringComparison.OrdinalIgnoreCase)
            || status.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || status.Contains("unable", StringComparison.OrdinalIgnoreCase);
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null) {
        if (EqualityComparer<T>.Default.Equals(field, value)) {
            return false;
        }

        field = value;
        NotifyPropertyChanged(propertyName);
        return true;
    }

    private void NotifyPropertyChanged([CallerMemberName] string? propertyName = null) {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void AppendLog(string message) {
        if (string.IsNullOrWhiteSpace(message)) {
            return;
        }

        _uiContext.Post(_ => AppendLogCore(message), null);
    }

    private void AppendLogCore(string message) {
        if (string.IsNullOrWhiteSpace(message)) {
            return;
        }

        ProcessLogs.Add(
            new ProcessLogEntryViewModel(
                DateTime.Now.ToString("HH:mm:ss"),
                message.Trim()));
    }

    private static string TrimForLog(string text, int maxLength = 140) {
        if (string.IsNullOrWhiteSpace(text)) {
            return "(empty)";
        }

        string singleLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();

        if (singleLine.Length <= maxLength) {
            return singleLine;
        }

        return $"{singleLine[..maxLength]}...";
    }

    private static string MaskApiKey(string apiKey) {
        if (string.IsNullOrWhiteSpace(apiKey)) {
            return "empty";
        }

        string trimmed = apiKey.Trim();

        if (trimmed.Length <= 8) {
            return $"{trimmed[..Math.Min(2, trimmed.Length)]}***";
        }

        return $"{trimmed[..4]}...{trimmed[^4..]}";
    }

    private static bool IsOpenAiEngineId(string engineId) {
        return engineId.StartsWith("openai_", StringComparison.OrdinalIgnoreCase);
    }
}
