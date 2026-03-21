using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using VoxTranscriber.Abstractions;
using VoxTranscriber.Audio;
using VoxTranscriber.Services;

namespace VoxTranscriber.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable {
    private static readonly TimeSpan PlaceholderSegmentDuration = TimeSpan.FromSeconds(10);
    private const string AudioFileDialogFilter = "Audio Files|*.wav;*.mp3;*.flac;*.aac;*.m4a;*.ogg;*.wma;*.mp4|All Files|*.*";
    private static readonly HashSet<string> SupportedAudioFileExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".wav",
        ".mp3",
        ".flac",
        ".aac",
        ".m4a",
        ".ogg",
        ".wma",
        ".mp4",
    };

    private readonly IAudioPlaybackService _audioPlaybackService;
    private readonly OpenAiTranscriptionOptions _openAiOptions;
    private readonly OpenAiSettingsStore _openAiSettingsStore;
    private readonly OpenAiApiKeyValidationService _openAiApiKeyValidationService;
    private readonly ChunkedSpeakerDiarizationService _speakerDiarizationService;
    private readonly ProcessLogService _processLogService;
    private readonly TranscriptSessionStore _sessionStore;
    private readonly AppPreferencesStore _appPreferencesStore;
    private readonly SynchronizationContext _uiContext;
    private readonly DispatcherTimer _audioTimelineTimer;
    private readonly DispatcherTimer _sessionAutosaveTimer;
    private readonly SemaphoreSlim _sessionSaveSemaphore = new(1, 1);

    private readonly EngineOptionViewModel _manualEngine;
    private readonly EngineOptionViewModel _autoTranscribeEngine;
    private readonly TranscriptModeOptionViewModel _segmentTranscriptMode;
    private readonly TranscriptModeOptionViewModel _speakerDiarizationMode;
    private readonly ApplicationUpdateService? _applicationUpdateService;
    private EngineOptionViewModel? _selectedEngine;
    private TranscriptModeOptionViewModel? _selectedTranscriptMode;
    private TranscriptSessionSummary? _selectedRecentSession;
    private TranscriptSessionDocument? _currentSessionDocument;
    private string _currentSessionDisplayName = "No session loaded.";
    private string _currentSessionAudioIssue = string.Empty;
    private string _finalizedText = string.Empty;
    private string _statusMessage = "Ready.";
    private string _openAiApiKey;
    private bool _isBusy;
    private bool _isCurrentSessionAudioMissing;
    private string _loadedAudioFilePath = string.Empty;
    private bool _isAudioPlaying;
    private bool _isPlaybackMuted;
    private double _audioSeekMaximumSeconds;
    private double _audioSeekPositionSeconds;
    private string _audioElapsedText = "00:00";
    private string _audioRemainingText = "-00:00";
    private bool _copyFinalizedWithTimeline;
    private bool _autoTranscribeWithAi;
    private bool _isUpdatingSeekFromPlayback;
    private bool _suppressSessionAutosave;
    private string _applicationVersionStatusText = string.Empty;
    private int _selectedTranscriptViewIndex;

    public MainViewModel(
        IEnumerable<TranscriptionModelOption> models,
        IAudioPlaybackService audioPlaybackService,
        OpenAiTranscriptionOptions openAiOptions,
        OpenAiSettingsStore openAiSettingsStore,
        OpenAiApiKeyValidationService openAiApiKeyValidationService,
        ChunkedSpeakerDiarizationService speakerDiarizationService,
        ProcessLogService processLogService,
        TranscriptSessionStore sessionStore,
        AppPreferencesStore appPreferencesStore,
        AppPreferencesSnapshot appPreferencesSnapshot,
        ApplicationUpdateService? applicationUpdateService = null) {
        _audioPlaybackService = audioPlaybackService;
        _openAiOptions = openAiOptions;
        _openAiSettingsStore = openAiSettingsStore;
        _openAiApiKeyValidationService = openAiApiKeyValidationService;
        _speakerDiarizationService = speakerDiarizationService;
        _processLogService = processLogService;
        _sessionStore = sessionStore;
        _appPreferencesStore = appPreferencesStore;
        _applicationUpdateService = applicationUpdateService;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();

        _openAiApiKey = _openAiOptions.ApiKey;
        _copyFinalizedWithTimeline = appPreferencesSnapshot.CopyFinalizedWithTimeline;
        _autoTranscribeWithAi = appPreferencesSnapshot.AutoTranscribeWithAi;

        Engines = new ObservableCollection<EngineOptionViewModel>(
            models.Select(model => new EngineOptionViewModel(model)));
        _autoTranscribeEngine = ResolveEngine(
            Engines,
            OpenAiTranscriptionModelCatalog.Gpt4oTranscribe,
            "Online: OpenAI gpt-4o-transcribe");
        _manualEngine = ResolveEngine(
            Engines,
            OpenAiTranscriptionModelCatalog.ManualTranscription,
            "No AI assist: Manual transcription");
        _segmentTranscriptMode = new TranscriptModeOptionViewModel(
            TranscriptGenerationMode.Segments,
            "Segments (10s)",
            "10s chunks for manual or AI text.",
            OnTranscriptModeOptionSelected);
        _speakerDiarizationMode = new TranscriptModeOptionViewModel(
            TranscriptGenerationMode.SpeakerDiarization,
            "Speaker diarization",
            "Label speakers automatically.",
            OnTranscriptModeOptionSelected);
        TranscriptModes = new ObservableCollection<TranscriptModeOptionViewModel> {
            _segmentTranscriptMode,
            _speakerDiarizationMode,
        };
        ProcessLogs = new ObservableCollection<ProcessLogEntryViewModel>();
        FinalizedTranscriptLines = new ObservableCollection<FinalizedTranscriptLineViewModel>();
        SpeakerTranscriptLines = new ObservableCollection<FinalizedTranscriptLineViewModel>();
        RecentSessions = new ObservableCollection<TranscriptSessionSummary>();
        ProcessLogs.CollectionChanged += OnProcessLogsCollectionChanged;
        FinalizedTranscriptLines.CollectionChanged += OnFinalizedTranscriptLinesCollectionChanged;
        SpeakerTranscriptLines.CollectionChanged += OnSpeakerTranscriptLinesCollectionChanged;

        ClearCommand = new AsyncRelayCommand(ClearAsync, CanClear);
        OpenAudioFileCommand = new AsyncRelayCommand(OpenAudioFileAsync, CanOpenAudioFile);
        OpenSelectedSessionCommand = new AsyncRelayCommand(OpenSelectedSessionAsync, CanOpenSelectedSession);
        DeleteSelectedSessionCommand = new AsyncRelayCommand(DeleteSelectedSessionAsync, CanDeleteSelectedSession);
        PlayAudioCommand = new AsyncRelayCommand(PlayAudioAsync, CanPlayAudio);
        PauseAudioCommand = new AsyncRelayCommand(PauseAudioAsync, CanPauseAudio);

        _processLogService.LogEmitted += OnProcessLogEmitted;
        _audioPlaybackService.PlaybackStateChanged += OnAudioPlaybackStateChanged;
        _isAudioPlaying = _audioPlaybackService.IsPlaying;
        _isPlaybackMuted = _audioPlaybackService.IsMuted;

        _audioTimelineTimer = new DispatcherTimer {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _audioTimelineTimer.Tick += OnAudioTimelineTick;
        _audioTimelineTimer.Start();

        _sessionAutosaveTimer = new DispatcherTimer {
            Interval = TimeSpan.FromMilliseconds(800),
        };
        _sessionAutosaveTimer.Tick += OnSessionAutosaveTimerTick;

        SelectedEngine = _autoTranscribeWithAi ? _autoTranscribeEngine : _manualEngine;
        SelectedTranscriptMode = _segmentTranscriptMode;
        SelectedTranscriptViewIndex = 0;
        _applicationVersionStatusText = _applicationUpdateService?.FooterStatusText
            ?? BuildInstalledVersionStatus();
        if (_applicationUpdateService is not null) {
            _applicationUpdateService.StatusChanged += OnApplicationUpdateStatusChanged;
        }

        AppendLogCore("Application initialized.");
        AppendLogCore($"Loaded {Engines.Count} transcription mode option(s).");

        if (!string.IsNullOrWhiteSpace(_openAiApiKey)) {
            AppendLogCore($"OpenAI API key loaded ({MaskApiKey(_openAiApiKey)}).");
        }
        else {
            AppendLogCore("OpenAI API key is not configured.");
        }

        AppendLogCore("Auto Transcribe with AI uses the fixed OpenAI gpt-4o-transcribe engine.");
        AppendLogCore($"Auto Transcribe with AI: {(_autoTranscribeWithAi ? "ON" : "OFF")}.");
        AppendLogCore($"Startup mode: {SelectedEngine?.DisplayName ?? "Unavailable"}.");
        AppendLogCore($"Transcript mode: {SelectedTranscriptMode?.DisplayName ?? "Unavailable"}.");

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
    public ObservableCollection<TranscriptModeOptionViewModel> TranscriptModes { get; }
    public ObservableCollection<ProcessLogEntryViewModel> ProcessLogs { get; }
    public ObservableCollection<FinalizedTranscriptLineViewModel> FinalizedTranscriptLines { get; }
    public ObservableCollection<FinalizedTranscriptLineViewModel> SpeakerTranscriptLines { get; }

    public IEnumerable<FinalizedTranscriptLineViewModel> CurrentTranscriptLines =>
        IsSpeakerTranscriptViewSelected
            ? SpeakerTranscriptLines
            : FinalizedTranscriptLines;
    public ObservableCollection<TranscriptSessionSummary> RecentSessions { get; }

    public AsyncRelayCommand ClearCommand { get; }
    public AsyncRelayCommand OpenAudioFileCommand { get; }
    public AsyncRelayCommand OpenSelectedSessionCommand { get; }
    public AsyncRelayCommand DeleteSelectedSessionCommand { get; }
    public AsyncRelayCommand PlayAudioCommand { get; }
    public AsyncRelayCommand PauseAudioCommand { get; }

    public EngineOptionViewModel? SelectedEngine {
        get => _selectedEngine;
        set {
            if (!SetProperty(ref _selectedEngine, value)) {
                return;
            }

            NotifyPropertyChanged(nameof(IsOpenAiEngineSelected));
            NotifyPropertyChanged(nameof(IsManualTranscriptionSelected));
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
            NotifyPropertyChanged(nameof(HasPendingSessionSelection));
            NotifyPropertyChanged(nameof(ShouldShowTranscriptChooseFileAction));
            NotifyPropertyChanged(nameof(TranscriptEmptyStateTitle));
            NotifyPropertyChanged(nameof(TranscriptEmptyStateMessage));
        }
    }

    public bool HasRecentSessions => RecentSessions.Count > 0;

    public bool HasProcessLogs => ProcessLogs.Count > 0;

    public string CurrentSessionDisplayName {
        get => _currentSessionDisplayName;
        private set {
            if (!SetProperty(ref _currentSessionDisplayName, value)) {
                return;
            }

            NotifyPropertyChanged(nameof(TranscriptPaneSubtitle));
        }
    }

    public string CurrentSessionAudioIssue {
        get => _currentSessionAudioIssue;
        private set {
            if (!SetProperty(ref _currentSessionAudioIssue, value)) {
                return;
            }

            NotifyPropertyChanged(nameof(HasCurrentSessionAudioIssue));
        }
    }

    public bool HasCurrentSession => _currentSessionDocument is not null;

    public bool HasPendingSessionSelection =>
        SelectedRecentSession is not null && !HasCurrentSession;

    public bool ShouldShowTranscriptChooseFileAction =>
        !HasCurrentSession && !HasPendingSessionSelection;

    public bool HasCurrentSessionAudioIssue => !string.IsNullOrWhiteSpace(CurrentSessionAudioIssue);

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

    public bool IsOpenAiEngineSelected =>
        OpenAiTranscriptionModelCatalog.UsesAiAssist(SelectedEngine?.Id ?? string.Empty);

    public bool IsManualTranscriptionSelected =>
        OpenAiTranscriptionModelCatalog.IsManualOnly(SelectedEngine?.Id ?? string.Empty);

    public bool IsEngineSelectionEnabled =>
        !IsBusy;

    public bool IsTranscriptModeSelectionEnabled =>
        !IsBusy;

    public bool IsOpenAiSettingsEnabled =>
        !IsBusy;

    public bool IsSegmentTranscriptionEnabled =>
        SelectedEngine is not null
        && IsAudioFileLoaded
        && !IsBusy;

    public bool IsTranscriptGenerationEnabled =>
        SelectedTranscriptMode is not null
        && IsAudioFileLoaded
        && !IsBusy;

    public TranscriptModeOptionViewModel? SelectedTranscriptMode {
        get => _selectedTranscriptMode;
        set {
            if (!SetProperty(ref _selectedTranscriptMode, value)) {
                return;
            }

            SelectedTranscriptViewIndex =
                value?.Mode == TranscriptGenerationMode.SpeakerDiarization ? 1 : 0;
            NotifyPropertyChanged(nameof(IsSegmentModeSelected));
            NotifyPropertyChanged(nameof(IsSpeakerDiarizationModeSelected));
            NotifyPropertyChanged(nameof(CurrentTranscriptLines));
            NotifyPropertyChanged(nameof(IsAutoTranscribeSettingVisible));
            NotifyPropertyChanged(nameof(IsSpeakerDiarizationNoticeVisible));
            NotifyPropertyChanged(nameof(IsSegmentTranscriptViewSelected));
            NotifyPropertyChanged(nameof(IsSpeakerTranscriptViewSelected));
            NotifyPropertyChanged(nameof(IsCopyWithTimelineOptionVisible));
            NotifyPropertyChanged(nameof(IsSpeakerCopyFormatVisible));
            NotifyCurrentTranscriptStateChanged();
            NotifyAiAssistStateChanged();
            NotifyInteractionAvailabilityChanged();
            ScheduleSessionAutosave();

            foreach (TranscriptModeOptionViewModel mode in TranscriptModes) {
                mode.IsSelected = ReferenceEquals(mode, value);
            }

            if (value is null) {
                AppendLog("Transcript mode cleared.");
            }
            else {
                AppendLog($"Transcript mode selected: {value.DisplayName}.");
            }
        }
    }

    public bool IsSegmentModeSelected =>
        SelectedTranscriptMode?.Mode == TranscriptGenerationMode.Segments;

    public bool IsSpeakerDiarizationModeSelected =>
        SelectedTranscriptMode?.Mode == TranscriptGenerationMode.SpeakerDiarization;

    public bool IsAutoTranscribeSettingVisible =>
        IsSegmentModeSelected;

    public bool IsSpeakerDiarizationNoticeVisible =>
        IsSpeakerDiarizationModeSelected;

    public int SelectedTranscriptViewIndex {
        get => _selectedTranscriptViewIndex;
        set {
            int normalized = value <= 0 ? 0 : 1;
            if (!SetProperty(ref _selectedTranscriptViewIndex, normalized)) {
                return;
            }

            ScheduleSessionAutosave();
        }
    }

    public bool IsSegmentTranscriptViewSelected =>
        IsSegmentModeSelected;

    public bool IsSpeakerTranscriptViewSelected =>
        IsSpeakerDiarizationModeSelected;

    public bool IsCopyWithTimelineOptionVisible =>
        IsSegmentTranscriptViewSelected;

    public bool IsSpeakerCopyFormatVisible =>
        IsSpeakerTranscriptViewSelected;

    public bool HasCurrentTranscriptLines =>
        CurrentTranscriptLines.Any();

    public bool IsTranscriptEmptyStateVisible =>
        !HasCurrentTranscriptLines;

    public bool CanCopyTranscript =>
        HasCurrentTranscriptLines && !IsBusy;

    public static bool IsSupportedAudioFilePath(string? filePath) {
        if (string.IsNullOrWhiteSpace(filePath)) {
            return false;
        }

        string extension = Path.GetExtension(filePath);
        return !string.IsNullOrWhiteSpace(extension)
            && SupportedAudioFileExtensions.Contains(extension);
    }

    public string GenerateTranscriptButtonText {
        get {
            if (IsSegmentModeSelected && !AutoTranscribeWithAi) {
                return HasFinalizedTranscriptLines
                    ? "Refresh Timeline"
                    : "Create Timeline";
            }

            return HasCurrentTranscriptLines
                ? "Regenerate"
                : "Generate";
        }
    }

    public string TranscriptPaneSubtitle {
        get {
            if (IsAudioFileLoaded) {
                return LoadedAudioFileName;
            }

            if (HasCurrentSession) {
                return CurrentSessionDisplayName;
            }

            return string.Empty;
        }
    }

    public string TranscriptEmptyStateTitle {
        get {
            if (HasPendingSessionSelection) {
                return "Session selected";
            }

            if (HasCurrentSession) {
                return "No transcript lines";
            }

            if (!IsAudioFileLoaded) {
                return "No transcript";
            }

            if (IsSegmentModeSelected && !AutoTranscribeWithAi) {
                return "Timeline ready";
            }

            return "Ready";
        }
    }

    public string TranscriptEmptyStateMessage {
        get {
            if (HasPendingSessionSelection) {
                return "A recent session is selected. Click Open in Sessions to load it.";
            }

            if (HasCurrentSession) {
                if (IsSegmentModeSelected && !AutoTranscribeWithAi) {
                    return "No timeline rows yet. Create timeline to start editing this session.";
                }

                return "This session has no transcript lines yet. Choose a mode, then generate.";
            }

            if (!IsAudioFileLoaded) {
                return "Drop audio here, choose a file, or open a session.";
            }

            if (IsSegmentModeSelected && !AutoTranscribeWithAi) {
                return "Create the timeline, then fill rows manually or turn on AI.";
            }

            if (IsSpeakerDiarizationModeSelected && string.IsNullOrWhiteSpace(OpenAiApiKey)) {
                return "Add an API key, then run diarization.";
            }

            return "Choose a mode, then generate the transcript.";
        }
    }

    public bool HasFinalizedTranscriptLines =>
        FinalizedTranscriptLines.Count > 0;

    public bool HasSpeakerTranscriptLines =>
        SpeakerTranscriptLines.Count > 0;

    public string AutoTranscribeAssistStatusText {
        get {
            if (AutoTranscribeWithAi) {
                return string.IsNullOrWhiteSpace(OpenAiApiKey)
                    ? "On, but an API key is still required."
                    : "On and ready to fill segment text.";
            }

            return string.IsNullOrWhiteSpace(OpenAiApiKey)
                ? "Off. Add an API key to enable it."
                : "Off. Turn it on to fill segment text.";
        }
    }

    public bool AutoTranscribeWithAi {
        get => _autoTranscribeWithAi;
        set {
            if (!SetProperty(ref _autoTranscribeWithAi, value)) {
                return;
            }

            SelectedEngine = value ? _autoTranscribeEngine : _manualEngine;
            SaveAppPreferences();
            NotifyAiAssistStateChanged();
            NotifyCurrentTranscriptStateChanged();
            AppendLog($"Auto Transcribe with AI: {(value ? "ON" : "OFF")}.");
        }
    }

    public bool CopyFinalizedWithTimeline {
        get => _copyFinalizedWithTimeline;
        set {
            if (!SetProperty(ref _copyFinalizedWithTimeline, value)) {
                return;
            }

            SaveAppPreferences();
            AppendLog($"Copy finalized transcript with timeline: {(value ? "ON" : "OFF")}.");
        }
    }

    public string ApplicationVersionStatusText {
        get => _applicationVersionStatusText;
        private set => SetProperty(ref _applicationVersionStatusText, value);
    }

    public string LoadedAudioFilePath {
        get => _loadedAudioFilePath;
        private set {
            if (!SetProperty(ref _loadedAudioFilePath, value)) {
                return;
            }

            NotifyPropertyChanged(nameof(LoadedAudioFileName));
            NotifyPropertyChanged(nameof(IsAudioFileLoaded));
            NotifyPropertyChanged(nameof(TranscriptPaneSubtitle));
            NotifyCurrentTranscriptStateChanged();
            NotifyInteractionAvailabilityChanged();
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

            return "No audio selected.";
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

    public bool IsPlaybackMuted {
        get => _isPlaybackMuted;
        set {
            if (!SetProperty(ref _isPlaybackMuted, value)) {
                return;
            }

            _audioPlaybackService.IsMuted = value;
            AppendLog($"Playback mute: {(value ? "ON" : "OFF")}.");
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
            NotifyPropertyChanged(nameof(CanCopyTranscript));
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
            NotifyAiAssistStateChanged();
            NotifyCurrentTranscriptStateChanged();
            AppendLog($"OpenAI API key updated ({MaskApiKey(_openAiOptions.ApiKey)}).");
            RefreshCommandStates();
        }
    }

    public ValueTask DisposeAsync() {
        AppendLog("Disposing transcription resources...");

        _sessionAutosaveTimer.Stop();
        _sessionAutosaveTimer.Tick -= OnSessionAutosaveTimerTick;
        TrySaveCurrentSession(
            updatedTranscriptMode: null,
            showErrorDialog: false,
            successLogMessage: string.Empty);

        if (_applicationUpdateService is not null) {
            _applicationUpdateService.StatusChanged -= OnApplicationUpdateStatusChanged;
        }

        _processLogService.LogEmitted -= OnProcessLogEmitted;
        _audioPlaybackService.PlaybackStateChanged -= OnAudioPlaybackStateChanged;
        ProcessLogs.CollectionChanged -= OnProcessLogsCollectionChanged;
        FinalizedTranscriptLines.CollectionChanged -= OnFinalizedTranscriptLinesCollectionChanged;
        SpeakerTranscriptLines.CollectionChanged -= OnSpeakerTranscriptLinesCollectionChanged;
        UnsubscribeFromFinalizedLineChanges();
        UnsubscribeFromSpeakerLineChanges();
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

    public void PrepareForRequiredUpdateShutdown() {
        AppendLog("An application update is required. Canceling active work.");

        try {
            if (IsAudioFileLoaded) {
                _audioPlaybackService.Stop();
            }
        }
        catch (Exception ex) {
            AppendLog($"Audio stop during update shutdown failed: {ex.Message}");
        }

        IsAudioPlaying = _audioPlaybackService.IsPlaying;

        if (IsAudioFileLoaded) {
            UpdateAudioTimelineFromPlayback();
        }
        else {
            ResetAudioTimeline();
        }

        StatusMessage = "A newer version of the application is required.";
    }

    public Task<bool> CreatePlaceholdersForSegmentTranscriptionAsync() {
        return Task.FromResult(CreatePlaceholdersCore());
    }

    public async Task<bool> GenerateSpeakerDiarizationTranscriptAsync(CancellationToken cancellationToken) {
        if (!IsAudioFileLoaded) {
            AppendLog("Speaker diarization aborted: no audio file is loaded in preview.");
            return false;
        }

        if (!EnsureCurrentSessionForLoadedAudio()) {
            AppendLog("Speaker diarization aborted: current audio is not associated with a session.");
            return false;
        }

        bool hasExistingTranscript = HasExistingTranscriptContent(TranscriptGenerationMode.SpeakerDiarization);
        if (hasExistingTranscript && !ConfirmTranscriptReplacement(
                operationName: "Speaker diarization",
                transcriptMode: TranscriptGenerationMode.SpeakerDiarization,
                canceledStatusMessage: "Speaker diarization canceled. Existing speaker transcript was kept.")) {
            return false;
        }

        if (hasExistingTranscript) {
            ResetCurrentSessionTranscriptState(TranscriptGenerationMode.SpeakerDiarization);
        }

        ClearTranscriptAndLogs(unloadAudioPreview: false, transcriptMode: TranscriptGenerationMode.SpeakerDiarization);

        if (hasExistingTranscript
            && !TrySaveCurrentSession(
                updatedTranscriptMode: null,
                showErrorDialog: true,
                successLogMessage: "Existing speaker transcript cleared before speaker diarization.")) {
            AppendLog("Speaker diarization aborted: existing speaker transcript could not be cleared safely.");
            return false;
        }

        IsBusy = true;
        StatusMessage = "Transcribing with speaker diarization...";

        try {
            SpeakerDiarizationResult result = await _speakerDiarizationService.DiarizeAudioFileAsync(
                LoadedAudioFilePath,
                cancellationToken);

            ApplySpeakerDiarizationResult(result);
            SelectedTranscriptViewIndex = 1;

            if (!TrySaveCurrentSession(
                    updatedTranscriptMode: TranscriptGenerationMode.SpeakerDiarization,
                    showErrorDialog: true,
                    successLogMessage: "Session saved after speaker diarization.")) {
                AppendLog("Speaker diarization aborted: diarized transcript could not be saved.");
                return false;
            }

            if (_currentSessionDocument is not null) {
                LoadRecentSessions(_currentSessionDocument.SessionId);
            }

            StatusMessage = $"Speaker diarization completed with {SpeakerTranscriptLines.Count:N0} line(s).";
            AppendLog($"Speaker diarization completed with {SpeakerTranscriptLines.Count:N0} line(s).");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            StatusMessage = "Speaker diarization canceled.";
            AppendLog("Speaker diarization canceled.");
            return false;
        }
        finally {
            IsBusy = false;
        }
    }

    public string BuildClipboardTranscriptText() {
        return IsSpeakerTranscriptViewSelected
            ? BuildSpeakerTranscriptText(includeTimeline: true)
            : BuildSegmentTranscriptText(includeTimeline: CopyFinalizedWithTimeline);
    }

    private bool CreatePlaceholdersCore() {
        if (!IsAudioFileLoaded) {
            AppendLog("Create placeholders aborted: no audio file is loaded in preview.");
            return false;
        }

        if (!EnsureCurrentSessionForLoadedAudio()) {
            AppendLog("Create placeholders aborted: current audio is not associated with a session.");
            return false;
        }

        if (!TryResolveLoadedAudioDuration(out TimeSpan duration) || duration <= TimeSpan.Zero) {
            RaiseError("Unable to determine the loaded audio duration for placeholder creation.");
            AppendLog("Create placeholders aborted: audio duration is unavailable.");
            return false;
        }

        bool hasExistingTranscript = HasExistingTranscriptContent(TranscriptGenerationMode.Segments);
        if (hasExistingTranscript && !ConfirmTranscriptReplacement(
                operationName: "Create placeholders",
                transcriptMode: TranscriptGenerationMode.Segments,
                canceledStatusMessage: "Placeholder creation canceled. Existing transcript was kept.")) {
            return false;
        }

        if (hasExistingTranscript) {
            ResetCurrentSessionTranscriptState(TranscriptGenerationMode.Segments);
        }

        ClearTranscriptAndLogs(unloadAudioPreview: false, transcriptMode: TranscriptGenerationMode.Segments);

        if (hasExistingTranscript
            && !TrySaveCurrentSession(
                updatedTranscriptMode: null,
                showErrorDialog: true,
                successLogMessage: "Existing transcript cleared before placeholder creation.")) {
            AppendLog("Create placeholders aborted: existing transcript could not be cleared safely.");
            return false;
        }

        IsBusy = true;
        StatusMessage = "Creating placeholder transcript...";

        try {
            CreatePlaceholderTranscript(duration);

            if (!TrySaveCurrentSession(
                    updatedTranscriptMode: null,
                    showErrorDialog: true,
                    successLogMessage: "Session saved after placeholder creation.")) {
                AppendLog("Create placeholders aborted: generated placeholders could not be saved.");
                return false;
            }

            if (_currentSessionDocument is not null) {
                LoadRecentSessions(_currentSessionDocument.SessionId);
            }

            StatusMessage = "Placeholder transcript created.";
            AppendLog($"Placeholder transcript created with {FinalizedTranscriptLines.Count:N0} segment(s).");
            return true;
        }
        finally {
            IsBusy = false;
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

        LoadSessionFromImportedAudio(selectedFilePath);
        return Task.CompletedTask;
    }

    public bool TryImportAudioFileFromPath(string filePath) {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) {
            AppendLog("Dropped file rejected: file path is missing or no longer exists.");
            return false;
        }

        if (!IsSupportedAudioFilePath(filePath)) {
            string extension = Path.GetExtension(filePath);
            AppendLog($"Dropped file rejected: unsupported audio type '{extension}'.");
            RaiseError("Unsupported audio file. Use WAV, MP3, FLAC, AAC, M4A, OGG, WMA, or MP4.");
            return false;
        }

        AppendLog($"Audio file dropped: {Path.GetFileName(filePath)}");
        LoadSessionFromImportedAudio(filePath);
        return true;
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
            updatedTranscriptMode: null,
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
            UnsubscribeFromSpeakerLineChanges();
            FinalizedTranscriptLines.Clear();
            SpeakerTranscriptLines.Clear();
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
            NotifyPropertyChanged(nameof(HasPendingSessionSelection));
            NotifyPropertyChanged(nameof(ShouldShowTranscriptChooseFileAction));
            NotifyPropertyChanged(nameof(IsTranscriptEmptyStateVisible));
            NotifyPropertyChanged(nameof(LoadedAudioFileName));
        }

        _statusMessage = unloadAudioPreview
            ? "Transcript, logs, and audio preview cleared."
            : "Transcript and logs cleared.";
        NotifyPropertyChanged(nameof(StatusMessage));
        RefreshCommandStates();
    }

    private void ClearTranscriptAndLogs(bool unloadAudioPreview, TranscriptGenerationMode transcriptMode) {
        _sessionAutosaveTimer.Stop();

        if (unloadAudioPreview) {
            _audioPlaybackService.UnloadFile();
        }

        _suppressSessionAutosave = true;
        try {
            switch (transcriptMode) {
                case TranscriptGenerationMode.SpeakerDiarization:
                    UnsubscribeFromSpeakerLineChanges();
                    SpeakerTranscriptLines.Clear();
                    break;
                default:
                    UnsubscribeFromFinalizedLineChanges();
                    FinalizedTranscriptLines.Clear();
                    FinalizedText = string.Empty;
                    break;
            }

            ProcessLogs.Clear();
        }
        finally {
            _suppressSessionAutosave = false;
        }

        _statusMessage = unloadAudioPreview
            ? "Transcript, logs, and audio preview cleared."
            : "Transcript and logs cleared.";
        NotifyPropertyChanged(nameof(StatusMessage));
        RefreshCommandStates();
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

    private bool CanClear() {
        return !IsBusy;
    }

    private bool CanOpenAudioFile() {
        return !IsBusy;
    }

    private bool CanOpenSelectedSession() {
        return SelectedRecentSession is not null && !IsBusy;
    }

    private bool CanDeleteSelectedSession() {
        return SelectedRecentSession is not null && !IsBusy;
    }

    private bool CanPlayAudio() {
        return IsAudioFileLoaded && !IsAudioPlaying;
    }

    private bool CanPauseAudio() {
        return IsAudioFileLoaded && IsAudioPlaying;
    }

    private void RefreshCommandStates() {
        ClearCommand.RaiseCanExecuteChanged();
        OpenAudioFileCommand.RaiseCanExecuteChanged();
        OpenSelectedSessionCommand.RaiseCanExecuteChanged();
        DeleteSelectedSessionCommand.RaiseCanExecuteChanged();
        PlayAudioCommand.RaiseCanExecuteChanged();
        PauseAudioCommand.RaiseCanExecuteChanged();
    }

    private void NotifyInteractionAvailabilityChanged() {
        NotifyPropertyChanged(nameof(IsEngineSelectionEnabled));
        NotifyPropertyChanged(nameof(IsTranscriptModeSelectionEnabled));
        NotifyPropertyChanged(nameof(IsOpenAiSettingsEnabled));
        NotifyPropertyChanged(nameof(IsSegmentTranscriptionEnabled));
        NotifyPropertyChanged(nameof(IsTranscriptGenerationEnabled));
    }

    private void NotifyCurrentTranscriptStateChanged() {
        NotifyPropertyChanged(nameof(CurrentTranscriptLines));
        NotifyPropertyChanged(nameof(HasCurrentTranscriptLines));
        NotifyPropertyChanged(nameof(IsTranscriptEmptyStateVisible));
        NotifyPropertyChanged(nameof(CanCopyTranscript));
        NotifyPropertyChanged(nameof(GenerateTranscriptButtonText));
        NotifyPropertyChanged(nameof(TranscriptEmptyStateTitle));
        NotifyPropertyChanged(nameof(TranscriptEmptyStateMessage));
    }

    private void NotifyAiAssistStateChanged() {
        NotifyPropertyChanged(nameof(AutoTranscribeAssistStatusText));
    }

    private void SaveAppPreferences() {
        _appPreferencesStore.Save(new AppPreferencesSnapshot(
            CopyFinalizedWithTimeline: _copyFinalizedWithTimeline,
            AutoTranscribeWithAi: _autoTranscribeWithAi));
    }

    private bool EnsureSelectedModelConfigured() {
        if (SelectedEngine is null) {
            AppendLog("Transcription configuration check failed: no available transcription mode.");
            return false;
        }

        if (!IsOpenAiEngineSelected) {
            AppendLog("OpenAI transcription blocked: Auto Transcribe with AI is turned off.");
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
                updatedTranscriptMode: null,
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
                updatedTranscriptMode: null,
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
            NotifyPropertyChanged(nameof(HasPendingSessionSelection));
            NotifyPropertyChanged(nameof(ShouldShowTranscriptChooseFileAction));
            NotifyPropertyChanged(nameof(IsTranscriptEmptyStateVisible));
            NotifyPropertyChanged(nameof(LoadedAudioFileName));

            ApplyTranscriptDocument(loadResult.Document.Transcript);
            ApplySpeakerTranscriptDocument(loadResult.Document.SpeakerTranscript);
            ApplyEditingDocument(loadResult.Document.Editing);

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

        foreach (TranscriptSessionLineDocument line in transcript.Lines) {
            bool hasTimeline = line.StartSeconds is not null || line.EndSeconds is not null;
            if (!hasTimeline && string.IsNullOrWhiteSpace(line.Text)) {
                continue;
            }

            var item = new FinalizedTranscriptLineViewModel(
                startOffset: line.StartSeconds is null ? null : TimeSpan.FromSeconds(Math.Max(line.StartSeconds.Value, 0)),
                endOffset: line.EndSeconds is null ? null : TimeSpan.FromSeconds(Math.Max(line.EndSeconds.Value, 0)),
                isTimestampEstimated: line.IsTimestampEstimated,
                text: line.Text,
                isManuallyReviewed: line.IsManuallyReviewed);
            item.PropertyChanged += OnFinalizedLinePropertyChanged;
            FinalizedTranscriptLines.Add(item);
        }

        RebuildFinalizedTextFromLines();
    }

    private void ApplySpeakerTranscriptDocument(TranscriptSessionTranscriptDocument transcript) {
        UnsubscribeFromSpeakerLineChanges();
        SpeakerTranscriptLines.Clear();

        foreach (TranscriptSessionLineDocument line in transcript.Lines) {
            if (line.StartSeconds is null && string.IsNullOrWhiteSpace(line.Text)) {
                continue;
            }

            TimeSpan startOffset = line.StartSeconds is null
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(Math.Max(line.StartSeconds.Value, 0));
            TimeSpan? endOffset = line.EndSeconds is null
                ? null
                : TimeSpan.FromSeconds(Math.Max(line.EndSeconds.Value, 0));

            var item = new FinalizedTranscriptLineViewModel(
                startOffset: startOffset,
                endOffset: endOffset,
                isTimestampEstimated: false,
                text: line.Text,
                speakerLabel: line.SpeakerLabel);
            item.PropertyChanged += OnSpeakerTranscriptLinePropertyChanged;
            SpeakerTranscriptLines.Add(item);
        }
    }

    private void ApplyEditingDocument(TranscriptSessionEditingDocument editing) {
        SelectedTranscriptMode = ResolveTranscriptMode(editing.SelectedTranscriptMode);
        SelectedTranscriptViewIndex = editing.SelectedTranscriptViewIndex;
    }

    private TranscriptModeOptionViewModel ResolveTranscriptMode(string? value) {
        if (Enum.TryParse(value, ignoreCase: true, out TranscriptGenerationMode parsedMode)) {
            return parsedMode == TranscriptGenerationMode.SpeakerDiarization
                ? _speakerDiarizationMode
                : _segmentTranscriptMode;
        }

        return _segmentTranscriptMode;
    }

    private void OnTranscriptModeOptionSelected(TranscriptModeOptionViewModel option) {
        if (option is null || ReferenceEquals(SelectedTranscriptMode, option)) {
            return;
        }

        SelectedTranscriptMode = option;
    }

    private void ApplySpeakerDiarizationResult(SpeakerDiarizationResult result) {
        _suppressSessionAutosave = true;

        try {
            UnsubscribeFromSpeakerLineChanges();
            SpeakerTranscriptLines.Clear();

            var speakerLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (SpeakerDiarizationSegment segment in result.Segments.OrderBy(item => item.StartOffset)) {
                string speakerKey = segment.Speaker?.Trim() ?? string.Empty;
                if (!speakerLabels.TryGetValue(speakerKey, out string? displayLabel)) {
                    displayLabel = $"Speaker {speakerLabels.Count + 1}";
                    speakerLabels[speakerKey] = displayLabel;
                }

                var line = new FinalizedTranscriptLineViewModel(
                    startOffset: segment.StartOffset,
                    endOffset: segment.EndOffset,
                    isTimestampEstimated: false,
                    text: segment.Text,
                    speakerLabel: displayLabel);
                line.PropertyChanged += OnSpeakerTranscriptLinePropertyChanged;
                SpeakerTranscriptLines.Add(line);
            }
        }
        finally {
            _suppressSessionAutosave = false;
        }
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
        if (string.Equals(e.PropertyName, nameof(FinalizedTranscriptLineViewModel.IsManuallyReviewed), StringComparison.Ordinal)) {
            ScheduleSessionAutosave();
            return;
        }

        if (!string.Equals(e.PropertyName, nameof(FinalizedTranscriptLineViewModel.Text), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(FinalizedTranscriptLineViewModel.Timeline), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(FinalizedTranscriptLineViewModel.StartOffset), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(FinalizedTranscriptLineViewModel.EndOffset), StringComparison.Ordinal)) {
            return;
        }

        RebuildFinalizedTextFromLines();
        ScheduleSessionAutosave();
    }

    private void OnSpeakerTranscriptLinePropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (!string.Equals(e.PropertyName, nameof(FinalizedTranscriptLineViewModel.Text), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(FinalizedTranscriptLineViewModel.SpeakerLabel), StringComparison.Ordinal)) {
            return;
        }

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

    private void UnsubscribeFromSpeakerLineChanges() {
        foreach (FinalizedTranscriptLineViewModel line in SpeakerTranscriptLines) {
            line.PropertyChanged -= OnSpeakerTranscriptLinePropertyChanged;
        }
    }

    private void OnProcessLogEmitted(object? sender, string message) {
        _uiContext.Post(_ => AppendLogCore(message), null);
    }

    private void OnProcessLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) {
        NotifyPropertyChanged(nameof(HasProcessLogs));
    }

    private void OnFinalizedTranscriptLinesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) {
        NotifyPropertyChanged(nameof(HasFinalizedTranscriptLines));
        NotifyCurrentTranscriptStateChanged();
    }

    private void OnSpeakerTranscriptLinesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) {
        NotifyPropertyChanged(nameof(HasSpeakerTranscriptLines));
        NotifyCurrentTranscriptStateChanged();
    }

    private void OnApplicationUpdateStatusChanged(object? sender, EventArgs e) {
        _uiContext.Post(_ => {
            ApplicationVersionStatusText = _applicationUpdateService?.FooterStatusText
                ?? BuildInstalledVersionStatus();
        }, null);
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
            updatedTranscriptMode: null,
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

    private bool HasExistingTranscriptContent(TranscriptGenerationMode transcriptMode) {
        if (transcriptMode == TranscriptGenerationMode.SpeakerDiarization) {
            if (SpeakerTranscriptLines.Count > 0) {
                return true;
            }

            if (_currentSessionDocument is null) {
                return false;
            }

            return !string.IsNullOrWhiteSpace(_currentSessionDocument.SpeakerTranscript.FinalText)
                || _currentSessionDocument.SpeakerTranscript.Lines.Count > 0;
        }

        if (FinalizedTranscriptLines.Count > 0) {
            return true;
        }

        if (_currentSessionDocument is null) {
            return !string.IsNullOrWhiteSpace(FinalizedText);
        }

        return !string.IsNullOrWhiteSpace(_currentSessionDocument.Transcript.FinalText)
            || _currentSessionDocument.Transcript.Lines.Count > 0;
    }

    private bool ConfirmTranscriptReplacement(
        string operationName,
        TranscriptGenerationMode transcriptMode,
        string canceledStatusMessage) {
        EventHandler<ConfirmationRequest>? handler = ConfirmationRequested;
        if (handler is null) {
            RaiseError("The confirmation dialog is unavailable. The existing transcript was left unchanged.");
            AppendLog($"{operationName} canceled: transcript replacement confirmation is unavailable.");
            return false;
        }

        var request = new ConfirmationRequest(
            title: "Replace current transcript?",
            message: transcriptMode == TranscriptGenerationMode.SpeakerDiarization
                ? "This session already has speaker transcript content. Proceeding will remove the current speaker transcript and start a new diarization for this audio file."
                : "This session already has segment transcript content. Proceeding will remove the current segment transcript and start a new transcription for this audio file.",
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

    private void ResetCurrentSessionTranscriptState(TranscriptGenerationMode transcriptMode) {
        if (_currentSessionDocument is null) {
            return;
        }

        TranscriptSessionTranscriptDocument transcript = transcriptMode == TranscriptGenerationMode.SpeakerDiarization
            ? _currentSessionDocument.SpeakerTranscript
            : _currentSessionDocument.Transcript;

        transcript.FinalText = string.Empty;
        transcript.ModelId = string.Empty;
        transcript.LastTranscribedUtc = null;
        transcript.Lines.Clear();

        if (transcriptMode == TranscriptGenerationMode.Segments) {
            _currentSessionDocument.Editing.SelectedRowIndex = null;
        }
    }

    private void ClearCurrentSessionAfterDeletion() {
        _suppressSessionAutosave = true;

        try {
            UnsubscribeFromFinalizedLineChanges();
            UnsubscribeFromSpeakerLineChanges();
            FinalizedTranscriptLines.Clear();
            SpeakerTranscriptLines.Clear();
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
        NotifyPropertyChanged(nameof(HasPendingSessionSelection));
        NotifyPropertyChanged(nameof(ShouldShowTranscriptChooseFileAction));
        NotifyPropertyChanged(nameof(IsTranscriptEmptyStateVisible));
        NotifyPropertyChanged(nameof(LoadedAudioFileName));
        RefreshCommandStates();
    }

    private void QueueSessionAutosaveSave() {
        TranscriptSessionDocument? snapshot = CreateSessionSaveSnapshot(updatedTranscriptMode: null);
        if (snapshot is null) {
            return;
        }

        _ = SaveSessionSnapshotAsync(
            snapshot,
            showErrorDialog: false,
            successLogMessage: string.Empty);
    }

    private bool TrySaveCurrentSession(
        TranscriptGenerationMode? updatedTranscriptMode,
        bool showErrorDialog,
        string successLogMessage) {
        TranscriptSessionDocument? snapshot = CreateSessionSaveSnapshot(updatedTranscriptMode);
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

    private TranscriptSessionDocument? CreateSessionSaveSnapshot(TranscriptGenerationMode? updatedTranscriptMode) {
        if (_currentSessionDocument is null) {
            return null;
        }

        string displayName = string.IsNullOrWhiteSpace(_currentSessionDocument.DisplayName)
            ? Path.GetFileNameWithoutExtension(_currentSessionDocument.Audio.OriginalFileName)
            : _currentSessionDocument.DisplayName;
        DateTimeOffset updatedUtc = DateTimeOffset.UtcNow;
        DateTimeOffset? segmentLastTranscribedUtc = updatedTranscriptMode == TranscriptGenerationMode.Segments
            ? updatedUtc
            : _currentSessionDocument.Transcript.LastTranscribedUtc;
        DateTimeOffset? speakerLastTranscribedUtc = updatedTranscriptMode == TranscriptGenerationMode.SpeakerDiarization
            ? updatedUtc
            : _currentSessionDocument.SpeakerTranscript.LastTranscribedUtc;
        double? durationSeconds = _currentSessionDocument.Audio.DurationSeconds;

        if (_audioPlaybackService.Duration > TimeSpan.Zero) {
            durationSeconds = _audioPlaybackService.Duration.TotalSeconds;
        }

        List<TranscriptSessionLineDocument> segmentLines = FinalizedTranscriptLines
            .Select(line => new TranscriptSessionLineDocument {
                Text = line.Text,
                StartSeconds = line.StartOffset?.TotalSeconds,
                EndSeconds = line.EndOffset?.TotalSeconds,
                IsTimestampEstimated = line.IsTimestampEstimated,
                IsManuallyReviewed = line.IsManuallyReviewed,
            })
            .ToList();
        List<TranscriptSessionLineDocument> speakerLines = SpeakerTranscriptLines
            .Select(line => new TranscriptSessionLineDocument {
                Text = line.Text,
                SpeakerLabel = line.SpeakerLabel,
                StartSeconds = line.StartOffset?.TotalSeconds,
                EndSeconds = line.EndOffset?.TotalSeconds,
            })
            .ToList();

        _currentSessionDocument.DisplayName = displayName;
        _currentSessionDocument.UpdatedUtc = updatedUtc;
        _currentSessionDocument.Audio.DurationSeconds = durationSeconds;
        _currentSessionDocument.Transcript.FinalText = BuildSegmentTranscriptText(includeTimeline: false);
        _currentSessionDocument.Transcript.ModelId = SelectedEngine?.Id ?? _currentSessionDocument.Transcript.ModelId;
        _currentSessionDocument.Transcript.LastTranscribedUtc = segmentLastTranscribedUtc;
        _currentSessionDocument.Transcript.Lines = segmentLines
            .Select(line => new TranscriptSessionLineDocument {
                Text = line.Text,
                StartSeconds = line.StartSeconds,
                EndSeconds = line.EndSeconds,
                IsTimestampEstimated = line.IsTimestampEstimated,
                IsManuallyReviewed = line.IsManuallyReviewed,
            })
            .ToList();
        _currentSessionDocument.SpeakerTranscript.FinalText = BuildSpeakerTranscriptText(includeTimeline: true);
        _currentSessionDocument.SpeakerTranscript.ModelId =
            updatedTranscriptMode == TranscriptGenerationMode.SpeakerDiarization
                ? OpenAiTranscriptionModelCatalog.Gpt4oTranscribeDiarize
                : _currentSessionDocument.SpeakerTranscript.ModelId;
        _currentSessionDocument.SpeakerTranscript.LastTranscribedUtc = speakerLastTranscribedUtc;
        _currentSessionDocument.SpeakerTranscript.Lines = speakerLines
            .Select(line => new TranscriptSessionLineDocument {
                Text = line.Text,
                SpeakerLabel = line.SpeakerLabel,
                StartSeconds = line.StartSeconds,
                EndSeconds = line.EndSeconds,
                IsTimestampEstimated = line.IsTimestampEstimated,
                IsManuallyReviewed = line.IsManuallyReviewed,
            })
            .ToList();
        _currentSessionDocument.Editing.SelectedTranscriptMode = SelectedTranscriptMode?.Mode.ToString() ?? string.Empty;
        _currentSessionDocument.Editing.SelectedTranscriptViewIndex = SelectedTranscriptViewIndex;

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
                Lines = segmentLines,
            },
            SpeakerTranscript = new TranscriptSessionTranscriptDocument {
                FinalText = _currentSessionDocument.SpeakerTranscript.FinalText,
                ModelId = _currentSessionDocument.SpeakerTranscript.ModelId,
                LastTranscribedUtc = _currentSessionDocument.SpeakerTranscript.LastTranscribedUtc,
                Lines = speakerLines,
            },
            Editing = new TranscriptSessionEditingDocument {
                SelectedRowIndex = _currentSessionDocument.Editing.SelectedRowIndex,
                SelectedTranscriptMode = _currentSessionDocument.Editing.SelectedTranscriptMode,
                SelectedTranscriptViewIndex = _currentSessionDocument.Editing.SelectedTranscriptViewIndex,
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

    private string BuildSegmentTranscriptText(bool includeTimeline) {
        return string.Join(
            Environment.NewLine,
            FinalizedTranscriptLines
                .Select(line => {
                    string text = line.Text?.Trim() ?? string.Empty;

                    if (!includeTimeline) {
                        return text;
                    }

                    string timeline = line.Timeline?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(timeline)) {
                        return text;
                    }

                    return string.IsNullOrWhiteSpace(text) ? timeline : $"{timeline} {text}";
                })
                .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private string BuildSpeakerTranscriptText(bool includeTimeline) {
        return string.Join(
            Environment.NewLine,
            SpeakerTranscriptLines
                .Select(line => {
                    string speakerLabel = string.IsNullOrWhiteSpace(line.SpeakerLabel)
                        ? "Speaker"
                        : line.SpeakerLabel.Trim();
                    string text = line.Text?.Trim() ?? string.Empty;
                    string speakerText = string.IsNullOrWhiteSpace(text)
                        ? speakerLabel
                        : $"{speakerLabel}: {text}";

                    if (!includeTimeline) {
                        return speakerText;
                    }

                    string timeline = line.Timeline?.Trim() ?? string.Empty;
                    return string.IsNullOrWhiteSpace(timeline)
                        ? speakerText
                        : $"{timeline} {speakerText}";
                })
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

    private static string BuildInstalledVersionStatus() {
        Version version = ApplicationDeploymentInfo.CurrentVersion;

        if (version.Revision > 0) {
            return $"Version {version.ToString(4)}";
        }

        if (version.Build >= 0) {
            return $"Version {version.ToString(3)}";
        }

        return $"Version {version.ToString(2)}";
    }

    private static EngineOptionViewModel ResolveEngine(
        IEnumerable<EngineOptionViewModel> engines,
        string id,
        string fallbackDisplayName) {
        EngineOptionViewModel? match = engines.FirstOrDefault(engine =>
            string.Equals(engine.Id, id, StringComparison.OrdinalIgnoreCase));

        if (match is not null) {
            return match;
        }

        return new EngineOptionViewModel(new TranscriptionModelOption(id, fallbackDisplayName));
    }
}


