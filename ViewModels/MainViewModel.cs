using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioTranscript.Abstractions;
using AudioTranscript.Engines;
using AudioTranscript.Services;
using Microsoft.Win32;

namespace AudioTranscript.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable {
    private readonly LiveTranscriptionCoordinator _liveCoordinator;
    private readonly OpenAiOptions _openAiOptions;
    private readonly WhisperProvisioningService _whisperProvisioningService;
    private readonly SynchronizationContext _uiContext;

    private EngineOptionViewModel? _selectedEngine;
    private string _interimText = "Interim transcript appears here while the model is revising...";
    private string _finalizedText = string.Empty;
    private string _statusMessage = "Ready.";
    private string _languageHint = "auto";
    private string _openAiApiKey;
    private string _openAiModel;
    private bool _includeTimestamps = true;
    private bool _isBusy;
    private bool _isLiveRunning;

    public MainViewModel(
        TranscriptionEngineRegistry engineRegistry,
        LiveTranscriptionCoordinator liveCoordinator,
        OpenAiOptions openAiOptions,
        WhisperProvisioningService whisperProvisioningService) {
        _liveCoordinator = liveCoordinator;
        _openAiOptions = openAiOptions;
        _whisperProvisioningService = whisperProvisioningService;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();

        _openAiApiKey = _openAiOptions.ApiKey;
        _openAiModel = _openAiOptions.Model;

        Engines = new ObservableCollection<EngineOptionViewModel>(
            engineRegistry.Engines.Select(engine => new EngineOptionViewModel(engine)));

        CapabilityLabels = new ObservableCollection<string>();

        TranscribeFileCommand = new AsyncRelayCommand(TranscribeFileAsync, CanTranscribeFile);
        StartLiveCommand = new AsyncRelayCommand(StartLiveAsync, CanStartLive);
        StopLiveCommand = new AsyncRelayCommand(StopLiveAsync, CanStopLive);
        ClearCommand = new AsyncRelayCommand(ClearAsync);

        _liveCoordinator.UpdateReceived += OnUpdateReceived;
        _liveCoordinator.StatusChanged += OnStatusChanged;

        SelectedEngine = Engines.FirstOrDefault();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<EngineOptionViewModel> Engines { get; }

    public ObservableCollection<string> CapabilityLabels { get; }

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

            RefreshCapabilities();
            RefreshCommandStates();
        }
    }

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
        set => SetProperty(ref _statusMessage, value);
    }

    public string LanguageHint {
        get => _languageHint;
        set => SetProperty(ref _languageHint, value);
    }

    public bool IncludeTimestamps {
        get => _includeTimestamps;
        set => SetProperty(ref _includeTimestamps, value);
    }

    public bool IsBusy {
        get => _isBusy;
        private set {
            if (!SetProperty(ref _isBusy, value)) {
                return;
            }

            RefreshCommandStates();
        }
    }

    public bool IsLiveRunning {
        get => _isLiveRunning;
        private set {
            if (!SetProperty(ref _isLiveRunning, value)) {
                return;
            }

            RefreshCommandStates();
        }
    }

    public string OpenAiApiKey {
        get => _openAiApiKey;
        set {
            if (!SetProperty(ref _openAiApiKey, value)) {
                return;
            }

            _openAiOptions.ApiKey = value.Trim();
            RefreshCommandStates();
        }
    }

    public string OpenAiModel {
        get => _openAiModel;
        set {
            if (!SetProperty(ref _openAiModel, value)) {
                return;
            }

            _openAiOptions.Model = value.Trim();
        }
    }

    public async ValueTask DisposeAsync() {
        _liveCoordinator.UpdateReceived -= OnUpdateReceived;
        _liveCoordinator.StatusChanged -= OnStatusChanged;
        await _liveCoordinator.DisposeAsync();
    }

    private async Task TranscribeFileAsync() {
        if (SelectedEngine is null) {
            return;
        }

        if (!await EnsureSelectedEngineConfiguredAsync()) {
            return;
        }

        var dialog = new OpenFileDialog {
            Title = "Select Audio File",
            Filter = "Audio Files|*.wav;*.mp3;*.flac;*.aac;*.m4a;*.ogg;*.wma;*.mp4|All Files|*.*",
            Multiselect = false,
        };

        bool? openResult = dialog.ShowDialog();

        if (openResult != true) {
            return;
        }

        IsBusy = true;
        StatusMessage = $"Transcribing file using {SelectedEngine.DisplayName}...";

        try {
            TranscriptUpdate update = await SelectedEngine.Engine.TranscribeFileAsync(
                dialog.FileName,
                BuildRequest(),
                CancellationToken.None);

            AppendFinal(update.Text);
            InterimText = string.Empty;
            StatusMessage = "File transcription completed.";
        }
        catch (Exception ex) {
            StatusMessage = $"File transcription failed: {ex.Message}";
        }
        finally {
            IsBusy = false;
        }
    }

    private async Task StartLiveAsync() {
        if (SelectedEngine is null || IsLiveRunning) {
            return;
        }

        if (!await EnsureSelectedEngineConfiguredAsync()) {
            return;
        }

        IsBusy = true;

        try {
            await _liveCoordinator.StartAsync(
                SelectedEngine.Engine,
                BuildRequest(),
                CancellationToken.None);

            IsLiveRunning = true;
            InterimText = "Monitoring default system playback...";
        }
        catch (Exception ex) {
            StatusMessage = $"Unable to start live transcription: {ex.Message}";
        }
        finally {
            IsBusy = false;
        }
    }

    private async Task StopLiveAsync() {
        if (!IsLiveRunning) {
            return;
        }

        IsBusy = true;

        try {
            await _liveCoordinator.StopAsync(CancellationToken.None);
            IsLiveRunning = false;
            InterimText = string.Empty;
        }
        catch (Exception ex) {
            StatusMessage = $"Unable to stop live transcription: {ex.Message}";
        }
        finally {
            IsBusy = false;
        }
    }

    private Task ClearAsync() {
        InterimText = string.Empty;
        FinalizedText = string.Empty;
        StatusMessage = "Transcript view cleared.";
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
            return false;
        }

        if (string.Equals(SelectedEngine.Engine.Id, "whisper_cpp", StringComparison.OrdinalIgnoreCase)) {
            StatusMessage = "Preparing local whisper.cpp engine...";
            WhisperProvisioningResult provisioning = await _whisperProvisioningService.EnsureReadyAsync(CancellationToken.None);

            if (!provisioning.IsReady) {
                StatusMessage = provisioning.Message;
                return false;
            }

            StatusMessage = provisioning.Message;
        }
        else if (string.Equals(SelectedEngine.Engine.Id, "openai_gpt4o_mini", StringComparison.OrdinalIgnoreCase)) {
            if (string.IsNullOrWhiteSpace(OpenAiApiKey)) {
                StatusMessage = "OpenAI API key is required for the online engine.";
                return false;
            }
        }

        return true;
    }

    private TranscriptionRequest BuildRequest() {
        string? language = LanguageHint.Trim();

        if (string.IsNullOrWhiteSpace(language)
            || string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase)) {
            language = null;
        }

        return new TranscriptionRequest(language, IncludeTimestamps);
    }

    private void RefreshCapabilities() {
        CapabilityLabels.Clear();

        if (SelectedEngine is null) {
            return;
        }

        foreach (string label in SelectedEngine.Engine.Capabilities.ToLabels()) {
            CapabilityLabels.Add(label);
        }
    }

    private void AppendFinal(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return;
        }

        string trimmed = text.Trim();

        FinalizedText = string.IsNullOrWhiteSpace(FinalizedText)
            ? trimmed
            : $"{FinalizedText}{Environment.NewLine}{trimmed}";
    }

    private void OnUpdateReceived(object? sender, TranscriptUpdate update) {
        _uiContext.Post(_ => {
            if (update.IsFinal) {
                AppendFinal(update.Text);
                InterimText = string.Empty;
                return;
            }

            InterimText = update.Text;
        }, null);
    }

    private void OnStatusChanged(object? sender, string status) {
        _uiContext.Post(_ => StatusMessage = status, null);
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null) {
        if (EqualityComparer<T>.Default.Equals(field, value)) {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
