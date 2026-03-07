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
    private static readonly TimeSpan PlaceholderSegmentDuration = TimeSpan.FromSeconds(10);
    private const string AudioFileDialogFilter = "Audio Files|*.wav;*.mp3;*.flac;*.aac;*.m4a;*.ogg;*.wma;*.mp4|All Files|*.*";

    private readonly ITranscriptionService _transcriptionService;
    private readonly IAudioPlaybackService _audioPlaybackService;
    private readonly OpenAiTranscriptionOptions _openAiOptions;
    private readonly OpenAiSettingsStore _openAiSettingsStore;
    private readonly OpenAiApiKeyValidationService _openAiApiKeyValidationService;
    private readonly ProcessLogService _processLogService;
    private readonly TranscriptSessionStore _sessionStore;
    private readonly AppPreferencesStore _appPreferencesStore;
    private readonly SynchronizationContext _uiContext;
    private readonly DispatcherTimer _audioTimelineTimer;
    private readonly DispatcherTimer _sessionAutosaveTimer;
    private readonly SemaphoreSlim _sessionSaveSemaphore = new(1, 1);

    private EngineOptionViewModel? _selectedEngine;
    private TranscriptSessionSummary? _selectedRecentSession;
    private TranscriptSessionDocument? _currentSessionDocument;
    private string _currentSessionDisplayName = "No session loaded.";
    private string _currentSessionAudioIssue = string.Empty;
    private string _finalizedText = string.Empty;
    private string _statusMessage = "Ready.";
    private string _openAiApiKey;
    private bool _isBusy;
    private bool _isFileTranscribing;
    private bool _isCurrentSessionAudioMissing;
    private string _loadedAudioFilePath = string.Empty;
    private bool _isAudioPlaying;
    private double _audioSeekMaximumSeconds;
    private double _audioSeekPositionSeconds;
    private string _audioElapsedText = "00:00";
    private string _audioRemainingText = "-00:00";
    private bool _copyFinalizedWithTimeline;
    private bool _isUpdatingSeekFromPlayback;
    private bool _suppressSessionAutosave;
    private CancellationTokenSource? _fileTranscriptionCts;

    public MainViewModel(
        IEnumerable<TranscriptionModelOption> models,
        ITranscriptionService transcriptionService,
        IAudioPlaybackService audioPlaybackService,
        OpenAiTranscriptionOptions openAiOptions,
        OpenAiSettingsStore openAiSettingsStore,
        OpenAiApiKeyValidationService openAiApiKeyValidationService,
        ProcessLogService processLogService,
        TranscriptSessionStore sessionStore,
        AppPreferencesStore appPreferencesStore,
        AppPreferencesSnapshot appPreferencesSnapshot) {
        _transcriptionService = transcriptionService;
        _audioPlaybackService = audioPlaybackService;
        _openAiOptions = openAiOptions;
        _openAiSettingsStore = openAiSettingsStore;
        _openAiApiKeyValidationService = openAiApiKeyValidationService;
        _processLogService = processLogService;
        _sessionStore = sessionStore;
        _appPreferencesStore = appPreferencesStore;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();

        _openAiApiKey = _openAiOptions.ApiKey;
        _copyFinalizedWithTimeline = appPreferencesSnapshot.CopyFinalizedWithTimeline;

        Engines = new ObservableCollection<EngineOptionViewModel>(
            models.Select(model => new EngineOptionViewModel(model)));
        ProcessLogs = new ObservableCollection<ProcessLogEntryViewModel>();
        FinalizedTranscriptLines = new ObservableCollection<FinalizedTranscriptLineViewModel>();
        RecentSessions = new ObservableCollection<TranscriptSessionSummary>();

        TranscribeFileCommand = new AsyncRelayCommand(TranscribeFileAsync, CanTranscribeFile);
        CreatePlaceholdersCommand = new AsyncRelayCommand(CreatePlaceholdersAsync, CanCreatePlaceholders);
        ClearCommand = new AsyncRelayCommand(ClearAsync, CanClear);
        CancelCommand = new AsyncRelayCommand(CancelFileTranscriptionAsync, CanCancelFileTranscription);
        OpenAudioFileCommand = new AsyncRelayCommand(OpenAudioFileAsync, CanOpenAudioFile);
        OpenSelectedSessionCommand = new AsyncRelayCommand(OpenSelectedSessionAsync, CanOpenSelectedSession);
        DeleteSelectedSessionCommand = new AsyncRelayCommand(DeleteSelectedSessionAsync, CanDeleteSelectedSession);
        PlayAudioCommand = new AsyncRelayCommand(PlayAudioAsync, CanPlayAudio);
        PauseAudioCommand = new AsyncRelayCommand(PauseAudioAsync, CanPauseAudio);
        StopAudioCommand = new AsyncRelayCommand(StopAudioAsync, CanStopAudio);
        RewindAudioCommand = new AsyncRelayCommand(RewindAudioAsync, CanSeekAudio);
        ForwardAudioCommand = new AsyncRelayCommand(ForwardAudioAsync, CanSeekAudio);

        _processLogService.LogEmitted += OnProcessLogEmitted;
        _audioPlaybackService.PlaybackStateChanged += OnAudioPlaybackStateChanged;
        _isAudioPlaying = _audioPlaybackService.IsPlaying;

        _audioTimelineTimer = new DispatcherTimer {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _audioTimelineTimer.Tick += OnAudioTimelineTick;
        _audioTimelineTimer.Start();

        _sessionAutosaveTimer = new DispatcherTimer {
            Interval = TimeSpan.FromMilliseconds(800),
        };
        _sessionAutosaveTimer.Tick += OnSessionAutosaveTimerTick;

        SelectedEngine = Engines.FirstOrDefault(engine =>
            string.Equals(engine.Id, OpenAiTranscriptionModelCatalog.Gpt4oMiniTranscribe, StringComparison.OrdinalIgnoreCase))
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

        LoadRecentSessions(selectSessionId: null);

        if (RecentSessions.Count > 0) {
            StatusMessage = "Recent sessions are available. Select one from the list to reopen it.";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<ConfirmationRequest>? ConfirmationRequested;
    public event EventHandler<ToastNotification>? ToastRequested;

    public ObservableCollection<EngineOptionViewModel> Engines { get; }
    public ObservableCollection<ProcessLogEntryViewModel> ProcessLogs { get; }
    public ObservableCollection<FinalizedTranscriptLineViewModel> FinalizedTranscriptLines { get; }
    public ObservableCollection<TranscriptSessionSummary> RecentSessions { get; }

    public AsyncRelayCommand TranscribeFileCommand { get; }
    public AsyncRelayCommand CreatePlaceholdersCommand { get; }
    public AsyncRelayCommand ClearCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }
    public AsyncRelayCommand OpenAudioFileCommand { get; }
    public AsyncRelayCommand OpenSelectedSessionCommand { get; }
    public AsyncRelayCommand DeleteSelectedSessionCommand { get; }
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

    public TranscriptSessionSummary? SelectedRecentSession {
        get => _selectedRecentSession;
        set {
            if (!SetProperty(ref _selectedRecentSession, value)) {
                return;
            }

            RefreshCommandStates();
        }
    }

    public bool HasRecentSessions => RecentSessions.Count > 0;

    public string CurrentSessionDisplayName {
        get => _currentSessionDisplayName;
        private set => SetProperty(ref _currentSessionDisplayName, value);
    }

    public string CurrentSessionAudioIssue {
        get => _currentSessionAudioIssue;
        private set => SetProperty(ref _currentSessionAudioIssue, value);
    }

    public bool HasCurrentSession => _currentSessionDocument is not null;

    public bool IsCurrentSessionAudioMissing {
        get => _isCurrentSessionAudioMissing;
        private set {
            if (!SetProperty(ref _isCurrentSessionAudioMissing, value)) {
                return;
            }

            NotifyPropertyChanged(nameof(LoadedAudioFileName));
            RefreshCommandStates();
        }
    }

    public bool IsOpenAiEngineSelected => SelectedEngine is not null;

    public bool IsEngineSelectionEnabled =>
        !IsBusy && !IsFileTranscribing;

    public bool IsOpenAiSettingsEnabled =>
        IsOpenAiEngineSelected && !IsBusy && !IsFileTranscribing;

    public bool CopyFinalizedWithTimeline {
        get => _copyFinalizedWithTimeline;
        set {
            if (!SetProperty(ref _copyFinalizedWithTimeline, value)) {
                return;
            }

            _appPreferencesStore.Save(value);
            AppendLog($"Copy finalized transcript with timeline: {(value ? "ON" : "OFF")}.");
        }
    }

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

    public string LoadedAudioFileName {
        get {
            if (!string.IsNullOrWhiteSpace(LoadedAudioFilePath)) {
                return Path.GetFileName(LoadedAudioFilePath);
            }

            if (_currentSessionDocument is not null && !string.IsNullOrWhiteSpace(_currentSessionDocument.Audio.OriginalFileName)) {
                return _currentSessionDocument.Audio.OriginalFileName;
            }

            return "No audio file selected.";
        }
    }

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

    public ValueTask DisposeAsync() {
        AppendLog("Disposing transcription resources...");

        try {
            _fileTranscriptionCts?.Cancel();
        }
        catch (ObjectDisposedException) {
            // Ignore cancellation race at teardown.
        }

        _fileTranscriptionCts?.Dispose();
        _fileTranscriptionCts = null;

        _sessionAutosaveTimer.Stop();
        _sessionAutosaveTimer.Tick -= OnSessionAutosaveTimerTick;
        TrySaveCurrentSession(
            updateTranscriptionMetadata: false,
            showErrorDialog: false,
            successLogMessage: string.Empty);

        _processLogService.LogEmitted -= OnProcessLogEmitted;
        _audioPlaybackService.PlaybackStateChanged -= OnAudioPlaybackStateChanged;
        UnsubscribeFromFinalizedLineChanges();
        _audioTimelineTimer.Stop();
        _audioTimelineTimer.Tick -= OnAudioTimelineTick;
        _audioPlaybackService.Dispose();
        AppendLog("Disposed transcription resources.");
        return ValueTask.CompletedTask;
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

    public void SeekAudioPreview(TimeSpan position) {
        if (!IsAudioFileLoaded) {
            return;
        }

        TimeSpan clamped = position < TimeSpan.Zero
            ? TimeSpan.Zero
            : position;
        TimeSpan duration = _audioPlaybackService.Duration;

        if (duration > TimeSpan.Zero && clamped > duration) {
            clamped = duration;
        }

        _audioPlaybackService.Seek(clamped);
        IsAudioPlaying = _audioPlaybackService.IsPlaying;
        UpdateAudioTimelineFromPlayback();
    }

    public void RestartAudioPreviewSegment(TimeSpan position) {
        if (!IsAudioFileLoaded) {
            return;
        }

        TimeSpan clamped = position < TimeSpan.Zero
            ? TimeSpan.Zero
            : position;
        TimeSpan duration = _audioPlaybackService.Duration;

        if (duration > TimeSpan.Zero && clamped > duration) {
            clamped = duration;
        }

        bool wasPlaying = _audioPlaybackService.IsPlaying;

        if (wasPlaying) {
            _audioPlaybackService.Pause();
        }

        _audioPlaybackService.Seek(clamped);
        _audioPlaybackService.Play();
        IsAudioPlaying = _audioPlaybackService.IsPlaying;
        UpdateAudioTimelineFromPlayback();
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

        if (_currentSessionDocument is null) {
            RaiseError("No session is loaded for the current audio file.");
            AppendLog("Transcribe aborted: current audio is not associated with a session.");
            return;
        }

        string selectedFilePath = LoadedAudioFilePath;
        if (!File.Exists(selectedFilePath)) {
            RaiseError($"Loaded audio file does not exist: {selectedFilePath}");
            AppendLog("Transcribe aborted: loaded audio file path is invalid.");
            return;
        }

        bool hasExistingTranscript = HasExistingTranscriptContent();
        if (hasExistingTranscript && !ConfirmTranscriptReplacement(
                operationName: "Transcribe",
                canceledStatusMessage: "Transcription canceled. Existing transcript was kept.")) {
            return;
        }

        if (hasExistingTranscript) {
            ResetCurrentSessionTranscriptState();
        }

        ClearTranscriptAndLogs(unloadAudioPreview: false);

        if (hasExistingTranscript
            && !TrySaveCurrentSession(
                updateTranscriptionMetadata: false,
                showErrorDialog: true,
                successLogMessage: "Existing transcript cleared before retranscription.")) {
            AppendLog("Transcribe aborted: existing transcript could not be cleared safely.");
            return;
        }

        AppendLog($"Using loaded audio file: {selectedFilePath}");

        try {
            long fileSize = new FileInfo(selectedFilePath).Length;
            AppendLog($"Selected file size: {fileSize:N0} bytes.");
        }
        catch {
            AppendLog("Unable to read selected file size.");
        }

        _fileTranscriptionCts?.Dispose();
        _fileTranscriptionCts = new CancellationTokenSource();
        CancellationToken transcriptionToken = _fileTranscriptionCts.Token;

        bool largeFileToastShown = false;
        var progress = new Progress<TranscriptionProgressUpdate>(update => {
            if (!string.IsNullOrWhiteSpace(update.StatusMessage)) {
                StatusMessage = update.StatusMessage;
            }

            if (update.IsLargeFile && !largeFileToastShown) {
                largeFileToastShown = true;
                RaiseToast(
                    "Large file detected",
                    "It will be transcribed in multiple parts automatically.",
                    ToastNotificationType.Info);
            }
        });

        IsBusy = true;
        IsFileTranscribing = true;
        StatusMessage = $"Transcribing file using {SelectedEngine.DisplayName}...";

        try {
            TranscriptionResult result = await _transcriptionService.TranscribeFileAsync(
                selectedFilePath,
                SelectedEngine.Id,
                transcriptionToken,
                progress);

            AppendFinalFromFileResult(result);
            StatusMessage = "File transcription completed.";
            AppendLog(
                $"File transcription completed. Received {result.Text.Length:N0} characters. " +
                $"Logprobs={result.TokenLogprobs.Count:N0}, low-confidence={result.LowConfidenceTokens.Count:N0}.");

            TrySaveCurrentSession(
                updateTranscriptionMetadata: true,
                showErrorDialog: true,
                successLogMessage: "Session saved after transcription.");
            LoadRecentSessions(_currentSessionDocument.SessionId);
        }
        catch (OperationCanceledException) when (transcriptionToken.IsCancellationRequested) {
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

    private Task CreatePlaceholdersAsync() {
        AppendLog("Command requested: Create Placeholders.");

        if (!IsAudioFileLoaded) {
            AppendLog("Create placeholders aborted: no audio file is loaded in preview.");
            return Task.CompletedTask;
        }

        if (!EnsureCurrentSessionForLoadedAudio()) {
            AppendLog("Create placeholders aborted: current audio is not associated with a session.");
            return Task.CompletedTask;
        }

        if (!TryResolveLoadedAudioDuration(out TimeSpan duration) || duration <= TimeSpan.Zero) {
            RaiseError("Unable to determine the loaded audio duration for placeholder creation.");
            AppendLog("Create placeholders aborted: audio duration is unavailable.");
            return Task.CompletedTask;
        }

        bool hasExistingTranscript = HasExistingTranscriptContent();
        if (hasExistingTranscript && !ConfirmTranscriptReplacement(
                operationName: "Create placeholders",
                canceledStatusMessage: "Placeholder creation canceled. Existing transcript was kept.")) {
            return Task.CompletedTask;
        }

        if (hasExistingTranscript) {
            ResetCurrentSessionTranscriptState();
        }

        ClearTranscriptAndLogs(unloadAudioPreview: false);

        if (hasExistingTranscript
            && !TrySaveCurrentSession(
                updateTranscriptionMetadata: false,
                showErrorDialog: true,
                successLogMessage: "Existing transcript cleared before placeholder creation.")) {
            AppendLog("Create placeholders aborted: existing transcript could not be cleared safely.");
            return Task.CompletedTask;
        }

        IsBusy = true;
        StatusMessage = "Creating placeholder transcript...";

        try {
            CreatePlaceholderTranscript(duration);

            if (!TrySaveCurrentSession(
                    updateTranscriptionMetadata: false,
                    showErrorDialog: true,
                    successLogMessage: "Session saved after placeholder creation.")) {
                AppendLog("Create placeholders aborted: generated placeholders could not be saved.");
                return Task.CompletedTask;
            }

            if (_currentSessionDocument is not null) {
                LoadRecentSessions(_currentSessionDocument.SessionId);
            }

            StatusMessage = "Placeholder transcript created.";
            AppendLog($"Placeholder transcript created with {FinalizedTranscriptLines.Count:N0} segment(s).");
        }
        finally {
            IsBusy = false;
            AppendLog("Command finished: Create Placeholders.");
        }

        return Task.CompletedTask;
    }

    private Task OpenAudioFileAsync() {
        AppendLog("Command requested: Open Audio Preview File.");
        AppendLog("Opening file picker for audio preview.");

        string? selectedFilePath = SelectAudioFilePath("Select Audio File for Preview");
        if (string.IsNullOrWhiteSpace(selectedFilePath)) {
            AppendLog("Open preview canceled: user did not select a file.");
            return Task.CompletedTask;
        }

        LoadSessionFromImportedAudio(selectedFilePath);
        return Task.CompletedTask;
    }

    private Task OpenSelectedSessionAsync() {
        if (SelectedRecentSession is null) {
            return Task.CompletedTask;
        }

        return LoadSessionByIdAsync(SelectedRecentSession.SessionId);
    }

    private async Task DeleteSelectedSessionAsync() {
        if (SelectedRecentSession is null) {
            return;
        }

        TranscriptSessionSummary sessionToDelete = SelectedRecentSession;
        if (!ConfirmSessionDeletion()) {
            AppendLog("Session deletion canceled.");
            return;
        }

        bool deletingCurrentSession =
            _currentSessionDocument is not null
            && string.Equals(_currentSessionDocument.SessionId, sessionToDelete.SessionId, StringComparison.OrdinalIgnoreCase);
        string? currentAudioPath = deletingCurrentSession ? LoadedAudioFilePath : null;

        IsBusy = true;
        StatusMessage = "Deleting session...";

        try {
            _sessionAutosaveTimer.Stop();

            if (deletingCurrentSession && IsAudioFileLoaded) {
                _audioPlaybackService.UnloadFile();
                LoadedAudioFilePath = string.Empty;
                IsAudioPlaying = false;
                ResetAudioTimeline();
            }

            await _sessionSaveSemaphore.WaitAsync();
            try {
                _sessionStore.DeleteSession(sessionToDelete.SessionId);
            }
            finally {
                _sessionSaveSemaphore.Release();
            }

            if (deletingCurrentSession) {
                ClearCurrentSessionAfterDeletion();
            }

            LoadRecentSessions(selectSessionId: null);
            StatusMessage = "Session deleted.";
            AppendLog($"Session deleted: {sessionToDelete.DisplayName}.");
        }
        catch (Exception ex) {
            if (deletingCurrentSession
                && !string.IsNullOrWhiteSpace(currentAudioPath)
                && File.Exists(currentAudioPath)) {
                TryLoadAudioPreview(currentAudioPath);
            }

            RaiseError($"Unable to delete session: {ex.Message}");
            AppendLog($"Session deletion failed: {ex.Message}");
        }
        finally {
            IsBusy = false;
        }
    }

    private Task ClearAsync() {
        _sessionAutosaveTimer.Stop();
        TrySaveCurrentSession(
            updateTranscriptionMetadata: false,
            showErrorDialog: false,
            successLogMessage: string.Empty);
        ClearOutputCore(unloadAudioPreview: true, clearSessionContext: true);
        return Task.CompletedTask;
    }

    private void ClearOutputCore(bool unloadAudioPreview, bool clearSessionContext) {
        _sessionAutosaveTimer.Stop();

        if (unloadAudioPreview) {
            _audioPlaybackService.UnloadFile();
        }

        _suppressSessionAutosave = true;
        try {
            UnsubscribeFromFinalizedLineChanges();
            FinalizedTranscriptLines.Clear();
            FinalizedText = string.Empty;
            ProcessLogs.Clear();
        }
        finally {
            _suppressSessionAutosave = false;
        }

        if (clearSessionContext) {
            _currentSessionDocument = null;
            CurrentSessionDisplayName = "No session loaded.";
            CurrentSessionAudioIssue = string.Empty;
            IsCurrentSessionAudioMissing = false;
            NotifyPropertyChanged(nameof(HasCurrentSession));
            NotifyPropertyChanged(nameof(LoadedAudioFileName));
        }

        _statusMessage = unloadAudioPreview
            ? "Transcript, logs, and audio preview cleared."
            : "Transcript and logs cleared.";
        NotifyPropertyChanged(nameof(StatusMessage));
        RefreshCommandStates();
    }

    private void ClearTranscriptAndLogs(bool unloadAudioPreview) {
        ClearOutputCore(unloadAudioPreview, clearSessionContext: false);
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
            && !IsFileTranscribing;
    }

    private bool CanCreatePlaceholders() {
        return CanTranscribeFile();
    }

    private bool CanClear() {
        return !IsBusy && !IsFileTranscribing;
    }

    private bool CanCancelFileTranscription() {
        return IsFileTranscribing;
    }

    private bool CanOpenAudioFile() {
        return !IsBusy && !IsFileTranscribing;
    }

    private bool CanOpenSelectedSession() {
        return SelectedRecentSession is not null && !IsBusy && !IsFileTranscribing;
    }

    private bool CanDeleteSelectedSession() {
        return SelectedRecentSession is not null && !IsBusy && !IsFileTranscribing;
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
        CreatePlaceholdersCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        OpenAudioFileCommand.RaiseCanExecuteChanged();
        OpenSelectedSessionCommand.RaiseCanExecuteChanged();
        DeleteSelectedSessionCommand.RaiseCanExecuteChanged();
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

    private void LoadSessionFromImportedAudio(string sourceFilePath) {
        try {
            _sessionAutosaveTimer.Stop();
            TrySaveCurrentSession(
                updateTranscriptionMetadata: false,
                showErrorDialog: false,
                successLogMessage: string.Empty);

            TranscriptSessionLoadResult loadResult = _sessionStore.ImportAudioFile(sourceFilePath);
            LoadSessionResult(loadResult, showAudioIssueDialog: true);
            LoadRecentSessions(loadResult.Document.SessionId);
            StatusMessage = $"Session loaded: {CurrentSessionDisplayName}.";
        }
        catch (Exception ex) {
            RaiseError($"Unable to import audio into a session: {ex.Message}");
        }
    }

    private bool EnsureCurrentSessionForLoadedAudio() {
        if (_currentSessionDocument is not null) {
            return true;
        }

        if (!IsAudioFileLoaded || string.IsNullOrWhiteSpace(LoadedAudioFilePath)) {
            return false;
        }

        try {
            TranscriptSessionLoadResult loadResult = _sessionStore.ImportAudioFile(LoadedAudioFilePath);
            LoadSessionResult(loadResult, showAudioIssueDialog: true);
            LoadRecentSessions(loadResult.Document.SessionId);
            return true;
        }
        catch (Exception ex) {
            RaiseError($"Unable to create a session for the loaded audio file: {ex.Message}");
            AppendLog($"Create placeholders canceled: session creation failed: {ex.Message}");
            return false;
        }
    }

    private Task LoadSessionByIdAsync(string sessionId) {
        try {
            _sessionAutosaveTimer.Stop();
            TrySaveCurrentSession(
                updateTranscriptionMetadata: false,
                showErrorDialog: false,
                successLogMessage: string.Empty);

            TranscriptSessionLoadResult loadResult = _sessionStore.LoadSession(sessionId);
            LoadSessionResult(loadResult, showAudioIssueDialog: false);
            TryRestoreCurrentSessionAudioAfterOpen();
            LoadRecentSessions(sessionId);
            StatusMessage = IsCurrentSessionAudioMissing
                ? $"Session loaded: {CurrentSessionDisplayName}. Audio restore is still required."
                : $"Session loaded: {CurrentSessionDisplayName}.";
        }
        catch (Exception ex) {
            RaiseError($"Unable to load session: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private void LoadSessionResult(TranscriptSessionLoadResult loadResult, bool showAudioIssueDialog) {
        _sessionAutosaveTimer.Stop();
        _suppressSessionAutosave = true;

        try {
            _currentSessionDocument = loadResult.Document;
            CurrentSessionDisplayName = string.IsNullOrWhiteSpace(loadResult.Document.DisplayName)
                ? loadResult.Document.Audio.OriginalFileName
                : loadResult.Document.DisplayName;

            NotifyPropertyChanged(nameof(HasCurrentSession));
            NotifyPropertyChanged(nameof(LoadedAudioFileName));

            ApplyTranscriptDocument(loadResult.Document.Transcript);

            CurrentSessionAudioIssue = string.Empty;
            IsCurrentSessionAudioMissing = false;

            if (loadResult.AudioAvailable && !string.IsNullOrWhiteSpace(loadResult.AudioFilePath)) {
                bool loaded = TryLoadAudioPreview(loadResult.AudioFilePath);
                if (!loaded) {
                    CurrentSessionAudioIssue = "The stored session audio file could not be loaded. Reopen the session and select the original audio file to restore playback.";
                    IsCurrentSessionAudioMissing = true;
                }
            }
            else {
                _audioPlaybackService.UnloadFile();
                LoadedAudioFilePath = string.Empty;
                IsAudioPlaying = false;
                ResetAudioTimeline();
                CurrentSessionAudioIssue = loadResult.AudioIssueMessage ?? string.Empty;
                IsCurrentSessionAudioMissing = !string.IsNullOrWhiteSpace(CurrentSessionAudioIssue);
            }
        }
        finally {
            _suppressSessionAutosave = false;
        }

        if (IsCurrentSessionAudioMissing && showAudioIssueDialog && !string.IsNullOrWhiteSpace(CurrentSessionAudioIssue)) {
            RaiseError($"{CurrentSessionAudioIssue} Reopen the session and select the original audio file to continue playback or retranscription.");
        }
    }

    private void TryRestoreCurrentSessionAudioAfterOpen() {
        if (_currentSessionDocument is null || !IsCurrentSessionAudioMissing) {
            return;
        }

        AppendLog("Session audio is unavailable. Prompting for the original audio file.");

        string? selectedFilePath = SelectAudioFilePath("Restore Session Audio");
        if (string.IsNullOrWhiteSpace(selectedFilePath)) {
            AppendLog("Restore audio canceled: user did not select a file.");
            return;
        }

        try {
            TranscriptSessionLoadResult loadResult = _sessionStore.RestoreAudioFile(_currentSessionDocument.SessionId, selectedFilePath);
            LoadSessionResult(loadResult, showAudioIssueDialog: true);
            StatusMessage = "Session audio restored.";
            RaiseToast(
                "Session audio restored",
                "The session copy is available again and ready for playback.",
                ToastNotificationType.Success);
        }
        catch (Exception ex) {
            RaiseError($"Unable to restore session audio: {ex.Message}");
        }
    }

    private bool TryLoadAudioPreview(string filePath) {
        try {
            _audioPlaybackService.LoadFile(filePath);
            LoadedAudioFilePath = _audioPlaybackService.LoadedFilePath ?? filePath;
            IsAudioPlaying = _audioPlaybackService.IsPlaying;
            UpdateAudioTimelineFromPlayback();
            AppendLog($"Audio preview loaded: {LoadedAudioFileName}");
            return true;
        }
        catch (Exception ex) {
            LoadedAudioFilePath = string.Empty;
            IsAudioPlaying = false;
            ResetAudioTimeline();
            AppendLog($"Audio preview load failed: {ex.Message}");
            return false;
        }
    }

    private void ApplyTranscriptDocument(TranscriptSessionTranscriptDocument transcript) {
        UnsubscribeFromFinalizedLineChanges();
        FinalizedTranscriptLines.Clear();

        foreach (TranscriptSessionLineDocument line in transcript.Lines.Where(line => !string.IsNullOrWhiteSpace(line.Text))) {
            var item = new FinalizedTranscriptLineViewModel(
                startOffset: line.StartSeconds is null ? null : TimeSpan.FromSeconds(Math.Max(line.StartSeconds.Value, 0)),
                endOffset: line.EndSeconds is null ? null : TimeSpan.FromSeconds(Math.Max(line.EndSeconds.Value, 0)),
                isTimestampEstimated: line.IsTimestampEstimated,
                text: line.Text);
            item.PropertyChanged += OnFinalizedLinePropertyChanged;
            FinalizedTranscriptLines.Add(item);
        }

        RebuildFinalizedTextFromLines();
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
                startOffset: line.StartOffset,
                endOffset: line.EndOffset,
                isTimestampEstimated: line.IsTimestampEstimated,
                text: line.Text.Trim()));

        AppendFinalEntries(formatted, result.Text);
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

    private void CreatePlaceholderTranscript(TimeSpan duration) {
        _suppressSessionAutosave = true;

        try {
            UnsubscribeFromFinalizedLineChanges();
            FinalizedTranscriptLines.Clear();

            for (TimeSpan start = TimeSpan.Zero; start < duration; start += PlaceholderSegmentDuration) {
                TimeSpan end = start + PlaceholderSegmentDuration;
                if (end > duration) {
                    end = duration;
                }

                var line = new FinalizedTranscriptLineViewModel(
                    startOffset: start,
                    endOffset: end,
                    isTimestampEstimated: true,
                    text: string.Empty);
                line.PropertyChanged += OnFinalizedLinePropertyChanged;
                FinalizedTranscriptLines.Add(line);
            }

            RebuildFinalizedTextFromLines();
        }
        finally {
            _suppressSessionAutosave = false;
        }
    }

    public bool InsertFinalizedTranscriptLine(int index, FinalizedTranscriptLineViewModel line) {
        ArgumentNullException.ThrowIfNull(line);

        int safeIndex = Math.Min(Math.Max(index, 0), FinalizedTranscriptLines.Count);
        line.PropertyChanged += OnFinalizedLinePropertyChanged;
        FinalizedTranscriptLines.Insert(safeIndex, line);
        RebuildFinalizedTextFromLines();
        if (!PersistTranscriptStructureChange()) {
            line.PropertyChanged -= OnFinalizedLinePropertyChanged;
            FinalizedTranscriptLines.RemoveAt(safeIndex);
            RebuildFinalizedTextFromLines();
            return false;
        }

        return true;
    }

    public bool RemoveFinalizedTranscriptLine(FinalizedTranscriptLineViewModel line) {
        ArgumentNullException.ThrowIfNull(line);

        int index = FinalizedTranscriptLines.IndexOf(line);
        if (index < 0) {
            return false;
        }

        line.PropertyChanged -= OnFinalizedLinePropertyChanged;

        if (!FinalizedTranscriptLines.Remove(line)) {
            line.PropertyChanged += OnFinalizedLinePropertyChanged;
            return false;
        }

        RebuildFinalizedTextFromLines();
        if (!PersistTranscriptStructureChange()) {
            line.PropertyChanged += OnFinalizedLinePropertyChanged;
            FinalizedTranscriptLines.Insert(index, line);
            RebuildFinalizedTextFromLines();
            return false;
        }

        return true;
    }

    private void OnFinalizedLinePropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (!string.Equals(e.PropertyName, nameof(FinalizedTranscriptLineViewModel.Text), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(FinalizedTranscriptLineViewModel.Timeline), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(FinalizedTranscriptLineViewModel.StartOffset), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(FinalizedTranscriptLineViewModel.EndOffset), StringComparison.Ordinal)) {
            return;
        }

        RebuildFinalizedTextFromLines();
        ScheduleSessionAutosave();
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

        if (_currentSessionDocument?.Audio.DurationSeconds is double durationSeconds && durationSeconds > 0) {
            return TimeSpan.FromSeconds(durationSeconds);
        }

        return TimeSpan.Zero;
    }

    private static bool HasMeaningfulTimeline(IReadOnlyList<TranscriptionTimedLine>? timedLines) {
        if (timedLines is null || timedLines.Count == 0) {
            return false;
        }

        return timedLines.Any(line =>
            line.StartOffset > TimeSpan.Zero
            || (line.EndOffset is not null && line.EndOffset > line.StartOffset));
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
            TimeSpan startOffset;
            TimeSpan endOffset;

            if (hasDuration && totalWeight > 0) {
                double startRatio = cumulativeWeight / (double)totalWeight;
                cumulativeWeight += Math.Max(parts[index].Length, 1);
                double endRatio = cumulativeWeight / (double)totalWeight;
                startOffset = TimeSpan.FromTicks((long)(timelineDuration.Ticks * startRatio));
                endOffset = TimeSpan.FromTicks((long)(timelineDuration.Ticks * endRatio));
            }
            else {
                startOffset = TimeSpan.FromSeconds(index);
                endOffset = TimeSpan.FromSeconds(index + 1);
            }

            output.Add(new TranscriptionTimedLine(
                Text: parts[index],
                StartOffset: startOffset,
                EndOffset: endOffset,
                IsTimestampEstimated: true));
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

    private void OnSessionAutosaveTimerTick(object? sender, EventArgs e) {
        _sessionAutosaveTimer.Stop();
        QueueSessionAutosaveSave();
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

    private void LoadRecentSessions(string? selectSessionId) {
        IReadOnlyList<TranscriptSessionSummary> sessions;

        try {
            sessions = _sessionStore.ListRecentSessions();
        }
        catch (Exception ex) {
            AppendLog($"Unable to load recent sessions: {ex.Message}");
            sessions = Array.Empty<TranscriptSessionSummary>();
        }

        RecentSessions.Clear();

        foreach (TranscriptSessionSummary session in sessions) {
            RecentSessions.Add(session);
        }

        NotifyPropertyChanged(nameof(HasRecentSessions));
        SelectedRecentSession = !string.IsNullOrWhiteSpace(selectSessionId)
            ? RecentSessions.FirstOrDefault(item => string.Equals(item.SessionId, selectSessionId, StringComparison.OrdinalIgnoreCase))
            : _selectedRecentSession is not null
                ? RecentSessions.FirstOrDefault(item => string.Equals(item.SessionId, _selectedRecentSession.SessionId, StringComparison.OrdinalIgnoreCase))
                : null;
    }

    private void ScheduleSessionAutosave() {
        if (_suppressSessionAutosave || _currentSessionDocument is null) {
            return;
        }

        _sessionAutosaveTimer.Stop();
        _sessionAutosaveTimer.Start();
    }

    private bool PersistTranscriptStructureChange() {
        _sessionAutosaveTimer.Stop();

        bool saved = TrySaveCurrentSession(
            updateTranscriptionMetadata: false,
            showErrorDialog: true,
            successLogMessage: string.Empty);

        if (saved && _currentSessionDocument is not null) {
            LoadRecentSessions(_currentSessionDocument.SessionId);
        }

        return saved;
    }

    private bool ConfirmSessionDeletion() {
        EventHandler<ConfirmationRequest>? handler = ConfirmationRequested;
        if (handler is null) {
            RaiseError("The confirmation dialog is unavailable. The session was left unchanged.");
            AppendLog("Session deletion canceled: confirmation dialog unavailable.");
            return false;
        }

        var request = new ConfirmationRequest(
            title: "Delete this Session?",
            message: "This will permanently remove the selected session and its stored files.",
            confirmButtonText: "Yes",
            cancelButtonText: "No");

        try {
            if (SynchronizationContext.Current == _uiContext) {
                handler(this, request);
            }
            else {
                _uiContext.Send(_ => handler(this, request), null);
            }
        }
        catch (Exception ex) {
            RaiseError($"Unable to confirm session deletion: {ex.Message}");
            AppendLog($"Session deletion canceled: confirmation failed: {ex.Message}");
            return false;
        }

        return request.IsConfirmed;
    }

    private bool HasExistingTranscriptContent() {
        if (FinalizedTranscriptLines.Count > 0) {
            return true;
        }

        if (_currentSessionDocument is null) {
            return !string.IsNullOrWhiteSpace(FinalizedText);
        }

        return !string.IsNullOrWhiteSpace(_currentSessionDocument.Transcript.FinalText)
            || _currentSessionDocument.Transcript.Lines.Count > 0;
    }

    private bool ConfirmTranscriptReplacement(string operationName, string canceledStatusMessage) {
        EventHandler<ConfirmationRequest>? handler = ConfirmationRequested;
        if (handler is null) {
            RaiseError("The confirmation dialog is unavailable. The existing transcript was left unchanged.");
            AppendLog($"{operationName} canceled: transcript replacement confirmation is unavailable.");
            return false;
        }

        var request = new ConfirmationRequest(
            title: "Replace current transcript?",
            message: "This session already has transcript content. Proceeding will remove the current transcript and start a new transcription for this audio file.",
            confirmButtonText: "Proceed",
            cancelButtonText: "Cancel");

        try {
            if (SynchronizationContext.Current == _uiContext) {
                handler(this, request);
            }
            else {
                _uiContext.Send(_ => handler(this, request), null);
            }
        }
        catch (Exception ex) {
            RaiseError($"Unable to confirm transcript replacement: {ex.Message}");
            AppendLog($"{operationName} canceled: transcript replacement confirmation failed: {ex.Message}");
            return false;
        }

        if (request.IsConfirmed) {
            AppendLog($"Transcript replacement confirmed by user for {operationName.ToLowerInvariant()}.");
            return true;
        }

        StatusMessage = canceledStatusMessage;
        AppendLog($"{operationName} canceled: existing transcript was preserved.");
        return false;
    }

    private void ResetCurrentSessionTranscriptState() {
        if (_currentSessionDocument is null) {
            return;
        }

        _currentSessionDocument.Transcript.FinalText = string.Empty;
        _currentSessionDocument.Transcript.ModelId = string.Empty;
        _currentSessionDocument.Transcript.LastTranscribedUtc = null;
        _currentSessionDocument.Transcript.Lines.Clear();
        _currentSessionDocument.Editing.SelectedRowIndex = null;
    }

    private void ClearCurrentSessionAfterDeletion() {
        _suppressSessionAutosave = true;

        try {
            UnsubscribeFromFinalizedLineChanges();
            FinalizedTranscriptLines.Clear();
            FinalizedText = string.Empty;
        }
        finally {
            _suppressSessionAutosave = false;
        }

        _currentSessionDocument = null;
        CurrentSessionDisplayName = "No session loaded.";
        CurrentSessionAudioIssue = string.Empty;
        IsCurrentSessionAudioMissing = false;
        LoadedAudioFilePath = string.Empty;
        IsAudioPlaying = false;
        ResetAudioTimeline();
        NotifyPropertyChanged(nameof(HasCurrentSession));
        NotifyPropertyChanged(nameof(LoadedAudioFileName));
        RefreshCommandStates();
    }

    private void QueueSessionAutosaveSave() {
        TranscriptSessionDocument? snapshot = CreateSessionSaveSnapshot(updateTranscriptionMetadata: false);
        if (snapshot is null) {
            return;
        }

        _ = SaveSessionSnapshotAsync(
            snapshot,
            showErrorDialog: false,
            successLogMessage: string.Empty);
    }

    private bool TrySaveCurrentSession(
        bool updateTranscriptionMetadata,
        bool showErrorDialog,
        string successLogMessage) {
        TranscriptSessionDocument? snapshot = CreateSessionSaveSnapshot(updateTranscriptionMetadata);
        if (snapshot is null) {
            return true;
        }

        try {
            SaveSessionSnapshot(snapshot);

            if (!string.IsNullOrWhiteSpace(successLogMessage)) {
                AppendLog(successLogMessage);
            }

            return true;
        }
        catch (Exception ex) {
            AppendLog($"Session save failed: {ex.Message}");
            StatusMessage = "Unable to save the current session.";

            if (showErrorDialog) {
                RaiseError($"Unable to save the current session: {ex.Message}");
            }

            return false;
        }
    }

    private TranscriptSessionDocument? CreateSessionSaveSnapshot(bool updateTranscriptionMetadata) {
        if (_currentSessionDocument is null) {
            return null;
        }

        string displayName = string.IsNullOrWhiteSpace(_currentSessionDocument.DisplayName)
            ? Path.GetFileNameWithoutExtension(_currentSessionDocument.Audio.OriginalFileName)
            : _currentSessionDocument.DisplayName;
        DateTimeOffset updatedUtc = DateTimeOffset.UtcNow;
        DateTimeOffset? lastTranscribedUtc = updateTranscriptionMetadata
            ? updatedUtc
            : _currentSessionDocument.Transcript.LastTranscribedUtc;
        double? durationSeconds = _currentSessionDocument.Audio.DurationSeconds;

        if (_audioPlaybackService.Duration > TimeSpan.Zero) {
            durationSeconds = _audioPlaybackService.Duration.TotalSeconds;
        }

        List<TranscriptSessionLineDocument> lines = FinalizedTranscriptLines
            .Select(line => new TranscriptSessionLineDocument {
                Text = line.Text,
                StartSeconds = line.StartOffset?.TotalSeconds,
                EndSeconds = line.EndOffset?.TotalSeconds,
                IsTimestampEstimated = line.IsTimestampEstimated,
            })
            .ToList();

        _currentSessionDocument.DisplayName = displayName;
        _currentSessionDocument.UpdatedUtc = updatedUtc;
        _currentSessionDocument.Audio.DurationSeconds = durationSeconds;
        _currentSessionDocument.Transcript.FinalText = BuildPlainTranscriptText();
        _currentSessionDocument.Transcript.ModelId = SelectedEngine?.Id ?? _currentSessionDocument.Transcript.ModelId;
        _currentSessionDocument.Transcript.LastTranscribedUtc = lastTranscribedUtc;
        _currentSessionDocument.Transcript.Lines = lines
            .Select(line => new TranscriptSessionLineDocument {
                Text = line.Text,
                StartSeconds = line.StartSeconds,
                EndSeconds = line.EndSeconds,
                IsTimestampEstimated = line.IsTimestampEstimated,
            })
            .ToList();

        return new TranscriptSessionDocument {
            SchemaVersion = _currentSessionDocument.SchemaVersion,
            SessionId = _currentSessionDocument.SessionId,
            DisplayName = _currentSessionDocument.DisplayName,
            CreatedUtc = _currentSessionDocument.CreatedUtc,
            UpdatedUtc = _currentSessionDocument.UpdatedUtc,
            Audio = new TranscriptSessionAudioDocument {
                StoredRelativePath = _currentSessionDocument.Audio.StoredRelativePath,
                OriginalFileName = _currentSessionDocument.Audio.OriginalFileName,
                FileSizeBytes = _currentSessionDocument.Audio.FileSizeBytes,
                DurationSeconds = _currentSessionDocument.Audio.DurationSeconds,
                Sha256 = _currentSessionDocument.Audio.Sha256,
            },
            Transcript = new TranscriptSessionTranscriptDocument {
                FinalText = _currentSessionDocument.Transcript.FinalText,
                ModelId = _currentSessionDocument.Transcript.ModelId,
                LastTranscribedUtc = _currentSessionDocument.Transcript.LastTranscribedUtc,
                Lines = lines,
            },
            Editing = new TranscriptSessionEditingDocument {
                SelectedRowIndex = _currentSessionDocument.Editing.SelectedRowIndex,
            },
        };
    }

    private void SaveSessionSnapshot(TranscriptSessionDocument snapshot) {
        _sessionSaveSemaphore.Wait();
        try {
            _sessionStore.Save(snapshot);
        }
        finally {
            _sessionSaveSemaphore.Release();
        }
    }

    private async Task SaveSessionSnapshotAsync(
        TranscriptSessionDocument snapshot,
        bool showErrorDialog,
        string successLogMessage) {
        try {
            await _sessionSaveSemaphore.WaitAsync().ConfigureAwait(false);
            try {
                _sessionStore.Save(snapshot);
            }
            finally {
                _sessionSaveSemaphore.Release();
            }

            if (!string.IsNullOrWhiteSpace(successLogMessage)) {
                AppendLog(successLogMessage);
            }
        }
        catch (Exception ex) {
            AppendLog($"Session save failed: {ex.Message}");
            _uiContext.Post(_ => StatusMessage = "Unable to save the current session.", null);

            if (showErrorDialog) {
                RaiseError($"Unable to save the current session: {ex.Message}");
            }
        }
    }

    private string BuildPlainTranscriptText() {
        return string.Join(
            Environment.NewLine,
            FinalizedTranscriptLines
                .Select(line => line.Text?.Trim() ?? string.Empty)
                .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private bool TryResolveLoadedAudioDuration(out TimeSpan duration) {
        duration = _audioPlaybackService.Duration;
        if (duration > TimeSpan.Zero) {
            return true;
        }

        if (_currentSessionDocument?.Audio.DurationSeconds is double sessionDurationSeconds && sessionDurationSeconds > 0) {
            duration = TimeSpan.FromSeconds(sessionDurationSeconds);
            return true;
        }

        duration = TimeSpan.Zero;
        return false;
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

    private void RaiseToast(string title, string message, ToastNotificationType type = ToastNotificationType.Info) {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(message)) {
            return;
        }

        _uiContext.Post(_ => ToastRequested?.Invoke(this, new ToastNotification(title, message, type)), null);
    }

    public void LogHandledException(string source, Exception ex) {
        if (ex is null) {
            return;
        }

        string prefix = string.IsNullOrWhiteSpace(source)
            ? "Handled error"
            : $"Handled error in {source}";
        AppendLog($"{prefix}: {ex.GetType().Name}: {ex.Message}");
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
