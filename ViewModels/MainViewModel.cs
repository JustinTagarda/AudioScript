using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using AudioTranscript.Abstractions;
using AudioTranscript.Audio;
using AudioTranscript.Services;

namespace AudioTranscript.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable {
    private const int SeekStepSeconds = 5;
    private const string AudioFileDialogFilter = "Audio Files|*.wav;*.mp3;*.flac;*.aac;*.m4a;*.ogg;*.wma;*.mp4|All Files|*.*";

    private readonly LiveTranscriptionCoordinator _liveCoordinator;
    private readonly ITranscriptionService _transcriptionService;
    private readonly IAudioPlaybackService _audioPlaybackService;
    private readonly OpenAiTranscriptionOptions _openAiOptions;
    private readonly OpenAiSettingsStore _openAiSettingsStore;
    private readonly OpenAiApiKeyValidationService _openAiApiKeyValidationService;
    private readonly ProcessLogService _processLogService;
    private readonly SynchronizationContext _uiContext;
    private readonly DispatcherTimer _audioTimelineTimer;

    private EngineOptionViewModel? _selectedEngine;
    private string _interimText = "Interim transcript appears here while the model is revising...";
    private string _finalizedText = string.Empty;
    private string _statusMessage = "Ready.";
    private string _openAiApiKey;
    private bool _isBusy;
    private bool _isLiveRunning;
    private bool _isFileTranscribing;
    private string _loadedAudioFilePath = string.Empty;
    private bool _isAudioPlaying;
    private double _audioSeekMaximumSeconds;
    private double _audioSeekPositionSeconds;
    private string _audioElapsedText = "00:00";
    private string _audioRemainingText = "-00:00";
    private bool _isUpdatingSeekFromPlayback;
    private CancellationTokenSource? _fileTranscriptionCts;

    public MainViewModel(
        IEnumerable<TranscriptionModelOption> models,
        LiveTranscriptionCoordinator liveCoordinator,
        ITranscriptionService transcriptionService,
        IAudioPlaybackService audioPlaybackService,
        OpenAiTranscriptionOptions openAiOptions,
        OpenAiSettingsStore openAiSettingsStore,
        OpenAiApiKeyValidationService openAiApiKeyValidationService,
        ProcessLogService processLogService) {
        _liveCoordinator = liveCoordinator;
        _transcriptionService = transcriptionService;
        _audioPlaybackService = audioPlaybackService;
        _openAiOptions = openAiOptions;
        _openAiSettingsStore = openAiSettingsStore;
        _openAiApiKeyValidationService = openAiApiKeyValidationService;
        _processLogService = processLogService;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();

        _openAiApiKey = _openAiOptions.ApiKey;

        Engines = new ObservableCollection<EngineOptionViewModel>(
            models.Select(model => new EngineOptionViewModel(model)));
        ProcessLogs = new ObservableCollection<ProcessLogEntryViewModel>();
        FinalizedTranscriptLines = new ObservableCollection<FinalizedTranscriptLineViewModel>();

        TranscribeFileCommand = new AsyncRelayCommand(TranscribeFileAsync, CanTranscribeFile);
        StartLiveCommand = new AsyncRelayCommand(StartLiveAsync, CanStartLive);
        StopLiveCommand = new AsyncRelayCommand(StopLiveAsync, CanStopLive);
        ClearCommand = new AsyncRelayCommand(ClearAsync, CanClear);
        CancelCommand = new AsyncRelayCommand(CancelFileTranscriptionAsync, CanCancelFileTranscription);
        OpenAudioFileCommand = new AsyncRelayCommand(OpenAudioFileAsync, CanOpenAudioFile);
        PlayAudioCommand = new AsyncRelayCommand(PlayAudioAsync, CanPlayAudio);
        PauseAudioCommand = new AsyncRelayCommand(PauseAudioAsync, CanPauseAudio);
        StopAudioCommand = new AsyncRelayCommand(StopAudioAsync, CanStopAudio);
        RewindAudioCommand = new AsyncRelayCommand(RewindAudioAsync, CanSeekAudio);
        ForwardAudioCommand = new AsyncRelayCommand(ForwardAudioAsync, CanSeekAudio);

        _liveCoordinator.UpdateReceived += OnUpdateReceived;
        _liveCoordinator.StatusChanged += OnStatusChanged;
        _processLogService.LogEmitted += OnProcessLogEmitted;
        _audioPlaybackService.PlaybackStateChanged += OnAudioPlaybackStateChanged;
        _isAudioPlaying = _audioPlaybackService.IsPlaying;
        _audioTimelineTimer = new DispatcherTimer {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _audioTimelineTimer.Tick += OnAudioTimelineTick;
        _audioTimelineTimer.Start();

        SelectedEngine = Engines.FirstOrDefault(engine =>
            string.Equals(engine.Id, OpenAiTranscriptionModelCatalog.Gpt4oTranscribe, StringComparison.OrdinalIgnoreCase))
            ?? Engines.FirstOrDefault();

        AppendLogCore("Application initialized.");
        AppendLogCore($"Loaded {Engines.Count} transcription model(s).");

        if (!string.IsNullOrWhiteSpace(_openAiApiKey)) {
            AppendLogCore($"OpenAI API key loaded ({MaskApiKey(_openAiApiKey)}).");
        }
        else {
            AppendLogCore("OpenAI API key is not configured.");
        }

        if (Engines.Count > 0) {
            string available = string.Join(", ", Engines.Select(model => model.DisplayName));
            AppendLogCore($"Available OpenAI models: {available}.");
        }

        if (SelectedEngine is not null) {
            AppendLogCore($"Startup selection: {SelectedEngine.DisplayName}.");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? ErrorOccurred;

    public ObservableCollection<EngineOptionViewModel> Engines { get; }
    public ObservableCollection<ProcessLogEntryViewModel> ProcessLogs { get; }
    public ObservableCollection<FinalizedTranscriptLineViewModel> FinalizedTranscriptLines { get; }

    public AsyncRelayCommand TranscribeFileCommand { get; }

    public AsyncRelayCommand StartLiveCommand { get; }

    public AsyncRelayCommand StopLiveCommand { get; }

    public AsyncRelayCommand ClearCommand { get; }

    public AsyncRelayCommand CancelCommand { get; }
    public AsyncRelayCommand OpenAudioFileCommand { get; }
    public AsyncRelayCommand PlayAudioCommand { get; }
    public AsyncRelayCommand PauseAudioCommand { get; }
    public AsyncRelayCommand StopAudioCommand { get; }
    public AsyncRelayCommand RewindAudioCommand { get; }
    public AsyncRelayCommand ForwardAudioCommand { get; }

    public EngineOptionViewModel? SelectedEngine {
        get => _selectedEngine;
        set {
            if (!SetProperty(ref _selectedEngine, value)) {
                return;
            }

            NotifyPropertyChanged(nameof(IsOpenAiEngineSelected));
            NotifyInteractionAvailabilityChanged();
            RefreshCommandStates();

            if (value is null) {
                AppendLog("Selected model cleared.");
            }
            else {
                AppendLog($"Selected model: {value.DisplayName} (id: {value.Id}).");
            }
        }
    }

    public bool IsOpenAiEngineSelected => SelectedEngine is not null;

    public bool IsEngineSelectionEnabled =>
        !IsBusy && !IsLiveRunning && !IsFileTranscribing;

    public bool IsOpenAiSettingsEnabled =>
        IsOpenAiEngineSelected && !IsBusy && !IsLiveRunning && !IsFileTranscribing;

    public string LoadedAudioFilePath {
        get => _loadedAudioFilePath;
        private set {
            if (!SetProperty(ref _loadedAudioFilePath, value)) {
                return;
            }

            NotifyPropertyChanged(nameof(LoadedAudioFileName));
            NotifyPropertyChanged(nameof(IsAudioFileLoaded));
            RefreshCommandStates();
        }
    }

    public string LoadedAudioFileName =>
        string.IsNullOrWhiteSpace(LoadedAudioFilePath)
            ? "No audio file selected."
            : Path.GetFileName(LoadedAudioFilePath);

    public bool IsAudioFileLoaded =>
        !string.IsNullOrWhiteSpace(LoadedAudioFilePath);

    public bool IsAudioPlaying {
        get => _isAudioPlaying;
        private set {
            if (!SetProperty(ref _isAudioPlaying, value)) {
                return;
            }

            RefreshCommandStates();
        }
    }

    public double AudioSeekMaximumSeconds {
        get => _audioSeekMaximumSeconds;
        private set => SetProperty(ref _audioSeekMaximumSeconds, value);
    }

    public double AudioSeekPositionSeconds {
        get => _audioSeekPositionSeconds;
        set {
            double clamped = Math.Max(0, Math.Min(value, AudioSeekMaximumSeconds));

            if (!SetProperty(ref _audioSeekPositionSeconds, clamped)) {
                return;
            }

            if (_isUpdatingSeekFromPlayback || !IsAudioFileLoaded) {
                return;
            }

            try {
                _audioPlaybackService.Seek(TimeSpan.FromSeconds(clamped));
            }
            catch (Exception ex) {
                AppendLog($"Audio seek failed: {ex.Message}");
            }

            UpdateAudioTimeLabels(
                elapsed: TimeSpan.FromSeconds(clamped),
                duration: TimeSpan.FromSeconds(AudioSeekMaximumSeconds));
        }
    }

    public string AudioElapsedText {
        get => _audioElapsedText;
        private set => SetProperty(ref _audioElapsedText, value);
    }

    public string AudioRemainingText {
        get => _audioRemainingText;
        private set => SetProperty(ref _audioRemainingText, value);
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
            NotifyInteractionAvailabilityChanged();
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
            NotifyInteractionAvailabilityChanged();
            RefreshCommandStates();
        }
    }

    public bool IsFileTranscribing {
        get => _isFileTranscribing;
        private set {
            if (!SetProperty(ref _isFileTranscribing, value)) {
                return;
            }

            NotifyInteractionAvailabilityChanged();
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
            _openAiSettingsStore.Save(_openAiOptions.ApiKey);
            AppendLog($"OpenAI API key updated ({MaskApiKey(_openAiOptions.ApiKey)}).");
            RefreshCommandStates();
        }
    }

    public async ValueTask DisposeAsync() {
        AppendLog("Disposing transcription resources...");

        try {
            _fileTranscriptionCts?.Cancel();
        }
        catch (ObjectDisposedException) {
            // Ignore cancellation race at teardown.
        }

        _fileTranscriptionCts?.Dispose();
        _fileTranscriptionCts = null;

        _liveCoordinator.UpdateReceived -= OnUpdateReceived;
        _liveCoordinator.StatusChanged -= OnStatusChanged;
        _processLogService.LogEmitted -= OnProcessLogEmitted;
        _audioPlaybackService.PlaybackStateChanged -= OnAudioPlaybackStateChanged;
        UnsubscribeFromFinalizedLineChanges();
        _audioTimelineTimer.Stop();
        _audioTimelineTimer.Tick -= OnAudioTimelineTick;
        _audioPlaybackService.Dispose();
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
            AppendLog("Transcribe aborted: no model selected.");
            return;
        }

        if (!IsAudioFileLoaded) {
            AppendLog("Transcribe aborted: no audio file is loaded in preview.");
            return;
        }

        if (!EnsureSelectedModelConfigured()) {
            AppendLog("Transcribe aborted: selected model is not ready.");
            return;
        }

        string selectedFilePath = LoadedAudioFilePath;
        if (!System.IO.File.Exists(selectedFilePath)) {
            RaiseError($"Loaded audio file does not exist: {selectedFilePath}");
            AppendLog("Transcribe aborted: loaded audio file path is invalid.");
            return;
        }

        ClearOutputCore();
        AppendLog($"Using loaded audio file: {selectedFilePath}");
        try {
            long fileSize = new System.IO.FileInfo(selectedFilePath).Length;
            AppendLog($"Selected file size: {fileSize:N0} bytes.");
        }
        catch {
            AppendLog("Unable to read selected file size.");
        }

        _fileTranscriptionCts?.Dispose();
        _fileTranscriptionCts = new CancellationTokenSource();
        CancellationToken transcriptionToken = _fileTranscriptionCts.Token;

        IsBusy = true;
        IsFileTranscribing = true;
        StatusMessage = $"Transcribing file using {SelectedEngine.DisplayName}...";

        try {
            TranscriptionResult result = await _transcriptionService.TranscribeFileAsync(
                selectedFilePath,
                SelectedEngine.Id,
                transcriptionToken);

            AppendFinalFromFileResult(result);
            InterimText = string.Empty;
            StatusMessage = "File transcription completed.";
            AppendLog(
                $"File transcription completed. Received {result.Text.Length:N0} characters. " +
                $"Logprobs={result.TokenLogprobs.Count:N0}, low-confidence={result.LowConfidenceTokens.Count:N0}.");
        }
        catch (OperationCanceledException) when (transcriptionToken.IsCancellationRequested) {
            InterimText = string.Empty;
            StatusMessage = "File transcription canceled.";
            AppendLog("File transcription canceled by user.");
        }
        catch (Exception ex) {
            RaiseError($"File transcription failed: {ex.Message}");
        }
        finally {
            _fileTranscriptionCts?.Dispose();
            _fileTranscriptionCts = null;
            IsFileTranscribing = false;
            IsBusy = false;
            AppendLog("Command finished: Transcribe File.");
        }
    }

    private Task OpenAudioFileAsync() {
        AppendLog("Command requested: Open Audio Preview File.");
        AppendLog("Opening file picker for audio preview.");

        string? selectedFilePath = SelectAudioFilePath("Select Audio File for Preview");
        if (string.IsNullOrWhiteSpace(selectedFilePath)) {
            AppendLog("Open preview canceled: user did not select a file.");
            return Task.CompletedTask;
        }

        TryLoadAudioPreview(selectedFilePath);
        return Task.CompletedTask;
    }

    private async Task StartLiveAsync() {
        AppendLog("Command requested: Start Live.");

        if (SelectedEngine is null || IsLiveRunning) {
            AppendLog("Start Live ignored: no model selected or live already running.");
            return;
        }

        if (!EnsureSelectedModelConfigured()) {
            AppendLog("Start Live aborted: selected model is not ready.");
            return;
        }

        ClearOutputCore();
        IsBusy = true;
        AppendLog($"Starting live transcription with {SelectedEngine.DisplayName}.");

        try {
            await _liveCoordinator.StartAsync(
                SelectedEngine.Id,
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
        ClearOutputCore();
        return Task.CompletedTask;
    }

    private void ClearOutputCore() {
        InterimText = string.Empty;
        UnsubscribeFromFinalizedLineChanges();
        FinalizedTranscriptLines.Clear();
        FinalizedText = string.Empty;
        ProcessLogs.Clear();
        _statusMessage = "Transcript and logs cleared.";
        NotifyPropertyChanged(nameof(StatusMessage));
    }

    private Task CancelFileTranscriptionAsync() {
        if (!IsFileTranscribing) {
            return Task.CompletedTask;
        }

        AppendLog("Command requested: Cancel.");
        StatusMessage = "Canceling file transcription...";

        try {
            _fileTranscriptionCts?.Cancel();
        }
        catch (ObjectDisposedException) {
            // Ignore cancellation race at teardown.
        }

        AppendLog("Cancel signal sent to file transcription.");
        return Task.CompletedTask;
    }

    private Task PlayAudioAsync() {
        if (!IsAudioFileLoaded) {
            return Task.CompletedTask;
        }

        try {
            _audioPlaybackService.Play();
            IsAudioPlaying = _audioPlaybackService.IsPlaying;
        }
        catch (Exception ex) {
            RaiseError($"Unable to play audio preview: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private Task PauseAudioAsync() {
        if (!IsAudioFileLoaded) {
            return Task.CompletedTask;
        }

        try {
            _audioPlaybackService.Pause();
            IsAudioPlaying = _audioPlaybackService.IsPlaying;
        }
        catch (Exception ex) {
            RaiseError($"Unable to pause audio preview: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private Task StopAudioAsync() {
        if (!IsAudioFileLoaded) {
            return Task.CompletedTask;
        }

        try {
            _audioPlaybackService.Stop();
            IsAudioPlaying = _audioPlaybackService.IsPlaying;
        }
        catch (Exception ex) {
            RaiseError($"Unable to stop audio preview: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private Task RewindAudioAsync() {
        if (!IsAudioFileLoaded) {
            return Task.CompletedTask;
        }

        try {
            double targetSeconds = Math.Max(0, AudioSeekPositionSeconds - SeekStepSeconds);
            AudioSeekPositionSeconds = targetSeconds;
        }
        catch (Exception ex) {
            RaiseError($"Unable to rewind audio preview: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private Task ForwardAudioAsync() {
        if (!IsAudioFileLoaded) {
            return Task.CompletedTask;
        }

        try {
            double targetSeconds = Math.Min(AudioSeekMaximumSeconds, AudioSeekPositionSeconds + SeekStepSeconds);
            AudioSeekPositionSeconds = targetSeconds;
        }
        catch (Exception ex) {
            RaiseError($"Unable to forward audio preview: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private bool CanTranscribeFile() {
        return SelectedEngine is not null
            && IsAudioFileLoaded
            && !IsBusy
            && !IsLiveRunning
            && !IsFileTranscribing;
    }

    private bool CanStartLive() {
        return SelectedEngine is not null && !IsBusy && !IsLiveRunning && !IsFileTranscribing;
    }

    private bool CanStopLive() {
        return IsLiveRunning && !IsBusy && !IsFileTranscribing;
    }

    private bool CanClear() {
        return !IsBusy && !IsLiveRunning && !IsFileTranscribing;
    }

    private bool CanCancelFileTranscription() {
        return IsFileTranscribing;
    }

    private bool CanOpenAudioFile() {
        return !IsBusy && !IsLiveRunning && !IsFileTranscribing;
    }

    private bool CanPlayAudio() {
        return IsAudioFileLoaded && !IsAudioPlaying;
    }

    private bool CanPauseAudio() {
        return IsAudioFileLoaded && IsAudioPlaying;
    }

    private bool CanStopAudio() {
        return IsAudioFileLoaded;
    }

    private bool CanSeekAudio() {
        return IsAudioFileLoaded;
    }

    private void RefreshCommandStates() {
        TranscribeFileCommand.RaiseCanExecuteChanged();
        StartLiveCommand.RaiseCanExecuteChanged();
        StopLiveCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        OpenAudioFileCommand.RaiseCanExecuteChanged();
        PlayAudioCommand.RaiseCanExecuteChanged();
        PauseAudioCommand.RaiseCanExecuteChanged();
        StopAudioCommand.RaiseCanExecuteChanged();
        RewindAudioCommand.RaiseCanExecuteChanged();
        ForwardAudioCommand.RaiseCanExecuteChanged();
    }

    private void NotifyInteractionAvailabilityChanged() {
        NotifyPropertyChanged(nameof(IsEngineSelectionEnabled));
        NotifyPropertyChanged(nameof(IsOpenAiSettingsEnabled));
    }

    private bool EnsureSelectedModelConfigured() {
        if (SelectedEngine is null) {
            AppendLog("Model configuration check failed: no selected model.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(OpenAiApiKey)) {
            RaiseError("OpenAI API key is required.");
            AppendLog("OpenAI transcription blocked: API key missing.");
            return false;
        }

        AppendLog("OpenAI transcription configuration verified.");
        return true;
    }

    private static string? SelectAudioFilePath(string dialogTitle) {
        var dialog = new Microsoft.Win32.OpenFileDialog {
            Title = dialogTitle,
            Filter = AudioFileDialogFilter,
            Multiselect = false,
        };

        return dialog.ShowDialog() == true
            ? dialog.FileName
            : null;
    }

    private void TryLoadAudioPreview(string filePath) {
        try {
            _audioPlaybackService.LoadFile(filePath);
            LoadedAudioFilePath = _audioPlaybackService.LoadedFilePath ?? filePath;
            IsAudioPlaying = _audioPlaybackService.IsPlaying;
            UpdateAudioTimelineFromPlayback();
            AppendLog($"Audio preview loaded: {LoadedAudioFileName}");
        }
        catch (Exception ex) {
            LoadedAudioFilePath = string.Empty;
            IsAudioPlaying = false;
            ResetAudioTimeline();
            AppendLog($"Audio preview load failed: {ex.Message}");
        }
    }

    private void AppendFinalFromFileResult(TranscriptionResult result) {
        TimeSpan timelineDuration = ResolveFileTimelineDuration(result);
        bool hasMeaningfulTimeline = HasMeaningfulTimeline(result.TimedLines);

        IEnumerable<TranscriptionTimedLine> lines =
            result.TimedLines is not null && result.TimedLines.Count > 0 && hasMeaningfulTimeline
                ? result.TimedLines
                : BuildMediaTimelineLines(result.Text, timelineDuration);

        IEnumerable<FinalizedTranscriptLineViewModel> formatted = lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .Select(line => new FinalizedTranscriptLineViewModel(
                timeline: FormatFileTimelineTimestamp(line.StartOffset),
                text: line.Text.Trim()));

        AppendFinalEntries(formatted, result.Text);
    }

    private void AppendFinalWithWallClockTimestamp(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return;
        }

        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        IEnumerable<FinalizedTranscriptLineViewModel> formatted = SplitTranscriptLines(text)
            .Select(line => new FinalizedTranscriptLineViewModel(timestamp, line));

        AppendFinalEntries(formatted, text);
    }

    private void AppendFinalEntries(IEnumerable<FinalizedTranscriptLineViewModel> lines, string rawTextForLog) {
        FinalizedTranscriptLineViewModel[] normalized = lines
            .Where(line => line is not null && !string.IsNullOrWhiteSpace(line.Text))
            .ToArray();

        if (normalized.Length == 0) {
            return;
        }

        foreach (FinalizedTranscriptLineViewModel line in normalized) {
            line.PropertyChanged += OnFinalizedLinePropertyChanged;
            FinalizedTranscriptLines.Add(line);
        }

        RebuildFinalizedTextFromLines();

        AppendLog($"Finalized text appended ({rawTextForLog.Trim().Length:N0} chars): {TrimForLog(rawTextForLog)}");
    }

    private void OnFinalizedLinePropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (!string.Equals(e.PropertyName, nameof(FinalizedTranscriptLineViewModel.Text), StringComparison.Ordinal)) {
            return;
        }

        RebuildFinalizedTextFromLines();
    }

    private void RebuildFinalizedTextFromLines() {
        string merged = string.Join(
            Environment.NewLine,
            FinalizedTranscriptLines.Select(line => {
                string timeline = line.Timeline?.Trim() ?? string.Empty;
                string text = line.Text?.Trim() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(timeline)) {
                    return text;
                }

                return string.IsNullOrWhiteSpace(text) ? timeline : $"{timeline} {text}";
            }));

        FinalizedText = merged;
    }

    private void UnsubscribeFromFinalizedLineChanges() {
        foreach (FinalizedTranscriptLineViewModel line in FinalizedTranscriptLines) {
            line.PropertyChanged -= OnFinalizedLinePropertyChanged;
        }
    }

    private TimeSpan ResolveFileTimelineDuration(TranscriptionResult result) {
        if (result.Duration is not null && result.Duration.Value > TimeSpan.Zero) {
            return result.Duration.Value;
        }

        TimeSpan previewDuration = _audioPlaybackService.Duration;
        if (previewDuration > TimeSpan.Zero) {
            return previewDuration;
        }

        return TimeSpan.Zero;
    }

    private static bool HasMeaningfulTimeline(IReadOnlyList<TranscriptionTimedLine>? timedLines) {
        if (timedLines is null || timedLines.Count == 0) {
            return false;
        }

        return timedLines.Any(line => line.StartOffset > TimeSpan.Zero);
    }

    private static IEnumerable<TranscriptionTimedLine> BuildMediaTimelineLines(string text, TimeSpan timelineDuration) {
        string[] parts = SplitTranscriptSegments(text).ToArray();

        if (parts.Length == 0) {
            return Array.Empty<TranscriptionTimedLine>();
        }

        var output = new List<TranscriptionTimedLine>(parts.Length);
        bool hasDuration = timelineDuration > TimeSpan.Zero;

        int totalWeight = parts.Sum(part => Math.Max(part.Length, 1));
        int cumulativeWeight = 0;

        for (int index = 0; index < parts.Length; index++) {
            TimeSpan offset;

            if (hasDuration && totalWeight > 0) {
                double ratio = cumulativeWeight / (double)totalWeight;
                offset = TimeSpan.FromTicks((long)(timelineDuration.Ticks * ratio));
            }
            else {
                offset = TimeSpan.FromSeconds(index);
            }

            output.Add(
                new TranscriptionTimedLine(
                    parts[index],
                    StartOffset: offset));

            cumulativeWeight += Math.Max(parts[index].Length, 1);
        }

        return output;
    }

    private static IEnumerable<string> SplitTranscriptLines(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return Array.Empty<string>();
        }

        return text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line));
    }

    private static IEnumerable<string> SplitTranscriptSegments(string text) {
        string[] byLine = SplitTranscriptLines(text).ToArray();

        if (byLine.Length > 1) {
            return byLine;
        }

        if (byLine.Length == 0) {
            return Array.Empty<string>();
        }

        string source = byLine[0];
        string[] bySentence = Regex.Split(source, @"(?<=[\.\!\?])\s+")
            .Select(part => part.Trim())
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        return bySentence.Length > 0 ? bySentence : byLine;
    }

    private static string FormatFileTimelineTimestamp(TimeSpan offset) {
        if (offset < TimeSpan.Zero) {
            offset = TimeSpan.Zero;
        }

        int totalMinutes = (int)offset.TotalMinutes;
        return $"{totalMinutes:00}:{offset.Seconds:00}";
    }

    private void OnUpdateReceived(object? sender, TranscriptUpdate update) {
        _uiContext.Post(_ => {
            if (update.IsFinal) {
                AppendLogCore($"Realtime final update received: {TrimForLog(update.Text)}");

                if (update.LowConfidenceTokens is not null && update.LowConfidenceTokens.Count > 0) {
                    AppendLogCore($"Realtime final update contains {update.LowConfidenceTokens.Count:N0} low-confidence token(s).");
                }

                AppendFinalWithWallClockTimestamp(update.Text);
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

    private void OnAudioPlaybackStateChanged(object? sender, EventArgs e) {
        _uiContext.Post(_ => {
            IsAudioPlaying = _audioPlaybackService.IsPlaying;
            string? loadedFilePath = _audioPlaybackService.LoadedFilePath;
            LoadedAudioFilePath = loadedFilePath ?? string.Empty;

            if (string.IsNullOrWhiteSpace(LoadedAudioFilePath)) {
                ResetAudioTimeline();
            }
            else {
                UpdateAudioTimelineFromPlayback();
            }
        }, null);
    }

    private void OnAudioTimelineTick(object? sender, EventArgs e) {
        if (!IsAudioFileLoaded) {
            return;
        }

        UpdateAudioTimelineFromPlayback();
    }

    private void UpdateAudioTimelineFromPlayback() {
        TimeSpan duration = _audioPlaybackService.Duration;
        TimeSpan position = _audioPlaybackService.Position;

        if (duration < TimeSpan.Zero) {
            duration = TimeSpan.Zero;
        }

        if (position < TimeSpan.Zero) {
            position = TimeSpan.Zero;
        }

        if (duration > TimeSpan.Zero && position > duration) {
            position = duration;
        }

        AudioSeekMaximumSeconds = Math.Max(duration.TotalSeconds, 0);

        _isUpdatingSeekFromPlayback = true;
        try {
            AudioSeekPositionSeconds = Math.Min(position.TotalSeconds, AudioSeekMaximumSeconds);
        }
        finally {
            _isUpdatingSeekFromPlayback = false;
        }

        UpdateAudioTimeLabels(position, duration);
    }

    private void ResetAudioTimeline() {
        AudioSeekMaximumSeconds = 0;

        _isUpdatingSeekFromPlayback = true;
        try {
            AudioSeekPositionSeconds = 0;
        }
        finally {
            _isUpdatingSeekFromPlayback = false;
        }

        AudioElapsedText = "00:00";
        AudioRemainingText = "-00:00";
    }

    private void UpdateAudioTimeLabels(TimeSpan elapsed, TimeSpan duration) {
        TimeSpan remaining = duration - elapsed;

        if (remaining < TimeSpan.Zero) {
            remaining = TimeSpan.Zero;
        }

        AudioElapsedText = FormatPlaybackTime(elapsed);
        AudioRemainingText = $"-{FormatPlaybackTime(remaining)}";
    }

    private static string FormatPlaybackTime(TimeSpan value) {
        if (value.TotalHours >= 1) {
            return value.ToString(@"hh\:mm\:ss");
        }

        return value.ToString(@"mm\:ss");
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
}
