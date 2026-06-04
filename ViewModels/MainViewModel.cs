using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using AudioScript.Abstractions;
using AudioScript.Audio;
using AudioScript.Services;
using NAudio.Wave;

namespace AudioScript.ViewModels;

public sealed record PremiumUpsellRequest(
    string FeatureName,
    string Message);

public sealed record TranscriptProcessingPanelSessionSnapshot(
    string SourceFileName,
    long SourceFileSizeBytes,
    TimeSpan? TotalAudioDuration,
    string EngineId,
    bool ResumeAvailable,
    double ProgressPercent,
    TimeSpan? Elapsed,
    TimeSpan? EstimatedRemaining);

public sealed record SpeakerDiarizationPanelSessionSnapshot(
    bool ResumeAvailable,
    bool RestartAvailable);

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const int BasicSessionLimit = 10;
    private const string SessionLimitUpsellMessage = "Basic is limited to 10 sessions. Delete sessions or upgrade to Premium to add more.";
    private const string AudioFileDialogFilter = "Audio Files|*.wav;*.mp3;*.flac;*.aac;*.m4a;*.ogg;*.wma;*.mp4|All Files|*.*";
    private const string SpeakerDiarizationEngineId = "pyannote-community-1";
    private const int SpeakerDiarizationJobVersion = 1;
    private const int TranscriptionJobVersion = 1;
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

    private readonly IChunkedAudioTranscriptionService _audioTranscriptionService;
    private readonly ChunkedSpeakerDiarizationService _speakerDiarizationService;
    private readonly IAudioPlaybackService _audioPlaybackService;
    private readonly ProcessLogService _processLogService;
    private readonly TranscriptSessionStore _sessionStore;
    private readonly AppPreferencesStore _appPreferencesStore;
    private readonly AppThemeService _appThemeService;
    private readonly IAppUpdateService? _appUpdateService;
    private readonly IEntitlementService? _entitlementService;
    private readonly Func<IReadOnlyList<TranscriptionModelOption>> _availableModelsProvider;
    private readonly SynchronizationContext _uiContext;
    private readonly DispatcherTimer _audioTimelineTimer;
    private readonly DispatcherTimer _sessionAutosaveTimer;
    private readonly SemaphoreSlim _sessionSaveSemaphore = new(1, 1);
    private readonly object _processLogsSync = new();

    private readonly EngineOptionViewModel _autoTranscribeEngine;
    private EngineOptionViewModel? _selectedEngine;
    private TranscriptSessionSummary? _selectedRecentSession;
    private IReadOnlyList<TranscriptSessionSummary> _allRecentSessions = Array.Empty<TranscriptSessionSummary>();
    private TranscriptSessionDocument? _currentSessionDocument;
    private bool _lastSpeakerDetectionUsedHeuristicFallback;
    private TranscribeAudioWorkflowState? _transcribeAudioWorkflow;
    private string _currentSessionDisplayName = "No session loaded.";
    private string _currentSessionAudioIssue = string.Empty;
    private string _finalizedText = string.Empty;
    private bool _isBusy;
    private bool _isCurrentSessionAudioMissing;
    private string _loadedAudioFilePath = string.Empty;
    private bool _isAudioPlaying;
    private bool _isMediaPlayerPanelVisible;
    private double _audioSeekMaximumSeconds;
    private double _audioSeekPositionSeconds;
    private string _audioElapsedText = "00:00";
    private string _audioRemainingText = "-00:00";
    private bool _autoPlayTimelineSelection;
    private string _recentSessionsFilterText = string.Empty;
    private RecentSessionsSortMode _recentSessionsSortMode;
    private bool _recentSessionsSortDescending;
    private bool _isGenerationRunning;
    private bool _isLiveTranscriptionRunning;
    private LiveAudioSourceKind _preferredLiveAudioSourceKind;
    private int _preferredLiveAudioDeviceNumber;
    private bool _liveAudioAutoGainEnabled;
    private double _liveAudioGainLevel;
    private AppThemePreference _selectedThemePreference;
    private bool _isUpdatingSeekFromPlayback;
    private bool _isApplyingSpeakerDiarizationLabels;
    private bool _pendingTranscribeAudioResume;
    private bool _pendingSpeakerDiarizationResume;
    private bool _suppressSessionAutosave;
    private int _activeLiveInterimSequenceIndex = -1;
    private AppUpdateSnapshot _appUpdateSnapshot;
    private AppEntitlementSnapshot _entitlementSnapshot;
    private int _selectedTranscriptViewIndex;
    private string _pendingImportedAudioFilePath = string.Empty;
    private string _transcriptExportDirectory = string.Empty;
    private readonly bool _isSpeakerDiarizationRuntimeAvailable;
    private readonly string _speakerDiarizationRuntimeStatusMessage;
    private int _isHandlingAudioSelection;

    public MainViewModel(
        IEnumerable<TranscriptionModelOption> models,
        IChunkedAudioTranscriptionService audioTranscriptionService,
        ChunkedSpeakerDiarizationService speakerDiarizationService,
        IAudioPlaybackService audioPlaybackService,
        ProcessLogService processLogService,
        TranscriptSessionStore sessionStore,
        AppPreferencesStore appPreferencesStore,
        AppThemeService appThemeService,
        AppPreferencesSnapshot appPreferencesSnapshot,
        IAppUpdateService? appUpdateService = null,
        IEntitlementService? entitlementService = null,
        Func<IReadOnlyList<TranscriptionModelOption>>? availableModelsProvider = null,
        bool isSpeakerDiarizationRuntimeAvailable = true,
        string? speakerDiarizationRuntimeStatusMessage = null)
    {
        _audioTranscriptionService = audioTranscriptionService;
        _speakerDiarizationService = speakerDiarizationService;
        _audioPlaybackService = audioPlaybackService;
        _processLogService = processLogService;
        _sessionStore = sessionStore;
        _appPreferencesStore = appPreferencesStore;
        _appThemeService = appThemeService;
        _appUpdateService = appUpdateService;
        _entitlementService = entitlementService;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _appUpdateSnapshot = appUpdateService?.CurrentSnapshot
            ?? AppUpdateSnapshot.Idle(new AppVersionProvider().InstalledVersion);
        _entitlementSnapshot = entitlementService?.CurrentSnapshot
            ?? AppEntitlementSnapshot.Development("AudioScript Premium");
        _availableModelsProvider = availableModelsProvider ?? (() => models.ToArray());
        _isSpeakerDiarizationRuntimeAvailable = isSpeakerDiarizationRuntimeAvailable;
        _speakerDiarizationRuntimeStatusMessage = string.IsNullOrWhiteSpace(speakerDiarizationRuntimeStatusMessage)
            ? "Speaker diarization dependencies are unavailable."
            : speakerDiarizationRuntimeStatusMessage.Trim();

        _autoPlayTimelineSelection = appPreferencesSnapshot.AutoPlayTimelineSelection;
        _recentSessionsSortMode = appPreferencesSnapshot.RecentSessionsSortMode;
        _recentSessionsSortDescending = appPreferencesSnapshot.RecentSessionsSortDescending;
        _selectedThemePreference = appPreferencesSnapshot.ThemePreference;
        _preferredLiveAudioSourceKind = appPreferencesSnapshot.LiveAudioSourceKind;
        _preferredLiveAudioDeviceNumber = appPreferencesSnapshot.LiveAudioDeviceNumber;
        _liveAudioAutoGainEnabled = appPreferencesSnapshot.LiveAudioAutoGainEnabled;
        _liveAudioGainLevel = Math.Clamp(appPreferencesSnapshot.LiveAudioGainLevel, 0, 1);
        _transcriptExportDirectory = appPreferencesSnapshot.TranscriptExportDirectory?.Trim() ?? string.Empty;

        Engines = new ObservableCollection<EngineOptionViewModel>(
            FilterAccessibleEngines(models).Select(model => new EngineOptionViewModel(model)));
        _autoTranscribeEngine = ResolveEngine(
            Engines,
            TranscriptionModelCatalog.WhisperSmall,
            "Whisper small");
        ProcessLogs = new ObservableCollection<ProcessLogEntryViewModel>();
        FinalizedTranscriptLines = new ObservableCollection<FinalizedTranscriptLineViewModel>();
        RecentSessions = new ObservableCollection<TranscriptSessionSummary>();
        ThemeOptions = AppThemeService.ThemeOptions;
        ProcessLogs.CollectionChanged += OnProcessLogsCollectionChanged;
        FinalizedTranscriptLines.CollectionChanged += OnFinalizedTranscriptLinesCollectionChanged;

        CloseCommand = new AsyncRelayCommand(CloseAsync, CanClose);
        OpenAudioFileCommand = new AsyncRelayCommand(OpenAudioFileAsync, CanOpenAudioFile);
        OpenAudioFileForTranscriptionCommand = new AsyncRelayCommand(OpenAudioFileForTranscriptionAsync, CanOpenAudioFile);
        DeleteSelectedSessionCommand = new AsyncRelayCommand(DeleteSelectedSessionAsync, CanDeleteSelectedSession);
        PlayAudioCommand = new AsyncRelayCommand(PlayAudioAsync, CanPlayAudio);
        PauseAudioCommand = new AsyncRelayCommand(PauseAudioAsync, CanPauseAudio);
        UpgradeToPremiumCommand = new AsyncRelayCommand(RequestUpgradeToPremiumAsync, CanRequestUpgradeToPremium);
        CheckForAppUpdateCommand = new AsyncRelayCommand(CheckForAppUpdateAsync, CanCheckForAppUpdate);

        _processLogService.LogEmitted += OnProcessLogEmitted;
        _audioPlaybackService.PlaybackStateChanged += OnAudioPlaybackStateChanged;
        _isAudioPlaying = _audioPlaybackService.IsPlaying;

        _audioTimelineTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _audioTimelineTimer.Tick += OnAudioTimelineTick;
        _audioTimelineTimer.Start();

        _sessionAutosaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(800),
        };
        _sessionAutosaveTimer.Tick += OnSessionAutosaveTimerTick;

        SelectedEngine = ResolveEngine(
            Engines,
            appPreferencesSnapshot.SelectedEngineId,
            _autoTranscribeEngine.DisplayName);
        SelectedTranscriptViewIndex = 0;
        if (_appUpdateService is not null)
        {
            _appUpdateService.SnapshotChanged += OnAppUpdateSnapshotChanged;
        }

        if (_entitlementService is not null)
        {
            _entitlementService.SnapshotChanged += OnEntitlementSnapshotChanged;
        }

        AppendLogCore("Application initialized.");
        AppendLogCore($"Loaded {Engines.Count} transcription mode option(s).");

        AppendLogCore("Transcription uses installed offline engines.");
        AppendLogCore($"Theme preference: {AppThemeService.GetDisplayName(_selectedThemePreference)}.");
        AppendLogCore($"Startup mode: {SelectedEngine?.DisplayName ?? "Unavailable"}.");
        LoadRecentSessions(selectSessionId: null);

    }

    public MainViewModel(
        IEnumerable<TranscriptionModelOption> models,
        IAudioTranscriptionService audioTranscriptionService,
        ChunkedSpeakerDiarizationService speakerDiarizationService,
        IAudioPlaybackService audioPlaybackService,
        ProcessLogService processLogService,
        TranscriptSessionStore sessionStore,
        AppPreferencesStore appPreferencesStore,
        AppThemeService appThemeService,
        AppPreferencesSnapshot appPreferencesSnapshot,
        IAppUpdateService? appUpdateService = null,
        IEntitlementService? entitlementService = null,
        Func<IReadOnlyList<TranscriptionModelOption>>? availableModelsProvider = null,
        bool isSpeakerDiarizationRuntimeAvailable = true,
        string? speakerDiarizationRuntimeStatusMessage = null)
        : this(
            models,
            new PassThroughChunkedAudioTranscriptionService(audioTranscriptionService),
            speakerDiarizationService,
            audioPlaybackService,
            processLogService,
            sessionStore,
            appPreferencesStore,
            appThemeService,
            appPreferencesSnapshot,
            appUpdateService,
            entitlementService,
            availableModelsProvider,
            isSpeakerDiarizationRuntimeAvailable,
            speakerDiarizationRuntimeStatusMessage)
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<ConfirmationRequest>? ConfirmationRequested;
    public event EventHandler<ToastNotification>? ToastRequested;
    public event EventHandler? NewAudioFileStagedForTranscribeAudio;
    public event EventHandler? SessionLoadStarting;
    public event EventHandler<PremiumUpsellRequest>? PremiumUpsellRequested;

    public ObservableCollection<EngineOptionViewModel> Engines { get; }
    public ObservableCollection<ProcessLogEntryViewModel> ProcessLogs { get; }
    public ObservableCollection<FinalizedTranscriptLineViewModel> FinalizedTranscriptLines { get; }
    public IReadOnlyList<AppThemeOption> ThemeOptions { get; }

    public IEnumerable<FinalizedTranscriptLineViewModel> CurrentTranscriptLines =>
        FinalizedTranscriptLines;
    public ObservableCollection<TranscriptSessionSummary> RecentSessions { get; }

    public AsyncRelayCommand CloseCommand { get; }
    public AsyncRelayCommand OpenAudioFileCommand { get; }
    public AsyncRelayCommand OpenAudioFileForTranscriptionCommand { get; }
    public AsyncRelayCommand DeleteSelectedSessionCommand { get; }
    public AsyncRelayCommand PlayAudioCommand { get; }
    public AsyncRelayCommand PauseAudioCommand { get; }
    public AsyncRelayCommand UpgradeToPremiumCommand { get; }
    public AsyncRelayCommand CheckForAppUpdateCommand { get; }

    public IAppUpdateService? AppUpdateService => _appUpdateService;

    public string RecentSessionsFilterText
    {
        get => _recentSessionsFilterText;
        set
        {
            string normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(_recentSessionsFilterText, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _recentSessionsFilterText = normalized;
            NotifyPropertyChanged();
            NotifyPropertyChanged(nameof(HasRecentSessionsFilterText));
            NotifyPropertyChanged(nameof(RecentSessionsEmptyStateTitle));
            NotifyPropertyChanged(nameof(RecentSessionsEmptyStateMessage));

            string? highlightedSessionId = SelectedRecentSession?.SessionId
                ?? _currentSessionDocument?.SessionId;
            RefreshRecentSessionsView(highlightedSessionId);
        }
    }

    public bool HasRecentSessionsFilterText =>
        !string.IsNullOrWhiteSpace(_recentSessionsFilterText);

    public string RecentSessionsEmptyStateTitle =>
        HasRecentSessionsFilterText
            ? "No matching sessions"
            : "No saved sessions yet";

    public string RecentSessionsEmptyStateMessage =>
        HasRecentSessionsFilterText
            ? "Clear the filter to see all saved sessions."
            : "Import an audio file to create your first transcript session.";

    public void RefreshEngines(
        IEnumerable<TranscriptionModelOption> models,
        string? preferredSelectedEngineId = null)
    {
        ArgumentNullException.ThrowIfNull(models);

        string previousEngineId = SelectedEngineId;
        string preferredEngineId = preferredSelectedEngineId?.Trim() ?? string.Empty;
        EngineOptionViewModel? fallbackEngine = null;
        Engines.Clear();

        foreach (TranscriptionModelOption model in FilterAccessibleEngines(models))
        {
            var option = new EngineOptionViewModel(model);
            Engines.Add(option);
            if (string.Equals(model.Id, TranscriptionModelCatalog.WhisperSmall, StringComparison.OrdinalIgnoreCase))
            {
                fallbackEngine = option;
            }
        }

        fallbackEngine ??= ResolveEngine(Engines, TranscriptionModelCatalog.WhisperSmall, "Whisper small");
        EngineOptionViewModel? preferredSelection = string.IsNullOrWhiteSpace(preferredEngineId)
            ? null
            : Engines.FirstOrDefault(engine =>
                string.Equals(engine.Id, preferredEngineId, StringComparison.OrdinalIgnoreCase));
        EngineOptionViewModel? previousSelection = Engines.FirstOrDefault(engine =>
            string.Equals(engine.Id, previousEngineId, StringComparison.OrdinalIgnoreCase));
        SelectedEngine = preferredSelection ?? previousSelection ?? fallbackEngine;

        AppendLog($"Available transcription engines refreshed ({Engines.Count:N0} option(s)).");
    }

    public AppThemePreference SelectedThemePreference
    {
        get => _selectedThemePreference;
        set
        {
            if (!SetProperty(ref _selectedThemePreference, value))
            {
                return;
            }

            _appThemeService.Apply(value);
            SaveAppPreferences();
            AppendLog($"Theme preference: {AppThemeService.GetDisplayName(value)}.");
        }
    }

    public EngineOptionViewModel? SelectedEngine
    {
        get => _selectedEngine;
        set
        {
            if (!SetProperty(ref _selectedEngine, value))
            {
                return;
            }

            NotifyPropertyChanged(nameof(SelectedEngineId));
            NotifyPropertyChanged(nameof(IsTranscribeAudioTranscriptionEnabled));
            NotifyPropertyChanged(nameof(IsTranscriptGenerationEnabled));
            NotifyInteractionAvailabilityChanged();
            RefreshCommandStates();
            SaveAppPreferences();

            if (value is null)
            {
                AppendLog("Selected model cleared.");
            }
            else
            {
                AppendLog($"Selected model: {value.DisplayName} (id: {value.Id}).");
            }
        }
    }

    public TranscriptSessionSummary? SelectedRecentSession
    {
        get => _selectedRecentSession;
        set
        {
            if (!SetProperty(ref _selectedRecentSession, value))
            {
                return;
            }

            RefreshCommandStates();
            NotifyPropertyChanged(nameof(HasPendingSessionSelection));
            NotifyPropertyChanged(nameof(ShouldShowTranscriptChooseFileAction));
            NotifyPropertyChanged(nameof(ShouldShowTranscriptTranscribeAudioAction));
            NotifyPropertyChanged(nameof(TranscriptEmptyStateTitle));
            NotifyPropertyChanged(nameof(TranscriptEmptyStateMessage));
        }
    }

    public bool HasRecentSessions => RecentSessions.Count > 0;

    public bool HasProcessLogs => ProcessLogs.Count > 0;

    public void ApplyRecentSessionsSort(RecentSessionsSortMode sortMode)
    {
        bool sortChanged = _recentSessionsSortMode != sortMode;
        if (!sortChanged)
        {
            _recentSessionsSortDescending = !_recentSessionsSortDescending;
        }
        else
        {
            _recentSessionsSortMode = sortMode;
            _recentSessionsSortDescending = sortMode == RecentSessionsSortMode.CreatedDate;
        }

        NotifyPropertyChanged(nameof(IsRecentSessionsSortByCreatedDateSelected));
        NotifyPropertyChanged(nameof(IsRecentSessionsSortByNameSelected));
        SaveAppPreferences();

        string? highlightedSessionId = SelectedRecentSession?.SessionId
            ?? _currentSessionDocument?.SessionId;
        LoadRecentSessions(highlightedSessionId, pinSelectedToTop: false);
    }

    public string CurrentSessionDisplayName
    {
        get => _currentSessionDisplayName;
        private set
        {
            if (!SetProperty(ref _currentSessionDisplayName, value))
            {
                return;
            }

            NotifyPropertyChanged(nameof(TranscriptPaneSubtitle));
        }
    }

    public string CurrentSessionAudioIssue
    {
        get => _currentSessionAudioIssue;
        private set
        {
            if (!SetProperty(ref _currentSessionAudioIssue, value))
            {
                return;
            }

            NotifyPropertyChanged(nameof(HasCurrentSessionAudioIssue));
        }
    }

    public bool HasCurrentSession => _currentSessionDocument is not null;

    public bool IsCurrentSessionLiveTranscriptionSession =>
        _currentSessionDocument is not null
        && string.Equals(
            _currentSessionDocument.Audio.StorageKind,
            AudioStorageKinds.LiveRecordingManifest,
            StringComparison.OrdinalIgnoreCase);

    public bool IsCurrentSessionAudioTranscriptionSession =>
        _currentSessionDocument is not null
        && string.Equals(
            _currentSessionDocument.Audio.StorageKind,
            AudioStorageKinds.ImportedFile,
            StringComparison.OrdinalIgnoreCase);

    public bool HasPendingSessionSelection =>
        false;

    public bool ShouldShowTranscriptChooseFileAction =>
        !HasCurrentSession
        && !IsAudioFileLoaded;

    public bool ShouldShowTranscriptTranscribeAudioAction =>
        IsAudioFileLoaded
        && !HasCurrentTranscriptLines;

    public bool HasCurrentSessionAudioIssue => !string.IsNullOrWhiteSpace(CurrentSessionAudioIssue);

    public bool IsCurrentSessionAudioMissing
    {
        get => _isCurrentSessionAudioMissing;
        private set
        {
            if (!SetProperty(ref _isCurrentSessionAudioMissing, value))
            {
                return;
            }

            NotifyPropertyChanged(nameof(LoadedAudioFileName));
            RefreshCommandStates();
        }
    }

    public string SelectedEngineId =>
        SelectedEngine?.Id ?? string.Empty;

    public bool IsEngineSelectionEnabled =>
        !IsBusy;

    public bool HasPremium =>
        _entitlementSnapshot.HasPremium;

    public bool IsPremiumProductAvailable =>
        _entitlementSnapshot.IsPremiumProductAvailable;

    public PremiumEntitlementState PremiumEntitlementState =>
        _entitlementSnapshot.State;

    public bool IsPremiumEntitlementChecking =>
        _entitlementSnapshot.State == PremiumEntitlementState.Checking;

    public bool IsPremiumEntitlementVerificationFailed =>
        _entitlementSnapshot.State is PremiumEntitlementState.VerificationFailed
            or PremiumEntitlementState.VerificationInconclusive;

    public bool CanPromptPremiumPurchase =>
        !IsDevelopmentUnpackagedMode
        && _entitlementSnapshot.State == PremiumEntitlementState.VerifiedBasic;

    public bool IsPremiumStatusBannerVisible =>
        !IsDevelopmentUnpackagedMode
        && _entitlementSnapshot.State != PremiumEntitlementState.VerifiedPremium;

    public string PremiumProductDisplayName =>
        _entitlementSnapshot.PremiumProductDisplayName;

    public bool IsDevelopmentUnpackagedMode =>
        !_entitlementSnapshot.IsPackaged;

    public bool HasUnlimitedLiveTranscription =>
        IsDevelopmentUnpackagedMode
        || AppFeatureAccess.HasUnlimitedLiveTranscription(HasPremium);

    public TimeSpan? LiveTranscriptionLimit =>
        HasUnlimitedLiveTranscription
            ? null
            : AppFeatureAccess.GetLiveTranscriptionLimit(HasPremium);

    public bool CanUseLiveTranscription =>
        IsDevelopmentUnpackagedMode
        || AppFeatureAccess.CanAccessFeature(AppFeature.LiveTranscription, HasPremium);

    public bool CanUseSpeakerDiarization =>
        _isSpeakerDiarizationRuntimeAvailable
        && (IsDevelopmentUnpackagedMode
        || AppFeatureAccess.CanAccessFeature(AppFeature.SpeakerDiarization, HasPremium));
    
    public bool IsSpeakerDiarizationRuntimeAvailable =>
        _isSpeakerDiarizationRuntimeAvailable;

    public string SpeakerDiarizationRuntimeStatusMessage =>
        _speakerDiarizationRuntimeStatusMessage;

    public bool IsTranscribeAudioTranscriptionEnabled =>
        SelectedEngine is not null
        && TranscriptionModelCatalog.SupportsFileTranscription(SelectedEngine.Id)
        && IsAudioFileLoaded
        && !IsBusy;

    public bool IsTranscriptGenerationEnabled =>
        IsAudioFileLoaded
        && !IsBusy
        && SelectedEngine is not null
        && TranscriptionModelCatalog.SupportsFileTranscription(SelectedEngine.Id);

    public bool IsGenerationRunning
    {
        get => _isGenerationRunning;
        private set
        {
            if (!SetProperty(ref _isGenerationRunning, value))
            {
                return;
            }
        }
    }

    public bool IsLiveTranscriptionRunning
    {
        get => _isLiveTranscriptionRunning;
        private set
        {
            if (!SetProperty(ref _isLiveTranscriptionRunning, value))
            {
                return;
            }

            NotifyPropertyChanged(nameof(ShouldShowLiveTranscriptionPanel));
        }
    }

    public int SelectedTranscriptViewIndex
    {
        get => _selectedTranscriptViewIndex;
        set
        {
            int normalized = value <= 0 ? 0 : 1;
            if (!SetProperty(ref _selectedTranscriptViewIndex, normalized))
            {
                return;
            }

            ScheduleSessionAutosave();
        }
    }

    public bool IsTranscribeAudioTranscriptViewSelected =>
        true;

    public bool HasCurrentTranscriptLines =>
        CurrentTranscriptLines.Any();

    public bool HasSpeakerLabels =>
        FinalizedTranscriptLines.Any(line => !string.IsNullOrWhiteSpace(line.SpeakerLabel));

    public bool IsCurrentTranscriptionJobIncomplete =>
        _currentSessionDocument?.Transcript.TranscriptionJob is TranscriptionJobDocument job
        && IsIncompleteTranscriptionJob(job);

    public bool LastSpeakerDetectionUsedHeuristicFallback => _lastSpeakerDetectionUsedHeuristicFallback;

    public bool IsTranscriptEmptyStateVisible =>
        !HasCurrentTranscriptLines;

    public bool IsTranscriptDataEmpty =>
        !HasCurrentTranscriptLines
        && string.IsNullOrWhiteSpace(FinalizedText);

    public bool HasNonEmptyCurrentTranscriptionSession =>
        (IsCurrentSessionLiveTranscriptionSession || IsCurrentSessionAudioTranscriptionSession)
        && !IsTranscriptDataEmpty;

    public bool ShouldShowLiveTranscriptionPanel =>
        IsCurrentSessionLiveTranscriptionSession
        && (IsTranscriptDataEmpty || IsLiveTranscriptionRunning);

    public bool CanCopyTranscript =>
        HasCurrentTranscriptLines
        && HasNonEmptyCurrentTranscriptionSession
        && !IsBusy;

    public bool CanExportAudio =>
        IsAudioFileLoaded
        && HasNonEmptyCurrentTranscriptionSession
        && !IsBusy;

    public static bool IsSupportedAudioFilePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string extension = Path.GetExtension(filePath);
        return !string.IsNullOrWhiteSpace(extension)
            && SupportedAudioFileExtensions.Contains(extension);
    }

    public bool CanRunLivePrimaryAction =>
        !IsGenerationRunning && !IsBusy;

    public bool CanRunTranscribeAudioPrimaryAction =>
        !IsGenerationRunning && IsTranscribeAudioTranscriptionEnabled;

    public bool CanRunDetectSpeakerPrimaryAction =>
        HasCurrentSession
        && IsAudioFileLoaded
        && HasNonEmptyCurrentTranscriptionSession
        && !IsGenerationRunning
        && !IsBusy;

    public bool CanRunDetectSpeakersPrimaryAction =>
        CanRunDetectSpeakerPrimaryAction;

    public LiveAudioSourceKind PreferredLiveAudioSourceKind => _preferredLiveAudioSourceKind;

    public int PreferredLiveAudioDeviceNumber => _preferredLiveAudioDeviceNumber;

    public LiveAudioGainOptions PreferredLiveAudioGainOptions =>
        new(_liveAudioAutoGainEnabled, _liveAudioGainLevel);

    public string TranscriptPaneSubtitle
    {
        get
        {
            if (IsAudioFileLoaded)
            {
                return LoadedAudioFileName;
            }

            if (HasCurrentSession)
            {
                return CurrentSessionDisplayName;
            }

            return string.Empty;
        }
    }

    public string TranscriptEmptyStateTitle
    {
        get
        {
            if (IsAudioFileLoaded && !HasCurrentTranscriptLines)
            {
                return "Ready to transcribe";
            }

            if (HasCurrentSession)
            {
                return "No transcript lines";
            }

            if (!IsAudioFileLoaded)
            {
                return "No transcript";
            }

            return "Ready";
        }
    }

    public string TranscriptEmptyStateMessage
    {
        get
        {
            if (HasCurrentTranscriptLines)
            {
                return "Transcript rows are available.";
            }

            if (IsAudioFileLoaded && !HasCurrentTranscriptLines)
            {
                return "Click the button below to transcribe this audio file.";
            }

            if (HasCurrentSession)
            {
                return "This session has no transcript lines yet.";
            }

            if (!IsAudioFileLoaded)
            {
                return "Drop audio here, choose a file, or open a session.";
            }

            return "Transcript rows are available.";
        }
    }

    public string TranscriptExportDirectory
    {
        get => _transcriptExportDirectory;
    }

    public void SetTranscriptExportDirectory(string? directory)
    {
        string normalized = directory?.Trim() ?? string.Empty;
        if (string.Equals(_transcriptExportDirectory, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _transcriptExportDirectory = normalized;
        NotifyPropertyChanged(nameof(TranscriptExportDirectory));
        SaveAppPreferences();
    }

    public bool HasFinalizedTranscriptLines =>
        FinalizedTranscriptLines.Count > 0;

    public bool AutoPlayTimelineSelection
    {
        get => _autoPlayTimelineSelection;
        set
        {
            if (!SetProperty(ref _autoPlayTimelineSelection, value))
            {
                return;
            }

            SaveAppPreferences();
            AppendLog($"Auto play selected timeline: {(value ? "ON" : "OFF")}.");
        }
    }

    public string ApplicationAccessTierText
    {
        get
        {
            if (_entitlementSnapshot.State == PremiumEntitlementState.Checking)
            {
                return string.Empty;
            }

            return _entitlementSnapshot.HasPremium ? "Premium" : "Basic";
        }
    }

    public bool IsRecentSessionsSortByCreatedDateSelected =>
        _recentSessionsSortMode == RecentSessionsSortMode.CreatedDate;

    public bool IsRecentSessionsSortByNameSelected =>
        _recentSessionsSortMode == RecentSessionsSortMode.Name;

    public bool IsApplicationAccessTierVisible =>
        !IsDevelopmentUnpackagedMode
        && _entitlementSnapshot.State != PremiumEntitlementState.Checking;

    public bool IsUpgradeButtonVisible =>
        !IsDevelopmentUnpackagedMode
        && _entitlementSnapshot.State != PremiumEntitlementState.Checking
        && !_entitlementSnapshot.HasPremium;

    public string ApplicationUpdateStageText => _appUpdateSnapshot.StageText;

    public string ApplicationUpdateMessageText => _appUpdateSnapshot.StatusMessage;

    public AppUpdateState ApplicationUpdateState => _appUpdateSnapshot.State;

    public bool IsApplicationUpdateProgressVisible => _appUpdateSnapshot.IsProgressVisible;

    public double ApplicationUpdateProgressPercent => _appUpdateSnapshot.ProgressValue * 100;

    public bool IsApplicationUpdateActive =>
        _appUpdateSnapshot.State is AppUpdateState.Downloading or AppUpdateState.Installing;

    public bool IsUpdateButtonVisible =>
        _appUpdateSnapshot.State == AppUpdateState.UpdateAvailable
        || _appUpdateSnapshot.HasActiveQueueItem;

    public bool IsUpdateButtonEnabled =>
        IsUpdateButtonVisible && !IsApplicationUpdateActive;

    public string AppVersionText =>
        _appUpdateSnapshot.InstalledVersion;

    public bool IsApplicationFooterCompactMode =>
        IsApplicationUpdateActive;

    public bool IsApplicationFooterDefaultVisible =>
        !IsApplicationFooterCompactMode;

    public string PremiumStatusText =>
        _entitlementSnapshot.StatusMessage;

    public string LoadedAudioFilePath
    {
        get => _loadedAudioFilePath;
        private set
        {
            if (!SetProperty(ref _loadedAudioFilePath, value))
            {
                return;
            }

            NotifyPropertyChanged(nameof(LoadedAudioFileName));
            NotifyPropertyChanged(nameof(IsAudioFileLoaded));
            NotifyPropertyChanged(nameof(CanExportAudio));
            NotifyPropertyChanged(nameof(TranscriptPaneSubtitle));
            NotifyCurrentTranscriptStateChanged();
            NotifyInteractionAvailabilityChanged();
            RefreshCommandStates();
        }
    }

    public string LoadedAudioFileName
    {
        get
        {
            if (_currentSessionDocument is not null
                && string.Equals(
                    _currentSessionDocument.Audio.StorageKind,
                    AudioStorageKinds.LiveRecordingManifest,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(_currentSessionDocument.Audio.OriginalFileName))
                {
                    return _currentSessionDocument.Audio.OriginalFileName;
                }

                return _currentSessionDocument.DisplayName;
            }

            if (!string.IsNullOrWhiteSpace(LoadedAudioFilePath))
            {
                return Path.GetFileName(LoadedAudioFilePath);
            }

            if (_currentSessionDocument is not null && !string.IsNullOrWhiteSpace(_currentSessionDocument.Audio.OriginalFileName))
            {
                return _currentSessionDocument.Audio.OriginalFileName;
            }

            return "No audio selected.";
        }
    }

    public bool IsAudioFileLoaded =>
        !string.IsNullOrWhiteSpace(LoadedAudioFilePath);

    public bool IsAudioPlaying
    {
        get => _isAudioPlaying;
        private set
        {
            if (!SetProperty(ref _isAudioPlaying, value))
            {
                return;
            }

            RefreshCommandStates();
        }
    }

    public bool IsMediaPlayerPanelVisible
    {
        get => _isMediaPlayerPanelVisible;
        set
        {
            if (!SetProperty(ref _isMediaPlayerPanelVisible, value))
            {
                return;
            }

            if (!value && IsAudioPlaying)
            {
                EnsureAudioPreviewPaused();
            }

            RefreshCommandStates();
        }
    }

    public double AudioSeekMaximumSeconds
    {
        get => _audioSeekMaximumSeconds;
        private set => SetProperty(ref _audioSeekMaximumSeconds, value);
    }

    public double AudioSeekPositionSeconds
    {
        get => _audioSeekPositionSeconds;
        set
        {
            double clamped = Math.Max(0, Math.Min(value, AudioSeekMaximumSeconds));

            if (!SetProperty(ref _audioSeekPositionSeconds, clamped))
            {
                return;
            }

            if (_isUpdatingSeekFromPlayback || !IsAudioFileLoaded)
            {
                return;
            }

            try
            {
                _audioPlaybackService.Seek(TimeSpan.FromSeconds(clamped));
            }
            catch (Exception ex)
            {
                AppendLog($"Audio seek failed: {ex.Message}");
            }

            UpdateAudioTimeLabels(
                elapsed: TimeSpan.FromSeconds(clamped),
                duration: TimeSpan.FromSeconds(AudioSeekMaximumSeconds));
        }
    }

    public string AudioElapsedText
    {
        get => _audioElapsedText;
        private set => SetProperty(ref _audioElapsedText, value);
    }

    public string AudioRemainingText
    {
        get => _audioRemainingText;
        private set => SetProperty(ref _audioRemainingText, value);
    }

    public string FinalizedText
    {
        get => _finalizedText;
        set => SetProperty(ref _finalizedText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            AppendLog($"Busy state: {(value ? "ON" : "OFF")}.");
            NotifyPropertyChanged(nameof(CanCopyTranscript));
            NotifyPropertyChanged(nameof(CanExportAudio));
            NotifyInteractionAvailabilityChanged();
            RefreshCommandStates();
        }
    }

    public ValueTask DisposeAsync()
    {
        AppendLog("Disposing transcription resources...");

        if (_appUpdateService is not null)
        {
            _appUpdateService.SnapshotChanged -= OnAppUpdateSnapshotChanged;
        }
        if (_entitlementService is not null)
        {
            _entitlementService.SnapshotChanged -= OnEntitlementSnapshotChanged;
        }

        PersistPendingTranscribeAudioSessionForShutdown();
        _sessionAutosaveTimer.Stop();
        _sessionAutosaveTimer.Tick -= OnSessionAutosaveTimerTick;
        TrySaveCurrentSession(
            updatedTranscriptMode: null,
            showErrorDialog: false,
            successLogMessage: string.Empty);

        _processLogService.LogEmitted -= OnProcessLogEmitted;
        _audioPlaybackService.PlaybackStateChanged -= OnAudioPlaybackStateChanged;
        ProcessLogs.CollectionChanged -= OnProcessLogsCollectionChanged;
        FinalizedTranscriptLines.CollectionChanged -= OnFinalizedTranscriptLinesCollectionChanged;
        UnsubscribeFromFinalizedLineChanges();
        _audioTimelineTimer.Stop();
        _audioTimelineTimer.Tick -= OnAudioTimelineTick;
        _audioPlaybackService.Dispose();
        _ = _entitlementService?.DisposeAsync();
        AppendLog("Disposed transcription resources.");
        return ValueTask.CompletedTask;
    }

    private void PersistPendingTranscribeAudioSessionForShutdown()
    {
        if (_currentSessionDocument is not null
            || _transcribeAudioWorkflow is null
            || _transcribeAudioWorkflow.HasStarted
            || _transcribeAudioWorkflow.Kind != TranscribeAudioWorkflowKind.NewFile)
        {
            return;
        }

        try
        {
            AppendLog("App shutdown detected with pending Transcribe Audio session. Persisting staged audio session before exit.");
            if (EnsureCurrentSessionForAudioFile(_transcribeAudioWorkflow.SourceAudioPath))
            {
                _transcribeAudioWorkflow.CreatedSessionId = _currentSessionDocument?.SessionId;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Shutdown persistence for pending Transcribe Audio session failed: {ex.Message}");
        }
    }

    public void SeekAudioPreview(TimeSpan position)
    {
        if (!IsAudioFileLoaded)
        {
            return;
        }

        TimeSpan clamped = position < TimeSpan.Zero
            ? TimeSpan.Zero
            : position;
        TimeSpan duration = _audioPlaybackService.Duration;

        if (duration > TimeSpan.Zero && clamped > duration)
        {
            clamped = duration;
        }

        _audioPlaybackService.Seek(clamped);
        IsAudioPlaying = _audioPlaybackService.IsPlaying;
        UpdateAudioTimelineFromPlayback();
    }

    public void RestartAudioPreviewSegment(TimeSpan position)
    {
        if (!IsAudioFileLoaded)
        {
            return;
        }

        TimeSpan clamped = position < TimeSpan.Zero
            ? TimeSpan.Zero
            : position;
        TimeSpan duration = _audioPlaybackService.Duration;

        if (duration > TimeSpan.Zero && clamped > duration)
        {
            clamped = duration;
        }

        bool wasPlaying = _audioPlaybackService.IsPlaying;

        if (wasPlaying)
        {
            _audioPlaybackService.Pause();
        }

        _audioPlaybackService.Seek(clamped);
        _audioPlaybackService.Play();
        IsAudioPlaying = _audioPlaybackService.IsPlaying;
        UpdateAudioTimelineFromPlayback();
    }

    public void EnsureAudioPreviewPaused()
    {
        if (!IsAudioFileLoaded)
        {
            return;
        }

        _audioPlaybackService.Pause();
        IsAudioPlaying = _audioPlaybackService.IsPlaying;
        UpdateAudioTimelineFromPlayback();
    }

    public void SetGenerationRunning(bool isRunning, bool isLiveTranscriptionRunning = false)
    {
        IsLiveTranscriptionRunning = isLiveTranscriptionRunning;
        IsGenerationRunning = isRunning;
        NotifyInteractionAvailabilityChanged();
    }

    public void SetPreferredLiveAudioSource(AudioInputDeviceOption option)
    {
        if (_preferredLiveAudioSourceKind == option.Kind
            && _preferredLiveAudioDeviceNumber == option.DeviceNumber)
        {
            return;
        }

        _preferredLiveAudioSourceKind = option.Kind;
        _preferredLiveAudioDeviceNumber = option.DeviceNumber;
        SaveAppPreferences();
    }

    public void SetPreferredLiveAudioGain(LiveAudioGainOptions options)
    {
        LiveAudioGainOptions validated = options.Validate();
        if (_liveAudioAutoGainEnabled == validated.IsAutomaticGainEnabled
            && Math.Abs(_liveAudioGainLevel - validated.ManualGainLevel) < 0.0001)
        {
            return;
        }

        _liveAudioAutoGainEnabled = validated.IsAutomaticGainEnabled;
        _liveAudioGainLevel = validated.ManualGainLevel;
        SaveAppPreferences();
    }

    public bool EnsureLiveTranscriptSession(string inputDeviceName)
    {
        if (_currentSessionDocument is not null)
        {
            SelectedTranscriptViewIndex = 0;
            return true;
        }

        if (!EnsureCanCreateNewSession("live transcription session"))
        {
            return false;
        }

        try
        {
            TranscriptSessionLoadResult loadResult = _sessionStore.CreateLiveSession(
                $"Live Transcription {DateTimeOffset.Now:yyyy-MM-dd HH-mm}");
            LoadSessionResult(loadResult, showAudioIssueDialog: false);
            SelectedTranscriptViewIndex = 0;
            LoadRecentSessions(loadResult.Document.SessionId, pinSelectedToTop: true);
            AppendLog($"Live transcription session created for input device: {inputDeviceName}.");
            return true;
        }
        catch (Exception ex)
        {
            RaiseError($"Unable to create a live transcription session: {ex.Message}");
            AppendLog($"Live transcription session creation failed: {ex.Message}");
            return false;
        }
    }

    public LiveRecordingSession CreateLiveRecordingSession(
        string inputDeviceName,
        TimeSpan? segmentDuration = null)
    {
        if (_currentSessionDocument is null)
        {
            throw new InvalidOperationException("A live transcript session must be created before recording can start.");
        }

        LiveRecordingSessionCreateResult result =
            _sessionStore.CreateLiveRecordingSession(_currentSessionDocument.SessionId, inputDeviceName, segmentDuration);
        _currentSessionDocument.Audio = TranscriptSessionStore.CloneAudioDocument(result.Audio);
        CurrentSessionAudioIssue = string.Empty;
        IsCurrentSessionAudioMissing = false;
        NotifyPropertyChanged(nameof(LoadedAudioFileName));
        return result.RecordingSession;
    }

    public TranscriptSessionLoadResult RefreshLiveRecordingMetadata()
    {
        if (_currentSessionDocument is null)
        {
            throw new InvalidOperationException("There is no current session to refresh.");
        }

        TranscriptSessionLoadResult loadResult = _sessionStore.UpdateLiveRecordingMetadata(_currentSessionDocument.SessionId);
        _currentSessionDocument.Audio = loadResult.Document.Audio;
        _currentSessionDocument.UpdatedUtc = loadResult.Document.UpdatedUtc;
        CurrentSessionAudioIssue = loadResult.AudioIssueMessage ?? string.Empty;
        IsCurrentSessionAudioMissing = !loadResult.AudioAvailable && !string.IsNullOrWhiteSpace(CurrentSessionAudioIssue);
        NotifyPropertyChanged(nameof(LoadedAudioFileName));
        return loadResult;
    }

    public bool LoadCurrentSessionAudioPreview()
    {
        if (_currentSessionDocument is null)
        {
            return false;
        }

        string? audioPath = _sessionStore.ResolveStoredAudioPathForPlayback(_currentSessionDocument);
        if (string.IsNullOrWhiteSpace(audioPath))
        {
            return false;
        }

        return TryLoadAudioPreview(audioPath);
    }

    public bool ConvertCurrentCompletedLiveSessionToAudioTranscriptionSession()
    {
        if (_currentSessionDocument is null || !IsCurrentSessionLiveRecordingManifest())
        {
            return false;
        }

        string? liveManifestPath = _sessionStore.ResolveStoredAudioPathForPlayback(_currentSessionDocument);
        if (string.IsNullOrWhiteSpace(liveManifestPath) || !File.Exists(liveManifestPath))
        {
            throw new InvalidOperationException("The current live session does not have recorded audio to convert.");
        }

        TranscriptSessionLoadResult loadResult = ConvertLiveSessionToImportedAudio(
            _currentSessionDocument,
            liveManifestPath);
        LoadSessionResult(loadResult, showAudioIssueDialog: true);
        LoadRecentSessions(loadResult.Document.SessionId);
        AppendLog("Completed live transcription session converted to audio transcription session.");
        return true;
    }

    public bool InitializeNewLiveTranscriptSession(string inputDeviceName)
    {
        if (!EnsureCanCreateNewSession("live transcription session"))
        {
            return false;
        }

        try
        {
            RaiseSessionLoadStarting();
            _sessionAutosaveTimer.Stop();
            if (!TrySaveCurrentSession(
                    updatedTranscriptMode: null,
                    showErrorDialog: true,
                    successLogMessage: string.Empty))
            {
                AppendLog("Live transcription session initialization aborted because the current session could not be saved.");
                return false;
            }

            ClearOutputCore(unloadAudioPreview: true, clearSessionContext: true);

            TranscriptSessionLoadResult loadResult = _sessionStore.CreateLiveSession(
                $"Live Transcription {DateTimeOffset.Now:yyyy-MM-dd HH-mm}");
            LoadSessionResult(loadResult, showAudioIssueDialog: false);
            SelectedTranscriptViewIndex = 0;
            LoadRecentSessions(loadResult.Document.SessionId, pinSelectedToTop: true);
            AppendLog($"New live transcription session created for input device: {inputDeviceName}.");
            return true;
        }
        catch (Exception ex)
        {
            RaiseError($"Unable to create a live transcription session: {ex.Message}");
            AppendLog($"Live transcription session creation failed: {ex.Message}");
            return false;
        }
    }

    public void SaveLiveTranscriptSession()
    {
        TrySaveCurrentSession(
            updatedTranscriptMode: TranscriptGenerationMode.Live,
            showErrorDialog: false,
            successLogMessage: "Live transcription session saved.");
        if (_currentSessionDocument is not null)
        {
            LoadRecentSessions(_currentSessionDocument.SessionId);
        }
    }

    public int AppendLiveTranscriptionResult(TranscriptionResult result)
    {
        ClearLiveInterimTranscriptionBlock();
        IReadOnlyList<TranscriptionTimedLine> timedLines = result.TimedLines
            ?.Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .OrderBy(line => line.StartOffset)
            .ToArray()
            ?? Array.Empty<TranscriptionTimedLine>();
        if (timedLines.Count == 0)
        {
            AppendLog("Live segment transcription produced no transcript rows.");
            return 0;
        }

        _suppressSessionAutosave = true;
        int addedCount = 0;
        try
        {
            var recentLines = new List<FinalizedTranscriptLineViewModel>(
                FinalizedTranscriptLines
                    .Where(line => !line.IsProvisional && !string.IsNullOrWhiteSpace(line.Text))
                    .TakeLast(6));
            for (int index = 0; index < timedLines.Count; index++)
            {
                TranscriptionTimedLine timedLine = timedLines[index];
                TranscriptionTimedLine adjustedLine = TrimBoundaryOverlapWithPreviousLine(timedLine, recentLines);
                TranscriptionTimedLine? nextTimedLine = index + 1 < timedLines.Count
                    ? timedLines[index + 1]
                    : null;
                adjustedLine = SanitizeLiveBoundaryLine(adjustedLine, nextTimedLine);
                if (string.IsNullOrWhiteSpace(adjustedLine.Text))
                {
                    continue;
                }

                if (IsDuplicateLiveSegmentBoundaryLine(adjustedLine))
                {
                    AppendLog(
                        $"Skipped duplicate live segment transcript row at {FormatOffset(adjustedLine.StartOffset)}: " +
                        $"'{BuildPreview(adjustedLine.Text)}'.");
                    continue;
                }

                var line = new FinalizedTranscriptLineViewModel(
                    startOffset: adjustedLine.StartOffset,
                    endOffset: adjustedLine.EndOffset,
                    isTimestampEstimated: adjustedLine.IsTimestampEstimated,
                    text: adjustedLine.Text.Trim());
                line.PropertyChanged += OnFinalizedLinePropertyChanged;
                FinalizedTranscriptLines.Add(line);
                recentLines.Add(line);
                addedCount++;
            }

            if (addedCount == 0)
            {
                AppendLog("Live segment transcription produced only duplicate transcript rows.");
                return 0;
            }

            ConsolidateRecentLiveTranscriptRows(Math.Max(0, FinalizedTranscriptLines.Count - (addedCount + 8)));
            RebuildFinalizedTextFromLines();
        }
        finally
        {
            _suppressSessionAutosave = false;
        }

        SelectedTranscriptViewIndex = 0;
        NotifyCurrentTranscriptStateChanged();
        ScheduleSessionAutosave();
        return addedCount;
    }

    private void ConsolidateRecentLiveTranscriptRows(int startIndex)
    {
        int index = Math.Max(1, startIndex);
        while (index < FinalizedTranscriptLines.Count)
        {
            FinalizedTranscriptLineViewModel previous = FinalizedTranscriptLines[index - 1];
            FinalizedTranscriptLineViewModel current = FinalizedTranscriptLines[index];
            string previousText = previous.Text?.Trim() ?? string.Empty;
            string currentText = current.Text?.Trim() ?? string.Empty;
            if (previousText.Length == 0 || currentText.Length == 0)
            {
                index++;
                continue;
            }

            if (TryReattachLeadingBoundaryFragment(previousText, currentText, out string reattachedPreviousText, out string reattachedCurrentText))
            {
                previous.Text = reattachedPreviousText;
                previousText = reattachedPreviousText;
                if (reattachedCurrentText.Length == 0)
                {
                    RemoveFinalizedTranscriptLineAt(index);
                    AppendLog(
                        $"Reattached entire leading boundary fragment at {FormatOffset(previous.StartOffset ?? TimeSpan.Zero)}: " +
                        $"'{BuildPreview(previousText)}'.");
                    continue;
                }

                current.Text = reattachedCurrentText;
                currentText = reattachedCurrentText;
                AppendLog(
                    $"Reattached boundary fragment at {FormatOffset(previous.StartOffset ?? TimeSpan.Zero)}: " +
                    $"'{BuildPreview(previousText)}' | '{BuildPreview(currentText)}'.");
            }

            if (index >= 2)
            {
                string earlierText = FinalizedTranscriptLines[index - 2].Text?.Trim() ?? string.Empty;
                if (ShouldDropRepeatedQuestionCarryover(earlierText, previousText, currentText))
                {
                    RemoveFinalizedTranscriptLineAt(index);
                    AppendLog(
                        $"Dropped repeated question carryover at {FormatOffset(current.StartOffset ?? TimeSpan.Zero)}: " +
                        $"'{BuildPreview(currentText)}'.");
                    continue;
                }
            }

            if (ShouldForceMergeShortLeadingFragment(previousText, currentText, out string forcedMergedText))
            {
                previous.Text = forcedMergedText;
                previous.SetTimelineOffsets(previous.StartOffset, current.EndOffset);
                RemoveFinalizedTranscriptLineAt(index);
                AppendLog(
                    $"Force-merged short leading fragment at {FormatOffset(previous.StartOffset ?? TimeSpan.Zero)}: " +
                    $"'{BuildPreview(previous.Text)}'.");
                continue;
            }

            if (IsSeverelyMalformedLiveBoundaryText(currentText))
            {
                RemoveFinalizedTranscriptLineAt(index);
                AppendLog(
                    $"Dropped malformed live row at {FormatOffset(current.StartOffset ?? TimeSpan.Zero)}: " +
                    $"'{BuildPreview(currentText)}'.");
                continue;
            }

            string normalizedPrevious = NormalizeLiveSegmentText(previousText);
            string normalizedCurrent = NormalizeLiveSegmentText(currentText);
            int overlap = CountSuffixPrefixTokenOverlap(normalizedPrevious, normalizedCurrent);
            if (overlap >= 2)
            {
                string[] currentTokens = SplitLiveBoundaryTokens(currentText);
                if (overlap < currentTokens.Length)
                {
                    string trimmedCurrent = string.Join(" ", currentTokens.Skip(overlap)).Trim();
                    if (trimmedCurrent.Length > 0)
                    {
                        current.Text = trimmedCurrent;
                        currentText = trimmedCurrent;
                        normalizedCurrent = NormalizeLiveSegmentText(trimmedCurrent);
                        AppendLog(
                            $"Trimmed repeated adjacent phrase at {FormatOffset(current.StartOffset ?? TimeSpan.Zero)}: " +
                            $"'{BuildPreview(trimmedCurrent)}'.");
                    }
                    else
                    {
                        RemoveFinalizedTranscriptLineAt(index);
                        AppendLog(
                            $"Dropped duplicate adjacent live row at {FormatOffset(current.StartOffset ?? TimeSpan.Zero)}: " +
                            $"'{BuildPreview(currentText)}'.");
                        continue;
                    }
                }
            }

            bool previousContainedByCurrent = IsShortContainedBoundaryFragment(normalizedPrevious, normalizedCurrent);
            if (previousContainedByCurrent)
            {
                RemoveFinalizedTranscriptLineAt(index - 1);
                AppendLog(
                    $"Dropped contained previous live row at {FormatOffset(previous.StartOffset ?? TimeSpan.Zero)}: " +
                    $"'{BuildPreview(previousText)}'.");
                index = Math.Max(1, index - 1);
                continue;
            }

            bool currentContainedByPrevious = IsShortContainedBoundaryFragment(normalizedCurrent, normalizedPrevious);
            if (currentContainedByPrevious)
            {
                RemoveFinalizedTranscriptLineAt(index);
                AppendLog(
                    $"Dropped contained current live row at {FormatOffset(current.StartOffset ?? TimeSpan.Zero)}: " +
                    $"'{BuildPreview(currentText)}'.");
                continue;
            }

            if (ShouldMergeAdjacentLiveRows(previousText, currentText))
            {
                string mergedText = $"{previousText} {currentText}".Trim();
                if (!ShouldKeepMergedLiveRow(previousText, currentText, mergedText))
                {
                    index++;
                    continue;
                }

                previous.Text = mergedText;
                previous.SetTimelineOffsets(previous.StartOffset, current.EndOffset);
                RemoveFinalizedTranscriptLineAt(index);
                AppendLog(
                    $"Merged adjacent live rows at {FormatOffset(previous.StartOffset ?? TimeSpan.Zero)}: " +
                    $"'{BuildPreview(previous.Text)}'.");
                continue;
            }

            index++;
        }
    }

    private TranscriptionTimedLine SanitizeLiveBoundaryLine(
        TranscriptionTimedLine candidate,
        TranscriptionTimedLine? nextCandidate)
    {
        string text = candidate.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return candidate;
        }

        string normalizedText = NormalizeLiveBoundaryText(text);
        if (!string.Equals(normalizedText, text, StringComparison.Ordinal))
        {
            text = normalizedText;
            candidate = new TranscriptionTimedLine(
                text,
                candidate.StartOffset,
                candidate.EndOffset,
                candidate.IsTimestampEstimated);
        }

        if (IsPlaceholderLiveBoundaryText(text))
        {
            AppendLog(
                $"Dropped placeholder live transcript row at {FormatOffset(candidate.StartOffset)}: " +
                $"'{BuildPreview(text)}'.");
            return new TranscriptionTimedLine(
                string.Empty,
                candidate.StartOffset,
                candidate.EndOffset,
                candidate.IsTimestampEstimated);
        }

        if (IsSeverelyMalformedLiveBoundaryText(text))
        {
            AppendLog(
                $"Dropped malformed live transcript row at {FormatOffset(candidate.StartOffset)}: " +
                $"'{BuildPreview(text)}'.");
            return new TranscriptionTimedLine(
                string.Empty,
                candidate.StartOffset,
                candidate.EndOffset,
                candidate.IsTimestampEstimated);
        }

        if (nextCandidate is null || string.IsNullOrWhiteSpace(nextCandidate.Text))
        {
            return candidate;
        }

        string normalizedCandidate = NormalizeLiveSegmentText(text);
        string normalizedNext = NormalizeLiveSegmentText(nextCandidate.Text);
        int candidateWordCount = CountWords(normalizedCandidate);
        if (candidateWordCount > 0
            && candidateWordCount <= 6
            && ContainsWholeNormalizedPhrase(normalizedNext, normalizedCandidate))
        {
            AppendLog(
                $"Dropped contained live boundary fragment at {FormatOffset(candidate.StartOffset)}: " +
                $"'{BuildPreview(text)}'.");
            return new TranscriptionTimedLine(
                string.Empty,
                candidate.StartOffset,
                candidate.EndOffset,
                candidate.IsTimestampEstimated);
        }

        int overlapWithNext = CountSuffixPrefixTokenOverlap(normalizedCandidate, normalizedNext);
        if (candidateWordCount > 0
            && candidateWordCount <= 7
            && overlapWithNext >= 3
            && overlapWithNext * 10 >= candidateWordCount * 7)
        {
            AppendLog(
                $"Dropped repeated live boundary fragment at {FormatOffset(candidate.StartOffset)}: " +
                $"'{BuildPreview(text)}'.");
            return new TranscriptionTimedLine(
                string.Empty,
                candidate.StartOffset,
                candidate.EndOffset,
                candidate.IsTimestampEstimated);
        }

        string trimmedTrailingStem = TrimTrailingStemFragment(text, nextCandidate.Text);
        if (!string.Equals(trimmedTrailingStem, text, StringComparison.Ordinal))
        {
            AppendLog(
                $"Trimmed trailing live boundary stem at {FormatOffset(candidate.StartOffset)}: " +
                $"'{BuildPreview(trimmedTrailingStem)}'.");
            return new TranscriptionTimedLine(
                trimmedTrailingStem,
                candidate.StartOffset,
                candidate.EndOffset,
                candidate.IsTimestampEstimated);
        }

        return candidate;
    }

    private static string NormalizeLiveToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(token.Length);
        foreach (char value in token.ToLowerInvariant())
        {
            if (char.IsPunctuation(value) || char.IsSymbol(value) || char.IsWhiteSpace(value))
            {
                continue;
            }

            builder.Append(value);
        }

        return builder.ToString();
    }

    public void UpsertLiveInterimTranscriptionBlock(
        string text,
        int sequenceIndex,
        TimeSpan startOffset,
        TimeSpan endOffset)
    {
        if (sequenceIndex < _activeLiveInterimSequenceIndex)
        {
            return;
        }

        string normalized = text?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            return;
        }

        ClearLiveInterimTranscriptionBlock();

        string[] lines = SplitLiveInterimRows(normalized);
        if (lines.Length == 0)
        {
            return;
        }

        TimeSpan safeEnd = endOffset >= startOffset
            ? endOffset
            : startOffset;
        TimeSpan total = safeEnd - startOffset;
        TimeSpan perRow = lines.Length > 0
            ? TimeSpan.FromTicks(total.Ticks / lines.Length)
            : TimeSpan.Zero;

        for (int index = 0; index < lines.Length; index++)
        {
            TimeSpan rowStart = startOffset + TimeSpan.FromTicks(perRow.Ticks * index);
            TimeSpan rowEnd = index == lines.Length - 1
                ? safeEnd
                : rowStart + perRow;

            var line = new FinalizedTranscriptLineViewModel(
                rowStart,
                rowEnd,
                isTimestampEstimated: true,
                text: lines[index],
                isTranscriptionPartial: true,
                isProvisional: true);
            line.PropertyChanged += OnFinalizedLinePropertyChanged;
            FinalizedTranscriptLines.Add(line);
        }

        _activeLiveInterimSequenceIndex = sequenceIndex;
        RebuildFinalizedTextFromLines();
        SelectedTranscriptViewIndex = 0;
        NotifyCurrentTranscriptStateChanged();
    }

    public void ClearLiveInterimTranscriptionBlock()
    {
        List<FinalizedTranscriptLineViewModel> provisional = FinalizedTranscriptLines
            .Where(line => line.IsProvisional)
            .ToList();
        foreach (FinalizedTranscriptLineViewModel line in provisional)
        {
            line.PropertyChanged -= OnFinalizedLinePropertyChanged;
            _ = FinalizedTranscriptLines.Remove(line);
        }

        _activeLiveInterimSequenceIndex = -1;
        RebuildFinalizedTextFromLines();
        NotifyCurrentTranscriptStateChanged();
    }

    private static string[] SplitLiveInterimRows(string text)
    {
        string[] rawLines = text
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .ToArray();
        if (rawLines.Length == 0)
        {
            return Array.Empty<string>();
        }

        var output = new List<string>();
        foreach (string rawLine in rawLines)
        {
            string[] sentencePieces = rawLine
                .Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries)
                .Select(piece => piece.Trim())
                .Where(piece => piece.Length > 0)
                .ToArray();

            if (sentencePieces.Length > 1)
            {
                output.AddRange(sentencePieces);
                continue;
            }

            string[] words = rawLine
                .Split([' '], StringSplitOptions.RemoveEmptyEntries)
                .ToArray();
            const int minWordsForChunking = 14;
            if (words.Length <= minWordsForChunking)
            {
                output.Add(rawLine);
                continue;
            }

            const int wordsPerChunk = 10;
            for (int index = 0; index < words.Length; index += wordsPerChunk)
            {
                string chunk = string.Join(" ", words.Skip(index).Take(wordsPerChunk)).Trim();
                if (chunk.Length > 0)
                {
                    output.Add(chunk);
                }
            }
        }

        return output.ToArray();
    }

    private bool IsDuplicateLiveSegmentBoundaryLine(TranscriptionTimedLine candidate)
    {
        string candidateText = NormalizeLiveSegmentText(candidate.Text);
        if (candidateText.Length == 0)
        {
            return false;
        }

        TimeSpan candidateStart = candidate.StartOffset;
        TimeSpan candidateEnd = ResolveLiveSegmentEnd(candidate.StartOffset, candidate.EndOffset);
        string[] candidateTokens = SplitLiveBoundaryTokens(candidate.Text);
        foreach (FinalizedTranscriptLineViewModel existing in EnumerateRecentBoundaryLines())
        {
            if (existing.StartOffset is not TimeSpan existingStart)
            {
                continue;
            }

            TimeSpan existingEnd = ResolveLiveSegmentEnd(existingStart, existing.EndOffset);
            bool overlapsOrTouches = RangesOverlapOrTouch(existingStart, existingEnd, candidateStart, candidateEnd);
            if (!overlapsOrTouches)
            {
                TimeSpan gap = candidateStart >= existingEnd
                    ? candidateStart - existingEnd
                    : existingStart - candidateEnd;
                if (gap > LiveBoundaryTrimProximityTolerance)
                {
                    continue;
                }
            }

            string existingText = NormalizeLiveSegmentText(existing.Text);
            if (string.Equals(existingText, candidateText, StringComparison.Ordinal))
            {
                return true;
            }

            bool containedFragment = ContainsWholeNormalizedPhrase(existingText, candidateText)
                && CountWords(candidateText) <= 10;
            if (containedFragment)
            {
                return true;
            }

            int candidateWordCount = CountWords(candidateText);
            int suffixPrefixOverlap = CountSuffixPrefixTokenOverlap(existingText, candidateText);
            int suffixPrefixOverlapWithoutLeadingFiller = CountSuffixPrefixTokenOverlapWithoutLeadingFiller(existingText, candidateText);
            int compactTokenOverlap = CountBoundaryPrefixTokenOverlap(
                SplitLiveBoundaryTokens(existing.Text),
                candidateTokens);
            int fuzzyTokenOverlap = CountBoundaryPrefixTokenOverlapAllowingSingleMismatch(
                SplitLiveBoundaryTokens(existing.Text),
                candidateTokens);
            int bestOverlap = Math.Max(
                Math.Max(Math.Max(suffixPrefixOverlap, suffixPrefixOverlapWithoutLeadingFiller), compactTokenOverlap),
                fuzzyTokenOverlap);
            bool isMostlyOverlapFragment = candidateWordCount > 0
                && bestOverlap >= 3
                && bestOverlap * 10 >= candidateWordCount * 7;
            if (isMostlyOverlapFragment)
            {
                return true;
            }
        }

        return false;
    }

    private static bool RangesOverlapOrTouch(
        TimeSpan firstStart,
        TimeSpan firstEnd,
        TimeSpan secondStart,
        TimeSpan secondEnd)
    {
        return secondStart <= firstEnd
            && firstStart <= secondEnd;
    }

    private static TimeSpan ResolveLiveSegmentEnd(TimeSpan start, TimeSpan? end)
    {
        return end is TimeSpan resolvedEnd && resolvedEnd > start
            ? resolvedEnd
            : start;
    }

    private static string NormalizeLiveSegmentText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        bool previousWasWhitespace = false;
        foreach (char value in text.Trim().ToLowerInvariant())
        {
            if (char.IsWhiteSpace(value))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                    previousWasWhitespace = true;
                }

                continue;
            }

            if (char.IsPunctuation(value) || char.IsSymbol(value))
            {
                continue;
            }

            builder.Append(value);
            previousWasWhitespace = false;
        }

        return builder.ToString().Trim();
    }

    private static bool IsPlaceholderLiveBoundaryText(string text)
    {
        string normalized = NormalizeLiveSegmentText(text).Replace(" ", string.Empty, StringComparison.Ordinal);
        return normalized is "blankaudio" or "silence";
    }

    private static bool IsShortContainedBoundaryFragment(string candidate, string container)
    {
        if (candidate.Length == 0 || container.Length == 0)
        {
            return false;
        }

        int candidateWordCount = CountWords(candidate);
        return candidateWordCount > 0
            && candidateWordCount <= 6
            && ContainsWholeNormalizedPhrase(container, candidate);
    }

    private static bool ShouldMergeAdjacentLiveRows(string previousText, string currentText)
    {
        string normalizedPrevious = NormalizeLiveSegmentText(previousText);
        string normalizedCurrent = NormalizeLiveSegmentText(currentText);
        int previousWords = CountWords(normalizedPrevious);
        int currentWords = CountWords(normalizedCurrent);
        if (previousWords == 0 || currentWords == 0)
        {
            return false;
        }

        bool previousLooksFragment = previousWords <= 6;
        bool previousEndsOpen = !EndsWithTerminalPunctuation(previousText);
        bool previousLooksIncomplete = previousLooksFragment
            || EndsWithContinuationWord(previousText)
            || (previousEndsOpen && previousWords <= 10);
        bool currentLooksContinuation = StartsWithContinuationWord(currentText)
            || char.IsLower(currentText.TrimStart()[0])
            || currentWords <= 4;

        return previousLooksIncomplete && currentLooksContinuation;
    }

    private static bool ShouldKeepMergedLiveRow(string previousText, string currentText, string mergedText)
    {
        if (IsSeverelyMalformedLiveBoundaryText(mergedText))
        {
            return false;
        }

        string normalizedMerged = NormalizeLiveSegmentText(mergedText);
        int mergedWords = CountWords(normalizedMerged);
        if (mergedWords > 22 || mergedText.Length > 160)
        {
            return false;
        }

        if (EndsWithTerminalPunctuation(previousText) && !StartsWithContinuationWord(currentText))
        {
            return false;
        }

        return true;
    }

    private static string NormalizeLiveBoundaryText(string text)
    {
        string normalized = text.Trim();
        if (normalized.Length == 0)
        {
            return normalized;
        }

        normalized = TrimWeakLeadingDash(normalized);
        normalized = TrimLeadingDiscourseFiller(normalized);
        normalized = RepairMalformedBoundaryPhrase(normalized);
        return normalized.Trim();
    }

    private static string TrimWeakLeadingDash(string text)
    {
        string trimmed = text.TrimStart();
        if (!trimmed.StartsWith("-", StringComparison.Ordinal))
        {
            return text;
        }

        string remainder = trimmed[1..].TrimStart();
        return remainder.Length > 0 ? remainder : string.Empty;
    }

    private static string TrimLeadingDiscourseFiller(string text)
    {
        string normalized = NormalizeLiveSegmentText(text);
        int wordCount = CountWords(normalized);
        if (wordCount < 7)
        {
            return text;
        }

        string[] patterns =
        [
            "and, you know, ",
            "and you know "
        ];

        foreach (string pattern in patterns)
        {
            if (!text.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string remainder = text[pattern.Length..].TrimStart();
            return remainder.Length > 0 ? CapitalizeFirstLetter(remainder) : string.Empty;
        }

        return text;
    }

    private static string RepairMalformedBoundaryPhrase(string text)
    {
        string repaired = text;
        repaired = Regex.Replace(repaired, @"\s*--\s*", " ", RegexOptions.CultureInvariant);
        repaired = ReplaceWholeWordPhrase(repaired, "like love that", "love that");
        repaired = ReplaceWholeWordPhrase(repaired, "it started the entire industry of the year", "that started the entire industry");
        repaired = ReplaceWholeWordPhrase(repaired, "it started the entire industry", "that started the entire industry");
        repaired = ReplaceWholeWordPhrase(repaired, "entire engine", "entire industry");
        repaired = ReplaceWholeWordPhrase(repaired, "the entire end", "the entire industry");
        repaired = ReplaceWholeWordPhrase(repaired, "entire end", "entire industry");
        repaired = ReplaceWholeWordPhrase(repaired, "this. but", "this but");
        repaired = ReplaceWholeWordPhrase(repaired, "and it was brilliant", "it was brilliant");
        repaired = ReplaceWholeWordPhrase(repaired, "he was the best of a lifetime", "he was the teacher of a lifetime");
        return repaired;
    }

    private static string ReplaceWholeWordPhrase(string text, string target, string replacement)
    {
        if (text.Length == 0)
        {
            return text;
        }

        string pattern = $@"\b{Regex.Escape(target)}\b";
        return Regex.Replace(text, pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string CapitalizeFirstLetter(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        int index = 0;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        if (index >= text.Length || !char.IsLetter(text[index]))
        {
            return text;
        }

        char upper = char.ToUpperInvariant(text[index]);
        if (upper == text[index])
        {
            return text;
        }

        string prefix = text[..index];
        string suffix = index + 1 < text.Length ? text[(index + 1)..] : string.Empty;
        return prefix + upper + suffix;
    }

    private static bool EndsWithContinuationWord(string text)
    {
        string[] tokens = SplitLiveBoundaryTokens(text);
        if (tokens.Length == 0)
        {
            return false;
        }

        string trailing = NormalizeLiveToken(tokens[^1]);
        return trailing is "a" or "an" or "the" or "of" or "to" or "and" or "or" or "but" or "with" or "in" or "on";
    }

    private static bool StartsWithContinuationWord(string text)
    {
        string[] tokens = SplitLiveBoundaryTokens(text);
        if (tokens.Length == 0)
        {
            return false;
        }

        string leading = NormalizeLiveToken(tokens[0]);
        return leading is "and" or "or" or "but" or "because" or "that" or "which" or "who" or "whom" or "whose" or "where" or "when" or "with" or "to" or "of" or "in" or "on" or "for";
    }

    private static bool EndsWithTerminalPunctuation(string text)
    {
        string trimmed = text.TrimEnd();
        if (trimmed.Length == 0)
        {
            return false;
        }

        char last = trimmed[^1];
        return last is '.' or '!' or '?' or ':' or ';';
    }

    private static bool IsSeverelyMalformedLiveBoundaryText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] tokens = SplitLiveBoundaryTokens(text);
        if (tokens.Length == 0)
        {
            return false;
        }

        if (HasRepeatedFunctionWord(tokens))
        {
            return true;
        }

        if (LooksLikeWeakBoundaryFragment(tokens))
        {
            return true;
        }

        if (HasUnexpectedLowercaseAfterSentenceBreak(text))
        {
            return true;
        }

        return HasUnexpectedCapitalizedContinuation(tokens);
    }

    private static bool LooksLikeWeakBoundaryFragment(string[] tokens)
    {
        if (tokens.Length == 0)
        {
            return false;
        }

        if (tokens.Length <= 4 && tokens[0].StartsWith("-", StringComparison.Ordinal))
        {
            return true;
        }

        string normalizedText = NormalizeLiveSegmentText(string.Join(" ", tokens));
        return normalizedText is "people have forgotten this"
            or "was missing at the time"
            or "you know ive never"
            or "but you know ive never"
            or "a it was a really";
    }


    private static bool ShouldDropRepeatedQuestionCarryover(string earlierText, string previousText, string currentText)
    {
        if (earlierText.Length == 0 || previousText.Length == 0 || currentText.Length == 0)
        {
            return false;
        }

        string normalizedEarlier = NormalizeLiveSegmentText(earlierText);
        string normalizedCurrent = NormalizeLiveSegmentText(currentText);
        if (!normalizedEarlier.Equals(normalizedCurrent, StringComparison.Ordinal))
        {
            return false;
        }

        if (!currentText.TrimEnd().EndsWith("?", StringComparison.Ordinal))
        {
            return false;
        }

        string normalizedPrevious = NormalizeLiveSegmentText(previousText);
        int previousWordCount = CountWords(normalizedPrevious);
        return previousWordCount > 0
            && previousWordCount <= 4
            && (EndsWithContinuationWord(previousText) || !EndsWithTerminalPunctuation(previousText));
    }

    private static bool ShouldForceMergeShortLeadingFragment(string previousText, string currentText, out string mergedText)
    {
        mergedText = string.Empty;
        if (previousText.Length == 0 || currentText.Length == 0)
        {
            return false;
        }

        string normalizedPrevious = NormalizeLiveSegmentText(previousText);
        if (CountWords(normalizedPrevious) is 0 or > 4)
        {
            return false;
        }

        if (!EndsWithContinuationWord(previousText))
        {
            return false;
        }

        string trimmedCurrent = currentText.TrimStart();
        if (trimmedCurrent.Length == 0 || !char.IsLower(trimmedCurrent[0]))
        {
            return false;
        }

        mergedText = RepairMalformedBoundaryPhrase($"{previousText} {currentText}".Trim());
        return true;
    }

    private static bool TryReattachLeadingBoundaryFragment(
        string previousText,
        string currentText,
        out string updatedPreviousText,
        out string updatedCurrentText)
    {
        updatedPreviousText = previousText;
        updatedCurrentText = currentText;

        if (previousText.Length == 0 || currentText.Length == 0)
        {
            return false;
        }

        if (!TrySplitLeadingSentenceFragment(currentText, out string leadingFragment, out string remainingText))
        {
            return false;
        }

        string normalizedLeading = NormalizeLiveSegmentText(leadingFragment);
        int leadingWords = CountWords(normalizedLeading);
        if (leadingWords == 0 || leadingWords > 4)
        {
            return false;
        }

        bool previousOpenEnded = !EndsWithTerminalPunctuation(previousText) || EndsWithContinuationWord(previousText);
        bool leadingLooksContinuation = StartsWithContinuationWord(leadingFragment) || char.IsLower(leadingFragment.TrimStart()[0]);
        if (!previousOpenEnded && !leadingLooksContinuation)
        {
            return false;
        }

        string mergedPrevious = $"{previousText.TrimEnd('.', '!', '?', ';', ':')} {leadingFragment}".Trim();
        mergedPrevious = RepairMalformedBoundaryPhrase(mergedPrevious);
        updatedPreviousText = mergedPrevious;
        updatedCurrentText = remainingText.Trim();
        return true;
    }

    private static bool TrySplitLeadingSentenceFragment(
        string text,
        out string leadingFragment,
        out string remainingText)
    {
        leadingFragment = string.Empty;
        remainingText = text;

        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        int boundaryIndex = trimmed.IndexOfAny(['.', '!', '?']);
        if (boundaryIndex <= 0 || boundaryIndex >= trimmed.Length - 1)
        {
            return false;
        }

        leadingFragment = trimmed[..(boundaryIndex + 1)].Trim();
        remainingText = trimmed[(boundaryIndex + 1)..].Trim();
        return remainingText.Length > 0;
    }

    private static bool HasRepeatedFunctionWord(string[] tokens)
    {
        for (int index = 1; index < tokens.Length; index++)
        {
            string previous = NormalizeLiveToken(tokens[index - 1]);
            string current = NormalizeLiveToken(tokens[index]);
            if (previous.Length == 0 || current.Length == 0)
            {
                continue;
            }

            if (string.Equals(previous, current, StringComparison.Ordinal)
                && previous is "a" or "an" or "the" or "and" or "or" or "but" or "to" or "of" or "in" or "on")
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasUnexpectedLowercaseAfterSentenceBreak(string text)
    {
        for (int index = 1; index < text.Length; index++)
        {
            if (text[index - 1] is not '.' and not '!' and not '?')
            {
                continue;
            }

            int cursor = index;
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
            {
                cursor++;
            }

            if (cursor < text.Length && char.IsLower(text[cursor]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasUnexpectedCapitalizedContinuation(string[] tokens)
    {
        for (int index = 1; index < tokens.Length; index++)
        {
            string token = tokens[index].Trim();
            if (token.Length == 0 || !char.IsUpper(token[0]))
            {
                continue;
            }

            string normalized = NormalizeLiveToken(token);
            if (normalized is not "and" and not "but" and not "or" and not "because" and not "that")
            {
                continue;
            }

            string previousToken = tokens[index - 1].TrimEnd();
            if (previousToken.Length == 0)
            {
                continue;
            }

            char lastPreviousChar = previousToken[^1];
            if (lastPreviousChar is not '.' and not '!' and not '?' and not ':' and not ';')
            {
                return true;
            }
        }

        return false;
    }

    private void RemoveFinalizedTranscriptLineAt(int index)
    {
        FinalizedTranscriptLineViewModel line = FinalizedTranscriptLines[index];
        line.PropertyChanged -= OnFinalizedLinePropertyChanged;
        FinalizedTranscriptLines.RemoveAt(index);
    }

    private static string TrimTrailingStemFragment(string currentText, string nextText)
    {
        string[] currentWords = currentText
            .Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (currentWords.Length == 0)
        {
            return currentText;
        }

        string[] nextWords = nextText
            .Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (nextWords.Length == 0)
        {
            return currentText;
        }

        string trailing = NormalizeLiveToken(currentWords[^1]);
        if (trailing.Length < 4)
        {
            return currentText;
        }

        foreach (string nextWord in nextWords.Take(2))
        {
            string normalizedNextWord = NormalizeLiveToken(nextWord);
            if (normalizedNextWord.Length <= trailing.Length)
            {
                continue;
            }

            if (normalizedNextWord.StartsWith(trailing, StringComparison.Ordinal))
            {
                return string.Join(" ", currentWords.Take(currentWords.Length - 1)).TrimEnd();
            }
        }

        return currentText;
    }

    private static string FormatOffset(TimeSpan offset)
    {
        return offset.ToString(offset.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
    }

    private static string BuildPreview(string? text)
    {
        string trimmed = text?.Trim() ?? string.Empty;
        return trimmed.Length <= 80 ? trimmed : $"{trimmed[..77]}...";
    }

    public void DeleteCurrentLiveSessionIfEmpty()
    {
        if (_currentSessionDocument is null)
        {
            return;
        }

        bool isLiveSession = string.Equals(
            _currentSessionDocument.Audio?.OriginalFileName,
            TranscriptSessionStore.LiveSessionAudioName,
            StringComparison.OrdinalIgnoreCase);
        if (!isLiveSession)
        {
            return;
        }

        bool hasInMemoryTranscriptEntries =
            FinalizedTranscriptLines.Count > 0
            || !string.IsNullOrWhiteSpace(FinalizedText);
        bool hasPersistedTranscriptEntries =
            _currentSessionDocument.Transcript.Lines.Count > 0
            || !string.IsNullOrWhiteSpace(_currentSessionDocument.Transcript.FinalText);
        bool hasRecordedAudio =
            string.Equals(
                _currentSessionDocument.Audio?.StorageKind,
                AudioStorageKinds.LiveRecordingManifest,
                StringComparison.OrdinalIgnoreCase)
            && (_currentSessionDocument.Audio?.FileSizeBytes > 0
                || _currentSessionDocument.Audio?.DurationSeconds > 0);
        if (hasInMemoryTranscriptEntries || hasPersistedTranscriptEntries || hasRecordedAudio)
        {
            return;
        }

        string sessionId = _currentSessionDocument.SessionId;
        string displayName = CurrentSessionDisplayName;

        try
        {
            _sessionAutosaveTimer.Stop();
            _sessionStore.DeleteSession(sessionId);
            ClearCurrentSessionAfterDeletion();
            LoadRecentSessions(selectSessionId: null);
            AppendLog($"Empty live transcription session discarded: {displayName}.");
        }
        catch (Exception ex)
        {
            AppendLog($"Empty live transcription session cleanup failed: {ex.Message}");
        }
    }

    public bool TryPrepareTranscribeAudioWorkflow(bool forceRestart = false)
    {
        if (!IsAudioFileLoaded)
        {
            AppendLog("Transcribe Audio aborted: no audio file is loaded in preview.");
            return false;
        }

        if (_transcribeAudioWorkflow is not null)
        {
            AppendLog("Transcribe Audio preparation ignored: a workflow is already active.");
            return false;
        }

        string sourcePath = string.IsNullOrWhiteSpace(_pendingImportedAudioFilePath)
            ? LoadedAudioFilePath
            : _pendingImportedAudioFilePath;
        _pendingTranscribeAudioResume = false;

        if (_currentSessionDocument is null)
        {
            _transcribeAudioWorkflow = new TranscribeAudioWorkflowState(
                TranscribeAudioWorkflowKind.NewFile,
                sourcePath,
                backupDocument: null,
                resumeRequested: false,
                forceRestartRequested: false);
            AppendLog("Transcribe Audio prepared for a new audio file.");
            return true;
        }

        if (forceRestart)
        {
            _transcribeAudioWorkflow = new TranscribeAudioWorkflowState(
                TranscribeAudioWorkflowKind.ExistingSession,
                sourcePath,
                backupDocument: null,
                resumeRequested: false,
                forceRestartRequested: true);
            AppendLog("Transcribe Audio prepared for the current session restart.");
            return true;
        }

        if (TryConfirmTranscribeAudioResumeChoice(sourcePath, out bool shouldResume))
        {
            _pendingTranscribeAudioResume = shouldResume;
            if (shouldResume)
            {
                _transcribeAudioWorkflow = new TranscribeAudioWorkflowState(
                    TranscribeAudioWorkflowKind.ExistingSession,
                    sourcePath,
                    backupDocument: null,
                    resumeRequested: true,
                    forceRestartRequested: false);
                AppendLog("Transcribe Audio prepared to resume the current session.");
                return true;
            }
        }

        bool hasExistingTranscript = HasExistingTranscriptContent(TranscriptGenerationMode.TranscribeAudio);
        if (hasExistingTranscript)
        {
            if (!ConfirmTranscriptReplacement(
                operationName: "Transcribe Audio"))
            {
                return false;
            }
        }

        TranscriptSessionDocument? backupDocument = hasExistingTranscript
            ? CreateSessionSaveSnapshot(updatedTranscriptMode: null)
            : null;

        if (hasExistingTranscript)
        {
            ResetCurrentSessionTranscriptState(TranscriptGenerationMode.TranscribeAudio);
            ClearTranscriptAndLogs(unloadAudioPreview: false, transcriptMode: TranscriptGenerationMode.TranscribeAudio);

            if (!TrySaveCurrentSession(
                    updatedTranscriptMode: null,
                    showErrorDialog: true,
                    successLogMessage: "Existing transcript cleared before Transcribe Audio."))
            {
                if (backupDocument is not null)
                {
                    RestoreTranscribeAudioBackup(backupDocument, saveRestoredSession: false);
                }

                AppendLog("Transcribe Audio aborted: existing transcript could not be cleared safely.");
                return false;
            }
        }

        _transcribeAudioWorkflow = new TranscribeAudioWorkflowState(
            TranscribeAudioWorkflowKind.ExistingSession,
            sourcePath,
            backupDocument,
            resumeRequested: false,
            forceRestartRequested: false);
        AppendLog("Transcribe Audio prepared for the current session.");
        return true;
    }

    public bool TryCommitPreparedTranscribeAudioWorkflowStart()
    {
        if (_transcribeAudioWorkflow is null || !_transcribeAudioWorkflow.ForceRestartRequested)
        {
            return true;
        }

        if (!HasExistingTranscriptContent(TranscriptGenerationMode.TranscribeAudio))
        {
            return true;
        }

        if (_transcribeAudioWorkflow.BackupDocument is null)
        {
            _transcribeAudioWorkflow.BackupDocument = CreateSessionSaveSnapshot(updatedTranscriptMode: null);
        }

        TranscriptSessionDocument? backupDocument = _transcribeAudioWorkflow.BackupDocument;
        ResetCurrentSessionTranscriptState(TranscriptGenerationMode.TranscribeAudio);
        ClearTranscriptAndLogs(unloadAudioPreview: false, transcriptMode: TranscriptGenerationMode.TranscribeAudio);

        if (TrySaveCurrentSession(
                updatedTranscriptMode: null,
                showErrorDialog: true,
                successLogMessage: "Existing transcript cleared before Transcribe Audio."))
        {
            return true;
        }

        if (backupDocument is not null)
        {
            RestoreTranscribeAudioBackup(backupDocument, saveRestoredSession: false);
        }

        AppendLog("Transcribe Audio aborted: existing transcript could not be cleared safely.");
        return false;
    }

    public async Task<bool> RunPreparedTranscribeAudioWorkflowAsync(
        CancellationToken cancellationToken,
        IProgress<TranscriptionProgressSnapshot>? progress = null)
    {
        if (_transcribeAudioWorkflow is null)
        {
            AppendLog("Transcribe Audio aborted: no prepared workflow is active.");
            return false;
        }

        _transcribeAudioWorkflow.HasStarted = true;

        if (_transcribeAudioWorkflow.Kind == TranscribeAudioWorkflowKind.NewFile
            && _currentSessionDocument is null)
        {
            if (!EnsureCurrentSessionForAudioFile(_transcribeAudioWorkflow.SourceAudioPath))
            {
                AppendLog("Transcribe Audio aborted: current audio is not associated with a session.");
                return false;
            }

            _transcribeAudioWorkflow.CreatedSessionId = _currentSessionDocument?.SessionId;
        }

        return await GenerateTranscribeAudioTranscriptAsync(cancellationToken, progress);
    }

    public void CompletePreparedTranscribeAudioWorkflow()
    {
        _transcribeAudioWorkflow = null;
    }

    public void ClosePendingTranscribeAudioWorkflow()
    {
        if (_transcribeAudioWorkflow is null)
        {
            return;
        }

        if (_transcribeAudioWorkflow.Kind == TranscribeAudioWorkflowKind.ExistingSession)
        {
            RestorePreparedTranscribeAudioWorkflowBackup();
        }
        else
        {
            ClearSelectedAudioPreview();
        }

        _transcribeAudioWorkflow = null;
    }

    public void CancelPreparedTranscribeAudioWorkflow()
    {
        if (_transcribeAudioWorkflow is null)
        {
            return;
        }

        if (_transcribeAudioWorkflow.Kind == TranscribeAudioWorkflowKind.ExistingSession)
        {
            if (_transcribeAudioWorkflow.BackupDocument is not null)
            {
                RestorePreparedTranscribeAudioWorkflowBackup();
            }
        }
        else
        {
            DeletePreparedTranscribeAudioTransientSession();
            ClearSelectedAudioPreview();
        }

        _transcribeAudioWorkflow = null;
    }

    public void FailPreparedTranscribeAudioWorkflow()
    {
        CancelPreparedTranscribeAudioWorkflow();
    }

    public void PausePreparedTranscribeAudioWorkflow()
    {
        _transcribeAudioWorkflow = null;
    }

    public TimeSpan GetCurrentTranscriptEndOffset()
    {
        return FinalizedTranscriptLines
            .Select(line => line.EndOffset ?? line.StartOffset ?? TimeSpan.Zero)
            .DefaultIfEmpty(TimeSpan.Zero)
            .Max();
    }

    public async Task<bool> GenerateTranscribeAudioTranscriptAsync(
        CancellationToken cancellationToken,
        IProgress<TranscriptionProgressSnapshot>? progress = null)
    {
        if (!IsAudioFileLoaded)
        {
            AppendLog("Transcribe Audio aborted: no audio file is loaded in preview.");
            return false;
        }

        string audioFilePath = LoadedAudioFilePath;
        string selectedEngineId = SelectedEngineId;
        AppendLog(
            $"Transcribe Audio starting. engine='{selectedEngineId}', audioPath='{audioFilePath}', " +
            $"pendingImportPath='{_pendingImportedAudioFilePath}'.");
        _processLogService.UpdateCrashContext(
            "transcribe_audio.viewmodel.start",
            $"engine='{selectedEngineId}', audioPath='{audioFilePath}'");
        bool shouldRestoreAudioPreview = ReleaseAudioPreviewForProcessing(audioFilePath);
        string transcriptionAudioFilePath = audioFilePath;
        bool deleteTranscriptionAudioFile = false;

        if (!EnsureCurrentSessionForAudioFile(audioFilePath))
        {
            AppendLog("Transcribe Audio aborted: current audio is not associated with a session.");
            RestoreAudioPreviewAfterProcessing(audioFilePath, shouldRestoreAudioPreview);
            return false;
        }

        IsBusy = true;
        try
        {
            selectedEngineId = ResolveSelectedFileTranscriptionEngineId();
            TranscriptionJobDocument job = ResolveTranscriptionJob();
            bool resume = _pendingTranscribeAudioResume
                && IsTranscriptionResumeEligible(
                    job,
                    selectedEngineId,
                    BuildTranscriptionAudioFingerprint(_currentSessionDocument!));
            (transcriptionAudioFilePath, deleteTranscriptionAudioFile) =
                PrepareAudioFilePathForTranscription(audioFilePath);
            _processLogService.UpdateCrashContext(
                "transcribe_audio.viewmodel.invoke_service",
                $"engine='{selectedEngineId}', transcriptionAudioPath='{transcriptionAudioFilePath}'");
            if (!resume)
            {
                InitializeTranscriptionJob(
                    job,
                    selectedEngineId,
                    BuildTranscriptionAudioFingerprint(_currentSessionDocument!));
            }
            else
            {
                job.Status = TranscriptionJobStatuses.Running;
                job.LastError = string.Empty;
                job.LastUpdatedUtc = DateTimeOffset.UtcNow;
                AppendLog($"Transcribe Audio resuming from chunk {job.LastCompletedChunkIndex + 2:N0}.");
            }

            if (!TrySaveCurrentSession(
                    updatedTranscriptMode: null,
                    showErrorDialog: false,
                    successLogMessage: resume
                        ? "Transcription resume checkpoint saved."
                        : "Transcription job checkpoint saved."))
            {
                AppendLog("Transcribe Audio aborted: initial transcription checkpoint could not be saved.");
                return false;
            }

            TranscriptionResult result = await _audioTranscriptionService.TranscribeAudioFileAsync(
                transcriptionAudioFilePath,
                selectedEngineId,
                cancellationToken,
                progress,
                job.LastCompletedChunkIndex + 1,
                FinalizedTranscriptLines
                    .Where(line => !string.IsNullOrWhiteSpace(line.Text) && line.StartOffset is not null)
                    .Select(line => new TranscriptionTimedLine(
                        line.Text.Trim(),
                        line.StartOffset!.Value,
                        line.EndOffset,
                        line.IsTimestampEstimated))
                    .ToArray(),
                chunkCommit => ApplyTranscriptionChunkCommit(job, chunkCommit));

            ApplyTranscriptionResult(result);
            FinalizeTranscriptionJob(job);
            new TranscriptionProgressReporter(progress).Report(
                TranscriptionProgressPhase.Completed,
                100,
                result.Duration ?? TimeSpan.Zero,
                result.Duration ?? TimeSpan.Zero,
                "Completed transcript.",
                overallPercent: 100,
                force: true);
            SelectedTranscriptViewIndex = 0;

            if (!TrySaveCurrentSession(
                    updatedTranscriptMode: TranscriptGenerationMode.TranscribeAudio,
                    showErrorDialog: true,
                    successLogMessage: "Session saved after Transcribe Audio."))
            {
                AppendLog("Transcribe Audio aborted: transcript could not be saved.");
                return false;
            }

            if (_currentSessionDocument is not null)
            {
                LoadRecentSessions(_currentSessionDocument.SessionId);
            }

            AppendLog($"Transcribe Audio completed with {FinalizedTranscriptLines.Count:N0} line(s).");
            _processLogService.UpdateCrashContext("transcribe_audio.viewmodel.completed");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MarkTranscriptionJobStopped(TranscriptionJobStatuses.Paused, string.Empty);
            if (!TrySaveCurrentSession(
                    updatedTranscriptMode: null,
                    showErrorDialog: false,
                    successLogMessage: "Transcription paused; completed chunks were kept."))
            {
                AppendLog("Transcription pause state could not be saved.");
            }
            AppendLog("Transcribe Audio paused.");
            _processLogService.UpdateCrashContext("transcribe_audio.viewmodel.canceled");
            return false;
        }
        catch (Exception ex)
        {
            MarkTranscriptionJobStopped(TranscriptionJobStatuses.Failed, ex.Message);
            if (!TrySaveCurrentSession(
                    updatedTranscriptMode: null,
                    showErrorDialog: false,
                    successLogMessage: "Transcription failed; completed chunks were kept."))
            {
                AppendLog("Transcription failure state could not be saved.");
            }
            _processLogService.UpdateCrashContext("transcribe_audio.viewmodel.failed", ex.GetType().FullName);
            AppendLog(
                $"Transcribe Audio failed. engine='{selectedEngineId}', audioPath='{audioFilePath}', error='{ex.Message}'.");
            throw;
        }
        finally
        {
            if (deleteTranscriptionAudioFile)
            {
                DeleteTemporaryTranscriptionAudioFile(transcriptionAudioFilePath);
            }

            RestoreAudioPreviewAfterProcessing(audioFilePath, shouldRestoreAudioPreview);
            IsBusy = false;
        }
    }

    public bool ConfirmSpeakerLabelOverwrite()
    {
        _pendingSpeakerDiarizationResume = false;
        if (!HasSpeakerLabels)
        {
            return true;
        }

        EventHandler<ConfirmationRequest>? handler = ConfirmationRequested;
        if (handler is null)
        {
            RaiseError("The confirmation dialog is unavailable. Existing speaker labels were left unchanged.");
            AppendLog("Detect Speaker canceled: speaker label overwrite confirmation is unavailable.");
            return false;
        }

        var request = new ConfirmationRequest(
            title: "Overwrite speaker labels?",
            message: "This session already has speaker labels. Detect Speaker will replace the existing speaker column values.",
            confirmButtonText: "Overwrite",
            cancelButtonText: "Cancel");

        try
        {
            if (SynchronizationContext.Current == _uiContext)
            {
                handler(this, request);
            }
            else
            {
                _uiContext.Send(_ => handler(this, request), null);
            }
        }
        catch (Exception ex)
        {
            RaiseError($"Unable to confirm speaker label overwrite: {ex.Message}");
            AppendLog($"Detect Speaker canceled: speaker label overwrite confirmation failed: {ex.Message}");
            return false;
        }

        if (request.IsConfirmed)
        {
            AppendLog("Speaker label overwrite confirmed by user.");
            return true;
        }

        AppendLog("Detect Speaker canceled: existing speaker labels were left unchanged.");
        return false;
    }

    public bool TryPrepareSpeakerDiarizationRun(bool shouldResume)
    {
        _pendingSpeakerDiarizationResume = false;
        if (shouldResume)
        {
            _pendingSpeakerDiarizationResume = true;
            AppendLog("Detect Speaker will resume the incomplete speaker diarization job.");
            return true;
        }

        if (!ConfirmSpeakerLabelOverwrite())
        {
            return false;
        }

        AppendLog("Detect Speaker will restart from the beginning.");
        return true;
    }

    public bool IsPreparedTranscribeAudioResumeRequested => _transcribeAudioWorkflow?.ResumeRequested == true;

    public bool IsPreparedTranscribeAudioForceRestartRequested => _transcribeAudioWorkflow?.ForceRestartRequested == true;

    public TranscriptProcessingPanelSessionSnapshot GetTranscriptProcessingPanelSessionSnapshot()
    {
        TranscriptSessionDocument? document = _currentSessionDocument;
        if (document is null)
        {
            return new TranscriptProcessingPanelSessionSnapshot(
                SourceFileName: LoadedAudioFileName,
                SourceFileSizeBytes: 0,
                TotalAudioDuration: null,
                EngineId: SelectedEngineId,
                ResumeAvailable: false,
                ProgressPercent: 0,
                Elapsed: null,
                EstimatedRemaining: null);
        }

        TranscriptionJobDocument job = document.Transcript?.TranscriptionJob ?? new TranscriptionJobDocument();
        string sourceFileName = string.IsNullOrWhiteSpace(document.Audio?.OriginalFileName)
            ? LoadedAudioFileName
            : document.Audio.OriginalFileName;
        long sourceFileSizeBytes = Math.Max(0, document.Audio?.FileSizeBytes ?? 0);
        TimeSpan? totalAudioDuration = document.Audio?.DurationSeconds is double durationSeconds && durationSeconds > 0
            ? TimeSpan.FromSeconds(durationSeconds)
            : null;
        string engineId = string.IsNullOrWhiteSpace(job.Engine)
            ? string.IsNullOrWhiteSpace(document.Transcript?.ModelId)
                ? SelectedEngineId
                : document.Transcript.ModelId
            : job.Engine;

        double progressPercent = 0;
        if (job.TotalChunks > 0 && job.LastCompletedChunkIndex >= 0)
        {
            progressPercent = Math.Clamp(((double)job.LastCompletedChunkIndex + 1) / job.TotalChunks * 100d, 0d, 100d);
        }

        TimeSpan? elapsed = null;
        if (job.StartedUtc is DateTimeOffset startedUtc)
        {
            DateTimeOffset endUtc = job.LastUpdatedUtc ?? job.CompletedUtc ?? DateTimeOffset.UtcNow;
            TimeSpan computedElapsed = endUtc - startedUtc;
            elapsed = computedElapsed < TimeSpan.Zero ? TimeSpan.Zero : computedElapsed;
        }

        TimeSpan? estimatedRemaining = null;
        if (elapsed is TimeSpan elapsedValue && progressPercent > 0.01d && progressPercent < 100d)
        {
            double remainingFactor = (100d - progressPercent) / progressPercent;
            estimatedRemaining = TimeSpan.FromTicks((long)(elapsedValue.Ticks * remainingFactor));
        }

        bool resumeAvailable = IsTranscriptionResumeEligible(
            job,
            SelectedEngineId,
            BuildTranscriptionAudioFingerprint(document),
            LoadedAudioFilePath);

        return new TranscriptProcessingPanelSessionSnapshot(
            SourceFileName: sourceFileName,
            SourceFileSizeBytes: sourceFileSizeBytes,
            TotalAudioDuration: totalAudioDuration,
            EngineId: engineId,
            ResumeAvailable: resumeAvailable,
            ProgressPercent: progressPercent,
            Elapsed: elapsed,
            EstimatedRemaining: estimatedRemaining);
    }

    public SpeakerDiarizationPanelSessionSnapshot GetSpeakerDiarizationPanelSessionSnapshot()
    {
        TranscriptSessionDocument? document = _currentSessionDocument;
        if (document is null)
        {
            return new SpeakerDiarizationPanelSessionSnapshot(
                ResumeAvailable: false,
                RestartAvailable: false);
        }

        SpeakerDiarizationJobDocument job = document.Transcript?.SpeakerDiarizationJob ?? new SpeakerDiarizationJobDocument();
        string audioFingerprint = BuildSpeakerDiarizationAudioFingerprint(document);
        string transcriptFingerprint = BuildSpeakerDiarizationTranscriptFingerprint();
        bool resumeAvailable = IsSpeakerDiarizationResumeEligible(
            job,
            audioFingerprint,
            transcriptFingerprint,
            expectedTotalChunks: null);
        bool restartAvailable = IsCurrentSpeakerDiarizationJob(job, audioFingerprint, transcriptFingerprint)
            && (resumeAvailable
                || string.Equals(job.Status, SpeakerDiarizationJobStatuses.Completed, StringComparison.OrdinalIgnoreCase));

        return new SpeakerDiarizationPanelSessionSnapshot(
            ResumeAvailable: resumeAvailable,
            RestartAvailable: restartAvailable);
    }

    private bool TryConfirmTranscribeAudioResumeChoice(string sourcePath, out bool shouldResume)
    {
        shouldResume = false;
        if (_currentSessionDocument?.Transcript.TranscriptionJob is not TranscriptionJobDocument job
            || !IsIncompleteTranscriptionJob(job)
            || !IsTranscriptionResumeEligible(
                job,
                SelectedEngineId,
                BuildTranscriptionAudioFingerprint(_currentSessionDocument),
                sourcePath))
        {
            return false;
        }

        shouldResume = true;
        AppendLog("Transcribe Audio will resume an incomplete transcription job.");
        return true;
    }

    private (string AudioFilePath, bool IsTemporary) PrepareAudioFilePathForTranscription(string audioFilePath)
    {
        if (!IsLiveRecordingManifestPath(audioFilePath))
        {
            return (audioFilePath, false);
        }

        string tempPath = Path.Combine(
            Path.GetTempPath(),
            $"AudioScript-live-session-{Guid.NewGuid():N}.wav");
        using var liveRecordingStream = new SegmentedLiveRecordingWaveStream(audioFilePath);
        liveRecordingStream.Position = 0;
        WaveFileWriter.CreateWaveFile(tempPath, liveRecordingStream);
        AppendLog($"Prepared live recording WAV for transcription: {Path.GetFileName(tempPath)}.");
        return (tempPath, true);
    }

    private bool IsCurrentSessionLiveRecordingManifest()
    {
        return string.Equals(
            _currentSessionDocument?.Audio?.StorageKind,
            AudioStorageKinds.LiveRecordingManifest,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLiveRecordingManifestPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string normalizedPath = filePath.Replace('\\', '/');
        return string.Equals(Path.GetFileName(normalizedPath), "manifest.json", StringComparison.OrdinalIgnoreCase)
            && normalizedPath.Contains("/audio/live/", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildConvertedLiveSessionAudioFileName(string? displayName)
    {
        string baseName = string.IsNullOrWhiteSpace(displayName)
            ? TranscriptSessionStore.LiveSessionAudioName
            : displayName.Trim();
        return $"{baseName}.wav";
    }

    private void DeleteTemporaryTranscriptionAudioFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Temporary transcription audio cleanup skipped: {ex.Message}");
        }
    }

    public async Task<bool> RunSpeakerDetectionAsync(
        CancellationToken cancellationToken,
        IProgress<TranscriptionProgressSnapshot>? progress = null)
    {
        if (_currentSessionDocument is null)
        {
            AppendLog("Detect Speaker aborted: no session is loaded.");
            return false;
        }

        if (FinalizedTranscriptLines.Count == 0)
        {
            AppendLog("Detect Speaker aborted: no transcript rows are present.");
            return false;
        }

        string? audioFilePath = _sessionStore.ResolveStoredAudioPathForPlayback(_currentSessionDocument);
        if (string.IsNullOrWhiteSpace(audioFilePath) || !File.Exists(audioFilePath))
        {
            AppendLog("Detect Speaker aborted: session audio is unavailable.");
            RaiseError("Session audio is unavailable. Restore or reopen the session audio before detecting speakers.");
            return false;
        }

        string previousLoadedAudioFilePath = LoadedAudioFilePath;
        bool shouldRestoreAudioPreview = ReleaseAudioPreviewForProcessing(previousLoadedAudioFilePath);
        SpeakerDiarizationRowSnapshot[] preRunSpeakerLabels = CaptureSpeakerLabelSnapshots();

        IsBusy = true;
        try
        {
            AppendLog($"Detect Speaker starting. audioPath='{audioFilePath}'.");
            TranscriptionResult transcriptionResult = BuildCurrentSessionTranscriptionResult();
            string audioFingerprint = BuildSpeakerDiarizationAudioFingerprint(_currentSessionDocument);
            string transcriptFingerprint = BuildSpeakerDiarizationTranscriptFingerprint();
            using ChunkedAudioFile chunkedAudio = _speakerDiarizationService.PrepareIncrementalDiarizationChunks(audioFilePath);
            SpeakerDiarizationJobDocument job = ResolveSpeakerDiarizationJob();
            bool resume = _pendingSpeakerDiarizationResume
                && IsSpeakerDiarizationResumeEligible(
                    job,
                    audioFingerprint,
                    transcriptFingerprint,
                    chunkedAudio.Chunks.Count);

            if (!resume)
            {
                InitializeSpeakerDiarizationJob(job, audioFingerprint, transcriptFingerprint, chunkedAudio.Chunks.Count);
            }
            else
            {
                job.Status = SpeakerDiarizationJobStatuses.Running;
                job.LastError = string.Empty;
                job.LastUpdatedUtc = DateTimeOffset.UtcNow;
                AppendLog($"Detect Speaker resuming from chunk {job.LastCompletedChunkIndex + 2:N0} of {job.TotalChunks:N0}.");
            }

            _lastSpeakerDetectionUsedHeuristicFallback = false;
            NotifyPropertyChanged(nameof(LastSpeakerDetectionUsedHeuristicFallback));
            if (!SaveSpeakerDiarizationCheckpoint("Speaker diarization checkpoint saved."))
            {
                AppendLog("Detect Speaker aborted: initial speaker diarization checkpoint could not be saved.");
                return false;
            }

            for (int chunkIndex = Math.Max(0, job.LastCompletedChunkIndex + 1);
                 chunkIndex < chunkedAudio.Chunks.Count;
                 chunkIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AudioChunkFile chunk = chunkedAudio.Chunks[chunkIndex];
                IReadOnlyList<SpeakerDiarizationRowWorkItem> rowWorkItems = BuildSpeakerDiarizationRowWorkItems(chunk.Plan);
                if (rowWorkItems.Count > 0)
                {
                    SpeakerDiarizationChunkCheckpoint? checkpoint = CaptureSpeakerDiarizationChunkCheckpoint(job, rowWorkItems);
                    TranscriptionResult chunkTranscription = BuildChunkSpeakerDiarizationTranscriptionResult(
                        transcriptionResult,
                        rowWorkItems,
                        chunk.Plan.RequestStart,
                        chunk.Plan.RequestEnd);
                    SpeakerDiarizationResult chunkResult = await _speakerDiarizationService.DiarizeChunkAsync(
                        chunk,
                        chunkTranscription,
                        cancellationToken,
                        CreateSpeakerDiarizationChunkProgress(
                            progress,
                            chunk.Plan,
                            chunkIndex,
                            chunkedAudio.Chunks.Count,
                            transcriptionResult.Duration,
                            rowWorkItems.Count(item => item.ShouldCommit)));
                    if (chunkResult.UsedHeuristicFallback)
                    {
                        throw new InvalidOperationException("Pyannote Community-1 became unavailable during incremental diarization.");
                    }

                    ApplySpeakerDiarizationChunk(job, chunk.Plan, rowWorkItems, chunkResult.Segments);
                    job.LastCompletedChunkIndex = chunkIndex;
                    job.LastUpdatedUtc = DateTimeOffset.UtcNow;
                    if (!SaveSpeakerDiarizationCheckpoint($"Speaker diarization checkpoint saved after chunk {chunkIndex + 1:N0} of {chunkedAudio.Chunks.Count:N0}."))
                    {
                        RestoreSpeakerDiarizationChunkCheckpoint(job, checkpoint);
                        AppendLog($"Detect Speaker aborted: checkpoint save failed after chunk {chunkIndex + 1:N0}.");
                        return false;
                    }
                }
                else
                {
                    job.LastCompletedChunkIndex = chunkIndex;
                    job.LastUpdatedUtc = DateTimeOffset.UtcNow;
                    if (!SaveSpeakerDiarizationCheckpoint($"Speaker diarization checkpoint saved after chunk {chunkIndex + 1:N0} of {chunkedAudio.Chunks.Count:N0}."))
                    {
                        AppendLog($"Detect Speaker aborted: checkpoint save failed after chunk {chunkIndex + 1:N0}.");
                        return false;
                    }
                }
            }

            FinalizeSpeakerDiarizationJob(job);
            new TranscriptionProgressReporter(progress).Report(
                TranscriptionProgressPhase.Completed,
                100,
                transcriptionResult.Duration ?? TimeSpan.Zero,
                transcriptionResult.Duration ?? TimeSpan.Zero,
                "Completed speaker detection.",
                overallPercent: 100,
                force: true);
            SelectedTranscriptViewIndex = 0;

            if (!TrySaveCurrentSession(
                    updatedTranscriptMode: null,
                    showErrorDialog: true,
                    successLogMessage: "Session saved after Detect Speaker."))
            {
                AppendLog("Detect Speaker aborted: speaker labels could not be saved.");
                return false;
            }

            LoadRecentSessions(_currentSessionDocument.SessionId);
            AppendLog($"Detect Speaker completed for {FinalizedTranscriptLines.Count:N0} line(s).");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MarkSpeakerDiarizationJobStopped(SpeakerDiarizationJobStatuses.Canceled, string.Empty);
            if (!SaveSpeakerDiarizationCheckpoint("Speaker diarization canceled; partial speaker labels were kept."))
            {
                AppendLog("Speaker diarization cancellation state could not be saved.");
            }
            AppendLog("Detect Speaker canceled.");
            return false;
        }
        catch (Exception ex)
        {
            MarkSpeakerDiarizationJobStopped(SpeakerDiarizationJobStatuses.Failed, ex.Message);
            RestorePreRunSpeakerLabelsIfNoChunkCompleted(preRunSpeakerLabels);
            if (!SaveSpeakerDiarizationCheckpoint("Speaker diarization failed; partial speaker labels were kept."))
            {
                AppendLog("Speaker diarization failure state could not be saved.");
            }
            AppendLog($"Detect Speaker failed. audioPath='{audioFilePath}', error='{ex.Message}'.");
            throw;
        }
        finally
        {
            RestoreAudioPreviewAfterProcessing(previousLoadedAudioFilePath, shouldRestoreAudioPreview);
            IsBusy = false;
        }
    }

    private TranscriptionJobDocument ResolveTranscriptionJob()
    {
        if (_currentSessionDocument is null)
        {
            throw new InvalidOperationException("No session is loaded.");
        }

        _currentSessionDocument.Transcript.TranscriptionJob ??= new TranscriptionJobDocument();
        return _currentSessionDocument.Transcript.TranscriptionJob;
    }

    private void InitializeTranscriptionJob(
        TranscriptionJobDocument job,
        string engineId,
        string audioFingerprint)
    {
        job.Status = TranscriptionJobStatuses.Running;
        job.Engine = engineId;
        job.JobVersion = TranscriptionJobVersion;
        job.AudioFingerprint = audioFingerprint;
        job.TotalChunks = 0;
        job.LastCompletedChunkIndex = -1;
        job.StartedUtc = DateTimeOffset.UtcNow;
        job.LastUpdatedUtc = job.StartedUtc;
        job.CompletedUtc = null;
        job.LastError = string.Empty;

        _suppressSessionAutosave = true;
        try
        {
            UnsubscribeFromFinalizedLineChanges();
            FinalizedTranscriptLines.Clear();
        }
        finally
        {
            _suppressSessionAutosave = false;
        }

        RebuildFinalizedTextFromLines();
        NotifyCurrentTranscriptStateChanged();
    }

    private void FinalizeTranscriptionJob(TranscriptionJobDocument job)
    {
        foreach (FinalizedTranscriptLineViewModel line in FinalizedTranscriptLines)
        {
            line.IsTranscriptionPartial = false;
        }

        job.Status = TranscriptionJobStatuses.Completed;
        job.LastCompletedChunkIndex = Math.Max(job.LastCompletedChunkIndex, job.TotalChunks - 1);
        job.LastUpdatedUtc = DateTimeOffset.UtcNow;
        job.CompletedUtc = job.LastUpdatedUtc;
        job.LastError = string.Empty;
        RebuildFinalizedTextFromLines();
        NotifyCurrentTranscriptStateChanged();
    }

    private void MarkTranscriptionJobStopped(string status, string error)
    {
        if (_currentSessionDocument?.Transcript.TranscriptionJob is not TranscriptionJobDocument job
            || !string.Equals(job.Status, TranscriptionJobStatuses.Running, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        job.Status = status;
        job.LastUpdatedUtc = DateTimeOffset.UtcNow;
        job.LastError = error?.Trim() ?? string.Empty;
        NotifyPropertyChanged(nameof(IsCurrentTranscriptionJobIncomplete));
    }

    private void ApplyTranscriptionChunkCommit(TranscriptionJobDocument job, TranscriptionChunkCommit chunkCommit)
    {
        var checkpoint = CaptureTranscriptionChunkCheckpoint(job);

        try
        {
            job.TotalChunks = chunkCommit.TotalChunks;
            job.LastCompletedChunkIndex = chunkCommit.ChunkIndex;
            job.LastUpdatedUtc = DateTimeOffset.UtcNow;

            foreach (TranscriptionTimedLine timedLine in chunkCommit.CommittedLines)
            {
                var line = new FinalizedTranscriptLineViewModel(
                    startOffset: timedLine.StartOffset,
                    endOffset: timedLine.EndOffset,
                    isTimestampEstimated: timedLine.IsTimestampEstimated,
                    text: timedLine.Text.Trim(),
                    isTranscriptionPartial: true);
                line.PropertyChanged += OnFinalizedLinePropertyChanged;
                FinalizedTranscriptLines.Add(line);
            }

            RebuildFinalizedTextFromLines();
            NotifyCurrentTranscriptStateChanged();
            if (!TrySaveCurrentSession(
                    updatedTranscriptMode: null,
                    showErrorDialog: false,
                    successLogMessage: $"Transcription checkpoint saved after chunk {chunkCommit.ChunkIndex + 1:N0} of {chunkCommit.TotalChunks:N0}."))
            {
                throw new InvalidOperationException("Unable to save the transcription checkpoint.");
            }
        }
        catch
        {
            RestoreTranscriptionChunkCheckpoint(job, checkpoint);
            throw;
        }
    }

    private TranscriptionChunkCheckpoint CaptureTranscriptionChunkCheckpoint(TranscriptionJobDocument job)
    {
        return new TranscriptionChunkCheckpoint(
            job.TotalChunks,
            job.LastCompletedChunkIndex,
            job.LastUpdatedUtc,
            FinalizedTranscriptLines.Count);
    }

    private void RestoreTranscriptionChunkCheckpoint(
        TranscriptionJobDocument job,
        TranscriptionChunkCheckpoint checkpoint)
    {
        while (FinalizedTranscriptLines.Count > checkpoint.RowCount)
        {
            FinalizedTranscriptLineViewModel line = FinalizedTranscriptLines[^1];
            line.PropertyChanged -= OnFinalizedLinePropertyChanged;
            FinalizedTranscriptLines.RemoveAt(FinalizedTranscriptLines.Count - 1);
        }

        job.TotalChunks = checkpoint.TotalChunks;
        job.LastCompletedChunkIndex = checkpoint.LastCompletedChunkIndex;
        job.LastUpdatedUtc = checkpoint.LastUpdatedUtc;
        RebuildFinalizedTextFromLines();
        NotifyCurrentTranscriptStateChanged();
    }

    private SpeakerDiarizationJobDocument ResolveSpeakerDiarizationJob()
    {
        if (_currentSessionDocument is null)
        {
            throw new InvalidOperationException("No session is loaded.");
        }

        _currentSessionDocument.Transcript.SpeakerDiarizationJob ??= new SpeakerDiarizationJobDocument();
        return _currentSessionDocument.Transcript.SpeakerDiarizationJob;
    }

    private void InitializeSpeakerDiarizationJob(
        SpeakerDiarizationJobDocument job,
        string audioFingerprint,
        string transcriptFingerprint,
        int totalChunks)
    {
        int nextRevision = Math.Max(1, job.Revision + 1);
        job.Status = SpeakerDiarizationJobStatuses.Running;
        job.Engine = SpeakerDiarizationEngineId;
        job.JobVersion = SpeakerDiarizationJobVersion;
        job.AudioFingerprint = audioFingerprint;
        job.TranscriptFingerprint = transcriptFingerprint;
        job.ChunkDurationSeconds = ChunkedSpeakerDiarizationService.SpeakerDiarizationChunkDuration.TotalSeconds;
        job.OverlapDurationSeconds = ChunkedSpeakerDiarizationService.SpeakerDiarizationOverlapDuration.TotalSeconds;
        job.TotalChunks = totalChunks;
        job.LastCompletedChunkIndex = -1;
        job.StartedUtc = DateTimeOffset.UtcNow;
        job.LastUpdatedUtc = job.StartedUtc;
        job.CompletedUtc = null;
        job.LastError = string.Empty;
        job.Revision = nextRevision;
        job.NextSpeakerIndex = 1;
        job.SpeakerMappings.Clear();

        _isApplyingSpeakerDiarizationLabels = true;
        try
        {
            foreach (FinalizedTranscriptLineViewModel line in FinalizedTranscriptLines)
            {
                if (string.Equals(line.SpeakerLabelSource, SpeakerLabelSources.Manual, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                line.SpeakerLabel = string.Empty;
                line.SpeakerLabelSource = string.Empty;
                line.DiarizationRevision = null;
                line.LastDiarizedChunkIndex = null;
            }
        }
        finally
        {
            _isApplyingSpeakerDiarizationLabels = false;
        }

        RebuildFinalizedTextFromLines();
        NotifyCurrentTranscriptStateChanged();
    }

    private void FinalizeSpeakerDiarizationJob(SpeakerDiarizationJobDocument job)
    {
        _isApplyingSpeakerDiarizationLabels = true;
        try
        {
            foreach (FinalizedTranscriptLineViewModel line in FinalizedTranscriptLines)
            {
                if (line.DiarizationRevision == job.Revision
                    && string.Equals(line.SpeakerLabelSource, SpeakerLabelSources.DiarizationPartial, StringComparison.OrdinalIgnoreCase))
                {
                    line.SpeakerLabelSource = SpeakerLabelSources.DiarizationFinal;
                }
            }
        }
        finally
        {
            _isApplyingSpeakerDiarizationLabels = false;
        }

        job.Status = SpeakerDiarizationJobStatuses.Completed;
        job.LastCompletedChunkIndex = job.TotalChunks - 1;
        job.LastUpdatedUtc = DateTimeOffset.UtcNow;
        job.CompletedUtc = job.LastUpdatedUtc;
        job.LastError = string.Empty;
        RebuildFinalizedTextFromLines();
        NotifyCurrentTranscriptStateChanged();
    }

    private void MarkSpeakerDiarizationJobStopped(string status, string error)
    {
        if (_currentSessionDocument?.Transcript.SpeakerDiarizationJob is not SpeakerDiarizationJobDocument job
            || !string.Equals(job.Status, SpeakerDiarizationJobStatuses.Running, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        job.Status = status;
        job.LastUpdatedUtc = DateTimeOffset.UtcNow;
        job.LastError = error?.Trim() ?? string.Empty;
    }

    private bool SaveSpeakerDiarizationCheckpoint(string successLogMessage)
    {
        return TrySaveCurrentSession(
            updatedTranscriptMode: null,
            showErrorDialog: false,
            successLogMessage: successLogMessage);
    }

    private SpeakerDiarizationChunkCheckpoint CaptureSpeakerDiarizationChunkCheckpoint(
        SpeakerDiarizationJobDocument job,
        IReadOnlyList<SpeakerDiarizationRowWorkItem> rowWorkItems)
    {
        return new SpeakerDiarizationChunkCheckpoint(
            job.LastCompletedChunkIndex,
            job.LastUpdatedUtc,
            job.NextSpeakerIndex,
            job.SpeakerMappings
                .Select(mapping => new SpeakerDiarizationSpeakerMapDocument
                {
                    ChunkSpeakerKey = mapping.ChunkSpeakerKey,
                    GlobalSpeakerLabel = mapping.GlobalSpeakerLabel,
                })
                .ToArray(),
            rowWorkItems
                .Where(item => item.ShouldCommit)
                .Select(item => new SpeakerDiarizationRowSnapshot(
                    item.Line,
                    item.Line.SpeakerLabel,
                    item.Line.SpeakerLabelSource,
                    item.Line.DiarizationRevision,
                    item.Line.LastDiarizedChunkIndex))
                .ToArray());
    }

    private void RestoreSpeakerDiarizationChunkCheckpoint(
        SpeakerDiarizationJobDocument job,
        SpeakerDiarizationChunkCheckpoint checkpoint)
    {
        RestoreSpeakerLabelSnapshots(checkpoint.Rows);

        job.LastCompletedChunkIndex = checkpoint.LastCompletedChunkIndex;
        job.LastUpdatedUtc = checkpoint.LastUpdatedUtc;
        job.NextSpeakerIndex = checkpoint.NextSpeakerIndex;
        job.SpeakerMappings.Clear();
        foreach (SpeakerDiarizationSpeakerMapDocument mapping in checkpoint.SpeakerMappings)
        {
            job.SpeakerMappings.Add(new SpeakerDiarizationSpeakerMapDocument
            {
                ChunkSpeakerKey = mapping.ChunkSpeakerKey,
                GlobalSpeakerLabel = mapping.GlobalSpeakerLabel,
            });
        }

        RebuildFinalizedTextFromLines();
        NotifyCurrentTranscriptStateChanged();
    }

    private SpeakerDiarizationRowSnapshot[] CaptureSpeakerLabelSnapshots() =>
        FinalizedTranscriptLines
            .Select(line => new SpeakerDiarizationRowSnapshot(
                line,
                line.SpeakerLabel,
                line.SpeakerLabelSource,
                line.DiarizationRevision,
                line.LastDiarizedChunkIndex))
            .ToArray();

    private void RestorePreRunSpeakerLabelsIfNoChunkCompleted(SpeakerDiarizationRowSnapshot[] snapshots)
    {
        if (_currentSessionDocument?.Transcript.SpeakerDiarizationJob is not SpeakerDiarizationJobDocument job
            || job.LastCompletedChunkIndex >= 0)
        {
            return;
        }

        RestoreSpeakerLabelSnapshots(snapshots);
        RebuildFinalizedTextFromLines();
        NotifyCurrentTranscriptStateChanged();
    }

    private void RestoreSpeakerLabelSnapshots(IEnumerable<SpeakerDiarizationRowSnapshot> snapshots)
    {
        _isApplyingSpeakerDiarizationLabels = true;
        try
        {
            foreach (SpeakerDiarizationRowSnapshot row in snapshots)
            {
                row.Line.SpeakerLabel = row.SpeakerLabel;
                row.Line.SpeakerLabelSource = row.SpeakerLabelSource;
                row.Line.DiarizationRevision = row.DiarizationRevision;
                row.Line.LastDiarizedChunkIndex = row.LastDiarizedChunkIndex;
            }
        }
        finally
        {
            _isApplyingSpeakerDiarizationLabels = false;
        }
    }

    private IReadOnlyList<SpeakerDiarizationRowWorkItem> BuildSpeakerDiarizationRowWorkItems(AudioChunkPlan plan)
    {
        var items = new List<SpeakerDiarizationRowWorkItem>();
        for (int index = 0; index < FinalizedTranscriptLines.Count; index++)
        {
            FinalizedTranscriptLineViewModel line = FinalizedTranscriptLines[index];
            if (line.StartOffset is not TimeSpan start || string.IsNullOrWhiteSpace(line.Text))
            {
                continue;
            }

            TimeSpan end = line.EndOffset is TimeSpan resolvedEnd && resolvedEnd > start
                ? resolvedEnd
                : start;
            TimeSpan midpoint = start + TimeSpan.FromTicks(Math.Max(0, (end - start).Ticks) / 2);
            if (midpoint < plan.RequestStart || midpoint > plan.RequestEnd)
            {
                continue;
            }

            bool shouldCommit = midpoint >= plan.KeepStart && midpoint <= plan.KeepEnd;
            items.Add(new SpeakerDiarizationRowWorkItem(index, line, shouldCommit));
        }

        return items;
    }

    private static TranscriptionResult BuildChunkSpeakerDiarizationTranscriptionResult(
        TranscriptionResult source,
        IReadOnlyList<SpeakerDiarizationRowWorkItem> rowWorkItems,
        TimeSpan requestStart,
        TimeSpan requestEnd)
    {
        IReadOnlyList<TranscriptionTimedLine> timedLines = rowWorkItems
            .Select(item =>
            {
                TimeSpan start = item.Line.StartOffset!.Value - requestStart;
                TimeSpan? end = item.Line.EndOffset is TimeSpan resolvedEnd
                    ? resolvedEnd - requestStart
                    : null;
                if (start < TimeSpan.Zero)
                {
                    start = TimeSpan.Zero;
                }

                if (end is TimeSpan normalizedEnd && normalizedEnd < start)
                {
                    end = start;
                }

                return new TranscriptionTimedLine(
                    item.Line.Text,
                    start,
                    end,
                    item.Line.IsTimestampEstimated);
            })
            .ToArray();

        return new TranscriptionResult(
            Text: string.Join(Environment.NewLine, rowWorkItems.Select(item => item.Line.Text)),
            Model: source.Model,
            CreatedAt: source.CreatedAt,
            Duration: requestEnd - requestStart,
            TokenLogprobs: Array.Empty<TranscriptionTokenLogprob>(),
            LowConfidenceTokens: Array.Empty<LowConfidenceToken>(),
            TimedLines: timedLines);
    }

    private void ApplySpeakerDiarizationChunk(
        SpeakerDiarizationJobDocument job,
        AudioChunkPlan plan,
        IReadOnlyList<SpeakerDiarizationRowWorkItem> rowWorkItems,
        IReadOnlyList<SpeakerDiarizationSegment> segments)
    {
        SpeakerDiarizationSegment[] orderedSegments = segments
            .Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
            .OrderBy(segment => segment.StartOffset)
            .ToArray();
        int count = Math.Min(rowWorkItems.Count, orderedSegments.Length);
        if (count == 0)
        {
            return;
        }

        Dictionary<string, string> localSpeakerLabels = ResolveChunkSpeakerLabels(job, rowWorkItems, orderedSegments, count, plan.Index);
        _isApplyingSpeakerDiarizationLabels = true;
        try
        {
            for (int index = 0; index < count; index++)
            {
                SpeakerDiarizationRowWorkItem item = rowWorkItems[index];
                if (!item.ShouldCommit
                    || string.Equals(item.Line.SpeakerLabelSource, SpeakerLabelSources.Manual, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string localSpeaker = NormalizeSpeakerKey(orderedSegments[index].Speaker);
                if (!localSpeakerLabels.TryGetValue(localSpeaker, out string? globalLabel))
                {
                    globalLabel = AllocateGlobalSpeakerLabel(job);
                    localSpeakerLabels[localSpeaker] = globalLabel;
                }

                item.Line.SpeakerLabel = globalLabel;
                item.Line.SpeakerLabelSource = SpeakerLabelSources.DiarizationPartial;
                item.Line.DiarizationRevision = job.Revision;
                item.Line.LastDiarizedChunkIndex = plan.Index;
            }
        }
        finally
        {
            _isApplyingSpeakerDiarizationLabels = false;
        }

        foreach ((string localSpeaker, string globalLabel) in localSpeakerLabels)
        {
            string chunkSpeakerKey = BuildChunkSpeakerMapKey(plan.Index, localSpeaker);
            SpeakerDiarizationSpeakerMapDocument? existing = job.SpeakerMappings.FirstOrDefault(mapping =>
                string.Equals(mapping.ChunkSpeakerKey, chunkSpeakerKey, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                job.SpeakerMappings.Add(new SpeakerDiarizationSpeakerMapDocument
                {
                    ChunkSpeakerKey = chunkSpeakerKey,
                    GlobalSpeakerLabel = globalLabel,
                });
            }
            else
            {
                existing.GlobalSpeakerLabel = globalLabel;
            }
        }

        RebuildFinalizedTextFromLines();
        NotifyCurrentTranscriptStateChanged();
    }

    private static Dictionary<string, string> ResolveChunkSpeakerLabels(
        SpeakerDiarizationJobDocument job,
        IReadOnlyList<SpeakerDiarizationRowWorkItem> rowWorkItems,
        IReadOnlyList<SpeakerDiarizationSegment> orderedSegments,
        int count,
        int chunkIndex)
    {
        var localVotes = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < count; index++)
        {
            string localSpeaker = NormalizeSpeakerKey(orderedSegments[index].Speaker);
            FinalizedTranscriptLineViewModel line = rowWorkItems[index].Line;
            if (string.IsNullOrWhiteSpace(line.SpeakerLabel))
            {
                continue;
            }

            if (!localVotes.TryGetValue(localSpeaker, out Dictionary<string, int>? votes))
            {
                votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                localVotes[localSpeaker] = votes;
            }

            votes[line.SpeakerLabel] = votes.TryGetValue(line.SpeakerLabel, out int countForLabel)
                ? countForLabel + 1
                : 1;
        }

        foreach ((string localSpeaker, Dictionary<string, int> votes) in localVotes)
        {
            string label = votes
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .First()
                .Key;
            resolved[localSpeaker] = label;
        }

        foreach (SpeakerDiarizationSpeakerMapDocument mapping in job.SpeakerMappings)
        {
            string prefix = $"{chunkIndex}:";
            if (mapping.ChunkSpeakerKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string localSpeaker = mapping.ChunkSpeakerKey[prefix.Length..];
                if (!resolved.ContainsKey(localSpeaker))
                {
                    resolved[localSpeaker] = mapping.GlobalSpeakerLabel;
                }
            }
        }

        foreach (SpeakerDiarizationSegment segment in orderedSegments.Take(count))
        {
            string localSpeaker = NormalizeSpeakerKey(segment.Speaker);
            if (!resolved.ContainsKey(localSpeaker))
            {
                resolved[localSpeaker] = AllocateGlobalSpeakerLabel(job);
            }
        }

        return resolved;
    }

    private static IProgress<TranscriptionProgressSnapshot>? CreateSpeakerDiarizationChunkProgress(
        IProgress<TranscriptionProgressSnapshot>? progress,
        AudioChunkPlan chunkPlan,
        int chunkIndex,
        int totalChunks,
        TimeSpan? totalDuration,
        int committedRowCount)
    {
        if (progress is null)
        {
            return null;
        }

        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        return new Progress<TranscriptionProgressSnapshot>(snapshot =>
        {
            double chunkPortion = totalChunks <= 0 ? 100 : 100d / totalChunks;
            double overallPercent = Math.Clamp((chunkIndex * chunkPortion) + (snapshot.Percent * chunkPortion / 100d), 0, 100);
            TimeSpan totalAudio = totalDuration ?? snapshot.TotalAudio;
            TimeSpan processedAudio = totalAudio > TimeSpan.Zero
                ? TimeSpan.FromTicks((long)(totalAudio.Ticks * (overallPercent / 100d)))
                : TimeSpan.Zero;
            string chunkRange = $"{FormatDurationForProgress(chunkPlan.KeepStart)}-{FormatDurationForProgress(chunkPlan.KeepEnd)}";
            string chunkProgress = snapshot.Percent < 1
                ? "analyzing"
                : $"{snapshot.Percent:0}%";
            progress.Report(TranscriptionProgressSnapshot.Create(
                snapshot.Phase,
                overallPercent,
                overallPercent,
                chunkIndex + 1,
                totalChunks,
                processedAudio,
                totalAudio,
                DateTimeOffset.UtcNow - startedUtc,
                $"Chunk {chunkIndex + 1:N0}/{totalChunks:N0} {chunkRange} - {chunkProgress}; {committedRowCount:N0} row(s) queued."));
        });
    }

    private static string FormatDurationForProgress(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1
            ? value.ToString(@"hh\:mm\:ss")
            : value.ToString(@"mm\:ss");
    }

    private bool IsSpeakerDiarizationResumeEligible(
        SpeakerDiarizationJobDocument job,
        string audioFingerprint,
        string transcriptFingerprint,
        int? expectedTotalChunks)
    {
        return IsIncompleteSpeakerDiarizationJob(job)
            && string.Equals(job.Engine, SpeakerDiarizationEngineId, StringComparison.OrdinalIgnoreCase)
            && job.JobVersion == SpeakerDiarizationJobVersion
            && IsCurrentSpeakerDiarizationJob(job, audioFingerprint, transcriptFingerprint)
            && Math.Abs(job.ChunkDurationSeconds - ChunkedSpeakerDiarizationService.SpeakerDiarizationChunkDuration.TotalSeconds) < 0.001d
            && Math.Abs(job.OverlapDurationSeconds - ChunkedSpeakerDiarizationService.SpeakerDiarizationOverlapDuration.TotalSeconds) < 0.001d
            && (expectedTotalChunks is null || job.TotalChunks == expectedTotalChunks.Value)
            && job.LastCompletedChunkIndex < job.TotalChunks - 1;
    }

    private static bool IsCurrentSpeakerDiarizationJob(
        SpeakerDiarizationJobDocument job,
        string audioFingerprint,
        string transcriptFingerprint)
    {
        return string.Equals(job.AudioFingerprint, audioFingerprint, StringComparison.OrdinalIgnoreCase)
            && string.Equals(job.TranscriptFingerprint, transcriptFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIncompleteSpeakerDiarizationJob(SpeakerDiarizationJobDocument job)
    {
        return string.Equals(job.Status, SpeakerDiarizationJobStatuses.Running, StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.Status, SpeakerDiarizationJobStatuses.Canceled, StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.Status, SpeakerDiarizationJobStatuses.Failed, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIncompleteTranscriptionJob(TranscriptionJobDocument job)
    {
        return string.Equals(job.Status, TranscriptionJobStatuses.Running, StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.Status, TranscriptionJobStatuses.Paused, StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.Status, TranscriptionJobStatuses.Failed, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSpeakerDiarizationAudioFingerprint(TranscriptSessionDocument document)
    {
        return string.Join(
            "|",
            document.Audio.Sha256,
            document.Audio.FileSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            document.Audio.DurationSeconds?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static string BuildTranscriptionAudioFingerprint(TranscriptSessionDocument document)
    {
        return BuildSpeakerDiarizationAudioFingerprint(document);
    }

    private static bool IsTranscriptionResumeEligible(
        TranscriptionJobDocument job,
        string engineId,
        string audioFingerprint,
        string? sourcePath = null)
    {
        if (!IsIncompleteTranscriptionJob(job)
            || !string.Equals(job.Engine, engineId, StringComparison.OrdinalIgnoreCase)
            || job.JobVersion != TranscriptionJobVersion
            || !string.Equals(job.AudioFingerprint, audioFingerprint, StringComparison.OrdinalIgnoreCase)
            || job.TotalChunks <= 0
            || job.LastCompletedChunkIndex >= job.TotalChunks - 1)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(sourcePath)
            || File.Exists(sourcePath);
    }

    private string BuildSpeakerDiarizationTranscriptFingerprint()
    {
        var builder = new StringBuilder();
        foreach (FinalizedTranscriptLineViewModel line in FinalizedTranscriptLines)
        {
            builder.Append(line.StartOffset?.TotalSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append('|');
            builder.Append(line.EndOffset?.TotalSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append('|');
            builder.Append(line.Text);
            builder.Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string NormalizeSpeakerKey(string? speaker)
    {
        return string.IsNullOrWhiteSpace(speaker) ? "speaker_unknown" : speaker.Trim();
    }

    private static string BuildChunkSpeakerMapKey(int chunkIndex, string localSpeaker)
    {
        return $"{chunkIndex}:{NormalizeSpeakerKey(localSpeaker)}";
    }

    private static string AllocateGlobalSpeakerLabel(SpeakerDiarizationJobDocument job)
    {
        int index = Math.Max(1, job.NextSpeakerIndex);
        string label = $"Speaker {index}";
        job.NextSpeakerIndex = index + 1;
        return label;
    }

    public string BuildClipboardTranscriptText()
    {
        return BuildTranscribeAudioTranscriptText(includeTimeline: true);
    }

    public int RenameSpeakerAcrossTranscript(string fromSpeaker, string toSpeaker)
    {
        string normalizedFrom = fromSpeaker?.Trim() ?? string.Empty;
        string normalizedTo = toSpeaker?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedFrom))
        {
            throw new ArgumentException("Select a row with a speaker label before renaming.", nameof(fromSpeaker));
        }

        if (string.IsNullOrWhiteSpace(normalizedTo))
        {
            throw new ArgumentException("Enter a speaker name.", nameof(toSpeaker));
        }

        if (string.Equals(normalizedFrom, normalizedTo, StringComparison.Ordinal))
        {
            throw new ArgumentException("Enter a different speaker name.", nameof(toSpeaker));
        }

        int changedRows = 0;
        _isApplyingSpeakerDiarizationLabels = true;
        try
        {
            foreach (FinalizedTranscriptLineViewModel line in FinalizedTranscriptLines)
            {
                if (!string.Equals(line.SpeakerLabel, normalizedFrom, StringComparison.Ordinal))
                {
                    continue;
                }

                line.SpeakerLabel = normalizedTo;
                line.SpeakerLabelSource = SpeakerLabelSources.Manual;
                line.DiarizationRevision = null;
                line.LastDiarizedChunkIndex = null;
                changedRows++;
            }
        }
        finally
        {
            _isApplyingSpeakerDiarizationLabels = false;
        }

        if (changedRows == 0)
        {
            return 0;
        }

        RebuildFinalizedTextFromLines();
        NotifyCurrentTranscriptStateChanged();
        if (!TrySaveCurrentSession(
                updatedTranscriptMode: null,
                showErrorDialog: true,
                successLogMessage: $"Speaker renamed from '{normalizedFrom}' to '{normalizedTo}' in {changedRows:N0} row(s)."))
        {
            throw new InvalidOperationException("Speaker labels were renamed but the session could not be saved.");
        }

        LoadRecentSessions(_currentSessionDocument?.SessionId);
        return changedRows;
    }

    private Task OpenAudioFileAsync()
    {
        return OpenAudioFileWithIntentAsync(AudioFileSelectionIntent.OpenOnly);
    }

    private Task OpenAudioFileForTranscriptionAsync()
    {
        return OpenAudioFileWithIntentAsync(AudioFileSelectionIntent.OpenForTranscribe);
    }

    private Task OpenAudioFileWithIntentAsync(AudioFileSelectionIntent intent)
    {
        AppendLog("Command requested: Open Audio Preview File.");
        AppendLog("Opening file picker for audio preview.");

        string? selectedFilePath = SelectAudioFilePath("Select Audio File for Preview");
        if (string.IsNullOrWhiteSpace(selectedFilePath))
        {
            AppendLog("Open preview canceled: user did not select a file.");
            return Task.CompletedTask;
        }

        if (!IsSupportedAudioFilePath(selectedFilePath))
        {
            string extension = Path.GetExtension(selectedFilePath);
            AppendLog($"Open preview canceled: unsupported audio type '{extension}'.");
            RaiseError("Unsupported audio file. Use WAV, MP3, FLAC, AAC, M4A, OGG, WMA, or MP4.");
            return Task.CompletedTask;
        }

        HandleSelectedAudioFile(selectedFilePath, intent);
        return Task.CompletedTask;
    }

    public bool TryImportAudioFileFromPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            AppendLog("Dropped file rejected: file path is missing or no longer exists.");
            return false;
        }

        if (!IsSupportedAudioFilePath(filePath))
        {
            string extension = Path.GetExtension(filePath);
            AppendLog($"Dropped file rejected: unsupported audio type '{extension}'.");
            RaiseError("Unsupported audio file. Use WAV, MP3, FLAC, AAC, M4A, OGG, WMA, or MP4.");
            return false;
        }

        AppendLog($"Audio file dropped: {Path.GetFileName(filePath)}");
        return HandleSelectedAudioFile(filePath, AudioFileSelectionIntent.OpenForTranscribe);
    }

    public Task LoadRecentSessionAsync(TranscriptSessionSummary session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return LoadSessionByIdAsync(session.SessionId);
    }

    public string? ValidateSessionRename(string sessionId, string proposedDisplayName)
    {
        string normalizedDisplayName = proposedDisplayName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return "Session id is required.";
        }

        if (string.IsNullOrWhiteSpace(normalizedDisplayName))
        {
            return "Enter a session name.";
        }

        try
        {
            string currentDisplayName = GetCurrentSessionDisplayNameForRename(sessionId);
            if (string.Equals(currentDisplayName, normalizedDisplayName, StringComparison.OrdinalIgnoreCase))
            {
                return "The session name is unchanged.";
            }

            if (_sessionStore.SessionDisplayNameExists(normalizedDisplayName, excludeSessionId: sessionId))
            {
                return $"A session named '{normalizedDisplayName}' already exists.";
            }
        }
        catch (Exception ex)
        {
            return $"Unable to validate session name: {ex.Message}";
        }

        return null;
    }

    public async Task<bool> RenameSessionAsync(string sessionId, string proposedDisplayName)
    {
        string? validationError = ValidateSessionRename(sessionId, proposedDisplayName);
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            RaiseError(validationError);
            return false;
        }

        string normalizedDisplayName = proposedDisplayName.Trim();
        TranscriptSessionDocument? targetDocument = null;
        bool renamingLoadedSession = _currentSessionDocument is not null
            && string.Equals(_currentSessionDocument.SessionId, sessionId, StringComparison.OrdinalIgnoreCase);

        IsBusy = true;
        try
        {
            _sessionAutosaveTimer.Stop();

            await _sessionSaveSemaphore.WaitAsync();
            try
            {
                if (renamingLoadedSession)
                {
                    TranscriptSessionDocument loadedDocument = _currentSessionDocument
                        ?? throw new InvalidOperationException("Loaded session is no longer available.");
                    targetDocument = loadedDocument;
                    loadedDocument.DisplayName = normalizedDisplayName;
                    loadedDocument.UpdatedUtc = DateTimeOffset.UtcNow;
                    _sessionStore.Save(loadedDocument);
                }
                else
                {
                    _sessionStore.RenameSessionDisplayName(sessionId, normalizedDisplayName);
                }
            }
            finally
            {
                _sessionSaveSemaphore.Release();
            }

            if (renamingLoadedSession && _currentSessionDocument is not null)
            {
                _currentSessionDocument.DisplayName = normalizedDisplayName;
                _currentSessionDocument.UpdatedUtc = targetDocument!.UpdatedUtc;
                CurrentSessionDisplayName = normalizedDisplayName;
            }

            LoadRecentSessions(
                selectSessionId: renamingLoadedSession
                    ? _currentSessionDocument?.SessionId
                    : sessionId);
            AppendLog($"Session renamed: {normalizedDisplayName}.");
            return true;
        }
        catch (Exception ex)
        {
            RaiseError($"Unable to rename session: {ex.Message}");
            AppendLog($"Session rename failed: {ex.Message}");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public bool PrepareOpenedLiveSessionForNewRecordingStart()
    {
        if (_currentSessionDocument is null
            || !IsCurrentSessionLiveTranscriptionSession
            || !IsTranscriptDataEmpty)
        {
            return false;
        }

        bool deletedAnyAudio = _sessionStore.ClearSessionStoredAudio(_currentSessionDocument.SessionId);
        _currentSessionDocument.Audio.StoredRelativePath = string.Empty;
        _currentSessionDocument.Audio.FileSizeBytes = 0;
        _currentSessionDocument.Audio.DurationSeconds = null;
        _currentSessionDocument.Audio.Sha256 = string.Empty;
        _currentSessionDocument.UpdatedUtc = DateTimeOffset.UtcNow;

        _audioPlaybackService.UnloadFile();
        LoadedAudioFilePath = string.Empty;
        IsAudioPlaying = false;
        ResetAudioTimeline();
        CurrentSessionAudioIssue = string.Empty;
        IsCurrentSessionAudioMissing = false;
        LoadRecentSessions(_currentSessionDocument.SessionId);

        AppendLog(deletedAnyAudio
            ? "Opened live session has empty transcript. Cleared previous recorded audio and prepared for new live start."
            : "Opened live session has empty transcript. No stored audio found; prepared for new live start.");
        return true;
    }

    public bool PruneCurrentLiveRecordingAudioAfterTime(double keepUntilSecondsInclusive)
    {
        if (_currentSessionDocument is null
            || !string.Equals(
                _currentSessionDocument.Audio.StorageKind,
                AudioStorageKinds.LiveRecordingManifest,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool pruned = _sessionStore.PruneLiveRecordingAudioAfterTime(
            _currentSessionDocument.SessionId,
            keepUntilSecondsInclusive);
        TranscriptSessionLoadResult refreshed = _sessionStore.LoadSession(_currentSessionDocument.SessionId);
        _currentSessionDocument.Audio = TranscriptSessionStore.CloneAudioDocument(refreshed.Document.Audio);
        _currentSessionDocument.UpdatedUtc = refreshed.Document.UpdatedUtc;
        CurrentSessionAudioIssue = refreshed.AudioIssueMessage ?? string.Empty;
        IsCurrentSessionAudioMissing = !refreshed.AudioAvailable && !string.IsNullOrWhiteSpace(CurrentSessionAudioIssue);
        NotifyPropertyChanged(nameof(LoadedAudioFileName));
        return pruned;
    }

    private async Task DeleteSelectedSessionAsync()
    {
        string operationId = Guid.NewGuid().ToString("N")[..8];
        TranscriptSessionSummary? selectedSession = SelectedRecentSession;
        string? sessionId = selectedSession?.SessionId ?? _currentSessionDocument?.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            LogDeleteTrace(operationId, "No session id resolved. Delete request ignored.");
            return;
        }

        bool deletingLoadedSession = _currentSessionDocument is not null
            && string.Equals(_currentSessionDocument.SessionId, sessionId, StringComparison.OrdinalIgnoreCase);
        string? sessionDisplayName = selectedSession?.DisplayName;
        if (string.IsNullOrWhiteSpace(sessionDisplayName) && deletingLoadedSession)
        {
            sessionDisplayName = string.IsNullOrWhiteSpace(_currentSessionDocument!.DisplayName)
                ? _currentSessionDocument.Audio.OriginalFileName
                : _currentSessionDocument.DisplayName;
        }

        sessionDisplayName = string.IsNullOrWhiteSpace(sessionDisplayName)
            ? sessionId
            : sessionDisplayName;
        LogDeleteTrace(
            operationId,
            $"Delete requested. targetSessionId='{sessionId}', displayName='{TrimForLog(sessionDisplayName)}', " +
            $"selectedSessionId='{selectedSession?.SessionId ?? "(none)"}', loadedSessionId='{_currentSessionDocument?.SessionId ?? "(none)"}', " +
            $"deletingLoadedSession={deletingLoadedSession}, recentCount={RecentSessions.Count}, " +
            $"recentSample={BuildRecentSessionsSnapshot()}.");

        if (!ConfirmSessionDeletion())
        {
            AppendLog("Session deletion canceled.");
            LogDeleteTrace(operationId, "Delete canceled by confirmation flow.");
            return;
        }

        string? currentAudioPath = deletingLoadedSession ? LoadedAudioFilePath : null;

        IsBusy = true;
        try
        {
            _sessionAutosaveTimer.Stop();
            LogDeleteTrace(operationId, "Delete execution started. Autosave stopped.");

            if (deletingLoadedSession && IsAudioFileLoaded)
            {
                _audioPlaybackService.UnloadFile();
                LoadedAudioFilePath = string.Empty;
                IsAudioPlaying = false;
                ResetAudioTimeline();
                LogDeleteTrace(operationId, "Loaded session audio unloaded before deletion.");
            }

            await _sessionSaveSemaphore.WaitAsync();
            try
            {
                LogDeleteTrace(operationId, "Delete lock acquired. Calling SessionStore.DeleteSession.");
                _sessionStore.DeleteSession(sessionId);
                EnsureSessionDeletedFromStorage(sessionId);
                LogDeleteTrace(operationId, "SessionStore delete completed and storage verification passed.");
            }
            finally
            {
                _sessionSaveSemaphore.Release();
                LogDeleteTrace(operationId, "Delete lock released.");
            }

            if (deletingLoadedSession)
            {
                ClearCurrentSessionAfterDeletion();
            }
            else if (SelectedRecentSession is not null
                && string.Equals(SelectedRecentSession.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                SelectedRecentSession = null;
            }

            RemoveSessionFromRecentSessions(sessionId);
            LogDeleteTrace(
                operationId,
                $"Removed deleted session from in-memory list. recentCount={RecentSessions.Count}, " +
                $"recentSample={BuildRecentSessionsSnapshot()}.");

            LoadRecentSessions(selectSessionId: null);
            LogDeleteTrace(
                operationId,
                $"Recent sessions reloaded. selectedSessionId='{SelectedRecentSession?.SessionId ?? "(none)"}', " +
                $"loadedSessionId='{_currentSessionDocument?.SessionId ?? "(none)"}', recentCount={RecentSessions.Count}, " +
                $"recentSample={BuildRecentSessionsSnapshot()}.");
            AppendLog($"Session deleted: {sessionDisplayName}.");
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(currentAudioPath)
                && File.Exists(currentAudioPath))
            {
                TryLoadAudioPreview(currentAudioPath);
            }

            RaiseError($"Unable to delete session: {ex.Message}");
            AppendLog($"Session deletion failed: {ex.Message}");
            LogDeleteTrace(operationId, $"Delete failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            LogDeleteTrace(operationId, "Delete operation finished.");
        }
    }

    private string GetCurrentSessionDisplayNameForRename(string sessionId)
    {
        if (_currentSessionDocument is not null
            && string.Equals(_currentSessionDocument.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(_currentSessionDocument.DisplayName)
                ? _currentSessionDocument.Audio.OriginalFileName
                : _currentSessionDocument.DisplayName;
        }

        return _sessionStore.GetSessionDisplayName(sessionId);
    }

    private Task CloseAsync()
    {
        RaiseSessionLoadStarting();
        _sessionAutosaveTimer.Stop();
        TrySaveCurrentSession(
            updatedTranscriptMode: null,
            showErrorDialog: false,
            successLogMessage: string.Empty);
        ClearOutputCore(unloadAudioPreview: true, clearSessionContext: true);
        return Task.CompletedTask;
    }

    private void ClearOutputCore(bool unloadAudioPreview, bool clearSessionContext)
    {
        _sessionAutosaveTimer.Stop();

        if (unloadAudioPreview)
        {
            _audioPlaybackService.UnloadFile();
            LoadedAudioFilePath = string.Empty;
            IsAudioPlaying = false;
            ResetAudioTimeline();
        }

        _suppressSessionAutosave = true;
        try
        {
            UnsubscribeFromFinalizedLineChanges();
            FinalizedTranscriptLines.Clear();
            FinalizedText = string.Empty;
            ClearProcessLogs();
        }
        finally
        {
            _suppressSessionAutosave = false;
        }

        if (clearSessionContext)
        {
            _currentSessionDocument = null;
            SelectedRecentSession = null;
            ClearPendingImportedAudioSelection();
            CurrentSessionDisplayName = "No session loaded.";
            CurrentSessionAudioIssue = string.Empty;
            IsCurrentSessionAudioMissing = false;
            NotifyCurrentTranscriptStateChanged();
            NotifyInteractionAvailabilityChanged();
            NotifyPropertyChanged(nameof(HasCurrentSession));
            NotifyPropertyChanged(nameof(HasPendingSessionSelection));
            NotifyPropertyChanged(nameof(LoadedAudioFileName));

            LoadRecentSessions(selectSessionId: null);
        }

        RefreshCommandStates();
    }

    private void ClearTranscriptAndLogs(bool unloadAudioPreview, TranscriptGenerationMode transcriptMode)
    {
        _sessionAutosaveTimer.Stop();

        if (unloadAudioPreview)
        {
            _audioPlaybackService.UnloadFile();
        }

        _suppressSessionAutosave = true;
        try
        {
            UnsubscribeFromFinalizedLineChanges();
            FinalizedTranscriptLines.Clear();
            FinalizedText = string.Empty;

            ClearProcessLogs();
        }
        finally
        {
            _suppressSessionAutosave = false;
        }

        RefreshCommandStates();
    }

    private Task PlayAudioAsync()
    {
        if (!IsAudioFileLoaded || !IsMediaPlayerPanelVisible)
        {
            return Task.CompletedTask;
        }

        try
        {
            _audioPlaybackService.Play();
            IsAudioPlaying = _audioPlaybackService.IsPlaying;
        }
        catch (Exception ex)
        {
            RaiseError($"Unable to play audio preview: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private Task PauseAudioAsync()
    {
        if (!IsAudioFileLoaded)
        {
            return Task.CompletedTask;
        }

        try
        {
            _audioPlaybackService.Pause();
            IsAudioPlaying = _audioPlaybackService.IsPlaying;
        }
        catch (Exception ex)
        {
            RaiseError($"Unable to pause audio preview: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private bool CanClose()
    {
        return HasCurrentSession && !IsBusy;
    }

    private bool CanOpenAudioFile()
    {
        return !IsBusy;
    }

    private bool CanDeleteSelectedSession()
    {
        return !IsBusy && (HasCurrentSession || SelectedRecentSession is not null);
    }

    private void EnsureSessionDeletedFromStorage(string sessionId)
    {
        string sessionDirectoryPath = _sessionStore.GetSessionDirectoryPath(sessionId);
        if (!Directory.Exists(sessionDirectoryPath))
        {
            return;
        }

        for (int attempt = 0; attempt < 3 && Directory.Exists(sessionDirectoryPath); attempt++)
        {
            Thread.Sleep(50);
        }

        if (Directory.Exists(sessionDirectoryPath))
        {
            throw new IOException($"Session directory still exists after deletion: '{sessionDirectoryPath}'.");
        }
    }

    private void RemoveSessionFromRecentSessions(string sessionId)
    {
        _allRecentSessions = _allRecentSessions
            .Where(item => !string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        RefreshRecentSessionsView();
    }

    private string BuildRecentSessionsSnapshot()
    {
        if (RecentSessions.Count == 0)
        {
            return "[]";
        }

        return "[" + string.Join(
            ",",
            RecentSessions
                .Take(6)
                .Select(session => session.SessionId)) + (RecentSessions.Count > 6 ? ",..." : string.Empty) + "]";
    }

    private void LogDeleteTrace(string operationId, string message)
    {
        string payload = $"[{operationId}] {message}";
        AppendLog($"Session delete trace: {payload}");
        _processLogService.Log("SessionDelete", payload, ProcessLogLevel.Debug);
    }

    private bool CanPlayAudio()
    {
        return IsAudioFileLoaded && IsMediaPlayerPanelVisible && !IsAudioPlaying;
    }

    private bool CanPauseAudio()
    {
        return IsAudioFileLoaded && IsAudioPlaying;
    }

    private static bool ContainsWholeNormalizedPhrase(string haystack, string needle)
    {
        if (needle.Length == 0 || haystack.Length == 0 || needle.Length > haystack.Length)
        {
            return false;
        }

        int index = haystack.IndexOf(needle, StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        bool startsAtWordBoundary = index == 0 || haystack[index - 1] == ' ';
        int end = index + needle.Length;
        bool endsAtWordBoundary = end == haystack.Length || haystack[end] == ' ';
        return startsAtWordBoundary && endsAtWordBoundary;
    }

    private static int CountWords(string text)
    {
        return text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
    }

    private static int CountSuffixPrefixTokenOverlap(string leftNormalizedText, string rightNormalizedText)
    {
        string[] leftTokens = leftNormalizedText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] rightTokens = rightNormalizedText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (leftTokens.Length == 0 || rightTokens.Length == 0)
        {
            return 0;
        }

        int maxWindow = Math.Min(leftTokens.Length, rightTokens.Length);
        for (int size = maxWindow; size >= 1; size--)
        {
            bool matches = true;
            for (int index = 0; index < size; index++)
            {
                if (!string.Equals(
                        leftTokens[leftTokens.Length - size + index],
                        rightTokens[index],
                        StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return size;
            }
        }

        return 0;
    }

    private static readonly TimeSpan LiveBoundaryTrimProximityTolerance = TimeSpan.FromSeconds(12);

    private TranscriptionTimedLine TrimBoundaryOverlapWithPreviousLine(
        TranscriptionTimedLine candidate,
        IReadOnlyList<FinalizedTranscriptLineViewModel> referenceLines)
    {
        if (referenceLines.Count == 0 || string.IsNullOrWhiteSpace(candidate.Text))
        {
            return candidate;
        }

        string[] candidateTokens = SplitLiveBoundaryTokens(candidate.Text);
        if (candidateTokens.Length == 0)
        {
            return candidate;
        }

        int bestOverlap = 0;
        int bestTrimCount = 0;
        foreach (FinalizedTranscriptLineViewModel previous in referenceLines
                     .Reverse()
                     .Where(line => !line.IsProvisional && !string.IsNullOrWhiteSpace(line.Text))
                     .Take(6))
        {
            if (previous.StartOffset is not TimeSpan previousStart)
            {
                continue;
            }

            TimeSpan previousEnd = ResolveLiveSegmentEnd(previousStart, previous.EndOffset);
            TimeSpan candidateEnd = ResolveLiveSegmentEnd(candidate.StartOffset, candidate.EndOffset);
            if (!RangesOverlapOrTouch(previousStart, previousEnd, candidate.StartOffset, candidateEnd))
            {
                TimeSpan gap = candidate.StartOffset >= previousEnd
                    ? candidate.StartOffset - previousEnd
                    : previousStart - candidateEnd;
                if (gap > LiveBoundaryTrimProximityTolerance)
                {
                    continue;
                }
            }

            string[] previousTokens = SplitLiveBoundaryTokens(previous.Text);
            int directOverlap = CountBoundaryPrefixTokenOverlap(previousTokens, candidateTokens);
            if (directOverlap > bestOverlap)
            {
                bestOverlap = directOverlap;
                bestTrimCount = directOverlap;
            }

            int fuzzyOverlap = CountBoundaryPrefixTokenOverlapAllowingSingleMismatch(previousTokens, candidateTokens);
            if (fuzzyOverlap > bestOverlap)
            {
                bestOverlap = fuzzyOverlap;
                bestTrimCount = fuzzyOverlap;
            }

            int fillerAdjustedTrimCount = CountBoundaryPrefixTokenOverlapWithLeadingFiller(previousTokens, candidateTokens);
            if (fillerAdjustedTrimCount > bestTrimCount)
            {
                bestTrimCount = fillerAdjustedTrimCount;
                bestOverlap = Math.Max(bestOverlap, fillerAdjustedTrimCount - 1);
            }
        }

        if (bestTrimCount == 0)
        {
            return candidate;
        }

        string trimmedText = string.Join(" ", candidateTokens.Skip(bestTrimCount)).Trim();
        if (trimmedText.Length == 0)
        {
            return candidate;
        }

        AppendLog(
            $"Trimmed {bestOverlap} overlapped boundary words at {FormatOffset(candidate.StartOffset)}: '{BuildPreview(trimmedText)}'.");
        return new TranscriptionTimedLine(
            trimmedText,
            candidate.StartOffset,
            candidate.EndOffset,
            candidate.IsTimestampEstimated);
    }

    private static string[] SplitLiveBoundaryTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        return text
            .Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static int CountBoundaryPrefixTokenOverlap(string[] leftTokens, string[] rightTokens)
    {
        if (leftTokens.Length == 0 || rightTokens.Length == 0)
        {
            return 0;
        }

        int bestRightSize = 0;
        int maxLeftWindow = leftTokens.Length;
        int maxRightWindow = rightTokens.Length;
        for (int leftSize = maxLeftWindow; leftSize >= 1; leftSize--)
        {
            string left = BuildCompactTokenWindow(leftTokens, leftTokens.Length - leftSize, leftSize);
            if (left.Length == 0)
            {
                continue;
            }

            for (int rightSize = maxRightWindow; rightSize >= 1; rightSize--)
            {
                string right = BuildCompactTokenWindow(rightTokens, 0, rightSize);
                if (right.Length == 0)
                {
                    continue;
                }

                if (string.Equals(left, right, StringComparison.Ordinal))
                {
                    bestRightSize = Math.Max(bestRightSize, rightSize);
                    break;
                }
            }
        }

        return bestRightSize >= 2 ? bestRightSize : 0;
    }

    private static int CountBoundaryPrefixTokenOverlapAllowingSingleMismatch(string[] leftTokens, string[] rightTokens)
    {
        if (leftTokens.Length == 0 || rightTokens.Length == 0)
        {
            return 0;
        }

        string[] normalizedLeft = leftTokens
            .Select(NormalizeLiveToken)
            .Where(token => token.Length > 0)
            .ToArray();
        string[] normalizedRight = rightTokens
            .Select(NormalizeLiveToken)
            .Where(token => token.Length > 0)
            .ToArray();
        if (normalizedLeft.Length == 0 || normalizedRight.Length == 0)
        {
            return 0;
        }

        int maxWindow = Math.Min(normalizedLeft.Length, normalizedRight.Length);
        for (int size = maxWindow; size >= 4; size--)
        {
            int leftStart = normalizedLeft.Length - size;
            int mismatches = 0;
            for (int index = 0; index < size; index++)
            {
                if (!string.Equals(normalizedLeft[leftStart + index], normalizedRight[index], StringComparison.Ordinal))
                {
                    mismatches++;
                    if (mismatches > 1)
                    {
                        break;
                    }
                }
            }

            if (mismatches <= 1)
            {
                return size;
            }
        }

        return 0;
    }

    private static int CountBoundaryPrefixTokenOverlapWithLeadingFiller(string[] leftTokens, string[] rightTokens)
    {
        if (rightTokens.Length < 2)
        {
            return 0;
        }

        string first = NormalizeLiveToken(rightTokens[0]);
        if (first is not ("you" or "we" or "they" or "it" or "this" or "that"))
        {
            return 0;
        }

        int shiftedOverlap = CountBoundaryPrefixTokenOverlap(leftTokens, rightTokens.Skip(1).ToArray());
        return shiftedOverlap >= 3 ? shiftedOverlap + 1 : 0;
    }

    private IEnumerable<FinalizedTranscriptLineViewModel> EnumerateRecentBoundaryLines()
    {
        return FinalizedTranscriptLines
            .Reverse()
            .Where(line => !line.IsProvisional && !string.IsNullOrWhiteSpace(line.Text))
            .Take(6);
    }

    private static string BuildCompactTokenWindow(string[] tokens, int startIndex, int count)
    {
        var builder = new StringBuilder();
        for (int index = startIndex; index < startIndex + count && index < tokens.Length; index++)
        {
            builder.Append(NormalizeLiveToken(tokens[index]));
        }

        return builder.ToString();
    }

    private static int CountSuffixPrefixTokenOverlapWithoutLeadingFiller(
        string leftNormalizedText,
        string rightNormalizedText)
    {
        string[] rightTokens = rightNormalizedText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rightTokens.Length < 2)
        {
            return 0;
        }

        string first = rightTokens[0];
        if (first is not ("you" or "we" or "they" or "it" or "this" or "that"))
        {
            return 0;
        }

        string shiftedRight = string.Join(" ", rightTokens.Skip(1));
        return CountSuffixPrefixTokenOverlap(leftNormalizedText, shiftedRight);
    }

    private bool CanRequestUpgradeToPremium()
    {
        return !HasPremium
            && !IsPremiumEntitlementChecking
            && CanPromptPremiumPurchase;
    }

    private Task RequestUpgradeToPremiumAsync()
    {
        if (IsDevelopmentUnpackagedMode)
        {
            return Task.CompletedTask;
        }

        PremiumUpsellRequested?.Invoke(
            this,
            new PremiumUpsellRequest(
                "Premium",
                $"{PremiumProductDisplayName} unlocks all premium features. Upgrade in Microsoft Store to continue."));
        return Task.CompletedTask;
    }

    private bool CanCheckForAppUpdate()
    {
        return _appUpdateService is not null
            && IsUpdateButtonEnabled;
    }

    private async Task CheckForAppUpdateAsync()
    {
        if (_appUpdateService is null)
        {
            return;
        }

        await _appUpdateService.RunUserInitiatedUpdateFlowAsync();
    }

    private void RefreshCommandStates()
    {
        CloseCommand.RaiseCanExecuteChanged();
        OpenAudioFileCommand.RaiseCanExecuteChanged();
        OpenAudioFileForTranscriptionCommand.RaiseCanExecuteChanged();
        DeleteSelectedSessionCommand.RaiseCanExecuteChanged();
        PlayAudioCommand.RaiseCanExecuteChanged();
        PauseAudioCommand.RaiseCanExecuteChanged();
        UpgradeToPremiumCommand.RaiseCanExecuteChanged();
        CheckForAppUpdateCommand.RaiseCanExecuteChanged();
    }

    private sealed class PassThroughChunkedAudioTranscriptionService : IChunkedAudioTranscriptionService
    {
        private readonly IAudioTranscriptionService _requestService;

        public PassThroughChunkedAudioTranscriptionService(IAudioTranscriptionService requestService)
        {
            _requestService = requestService ?? throw new ArgumentNullException(nameof(requestService));
        }

        public Task<TranscriptionResult> TranscribeAudioFileAsync(
            string audioFilePath,
            string model,
            CancellationToken cancellationToken,
            IProgress<TranscriptionProgressSnapshot>? progress = null,
            int startChunkIndex = 0,
            IReadOnlyList<TranscriptionTimedLine>? existingCommittedLines = null,
            Action<TranscriptionChunkCommit>? chunkCommitted = null)
        {
            return _requestService.TranscribeAudioFileAsync(audioFilePath, model, cancellationToken, progress);
        }
    }

    private void NotifyInteractionAvailabilityChanged()
    {
        NotifyPropertyChanged(nameof(IsEngineSelectionEnabled));
        NotifyPropertyChanged(nameof(HasPremium));
        NotifyPropertyChanged(nameof(IsPremiumProductAvailable));
        NotifyPropertyChanged(nameof(PremiumEntitlementState));
        NotifyPropertyChanged(nameof(IsPremiumEntitlementChecking));
        NotifyPropertyChanged(nameof(IsPremiumEntitlementVerificationFailed));
        NotifyPropertyChanged(nameof(CanPromptPremiumPurchase));
        NotifyPropertyChanged(nameof(IsPremiumStatusBannerVisible));
        NotifyPropertyChanged(nameof(IsDevelopmentUnpackagedMode));
        NotifyPropertyChanged(nameof(PremiumProductDisplayName));
        NotifyPropertyChanged(nameof(HasUnlimitedLiveTranscription));
        NotifyPropertyChanged(nameof(LiveTranscriptionLimit));
        NotifyPropertyChanged(nameof(CanUseLiveTranscription));
        NotifyPropertyChanged(nameof(CanUseSpeakerDiarization));
        NotifyPropertyChanged(nameof(PremiumStatusText));
        NotifyPropertyChanged(nameof(ApplicationAccessTierText));
        NotifyPropertyChanged(nameof(IsTranscribeAudioTranscriptionEnabled));
        NotifyPropertyChanged(nameof(IsTranscriptGenerationEnabled));
        NotifyPropertyChanged(nameof(CanRunLivePrimaryAction));
        NotifyPropertyChanged(nameof(CanRunTranscribeAudioPrimaryAction));
        NotifyPropertyChanged(nameof(CanRunDetectSpeakerPrimaryAction));
        NotifyPropertyChanged(nameof(CanRunDetectSpeakersPrimaryAction));
        NotifyPropertyChanged(nameof(IsApplicationAccessTierVisible));
        NotifyPropertyChanged(nameof(IsUpgradeButtonVisible));
        NotifyPropertyChanged(nameof(IsUpdateButtonVisible));
        NotifyPropertyChanged(nameof(IsUpdateButtonEnabled));
        NotifyPropertyChanged(nameof(AppVersionText));
    }

    private void NotifyCurrentTranscriptStateChanged()
    {
        NotifyPropertyChanged(nameof(CurrentTranscriptLines));
        NotifyPropertyChanged(nameof(HasCurrentTranscriptLines));
        NotifyPropertyChanged(nameof(IsTranscriptEmptyStateVisible));
        NotifyPropertyChanged(nameof(IsTranscriptDataEmpty));
        NotifyPropertyChanged(nameof(HasNonEmptyCurrentTranscriptionSession));
        NotifyPropertyChanged(nameof(ShouldShowLiveTranscriptionPanel));
        NotifyPropertyChanged(nameof(ShouldShowTranscriptChooseFileAction));
        NotifyPropertyChanged(nameof(ShouldShowTranscriptTranscribeAudioAction));
        NotifyPropertyChanged(nameof(CanCopyTranscript));
        NotifyPropertyChanged(nameof(CanExportAudio));
        NotifyPropertyChanged(nameof(CanRunDetectSpeakerPrimaryAction));
        NotifyPropertyChanged(nameof(CanRunDetectSpeakersPrimaryAction));
        NotifyPropertyChanged(nameof(IsTranscribeAudioTranscriptViewSelected));
        NotifyPropertyChanged(nameof(HasSpeakerLabels));
        NotifyPropertyChanged(nameof(IsCurrentTranscriptionJobIncomplete));
        NotifyPropertyChanged(nameof(TranscriptEmptyStateTitle));
        NotifyPropertyChanged(nameof(TranscriptEmptyStateMessage));
        NotifyPropertyChanged(nameof(IsCurrentSessionLiveTranscriptionSession));
        NotifyPropertyChanged(nameof(IsCurrentSessionAudioTranscriptionSession));
    }

    private void SaveAppPreferences()
    {
        _appPreferencesStore.Save(new AppPreferencesSnapshot(
            CopyFinalizedWithTimeline: false,
            AutoTranscribeWithAi: true,
            ThemePreference: _selectedThemePreference,
            AutoPlayTimelineSelection: _autoPlayTimelineSelection,
            RecentSessionsSortMode: _recentSessionsSortMode,
            RecentSessionsSortDescending: _recentSessionsSortDescending,
            LiveAudioSourceKind: _preferredLiveAudioSourceKind,
            LiveAudioDeviceNumber: _preferredLiveAudioDeviceNumber,
            SelectedEngineId: SelectedEngineId,
            LiveAudioAutoGainEnabled: _liveAudioAutoGainEnabled,
            LiveAudioGainLevel: _liveAudioGainLevel,
            TranscriptExportDirectory: _transcriptExportDirectory));
    }

    private bool EnsureSelectedModelConfigured()
    {
        if (SelectedEngine is null)
        {
            AppendLog("Transcription configuration check failed: no available transcription mode.");
            return false;
        }

        AppendLog("Transcription configuration verified.");
        return true;
    }

    private string ResolveSelectedFileTranscriptionEngineId()
    {
        string selectedEngineId = SelectedEngine?.Id ?? string.Empty;
        if (!TranscriptionModelCatalog.SupportsFileTranscription(selectedEngineId))
        {
            throw new InvalidOperationException("Select a transcription engine that supports audio files.");
        }

        return selectedEngineId;
    }

    private static IProgress<TranscriptionProgressSnapshot>? CreateOverallTranscribeAudioProgress(
        IProgress<TranscriptionProgressSnapshot>? progress)
    {
        if (progress is null)
        {
            return null;
        }

        const double transcriptionWeight = 0.8d;
        const double diarizationWeight = 0.2d;
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;

        return new Progress<TranscriptionProgressSnapshot>(snapshot =>
        {
            bool isDiarizationPhase = snapshot.Phase is TranscriptionProgressPhase.RunningSpeakerDiarization
                or TranscriptionProgressPhase.MergingSpeakerLabels;

            double stagePercent = snapshot.Percent;
            double overallPercent = snapshot.Percent;
            TimeSpan processedAudio = snapshot.ProcessedAudio;
            TimeSpan elapsed = DateTimeOffset.UtcNow - startedUtc;

            if (isDiarizationPhase)
            {
                stagePercent = snapshot.Phase switch
                {
                    TranscriptionProgressPhase.RunningSpeakerDiarization => snapshot.Percent * 0.9d,
                    TranscriptionProgressPhase.MergingSpeakerLabels => 95,
                    _ => snapshot.Percent,
                };

                overallPercent = snapshot.Phase switch
                {
                    TranscriptionProgressPhase.RunningSpeakerDiarization =>
                        (transcriptionWeight + (diarizationWeight * (stagePercent / 100d))) * 100d,
                    TranscriptionProgressPhase.MergingSpeakerLabels =>
                        (transcriptionWeight + (diarizationWeight * 0.95d)) * 100d,
                    _ => snapshot.Percent,
                };
            }
            else
            {
                overallPercent = snapshot.Phase == TranscriptionProgressPhase.Completed
                    ? transcriptionWeight * 100d
                    : snapshot.Percent * transcriptionWeight;
            }

            progress.Report(TranscriptionProgressSnapshot.Create(
                snapshot.Phase,
                stagePercent,
                overallPercent,
                snapshot.CurrentChunk,
                snapshot.TotalChunks,
                processedAudio,
                snapshot.TotalAudio,
                elapsed,
                snapshot.DetailMessage));
        });
    }

    private static string? SelectAudioFilePath(string dialogTitle)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = dialogTitle,
            Filter = AudioFileDialogFilter,
            Multiselect = false,
        };

        return dialog.ShowDialog() == true
            ? dialog.FileName
            : null;
    }

    private void LoadSessionFromImportedAudio(string sourceFilePath)
    {
        try
        {
            RaiseSessionLoadStarting();
            ClearPendingImportedAudioSelection();
            _sessionAutosaveTimer.Stop();
            TrySaveCurrentSession(
                updatedTranscriptMode: null,
                showErrorDialog: false,
                successLogMessage: string.Empty);

            TranscriptSessionLoadResult loadResult = _sessionStore.ImportAudioFile(sourceFilePath);
            LoadSessionResult(loadResult, showAudioIssueDialog: true);
            LoadRecentSessions(loadResult.Document.SessionId, pinSelectedToTop: true);
        }
        catch (Exception ex)
        {
            RaiseError($"Unable to import audio into a session: {ex.Message}");
        }
    }

    private bool HandleSelectedAudioFile(string sourceFilePath, AudioFileSelectionIntent intent)
    {
        if (Interlocked.Exchange(ref _isHandlingAudioSelection, 1) == 1)
        {
            AppendLog("Audio selection ignored: another selection is already being processed.");
            return false;
        }

        try
        {
            RaiseSessionLoadStarting();
            _sessionAutosaveTimer.Stop();
            TrySaveCurrentSession(
                updatedTranscriptMode: null,
                showErrorDialog: false,
                successLogMessage: string.Empty);

            if (_sessionStore.TryLoadExistingSessionForAudio(sourceFilePath, out TranscriptSessionLoadResult? loadResult)
                && loadResult is not null)
            {
                ClearPendingImportedAudioSelection();
                LoadSessionResult(loadResult, showAudioIssueDialog: true);
                LoadRecentSessions(loadResult.Document.SessionId, pinSelectedToTop: true);
                AppendLog("Selected audio matched an existing session and was loaded.");

                if (intent == AudioFileSelectionIntent.OpenForTranscribe)
                {
                    bool resumeAvailable = GetTranscriptProcessingPanelSessionSnapshot().ResumeAvailable;
                    if (IsTranscriptDataEmpty || resumeAvailable)
                    {
                        _uiContext.Post(_ => NewAudioFileStagedForTranscribeAudio?.Invoke(this, EventArgs.Empty), null);
                    }
                    else
                    {
                        RaiseToast(
                            "Existing session opened",
                            "A transcript already exists for this audio. Session opened without starting a new Transcribe Audio flow.",
                            ToastNotificationType.Info);
                    }
                }

                return true;
            }

            if (!EnsureCanCreateNewSession("audio session"))
            {
                AppendLog("Audio selection blocked: Basic session limit reached.");
                return false;
            }

            if (intent == AudioFileSelectionIntent.OpenForTranscribe)
            {
                TranscriptSessionLoadResult importedLoadResult = _sessionStore.ImportAudioFile(sourceFilePath);
                LoadSessionResult(importedLoadResult, showAudioIssueDialog: true);
                LoadRecentSessions(importedLoadResult.Document.SessionId, pinSelectedToTop: true);
                ClearPendingImportedAudioSelection();
                AppendLog("Selected audio does not have an existing session. Created and loaded a new session, ready for Transcribe Audio.");
                _uiContext.Post(_ => NewAudioFileStagedForTranscribeAudio?.Invoke(this, EventArgs.Empty), null);
                return true;
            }

            ClearOutputCore(unloadAudioPreview: true, clearSessionContext: true);
            if (!TryLoadAudioPreview(sourceFilePath))
            {
                RaiseError("Unable to load the selected audio file for preview.");
                AppendLog("Selected audio could not be staged because preview loading failed.");
                return false;
            }

            _pendingImportedAudioFilePath = sourceFilePath;
            AppendLog("Selected audio does not have an existing session. Preview loaded and session creation is deferred until Generate is clicked.");
            return true;
        }
        catch (Exception ex)
        {
            RaiseError($"Unable to process selected audio file: {ex.Message}");
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _isHandlingAudioSelection, 0);
        }
    }

    private bool EnsureCurrentSessionForLoadedAudio()
    {
        string sourcePathForSessionImport = string.IsNullOrWhiteSpace(_pendingImportedAudioFilePath)
            ? LoadedAudioFilePath
            : _pendingImportedAudioFilePath;
        return EnsureCurrentSessionForAudioFile(sourcePathForSessionImport);
    }

    private bool EnsureCurrentSessionForAudioFile(string sourcePathForSessionImport)
    {
        if (_currentSessionDocument is not null)
        {
            ClearPendingImportedAudioSelection();
            return true;
        }

        if (string.IsNullOrWhiteSpace(sourcePathForSessionImport))
        {
            return false;
        }

        if (!EnsureCanCreateNewSession("audio session"))
        {
            AppendLog("Session creation for loaded audio blocked: Basic session limit reached.");
            return false;
        }

        try
        {
            RaiseSessionLoadStarting();
            AppendLog(
                $"Ensuring current session. importSource='{sourcePathForSessionImport}', " +
                "operation='TranscribeAudio'.");
            TranscriptSessionLoadResult loadResult = _sessionStore.ImportAudioFile(sourcePathForSessionImport);
            LoadSessionResult(loadResult, showAudioIssueDialog: true);

            LoadRecentSessions(loadResult.Document.SessionId, pinSelectedToTop: true);
            ClearPendingImportedAudioSelection();
            return true;
        }
        catch (Exception ex)
        {
            RaiseError($"Unable to create a session for the loaded audio file: {ex.Message}");
            AppendLog($"Session creation for loaded audio failed: {ex.Message}");
            return false;
        }
    }

    private Task LoadSessionByIdAsync(string sessionId)
    {
        try
        {
            RaiseSessionLoadStarting();
            _sessionAutosaveTimer.Stop();
            TrySaveCurrentSession(
                updatedTranscriptMode: null,
                showErrorDialog: false,
                successLogMessage: string.Empty);

            TranscriptSessionLoadResult loadResult = _sessionStore.LoadSession(sessionId);
            loadResult = ConvertLegacyCompletedLiveSessionIfNeeded(loadResult);
            LoadSessionResult(loadResult, showAudioIssueDialog: false);
            TryRestoreCurrentSessionAudioAfterOpen();
            LoadRecentSessions(sessionId);
            AppendLog(IsCurrentSessionAudioMissing
                ? $"Session loaded: {CurrentSessionDisplayName}. Audio restore is still required."
                : $"Session loaded: {CurrentSessionDisplayName}.");
        }
        catch (Exception ex)
        {
            RaiseError($"Unable to load session: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private TranscriptSessionLoadResult ConvertLegacyCompletedLiveSessionIfNeeded(TranscriptSessionLoadResult loadResult)
    {
        if (!ShouldConvertLegacyCompletedLiveSessionOnOpen(loadResult))
        {
            return loadResult;
        }

        if (string.IsNullOrWhiteSpace(loadResult.AudioFilePath) || !File.Exists(loadResult.AudioFilePath))
        {
            return loadResult;
        }

        return ConvertLiveSessionToImportedAudio(loadResult.Document, loadResult.AudioFilePath);
    }

    private bool ShouldConvertLegacyCompletedLiveSessionOnOpen(TranscriptSessionLoadResult loadResult)
    {
        return string.Equals(
                loadResult.Document.Audio.StorageKind,
                AudioStorageKinds.LiveRecordingManifest,
                StringComparison.OrdinalIgnoreCase)
            && HasTranscriptContent(loadResult.Document.Transcript);
    }

    private static bool HasTranscriptContent(TranscriptSessionTranscriptDocument transcript)
    {
        return !string.IsNullOrWhiteSpace(transcript.FinalText)
            || transcript.Lines.Any(line => !string.IsNullOrWhiteSpace(line.Text));
    }

    private TranscriptSessionLoadResult ConvertLiveSessionToImportedAudio(
        TranscriptSessionDocument sessionDocument,
        string liveManifestPath)
    {
        string convertedAudioFileName = BuildConvertedLiveSessionAudioFileName(sessionDocument.DisplayName);
        (string materializedAudioPath, bool deleteMaterializedAudioFile) =
            PrepareAudioFilePathForTranscription(liveManifestPath);

        try
        {
            return _sessionStore.ConvertLiveSessionToImportedAudio(
                sessionDocument.SessionId,
                materializedAudioPath,
                convertedAudioFileName);
        }
        finally
        {
            if (deleteMaterializedAudioFile)
            {
                DeleteTemporaryTranscriptionAudioFile(materializedAudioPath);
            }
        }
    }

    private void LoadSessionResult(TranscriptSessionLoadResult loadResult, bool showAudioIssueDialog)
    {
        RaiseSessionLoadStarting();
        _sessionAutosaveTimer.Stop();
        _suppressSessionAutosave = true;

        try
        {
            ClearPendingImportedAudioSelection();
            _currentSessionDocument = loadResult.Document;
            CurrentSessionDisplayName = string.IsNullOrWhiteSpace(loadResult.Document.DisplayName)
                ? loadResult.Document.Audio.OriginalFileName
                : loadResult.Document.DisplayName;

            NotifyPropertyChanged(nameof(HasCurrentSession));
            NotifyPropertyChanged(nameof(HasPendingSessionSelection));
            NotifyPropertyChanged(nameof(ShouldShowTranscriptChooseFileAction));
            NotifyPropertyChanged(nameof(ShouldShowTranscriptTranscribeAudioAction));
            NotifyPropertyChanged(nameof(IsTranscriptEmptyStateVisible));
            NotifyPropertyChanged(nameof(CanRunDetectSpeakerPrimaryAction));
            NotifyPropertyChanged(nameof(CanRunDetectSpeakersPrimaryAction));
            NotifyPropertyChanged(nameof(LoadedAudioFileName));
            RefreshCommandStates();

            ApplyTranscriptDocument(loadResult.Document.Transcript);
            ApplyEditingDocument(loadResult.Document.Editing);
            NotifyCurrentTranscriptStateChanged();

            CurrentSessionAudioIssue = string.Empty;
            IsCurrentSessionAudioMissing = false;

            if (loadResult.AudioAvailable && !string.IsNullOrWhiteSpace(loadResult.AudioFilePath))
            {
                bool loaded = TryLoadAudioPreview(loadResult.AudioFilePath);
                if (!loaded)
                {
                    CurrentSessionAudioIssue = "The stored session audio file could not be loaded. Reopen the session and select the original audio file to restore playback.";
                    IsCurrentSessionAudioMissing = true;
                }
            }
            else
            {
                _audioPlaybackService.UnloadFile();
                LoadedAudioFilePath = string.Empty;
                IsAudioPlaying = false;
                ResetAudioTimeline();
                CurrentSessionAudioIssue = loadResult.AudioIssueMessage ?? string.Empty;
                IsCurrentSessionAudioMissing = !string.IsNullOrWhiteSpace(CurrentSessionAudioIssue);
            }
        }
        finally
        {
            _suppressSessionAutosave = false;
        }

        bool suppressMissingAudioToastForEmptyLiveSession =
            IsCurrentSessionLiveTranscriptionSession
            && IsTranscriptDataEmpty;

        if (IsCurrentSessionAudioMissing
            && !string.IsNullOrWhiteSpace(CurrentSessionAudioIssue)
            && !suppressMissingAudioToastForEmptyLiveSession)
        {
            RaiseToast(
                "Session audio unavailable",
                $"{CurrentSessionAudioIssue} Reopen the session and select the original audio file to continue playback or retranscription.",
                ToastNotificationType.Warning);
        }
    }

    private void TryRestoreCurrentSessionAudioAfterOpen()
    {
        if (_currentSessionDocument is null || !IsCurrentSessionAudioMissing)
        {
            return;
        }

        if (string.Equals(
                _currentSessionDocument.Audio.StorageKind,
                AudioStorageKinds.LiveRecordingManifest,
                StringComparison.OrdinalIgnoreCase))
        {
            AppendLog("Live recording audio is unavailable; there is no external original audio file to restore.");
            return;
        }

        AppendLog("Session audio is unavailable. Prompting for the original audio file.");

        string? selectedFilePath = SelectAudioFilePath("Restore Session Audio");
        if (string.IsNullOrWhiteSpace(selectedFilePath))
        {
            AppendLog("Restore audio canceled: user did not select a file.");
            return;
        }

        try
        {
            TranscriptSessionLoadResult loadResult = _sessionStore.RestoreAudioFile(_currentSessionDocument.SessionId, selectedFilePath);
            LoadSessionResult(loadResult, showAudioIssueDialog: true);
            RaiseToast(
                "Session audio restored",
                "The session copy is available again and ready for playback.",
                ToastNotificationType.Success);
        }
        catch (Exception ex)
        {
            RaiseError($"Unable to restore session audio: {ex.Message}");
        }
    }

    private bool TryLoadAudioPreview(string filePath)
    {
        try
        {
            if (_currentSessionDocument is not null
                && string.Equals(
                    _currentSessionDocument.Audio.StorageKind,
                    AudioStorageKinds.LiveRecordingManifest,
                    StringComparison.OrdinalIgnoreCase))
            {
                _audioPlaybackService.LoadLiveRecordingManifest(filePath);
            }
            else
            {
                _audioPlaybackService.LoadFile(filePath);
            }
            LoadedAudioFilePath = _audioPlaybackService.LoadedFilePath ?? filePath;
            IsAudioPlaying = _audioPlaybackService.IsPlaying;
            UpdateAudioTimelineFromPlayback();
            AppendLog($"Audio preview loaded: {LoadedAudioFileName}");
            return true;
        }
        catch (Exception ex)
        {
            LoadedAudioFilePath = string.Empty;
            IsAudioPlaying = false;
            ResetAudioTimeline();
            AppendLog($"Audio preview load failed: {ex.Message}");
            return false;
        }
    }

    private bool ReleaseAudioPreviewForProcessing(string audioFilePath)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath))
        {
            return false;
        }

        try
        {
            _audioPlaybackService.UnloadFile();
            LoadedAudioFilePath = string.Empty;
            IsAudioPlaying = false;
            ResetAudioTimeline();
            AppendLog("Audio preview released for transcription.");
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"Audio preview release failed before transcription: {ex.Message}");
            return false;
        }
    }

    private void RestoreAudioPreviewAfterProcessing(string audioFilePath, bool shouldRestore)
    {
        if (!shouldRestore || string.IsNullOrWhiteSpace(audioFilePath))
        {
            return;
        }

        if (!File.Exists(audioFilePath))
        {
            AppendLog("Audio preview was not restored after transcription because the file no longer exists.");
            return;
        }

        if (!TryLoadAudioPreview(audioFilePath))
        {
            AppendLog("Audio preview restore failed after transcription.");
        }
    }

    private void ApplyTranscriptDocument(TranscriptSessionTranscriptDocument transcript)
    {
        UnsubscribeFromFinalizedLineChanges();
        FinalizedTranscriptLines.Clear();

        foreach (TranscriptSessionLineDocument line in transcript.Lines)
        {
            bool hasTimeline = line.StartSeconds is not null || line.EndSeconds is not null;
            if (!hasTimeline && string.IsNullOrWhiteSpace(line.Text))
            {
                continue;
            }

            var item = new FinalizedTranscriptLineViewModel(
                startOffset: line.StartSeconds is null ? null : TimeSpan.FromSeconds(Math.Max(line.StartSeconds.Value, 0)),
                endOffset: line.EndSeconds is null ? null : TimeSpan.FromSeconds(Math.Max(line.EndSeconds.Value, 0)),
                isTimestampEstimated: line.IsTimestampEstimated,
                text: line.Text,
                speakerLabel: line.SpeakerLabel,
                isManuallyReviewed: line.IsManuallyReviewed,
                isTranscriptionPartial: line.IsTranscriptionPartial,
                speakerLabelSource: line.SpeakerLabelSource,
                diarizationRevision: line.DiarizationRevision,
                lastDiarizedChunkIndex: line.LastDiarizedChunkIndex);
            item.PropertyChanged += OnFinalizedLinePropertyChanged;
            FinalizedTranscriptLines.Add(item);
        }

        RebuildFinalizedTextFromLines();
    }

    private void ApplyEditingDocument(TranscriptSessionEditingDocument editing)
    {
        SelectedTranscriptViewIndex = editing.SelectedTranscriptViewIndex;
    }

    private void ApplyTranscriptionResult(TranscriptionResult result)
    {
        IReadOnlyList<TranscriptionTimedLine> timedLines = result.TimedLines
            ?.Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .OrderBy(line => line.StartOffset)
            .ToArray()
            ?? Array.Empty<TranscriptionTimedLine>();
        if (timedLines.Count == 0)
        {
            throw new InvalidOperationException("Transcription did not return any timed transcript segments.");
        }

        _suppressSessionAutosave = true;

        try
        {
            UnsubscribeFromFinalizedLineChanges();
            FinalizedTranscriptLines.Clear();

            foreach (TranscriptionTimedLine timedLine in timedLines)
            {
                var line = new FinalizedTranscriptLineViewModel(
                    startOffset: timedLine.StartOffset,
                    endOffset: timedLine.EndOffset,
                    isTimestampEstimated: timedLine.IsTimestampEstimated,
                    text: timedLine.Text.Trim(),
                    isTranscriptionPartial: false);
                line.PropertyChanged += OnFinalizedLinePropertyChanged;
                FinalizedTranscriptLines.Add(line);
            }

            RebuildFinalizedTextFromLines();
        }
        finally
        {
            _suppressSessionAutosave = false;
        }

        NotifyCurrentTranscriptStateChanged();
        ScheduleSessionAutosave();
    }

    private void ApplySpeakerDiarizationResult(SpeakerDiarizationResult result)
    {
        IReadOnlyList<SpeakerDiarizationSegment> segments = result.Segments
            .Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
            .OrderBy(segment => segment.StartOffset)
            .ToArray();
        if (segments.Count == 0)
        {
            throw new InvalidOperationException("Speaker diarization did not return any timed transcript segments.");
        }

        _suppressSessionAutosave = true;

        try
        {
            UnsubscribeFromFinalizedLineChanges();
            FinalizedTranscriptLines.Clear();

            var speakerLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (SpeakerDiarizationSegment segment in segments)
            {
                string speakerKey = segment.Speaker?.Trim() ?? string.Empty;
                if (!speakerLabels.TryGetValue(speakerKey, out string? displayLabel))
                {
                    displayLabel = $"Speaker {speakerLabels.Count + 1}";
                    speakerLabels[speakerKey] = displayLabel;
                }

                var line = new FinalizedTranscriptLineViewModel(
                    startOffset: segment.StartOffset,
                    endOffset: segment.EndOffset,
                    isTimestampEstimated: false,
                    text: segment.Text,
                    speakerLabel: displayLabel);
                line.PropertyChanged += OnFinalizedLinePropertyChanged;
                FinalizedTranscriptLines.Add(line);
            }

            RebuildFinalizedTextFromLines();
        }
        finally
        {
            _suppressSessionAutosave = false;
        }

        NotifyCurrentTranscriptStateChanged();
        ScheduleSessionAutosave();
    }

    private void ApplySpeakerLabelsToCurrentTranscript(SpeakerDiarizationResult result)
    {
        IReadOnlyList<SpeakerDiarizationSegment> segments = result.Segments
            .Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
            .OrderBy(segment => segment.StartOffset)
            .ToArray();
        if (segments.Count == 0)
        {
            throw new InvalidOperationException("Speaker diarization did not return any timed transcript segments.");
        }

        _isApplyingSpeakerDiarizationLabels = true;
        try
        {
            var speakerLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (FinalizedTranscriptLineViewModel line in FinalizedTranscriptLines)
            {
                line.SpeakerLabel = string.Empty;
                line.SpeakerLabelSource = string.Empty;
                line.DiarizationRevision = null;
                line.LastDiarizedChunkIndex = null;
            }

            int count = Math.Min(FinalizedTranscriptLines.Count, segments.Count);
            for (int index = 0; index < count; index++)
            {
                SpeakerDiarizationSegment segment = segments[index];
                string speakerKey = segment.Speaker?.Trim() ?? string.Empty;
                if (!speakerLabels.TryGetValue(speakerKey, out string? displayLabel))
                {
                    displayLabel = $"Speaker {speakerLabels.Count + 1}";
                    speakerLabels[speakerKey] = displayLabel;
                }

                FinalizedTranscriptLines[index].SpeakerLabel = displayLabel;
                FinalizedTranscriptLines[index].SpeakerLabelSource = result.UsedHeuristicFallback
                    ? SpeakerLabelSources.Heuristic
                    : SpeakerLabelSources.DiarizationFinal;
            }
        }
        finally
        {
            _isApplyingSpeakerDiarizationLabels = false;
        }

        RebuildFinalizedTextFromLines();
        NotifyCurrentTranscriptStateChanged();
        ScheduleSessionAutosave();
    }

    private TranscriptionResult BuildCurrentSessionTranscriptionResult()
    {
        IReadOnlyList<TranscriptionTimedLine> timedLines = FinalizedTranscriptLines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text) && line.StartOffset is not null)
            .Select(line => new TranscriptionTimedLine(
                line.Text.Trim(),
                line.StartOffset!.Value,
                line.EndOffset,
                line.IsTimestampEstimated))
            .ToArray();
        if (timedLines.Count == 0)
        {
            throw new InvalidOperationException("Current transcript does not contain timed rows for speaker detection.");
        }

        TimeSpan duration = _currentSessionDocument?.Audio.DurationSeconds is > 0
            ? TimeSpan.FromSeconds(_currentSessionDocument.Audio.DurationSeconds.Value)
            : timedLines
                .Select(line => line.EndOffset ?? line.StartOffset)
                .DefaultIfEmpty(TimeSpan.Zero)
                .Max();

        return new TranscriptionResult(
            Text: BuildTranscribeAudioTranscriptText(includeTimeline: false),
            Model: _currentSessionDocument?.Transcript.ModelId ?? SelectedEngineId,
            CreatedAt: _currentSessionDocument?.Transcript.LastTranscribedUtc ?? DateTimeOffset.UtcNow,
            Duration: duration,
            TokenLogprobs: Array.Empty<TranscriptionTokenLogprob>(),
            LowConfidenceTokens: Array.Empty<LowConfidenceToken>(),
            TimedLines: timedLines);
    }

    public bool InsertFinalizedTranscriptLine(int index, FinalizedTranscriptLineViewModel line)
    {
        ArgumentNullException.ThrowIfNull(line);

        int safeIndex = Math.Min(Math.Max(index, 0), FinalizedTranscriptLines.Count);
        line.PropertyChanged += OnFinalizedLinePropertyChanged;
        FinalizedTranscriptLines.Insert(safeIndex, line);
        RebuildFinalizedTextFromLines();
        if (!PersistTranscriptStructureChange())
        {
            line.PropertyChanged -= OnFinalizedLinePropertyChanged;
            FinalizedTranscriptLines.RemoveAt(safeIndex);
            RebuildFinalizedTextFromLines();
            return false;
        }

        return true;
    }

    public bool RemoveFinalizedTranscriptLine(FinalizedTranscriptLineViewModel line)
    {
        ArgumentNullException.ThrowIfNull(line);

        int index = FinalizedTranscriptLines.IndexOf(line);
        if (index < 0)
        {
            return false;
        }

        line.PropertyChanged -= OnFinalizedLinePropertyChanged;

        if (!FinalizedTranscriptLines.Remove(line))
        {
            line.PropertyChanged += OnFinalizedLinePropertyChanged;
            return false;
        }

        RebuildFinalizedTextFromLines();
        if (!PersistTranscriptStructureChange())
        {
            line.PropertyChanged += OnFinalizedLinePropertyChanged;
            FinalizedTranscriptLines.Insert(index, line);
            RebuildFinalizedTextFromLines();
            return false;
        }

        return true;
    }

    private void OnFinalizedLinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(FinalizedTranscriptLineViewModel.IsManuallyReviewed), StringComparison.Ordinal))
        {
            ScheduleSessionAutosave();
            return;
        }

        if (string.Equals(e.PropertyName, nameof(FinalizedTranscriptLineViewModel.SpeakerLabel), StringComparison.Ordinal))
        {
            if (!_isApplyingSpeakerDiarizationLabels && sender is FinalizedTranscriptLineViewModel line)
            {
                line.SpeakerLabelSource = string.IsNullOrWhiteSpace(line.SpeakerLabel)
                    ? string.Empty
                    : SpeakerLabelSources.Manual;
                line.DiarizationRevision = null;
                line.LastDiarizedChunkIndex = null;
            }

            NotifyPropertyChanged(nameof(HasSpeakerLabels));
        }

        if (!string.Equals(e.PropertyName, nameof(FinalizedTranscriptLineViewModel.Text), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(FinalizedTranscriptLineViewModel.SpeakerLabel), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(FinalizedTranscriptLineViewModel.Timeline), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(FinalizedTranscriptLineViewModel.StartOffset), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(FinalizedTranscriptLineViewModel.EndOffset), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(FinalizedTranscriptLineViewModel.IsTranscriptionPartial), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(FinalizedTranscriptLineViewModel.SpeakerLabelSource), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(FinalizedTranscriptLineViewModel.DiarizationRevision), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(FinalizedTranscriptLineViewModel.LastDiarizedChunkIndex), StringComparison.Ordinal))
        {
            return;
        }

        RebuildFinalizedTextFromLines();
        ScheduleSessionAutosave();
    }

    private void RebuildFinalizedTextFromLines()
    {
        FinalizedText = BuildTranscribeAudioTranscriptText(includeTimeline: true);
    }

    private void UnsubscribeFromFinalizedLineChanges()
    {
        foreach (FinalizedTranscriptLineViewModel line in FinalizedTranscriptLines)
        {
            line.PropertyChanged -= OnFinalizedLinePropertyChanged;
        }
    }

    private void OnProcessLogEmitted(object? sender, string message)
    {
        _uiContext.Post(_ => AppendLogCore(message), null);
    }

    private void OnProcessLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        NotifyPropertyChanged(nameof(HasProcessLogs));
    }

    private void OnFinalizedTranscriptLinesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        NotifyPropertyChanged(nameof(HasFinalizedTranscriptLines));
        NotifyPropertyChanged(nameof(HasSpeakerLabels));
        NotifyCurrentTranscriptStateChanged();
    }

    private void OnAudioPlaybackStateChanged(object? sender, EventArgs e)
    {
        _uiContext.Post(_ =>
        {
            IsAudioPlaying = _audioPlaybackService.IsPlaying;
            string? loadedFilePath = _audioPlaybackService.LoadedFilePath;
            LoadedAudioFilePath = loadedFilePath ?? string.Empty;

            if (string.IsNullOrWhiteSpace(LoadedAudioFilePath))
            {
                ResetAudioTimeline();
            }
            else
            {
                UpdateAudioTimelineFromPlayback();
            }
        }, null);
    }

    private void OnAudioTimelineTick(object? sender, EventArgs e)
    {
        if (!IsAudioFileLoaded)
        {
            return;
        }

        UpdateAudioTimelineFromPlayback();
    }

    private void OnSessionAutosaveTimerTick(object? sender, EventArgs e)
    {
        _sessionAutosaveTimer.Stop();
        QueueSessionAutosaveSave();
    }

    private void UpdateAudioTimelineFromPlayback()
    {
        TimeSpan duration = _audioPlaybackService.Duration;
        TimeSpan position = _audioPlaybackService.Position;

        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        if (duration > TimeSpan.Zero && position > duration)
        {
            position = duration;
        }

        AudioSeekMaximumSeconds = Math.Max(duration.TotalSeconds, 0);

        _isUpdatingSeekFromPlayback = true;
        try
        {
            AudioSeekPositionSeconds = Math.Min(position.TotalSeconds, AudioSeekMaximumSeconds);
        }
        finally
        {
            _isUpdatingSeekFromPlayback = false;
        }

        UpdateAudioTimeLabels(position, duration);
    }

    private void ResetAudioTimeline()
    {
        AudioSeekMaximumSeconds = 0;

        _isUpdatingSeekFromPlayback = true;
        try
        {
            AudioSeekPositionSeconds = 0;
        }
        finally
        {
            _isUpdatingSeekFromPlayback = false;
        }

        AudioElapsedText = "00:00";
        AudioRemainingText = "-00:00";
    }

    private void UpdateAudioTimeLabels(TimeSpan elapsed, TimeSpan duration)
    {
        TimeSpan remaining = duration - elapsed;

        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        AudioElapsedText = FormatPlaybackTime(elapsed);
        AudioRemainingText = $"-{FormatPlaybackTime(remaining)}";
    }

    private void LoadRecentSessions(string? selectSessionId, bool pinSelectedToTop = false)
    {
        IReadOnlyList<TranscriptSessionSummary> sessions;

        try
        {
            sessions = _sessionStore.ListRecentSessions();
        }
        catch (Exception ex)
        {
            AppendLog($"Unable to load recent sessions: {ex.Message}");
            sessions = Array.Empty<TranscriptSessionSummary>();
        }

        if (pinSelectedToTop && !string.IsNullOrWhiteSpace(selectSessionId))
        {
            TranscriptSessionSummary? selectedSession = sessions.FirstOrDefault(item =>
                string.Equals(item.SessionId, selectSessionId, StringComparison.OrdinalIgnoreCase));
            if (selectedSession is null)
            {
                try
                {
                    TranscriptSessionLoadResult loadResult = _sessionStore.LoadSession(selectSessionId);
                    selectedSession = CreateRecentSessionSummary(loadResult);
                }
                catch (Exception ex)
                {
                    AppendLog($"Unable to pin selected recent session '{selectSessionId}': {ex.Message}");
                }
            }

            if (selectedSession is not null)
            {
                sessions = sessions
                    .Where(item => !string.Equals(item.SessionId, selectSessionId, StringComparison.OrdinalIgnoreCase))
                    .Prepend(selectedSession)
                    .ToArray();
            }
        }

        sessions = SortRecentSessions(sessions);

        if (ShouldPinSelectedSessionToTop(selectSessionId, pinSelectedToTop))
        {
            TranscriptSessionSummary? selectedSession = sessions.FirstOrDefault(item =>
                string.Equals(item.SessionId, selectSessionId, StringComparison.OrdinalIgnoreCase));
            if (selectedSession is not null)
            {
                sessions = sessions
                    .Where(item => !string.Equals(item.SessionId, selectSessionId, StringComparison.OrdinalIgnoreCase))
                    .Prepend(selectedSession)
                    .ToArray();
            }
        }

        string? loadedSessionId = _currentSessionDocument?.SessionId;

        _allRecentSessions = sessions
            .Select(session => session with
            {
                IsLoaded = !string.IsNullOrWhiteSpace(loadedSessionId)
                    && string.Equals(session.SessionId, loadedSessionId, StringComparison.OrdinalIgnoreCase)
            })
            .ToArray();

        string? highlightedSessionId = !string.IsNullOrWhiteSpace(selectSessionId)
            ? selectSessionId
            : _currentSessionDocument?.SessionId;
        RefreshRecentSessionsView(highlightedSessionId);
    }

    private void RefreshRecentSessionsView(string? highlightedSessionId = null)
    {
        highlightedSessionId ??= SelectedRecentSession?.SessionId
            ?? _currentSessionDocument?.SessionId;

        IEnumerable<TranscriptSessionSummary> sessions = _allRecentSessions;
        if (HasRecentSessionsFilterText)
        {
            string filter = _recentSessionsFilterText;
            sessions = sessions.Where(session => SessionMatchesRecentSessionsFilter(session, filter));
        }

        List<TranscriptSessionSummary> visibleSessions = sessions.ToList();
        TranscriptSessionSummary? selectedSession = !string.IsNullOrWhiteSpace(highlightedSessionId)
            ? visibleSessions.FirstOrDefault(item => string.Equals(item.SessionId, highlightedSessionId, StringComparison.OrdinalIgnoreCase))
            : null;

        RecentSessions.Clear();
        foreach (TranscriptSessionSummary session in visibleSessions)
        {
            RecentSessions.Add(session);
        }

        NotifyPropertyChanged(nameof(HasRecentSessions));
        NotifyPropertyChanged(nameof(RecentSessionsEmptyStateTitle));
        NotifyPropertyChanged(nameof(RecentSessionsEmptyStateMessage));

        SelectedRecentSession = selectedSession;
    }

    private static bool SessionMatchesRecentSessionsFilter(TranscriptSessionSummary session, string filter)
    {
        return ContainsIgnoreCase(session.DisplayName, filter)
            || ContainsIgnoreCase(session.OriginalFileName, filter)
            || ContainsIgnoreCase(session.SummaryText, filter);
    }

    private static bool ContainsIgnoreCase(string? value, string filter)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldPinSelectedSessionToTop(string? selectSessionId, bool pinSelectedToTop)
    {
        return pinSelectedToTop
            && !string.IsNullOrWhiteSpace(selectSessionId)
            && _recentSessionsSortMode == RecentSessionsSortMode.CreatedDate
            && _recentSessionsSortDescending;
    }

    private IReadOnlyList<TranscriptSessionSummary> SortRecentSessions(IReadOnlyList<TranscriptSessionSummary> sessions)
    {
        IOrderedEnumerable<TranscriptSessionSummary> orderedSessions = _recentSessionsSortMode switch
        {
            RecentSessionsSortMode.Name => _recentSessionsSortDescending
                ? sessions.OrderByDescending(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                : sessions.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            _ => _recentSessionsSortDescending
                ? sessions.OrderByDescending(item => item.CreatedUtc)
                : sessions.OrderBy(item => item.CreatedUtc),
        };

        return (_recentSessionsSortMode == RecentSessionsSortMode.Name
                ? orderedSessions.ThenByDescending(item => item.CreatedUtc)
                : orderedSessions.ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            .ToArray();
    }

    private static TranscriptSessionSummary CreateRecentSessionSummary(TranscriptSessionLoadResult loadResult)
    {
        TranscriptSessionDocument document = loadResult.Document;
        string? audioPath = loadResult.AudioFilePath;
        bool hasStoredAudio = !string.IsNullOrWhiteSpace(audioPath) && File.Exists(audioPath);

        return new TranscriptSessionSummary(
            SessionId: document.SessionId,
            DisplayName: string.IsNullOrWhiteSpace(document.DisplayName)
                ? document.Audio.OriginalFileName
                : document.DisplayName,
            CreatedUtc: document.CreatedUtc,
            UpdatedUtc: document.UpdatedUtc,
            OriginalFileName: document.Audio.OriginalFileName,
            HasStoredAudio: hasStoredAudio,
            SummaryText: TranscriptSessionStore.BuildRecentSessionSummaryText(document));
    }

    private void ScheduleSessionAutosave()
    {
        if (_suppressSessionAutosave || _currentSessionDocument is null)
        {
            return;
        }

        _sessionAutosaveTimer.Stop();
        _sessionAutosaveTimer.Start();
    }

    private bool PersistTranscriptStructureChange()
    {
        _sessionAutosaveTimer.Stop();

        bool saved = TrySaveCurrentSession(
            updatedTranscriptMode: null,
            showErrorDialog: true,
            successLogMessage: string.Empty);

        if (saved && _currentSessionDocument is not null)
        {
            LoadRecentSessions(_currentSessionDocument.SessionId);
        }

        return saved;
    }

    private bool ConfirmSessionDeletion()
    {
        EventHandler<ConfirmationRequest>? handler = ConfirmationRequested;
        if (handler is null)
        {
            RaiseError("The confirmation dialog is unavailable. The session was left unchanged.");
            AppendLog("Session deletion canceled: confirmation dialog unavailable.");
            return false;
        }

        var request = new ConfirmationRequest(
            title: "Delete this Session?",
            message: "This will permanently remove the selected session and its stored files.",
            confirmButtonText: "Yes",
            cancelButtonText: "No");

        try
        {
            if (SynchronizationContext.Current == _uiContext)
            {
                handler(this, request);
            }
            else
            {
                _uiContext.Send(_ => handler(this, request), null);
            }
        }
        catch (Exception ex)
        {
            RaiseError($"Unable to confirm session deletion: {ex.Message}");
            AppendLog($"Session deletion canceled: confirmation failed: {ex.Message}");
            return false;
        }

        return request.IsConfirmed;
    }

    private bool HasExistingTranscriptContent(TranscriptGenerationMode transcriptMode)
    {
        if (FinalizedTranscriptLines.Count > 0)
        {
            return true;
        }

        if (_currentSessionDocument is null)
        {
            return !string.IsNullOrWhiteSpace(FinalizedText);
        }

        return !string.IsNullOrWhiteSpace(_currentSessionDocument.Transcript.FinalText)
            || _currentSessionDocument.Transcript.Lines.Count > 0;
    }

    private bool ConfirmTranscriptReplacement(
        string operationName)
    {
        EventHandler<ConfirmationRequest>? handler = ConfirmationRequested;
        if (handler is null)
        {
            RaiseError("The confirmation dialog is unavailable. The existing transcript was left unchanged.");
            AppendLog($"{operationName} canceled: transcript replacement confirmation is unavailable.");
            return false;
        }

        var request = new ConfirmationRequest(
            title: "Replace current transcript?",
            message: "This session already has transcript content. Proceeding will remove the current transcript and start a new transcription for this audio file.",
            confirmButtonText: "Proceed",
            cancelButtonText: "Cancel");

        try
        {
            if (SynchronizationContext.Current == _uiContext)
            {
                handler(this, request);
            }
            else
            {
                _uiContext.Send(_ => handler(this, request), null);
            }
        }
        catch (Exception ex)
        {
            RaiseError($"Unable to confirm transcript replacement: {ex.Message}");
            AppendLog($"{operationName} canceled: transcript replacement confirmation failed: {ex.Message}");
            return false;
        }

        if (request.IsConfirmed)
        {
            AppendLog($"Transcript replacement confirmed by user for {operationName.ToLowerInvariant()}.");
            return true;
        }

        AppendLog($"{operationName} canceled: existing transcript was preserved.");
        return false;
    }

    private void ResetCurrentSessionTranscriptState(TranscriptGenerationMode transcriptMode)
    {
        if (_currentSessionDocument is null)
        {
            return;
        }

        _currentSessionDocument.Transcript.FinalText = string.Empty;
        _currentSessionDocument.Transcript.ModelId = string.Empty;
        _currentSessionDocument.Transcript.LastTranscribedUtc = null;
        _currentSessionDocument.Transcript.Lines.Clear();
        _currentSessionDocument.Transcript.TranscriptionJob = new TranscriptionJobDocument();

        if (transcriptMode == TranscriptGenerationMode.TranscribeAudio)
        {
            _currentSessionDocument.Editing.SelectedRowIndex = null;
        }
    }

    private void ClearCurrentSessionAfterDeletion()
    {
        _suppressSessionAutosave = true;

        try
        {
            UnsubscribeFromFinalizedLineChanges();
            FinalizedTranscriptLines.Clear();
            FinalizedText = string.Empty;
        }
        finally
        {
            _suppressSessionAutosave = false;
        }

        _currentSessionDocument = null;
        SelectedRecentSession = null;
        ClearPendingImportedAudioSelection();
        CurrentSessionDisplayName = "No session loaded.";
        CurrentSessionAudioIssue = string.Empty;
        IsCurrentSessionAudioMissing = false;
        LoadedAudioFilePath = string.Empty;
        IsAudioPlaying = false;
        ResetAudioTimeline();
        NotifyCurrentTranscriptStateChanged();
        NotifyInteractionAvailabilityChanged();
        NotifyPropertyChanged(nameof(HasCurrentSession));
        NotifyPropertyChanged(nameof(HasPendingSessionSelection));
        NotifyPropertyChanged(nameof(LoadedAudioFileName));
        RefreshCommandStates();
    }

    private void QueueSessionAutosaveSave()
    {
        TranscriptSessionDocument? snapshot = CreateSessionSaveSnapshot(updatedTranscriptMode: null);
        if (snapshot is null)
        {
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
        string successLogMessage)
    {
        TranscriptSessionDocument? snapshot = CreateSessionSaveSnapshot(updatedTranscriptMode);
        if (snapshot is null)
        {
            return true;
        }

        try
        {
            SaveSessionSnapshot(snapshot);

            if (!string.IsNullOrWhiteSpace(successLogMessage))
            {
                AppendLog(successLogMessage);
            }

            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"Session save failed: {ex.Message}");

            if (showErrorDialog)
            {
                RaiseError($"Unable to save the current session: {ex.Message}");
            }

            return false;
        }
    }

    private void ClearPendingImportedAudioSelection()
    {
        _pendingImportedAudioFilePath = string.Empty;
    }

    private void RestorePreparedTranscribeAudioWorkflowBackup()
    {
        if (_transcribeAudioWorkflow?.BackupDocument is not TranscriptSessionDocument backupDocument)
        {
            return;
        }

        RestoreTranscribeAudioBackup(backupDocument, saveRestoredSession: true);
    }

    private void RestoreTranscribeAudioBackup(TranscriptSessionDocument backupDocument, bool saveRestoredSession)
    {
        if (_currentSessionDocument is null)
        {
            return;
        }

        _sessionAutosaveTimer.Stop();
        _currentSessionDocument.Transcript = CloneTranscriptDocument(backupDocument.Transcript);
        _currentSessionDocument.Editing = CloneEditingDocument(backupDocument.Editing);

        _suppressSessionAutosave = true;
        try
        {
            ApplyTranscriptDocument(_currentSessionDocument.Transcript);
            ApplyEditingDocument(_currentSessionDocument.Editing);
        }
        finally
        {
            _suppressSessionAutosave = false;
        }

        NotifyCurrentTranscriptStateChanged();

        if (saveRestoredSession)
        {
            TrySaveCurrentSession(
                updatedTranscriptMode: null,
                showErrorDialog: true,
                successLogMessage: "Existing transcript restored after Transcribe Audio.");
        }
    }

    private void DeletePreparedTranscribeAudioTransientSession()
    {
        if (_transcribeAudioWorkflow is null)
        {
            return;
        }

        string? sessionId = _transcribeAudioWorkflow.CreatedSessionId;
        sessionId = string.IsNullOrWhiteSpace(sessionId)
            ? _currentSessionDocument?.SessionId
            : sessionId;

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        try
        {
            _sessionAutosaveTimer.Stop();
            _audioPlaybackService.UnloadFile();
            _sessionStore.DeleteSession(sessionId);

            if (_currentSessionDocument is not null
                && string.Equals(_currentSessionDocument.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                ClearCurrentSessionAfterDeletion();
            }

            LoadRecentSessions(selectSessionId: null);
            AppendLog($"Transient Transcribe Audio session discarded: {sessionId}.");
        }
        catch (Exception ex)
        {
            RaiseError($"Unable to clean up the failed Transcribe Audio session: {ex.Message}");
            AppendLog($"Transient Transcribe Audio session cleanup failed: {ex.Message}");
        }
    }

    private void ClearSelectedAudioPreview()
    {
        _sessionAutosaveTimer.Stop();
        _audioPlaybackService.UnloadFile();
        ClearPendingImportedAudioSelection();
        LoadedAudioFilePath = string.Empty;
        IsAudioPlaying = false;
        ResetAudioTimeline();
        NotifyPropertyChanged(nameof(LoadedAudioFileName));
        NotifyCurrentTranscriptStateChanged();
        RefreshCommandStates();
    }

    private static TranscriptSessionTranscriptDocument CloneTranscriptDocument(TranscriptSessionTranscriptDocument source)
    {
        return new TranscriptSessionTranscriptDocument
        {
            FinalText = source.FinalText,
            ModelId = source.ModelId,
            LastTranscribedUtc = source.LastTranscribedUtc,
            TranscriptionJob = CloneTranscriptionJob(source.TranscriptionJob),
            SpeakerDiarizationJob = CloneSpeakerDiarizationJob(source.SpeakerDiarizationJob),
            Lines = source.Lines
                .Select(line => new TranscriptSessionLineDocument
                {
                    Text = line.Text,
                    SpeakerLabel = line.SpeakerLabel,
                    StartSeconds = line.StartSeconds,
                    EndSeconds = line.EndSeconds,
                    IsTimestampEstimated = line.IsTimestampEstimated,
                    IsManuallyReviewed = line.IsManuallyReviewed,
                    IsTranscriptionPartial = line.IsTranscriptionPartial,
                    SpeakerLabelSource = line.SpeakerLabelSource,
                    DiarizationRevision = line.DiarizationRevision,
                    LastDiarizedChunkIndex = line.LastDiarizedChunkIndex,
                })
                .ToList(),
        };
    }

    private static TranscriptionJobDocument CloneTranscriptionJob(TranscriptionJobDocument? source)
    {
        if (source is null)
        {
            return new TranscriptionJobDocument();
        }

        return new TranscriptionJobDocument
        {
            Status = source.Status,
            Engine = source.Engine,
            JobVersion = source.JobVersion,
            AudioFingerprint = source.AudioFingerprint,
            TotalChunks = source.TotalChunks,
            LastCompletedChunkIndex = source.LastCompletedChunkIndex,
            StartedUtc = source.StartedUtc,
            LastUpdatedUtc = source.LastUpdatedUtc,
            CompletedUtc = source.CompletedUtc,
            LastError = source.LastError,
        };
    }

    private static SpeakerDiarizationJobDocument CloneSpeakerDiarizationJob(SpeakerDiarizationJobDocument? source)
    {
        if (source is null)
        {
            return new SpeakerDiarizationJobDocument();
        }

        return new SpeakerDiarizationJobDocument
        {
            Status = source.Status,
            Engine = source.Engine,
            JobVersion = source.JobVersion,
            AudioFingerprint = source.AudioFingerprint,
            TranscriptFingerprint = source.TranscriptFingerprint,
            ChunkDurationSeconds = source.ChunkDurationSeconds,
            OverlapDurationSeconds = source.OverlapDurationSeconds,
            TotalChunks = source.TotalChunks,
            LastCompletedChunkIndex = source.LastCompletedChunkIndex,
            StartedUtc = source.StartedUtc,
            LastUpdatedUtc = source.LastUpdatedUtc,
            CompletedUtc = source.CompletedUtc,
            LastError = source.LastError,
            Revision = source.Revision,
            NextSpeakerIndex = source.NextSpeakerIndex,
            SpeakerMappings = source.SpeakerMappings
                .Select(mapping => new SpeakerDiarizationSpeakerMapDocument
                {
                    ChunkSpeakerKey = mapping.ChunkSpeakerKey,
                    GlobalSpeakerLabel = mapping.GlobalSpeakerLabel,
                })
                .ToList(),
        };
    }

    private static TranscriptSessionEditingDocument CloneEditingDocument(TranscriptSessionEditingDocument source)
    {
        return new TranscriptSessionEditingDocument
        {
            SelectedRowIndex = source.SelectedRowIndex,
            SelectedTranscriptViewIndex = source.SelectedTranscriptViewIndex,
        };
    }

    private TranscriptSessionDocument? CreateSessionSaveSnapshot(TranscriptGenerationMode? updatedTranscriptMode)
    {
        if (_currentSessionDocument is null)
        {
            return null;
        }

        string displayName = string.IsNullOrWhiteSpace(_currentSessionDocument.DisplayName)
            ? Path.GetFileNameWithoutExtension(_currentSessionDocument.Audio.OriginalFileName)
            : _currentSessionDocument.DisplayName;
        DateTimeOffset updatedUtc = DateTimeOffset.UtcNow;
        DateTimeOffset? segmentLastTranscribedUtc = updatedTranscriptMode == TranscriptGenerationMode.TranscribeAudio
            || updatedTranscriptMode == TranscriptGenerationMode.Live
            ? updatedUtc
            : _currentSessionDocument.Transcript.LastTranscribedUtc;
        double? durationSeconds = _currentSessionDocument.Audio.DurationSeconds;

        if (_audioPlaybackService.Duration > TimeSpan.Zero)
        {
            durationSeconds = _audioPlaybackService.Duration.TotalSeconds;
        }

        List<TranscriptSessionLineDocument> segmentLines = FinalizedTranscriptLines
            .Where(line => !line.IsProvisional)
            .Select(line => new TranscriptSessionLineDocument
            {
                Text = line.Text,
                StartSeconds = line.StartOffset?.TotalSeconds,
                EndSeconds = line.EndOffset?.TotalSeconds,
                IsTimestampEstimated = line.IsTimestampEstimated,
                SpeakerLabel = line.SpeakerLabel,
                IsManuallyReviewed = line.IsManuallyReviewed,
                IsTranscriptionPartial = line.IsTranscriptionPartial,
                SpeakerLabelSource = line.SpeakerLabelSource,
                DiarizationRevision = line.DiarizationRevision,
                LastDiarizedChunkIndex = line.LastDiarizedChunkIndex,
            })
            .ToList();
        _currentSessionDocument.DisplayName = displayName;
        _currentSessionDocument.UpdatedUtc = updatedUtc;
        EnsureLiveRecordingManifestAudioPath(_currentSessionDocument.Audio);
        _currentSessionDocument.Audio.DurationSeconds = durationSeconds;
        _currentSessionDocument.Transcript.FinalText = BuildTranscribeAudioTranscriptText(includeTimeline: false);
        _currentSessionDocument.Transcript.ModelId =
            updatedTranscriptMode switch
            {
                TranscriptGenerationMode.TranscribeAudio => SelectedEngineId,
                TranscriptGenerationMode.Live => SelectedEngineId,
                _ => _currentSessionDocument.Transcript.ModelId,
            };
        _currentSessionDocument.Transcript.LastTranscribedUtc = segmentLastTranscribedUtc;
        _currentSessionDocument.Transcript.Lines = segmentLines
            .Select(line => new TranscriptSessionLineDocument
            {
                Text = line.Text,
                StartSeconds = line.StartSeconds,
                EndSeconds = line.EndSeconds,
                IsTimestampEstimated = line.IsTimestampEstimated,
                SpeakerLabel = line.SpeakerLabel,
                IsManuallyReviewed = line.IsManuallyReviewed,
                IsTranscriptionPartial = line.IsTranscriptionPartial,
                SpeakerLabelSource = line.SpeakerLabelSource,
                DiarizationRevision = line.DiarizationRevision,
                LastDiarizedChunkIndex = line.LastDiarizedChunkIndex,
            })
            .ToList();
        _currentSessionDocument.Editing.SelectedTranscriptViewIndex = SelectedTranscriptViewIndex;

        return new TranscriptSessionDocument
        {
            SchemaVersion = _currentSessionDocument.SchemaVersion,
            SessionId = _currentSessionDocument.SessionId,
            DisplayName = _currentSessionDocument.DisplayName,
            CreatedUtc = _currentSessionDocument.CreatedUtc,
            UpdatedUtc = _currentSessionDocument.UpdatedUtc,
            Audio = new TranscriptSessionAudioDocument
            {
                StorageKind = _currentSessionDocument.Audio.StorageKind,
                StoredRelativePath = _currentSessionDocument.Audio.StoredRelativePath,
                OriginalFileName = _currentSessionDocument.Audio.OriginalFileName,
                FileSizeBytes = _currentSessionDocument.Audio.FileSizeBytes,
                DurationSeconds = _currentSessionDocument.Audio.DurationSeconds,
                Sha256 = _currentSessionDocument.Audio.Sha256,
            },
            Transcript = new TranscriptSessionTranscriptDocument
            {
                FinalText = _currentSessionDocument.Transcript.FinalText,
                ModelId = _currentSessionDocument.Transcript.ModelId,
                LastTranscribedUtc = _currentSessionDocument.Transcript.LastTranscribedUtc,
                TranscriptionJob = CloneTranscriptionJob(_currentSessionDocument.Transcript.TranscriptionJob),
                Lines = segmentLines,
                SpeakerDiarizationJob = CloneSpeakerDiarizationJob(_currentSessionDocument.Transcript.SpeakerDiarizationJob),
            },
            Editing = new TranscriptSessionEditingDocument
            {
                SelectedRowIndex = _currentSessionDocument.Editing.SelectedRowIndex,
                SelectedTranscriptViewIndex = _currentSessionDocument.Editing.SelectedTranscriptViewIndex,
            },
        };
    }

    private static void EnsureLiveRecordingManifestAudioPath(TranscriptSessionAudioDocument audio)
    {
        if (!string.Equals(
                audio.StorageKind,
                AudioStorageKinds.LiveRecordingManifest,
                StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(audio.StoredRelativePath))
        {
            return;
        }

        audio.StoredRelativePath = TranscriptSessionStore.LiveRecordingManifestRelativePath;
        audio.OriginalFileName = string.IsNullOrWhiteSpace(audio.OriginalFileName)
            ? TranscriptSessionStore.LiveSessionAudioName
            : audio.OriginalFileName;
    }

    private void SaveSessionSnapshot(TranscriptSessionDocument snapshot)
    {
        _sessionSaveSemaphore.Wait();
        try
        {
            _sessionStore.Save(snapshot);
        }
        finally
        {
            _sessionSaveSemaphore.Release();
        }
    }

    private async Task SaveSessionSnapshotAsync(
        TranscriptSessionDocument snapshot,
        bool showErrorDialog,
        string successLogMessage)
    {
        try
        {
            await _sessionSaveSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                _sessionStore.Save(snapshot);
            }
            finally
            {
                _sessionSaveSemaphore.Release();
            }

            if (!string.IsNullOrWhiteSpace(successLogMessage))
            {
                AppendLog(successLogMessage);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Session save failed: {ex.Message}");

            if (showErrorDialog)
            {
                RaiseError($"Unable to save the current session: {ex.Message}");
            }
        }
    }

    private string BuildTranscribeAudioTranscriptText(bool includeTimeline)
    {
        return string.Join(
            Environment.NewLine,
            FinalizedTranscriptLines
                .Where(line => !line.IsProvisional)
                .Select(line =>
                {
                    string speakerLabel = line.SpeakerLabel?.Trim() ?? string.Empty;
                    string text = line.Text?.Trim() ?? string.Empty;
                    string lineText = string.IsNullOrWhiteSpace(speakerLabel)
                        ? text
                        : string.IsNullOrWhiteSpace(text)
                            ? speakerLabel
                            : $"{speakerLabel}: {text}";

                    if (!includeTimeline)
                    {
                        return lineText;
                    }

                    string timeline = line.Timeline?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(timeline))
                    {
                        return lineText;
                    }

                    return string.IsNullOrWhiteSpace(lineText) ? timeline : $"{timeline} {lineText}";
                })
                .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private bool TryResolveLoadedAudioDuration(out TimeSpan duration)
    {
        duration = _audioPlaybackService.Duration;
        if (duration > TimeSpan.Zero)
        {
            return true;
        }

        if (_currentSessionDocument?.Audio.DurationSeconds is double sessionDurationSeconds && sessionDurationSeconds > 0)
        {
            duration = TimeSpan.FromSeconds(sessionDurationSeconds);
            return true;
        }

        duration = TimeSpan.Zero;
        return false;
    }

    private static string FormatPlaybackTime(TimeSpan value)
    {
        if (value.TotalHours >= 1)
        {
            return value.ToString(@"hh\:mm\:ss");
        }

        return value.ToString(@"mm\:ss");
    }

    private void RaiseError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        AppendLog($"ERROR: {message}");
        _uiContext.Post(_ => ErrorOccurred?.Invoke(this, message), null);
    }

    private void RaiseToast(string title, string message, ToastNotificationType type = ToastNotificationType.Info)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _uiContext.Post(_ => ToastRequested?.Invoke(this, new ToastNotification(title, message, type)), null);
    }

    private void RaiseSessionLoadStarting()
    {
        _uiContext.Post(_ => SessionLoadStarting?.Invoke(this, EventArgs.Empty), null);
    }

    private void OnAppUpdateSnapshotChanged(object? sender, AppUpdateSnapshot snapshot)
    {
        _uiContext.Post(_ =>
        {
            _appUpdateSnapshot = snapshot;
            NotifyPropertyChanged(nameof(ApplicationUpdateStageText));
            NotifyPropertyChanged(nameof(ApplicationUpdateMessageText));
            NotifyPropertyChanged(nameof(ApplicationUpdateState));
            NotifyPropertyChanged(nameof(IsApplicationUpdateProgressVisible));
            NotifyPropertyChanged(nameof(ApplicationUpdateProgressPercent));
            NotifyPropertyChanged(nameof(IsApplicationUpdateActive));
            NotifyPropertyChanged(nameof(IsApplicationFooterCompactMode));
            NotifyPropertyChanged(nameof(IsApplicationFooterDefaultVisible));
            NotifyPropertyChanged(nameof(IsUpdateButtonVisible));
            NotifyPropertyChanged(nameof(IsUpdateButtonEnabled));
            NotifyPropertyChanged(nameof(AppVersionText));
            RefreshCommandStates();
        }, null);
    }

    private bool EnsureCanCreateNewSession(string contextLabel)
    {
        if (IsDevelopmentUnpackagedMode)
        {
            return true;
        }

        if (HasPremium)
        {
            return true;
        }

        int sessionCount;
        try
        {
            sessionCount = _sessionStore.ListRecentSessions().Count;
        }
        catch (Exception ex)
        {
            AppendLog($"Session limit check skipped for {contextLabel}: {ex.Message}");
            return true;
        }

        if (sessionCount < BasicSessionLimit)
        {
            return true;
        }

        AppendLog(
            $"Session creation blocked for {contextLabel}: Basic limit reached " +
            $"({sessionCount}/{BasicSessionLimit}).");
        PremiumUpsellRequested?.Invoke(
            this,
            new PremiumUpsellRequest(
                "Session limit reached",
                SessionLimitUpsellMessage));
        return false;
    }

    private void OnEntitlementSnapshotChanged(object? sender, AppEntitlementSnapshot snapshot)
    {
        _uiContext.Post(_ =>
        {
            bool hadPremium = _entitlementSnapshot.HasPremium;
            _entitlementSnapshot = snapshot;
            NotifyInteractionAvailabilityChanged();

            if (hadPremium != snapshot.HasPremium)
            {
                RefreshEngines(_availableModelsProvider());
            }
            else
            {
                EnsureSelectedEngineAllowed();
            }
        }, null);
    }

    public bool CanInstallModel(string modelId)
    {
        return IsDevelopmentUnpackagedMode
            || AppFeatureAccess.CanInstallModel(modelId, HasPremium);
    }

    public async Task<PremiumPurchaseResult> RequestPremiumPurchaseAsync(CancellationToken cancellationToken = default)
    {
        if (_entitlementService is null)
        {
            return new PremiumPurchaseResult(
                PremiumPurchaseStatus.NotSupported,
                "Premium purchase is available only in the Microsoft Store version.");
        }

        PremiumPurchaseResult result = await _entitlementService.RequestPremiumPurchaseAsync(cancellationToken);
        if (result.Status is PremiumPurchaseStatus.Succeeded or PremiumPurchaseStatus.AlreadyOwned)
        {
            RefreshEngines(_availableModelsProvider());
        }

        return result;
    }

    public async Task RefreshPremiumEntitlementAsync(CancellationToken cancellationToken = default)
    {
        if (_entitlementService is null)
        {
            return;
        }

        await _entitlementService.RefreshAsync(cancellationToken);
    }

    public async Task RestorePremiumPurchaseAsync(CancellationToken cancellationToken = default)
    {
        await RefreshPremiumEntitlementAsync(cancellationToken);
    }

    private IEnumerable<TranscriptionModelOption> FilterAccessibleEngines(IEnumerable<TranscriptionModelOption> models)
    {
        if (IsDevelopmentUnpackagedMode)
        {
            return models;
        }

        return models.Where(model => AppFeatureAccess.CanUseModel(model.Id, HasPremium));
    }

    private void EnsureSelectedEngineAllowed()
    {
        if (SelectedEngine is null
            || IsDevelopmentUnpackagedMode
            || AppFeatureAccess.CanUseModel(SelectedEngine.Id, HasPremium))
        {
            return;
        }

        EngineOptionViewModel fallback = ResolveEngine(Engines, TranscriptionModelCatalog.WhisperSmall, "Whisper small");
        SelectedEngine = fallback;
        AppendLog("Selected model reverted to Whisper small because Premium is required for the previous engine.");
    }

    public void LogHandledException(string source, Exception ex)
    {
        if (ex is null)
        {
            return;
        }

        string prefix = string.IsNullOrWhiteSpace(source)
            ? "Handled error"
            : $"Handled error in {source}";
        AppendLog($"{prefix}: {ex.GetType().Name}: {ex.Message}");
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        NotifyPropertyChanged(propertyName);
        return true;
    }

    private void NotifyPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void AppendLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _uiContext.Post(_ => AppendLogCore(message), null);
    }

    private void AppendLogCore(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        lock (_processLogsSync)
        {
            ProcessLogs.Add(
                new ProcessLogEntryViewModel(
                    DateTime.Now.ToString("HH:mm:ss"),
                    message.Trim()));
        }
    }

    private void ClearProcessLogs()
    {
        lock (_processLogsSync)
        {
            ProcessLogs.Clear();
        }
    }

    private static string TrimForLog(string text, int maxLength = 140)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(empty)";
        }

        string singleLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();

        if (singleLine.Length <= maxLength)
        {
            return singleLine;
        }

        return $"{singleLine[..maxLength]}...";
    }

    private static EngineOptionViewModel ResolveEngine(
        IEnumerable<EngineOptionViewModel> engines,
        string id,
        string fallbackDisplayName)
    {
        string requestedId = string.IsNullOrWhiteSpace(id)
            ? TranscriptionModelCatalog.WhisperSmall
            : id;
        EngineOptionViewModel? match = engines.FirstOrDefault(engine =>
            string.Equals(engine.Id, requestedId, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            return match;
        }

        return new EngineOptionViewModel(new TranscriptionModelOption(
            Id: TranscriptionModelCatalog.WhisperSmall,
            DisplayName: fallbackDisplayName,
            IsLocal: true));
    }

    private sealed class TranscribeAudioWorkflowState
    {
        public TranscribeAudioWorkflowState(
            TranscribeAudioWorkflowKind kind,
            string sourceAudioPath,
            TranscriptSessionDocument? backupDocument,
            bool resumeRequested,
            bool forceRestartRequested)
        {
            Kind = kind;
            SourceAudioPath = sourceAudioPath;
            BackupDocument = backupDocument;
            ResumeRequested = resumeRequested;
            ForceRestartRequested = forceRestartRequested;
        }

        public TranscribeAudioWorkflowKind Kind { get; }

        public string SourceAudioPath { get; }

        public TranscriptSessionDocument? BackupDocument { get; set; }

        public bool ResumeRequested { get; }

        public bool ForceRestartRequested { get; }

        public string? CreatedSessionId { get; set; }

        public bool HasStarted { get; set; }
    }

    private enum AudioFileSelectionIntent
    {
        OpenOnly,
        OpenForTranscribe,
    }

    private enum TranscribeAudioWorkflowKind
    {
        ExistingSession,
        NewFile,
    }

    private sealed record SpeakerDiarizationRowWorkItem(
        int RowIndex,
        FinalizedTranscriptLineViewModel Line,
        bool ShouldCommit);

    private sealed record TranscriptionChunkCheckpoint(
        int TotalChunks,
        int LastCompletedChunkIndex,
        DateTimeOffset? LastUpdatedUtc,
        int RowCount);

    private sealed record SpeakerDiarizationRowSnapshot(
        FinalizedTranscriptLineViewModel Line,
        string SpeakerLabel,
        string SpeakerLabelSource,
        int? DiarizationRevision,
        int? LastDiarizedChunkIndex);

    private sealed record SpeakerDiarizationChunkCheckpoint(
        int LastCompletedChunkIndex,
        DateTimeOffset? LastUpdatedUtc,
        int NextSpeakerIndex,
        IReadOnlyList<SpeakerDiarizationSpeakerMapDocument> SpeakerMappings,
        IReadOnlyList<SpeakerDiarizationRowSnapshot> Rows);
}




