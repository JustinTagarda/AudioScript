using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using AudioScript.Abstractions;
using AudioScript.Audio;
using AudioScript.Services;
using NAudio.Wave;

namespace AudioScript.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const string AudioFileDialogFilter = "Audio Files|*.wav;*.mp3;*.flac;*.aac;*.m4a;*.ogg;*.wma;*.mp4|All Files|*.*";
    private const string SpeakerDiarizationEngineId = "pyannote-community-1";
    private const int SpeakerDiarizationJobVersion = 1;
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

    private readonly IAudioTranscriptionService _audioTranscriptionService;
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
    private bool _isPlaybackMuted;
    private double _audioSeekMaximumSeconds;
    private double _audioSeekPositionSeconds;
    private string _audioElapsedText = "00:00";
    private string _audioRemainingText = "-00:00";
    private bool _autoPlayTimelineSelection;
    private bool _isGenerationRunning;
    private bool _isLiveTranscriptionRunning;
    private LiveAudioSourceKind _preferredLiveAudioSourceKind;
    private int _preferredLiveAudioDeviceNumber;
    private bool _liveAudioAutoGainEnabled;
    private double _liveAudioGainLevel;
    private AppThemePreference _selectedThemePreference;
    private bool _isUpdatingSeekFromPlayback;
    private bool _isApplyingSpeakerDiarizationLabels;
    private bool _pendingSpeakerDiarizationResume;
    private bool _suppressSessionAutosave;
    private AppUpdateSnapshot _appUpdateSnapshot;
    private AppEntitlementSnapshot _entitlementSnapshot;
    private int _selectedTranscriptViewIndex;
    private string _pendingImportedAudioFilePath = string.Empty;
    private string _transcriptExportDirectory = string.Empty;

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
        Func<IReadOnlyList<TranscriptionModelOption>>? availableModelsProvider = null)
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

        _autoPlayTimelineSelection = appPreferencesSnapshot.AutoPlayTimelineSelection;
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
        DeleteSelectedSessionCommand = new AsyncRelayCommand(DeleteSelectedSessionAsync, CanDeleteSelectedSession);
        PlayAudioCommand = new AsyncRelayCommand(PlayAudioAsync, CanPlayAudio);
        PauseAudioCommand = new AsyncRelayCommand(PauseAudioAsync, CanPauseAudio);

        _processLogService.LogEmitted += OnProcessLogEmitted;
        _audioPlaybackService.PlaybackStateChanged += OnAudioPlaybackStateChanged;
        _isAudioPlaying = _audioPlaybackService.IsPlaying;
        _isPlaybackMuted = _audioPlaybackService.IsMuted;

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

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<ConfirmationRequest>? ConfirmationRequested;
    public event EventHandler<ToastNotification>? ToastRequested;
    public event EventHandler? NewAudioFileStagedForTranscribeAudio;

    public ObservableCollection<EngineOptionViewModel> Engines { get; }
    public ObservableCollection<ProcessLogEntryViewModel> ProcessLogs { get; }
    public ObservableCollection<FinalizedTranscriptLineViewModel> FinalizedTranscriptLines { get; }
    public IReadOnlyList<AppThemeOption> ThemeOptions { get; }

    public IEnumerable<FinalizedTranscriptLineViewModel> CurrentTranscriptLines =>
        FinalizedTranscriptLines;
    public ObservableCollection<TranscriptSessionSummary> RecentSessions { get; }

    public AsyncRelayCommand CloseCommand { get; }
    public AsyncRelayCommand OpenAudioFileCommand { get; }
    public AsyncRelayCommand DeleteSelectedSessionCommand { get; }
    public AsyncRelayCommand PlayAudioCommand { get; }
    public AsyncRelayCommand PauseAudioCommand { get; }

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

    public string PremiumProductDisplayName =>
        _entitlementSnapshot.PremiumProductDisplayName;

    public bool CanUseLiveTranscription =>
        AppFeatureAccess.CanAccessFeature(AppFeature.LiveTranscription, HasPremium);

    public bool CanUseSpeakerDiarization =>
        AppFeatureAccess.CanAccessFeature(AppFeature.SpeakerDiarization, HasPremium);

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

    public bool LastSpeakerDetectionUsedHeuristicFallback => _lastSpeakerDetectionUsedHeuristicFallback;

    public bool IsTranscriptEmptyStateVisible =>
        !HasCurrentTranscriptLines;

    public bool CanCopyTranscript =>
        HasCurrentTranscriptLines && !IsBusy;

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
        && HasCurrentTranscriptLines
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

    public string ApplicationVersionStatusText
    {
        get => $"Version {_appUpdateSnapshot.InstalledVersion}";
    }

    public string ApplicationUpdateStatusText
    {
        get
        {
            if (_appUpdateSnapshot.State == AppUpdateState.Idle
                || string.IsNullOrWhiteSpace(_appUpdateSnapshot.StageText))
            {
                return ApplicationVersionStatusText;
            }

            return string.IsNullOrWhiteSpace(_appUpdateSnapshot.StatusMessage)
                ? $"{ApplicationVersionStatusText} - {_appUpdateSnapshot.StageText}"
                : $"{ApplicationVersionStatusText} - {_appUpdateSnapshot.StageText}: {_appUpdateSnapshot.StatusMessage}";
        }
    }

    public string ApplicationUpdateStageText => _appUpdateSnapshot.StageText;

    public string ApplicationUpdateMessageText => _appUpdateSnapshot.StatusMessage;

    public AppUpdateState ApplicationUpdateState => _appUpdateSnapshot.State;

    public bool IsApplicationUpdateProgressVisible => _appUpdateSnapshot.IsProgressVisible;

    public double ApplicationUpdateProgressPercent => _appUpdateSnapshot.ProgressValue * 100;

    public bool IsApplicationUpdateActive =>
        _appUpdateSnapshot.State != AppUpdateState.Idle;

    public bool IsMandatoryApplicationUpdateAvailable =>
        _appUpdateSnapshot.IsMandatoryUpdateAvailable;

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

    public bool IsPlaybackMuted
    {
        get => _isPlaybackMuted;
        set
        {
            if (!SetProperty(ref _isPlaybackMuted, value))
            {
                return;
            }

            _audioPlaybackService.IsMuted = value;
            AppendLog($"Playback mute: {(value ? "ON" : "OFF")}.");
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
        AppendLog("Disposed transcription resources.");
        return ValueTask.CompletedTask;
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

    public bool InitializeNewLiveTranscriptSession(string inputDeviceName)
    {
        try
        {
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
            foreach (TranscriptionTimedLine timedLine in timedLines)
            {
                if (IsDuplicateLiveSegmentBoundaryLine(timedLine))
                {
                    AppendLog(
                        $"Skipped duplicate live segment transcript row at {FormatOffset(timedLine.StartOffset)}: " +
                        $"'{BuildPreview(timedLine.Text)}'.");
                    continue;
                }

                var line = new FinalizedTranscriptLineViewModel(
                    startOffset: timedLine.StartOffset,
                    endOffset: timedLine.EndOffset,
                    isTimestampEstimated: timedLine.IsTimestampEstimated,
                    text: timedLine.Text.Trim());
                line.PropertyChanged += OnFinalizedLinePropertyChanged;
                FinalizedTranscriptLines.Add(line);
                addedCount++;
            }

            if (addedCount == 0)
            {
                AppendLog("Live segment transcription produced only duplicate transcript rows.");
                return 0;
            }

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

    private bool IsDuplicateLiveSegmentBoundaryLine(TranscriptionTimedLine candidate)
    {
        string candidateText = NormalizeLiveSegmentText(candidate.Text);
        if (candidateText.Length == 0)
        {
            return false;
        }

        TimeSpan candidateStart = candidate.StartOffset;
        TimeSpan candidateEnd = ResolveLiveSegmentEnd(candidate.StartOffset, candidate.EndOffset);
        FinalizedTranscriptLineViewModel? existing = FinalizedTranscriptLines
            .LastOrDefault(line => line.StartOffset is not null);
        if (existing?.StartOffset is not TimeSpan existingStart)
        {
            return false;
        }

        string existingText = NormalizeLiveSegmentText(existing.Text);
        if (!string.Equals(existingText, candidateText, StringComparison.Ordinal))
        {
            return false;
        }

        TimeSpan existingEnd = ResolveLiveSegmentEnd(existingStart, existing.EndOffset);
        return RangesOverlapOrTouch(existingStart, existingEnd, candidateStart, candidateEnd);
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

    public bool TryPrepareTranscribeAudioWorkflow()
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

        if (_currentSessionDocument is null)
        {
            _transcribeAudioWorkflow = new TranscribeAudioWorkflowState(
                TranscribeAudioWorkflowKind.NewFile,
                sourcePath,
                backupDocument: null);
            AppendLog("Transcribe Audio prepared for a new audio file.");
            return true;
        }

        bool hasExistingTranscript = HasExistingTranscriptContent(TranscriptGenerationMode.TranscribeAudio);
        if (hasExistingTranscript && !ConfirmTranscriptReplacement(
                operationName: "Transcribe Audio",
                transcriptMode: TranscriptGenerationMode.TranscribeAudio))
        {
            return false;
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
            backupDocument);
        AppendLog("Transcribe Audio prepared for the current session.");
        return true;
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
            RestorePreparedTranscribeAudioWorkflowBackup();
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
            (transcriptionAudioFilePath, deleteTranscriptionAudioFile) =
                PrepareAudioFilePathForTranscription(audioFilePath);
            TranscriptionResult result = await _audioTranscriptionService.TranscribeAudioFileAsync(
                transcriptionAudioFilePath,
                selectedEngineId,
                cancellationToken,
                progress);

            ApplyTranscriptionResult(result);
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
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppendLog("Transcribe Audio canceled.");
            return false;
        }
        catch (Exception ex)
        {
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
        if (TryConfirmSpeakerDiarizationResumeChoice(out bool shouldResume))
        {
            _pendingSpeakerDiarizationResume = shouldResume;
            return true;
        }

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

    private (string AudioFilePath, bool IsTemporary) PrepareAudioFilePathForTranscription(string audioFilePath)
    {
        if (!IsCurrentSessionLiveRecordingManifest() || !IsLiveRecordingManifestPath(audioFilePath))
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
        return string.Equals(Path.GetFileName(filePath), "manifest.json", StringComparison.OrdinalIgnoreCase)
            && filePath.Contains(
                Path.Combine("audio", "live"),
                StringComparison.OrdinalIgnoreCase);
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

    private bool TryConfirmSpeakerDiarizationResumeChoice(out bool shouldResume)
    {
        shouldResume = false;
        if (_currentSessionDocument?.Transcript.SpeakerDiarizationJob is not SpeakerDiarizationJobDocument job
            || !IsIncompleteSpeakerDiarizationJob(job)
            || !IsSpeakerDiarizationResumeEligible(
                job,
                BuildSpeakerDiarizationAudioFingerprint(_currentSessionDocument),
                BuildSpeakerDiarizationTranscriptFingerprint(),
                expectedTotalChunks: null))
        {
            return false;
        }

        EventHandler<ConfirmationRequest>? handler = ConfirmationRequested;
        if (handler is null)
        {
            shouldResume = true;
            AppendLog("Detect Speaker will resume an incomplete speaker diarization job.");
            return true;
        }

        var request = new ConfirmationRequest(
            title: "Resume speaker detection?",
            message: "This session has an incomplete speaker detection job. Resume from the last saved checkpoint or restart speaker detection from the beginning.",
            confirmButtonText: "Resume",
            cancelButtonText: "Restart");

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
            RaiseError($"Unable to confirm speaker detection resume: {ex.Message}");
            AppendLog($"Detect Speaker canceled: resume confirmation failed: {ex.Message}");
            return false;
        }

        shouldResume = request.IsConfirmed;
        AppendLog(shouldResume
            ? "Speaker detection resume confirmed by user."
            : "Speaker detection restart selected by user.");
        return true;
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
            && string.Equals(job.AudioFingerprint, audioFingerprint, StringComparison.OrdinalIgnoreCase)
            && string.Equals(job.TranscriptFingerprint, transcriptFingerprint, StringComparison.OrdinalIgnoreCase)
            && Math.Abs(job.ChunkDurationSeconds - ChunkedSpeakerDiarizationService.SpeakerDiarizationChunkDuration.TotalSeconds) < 0.001d
            && Math.Abs(job.OverlapDurationSeconds - ChunkedSpeakerDiarizationService.SpeakerDiarizationOverlapDuration.TotalSeconds) < 0.001d
            && (expectedTotalChunks is null || job.TotalChunks == expectedTotalChunks.Value)
            && job.LastCompletedChunkIndex < job.TotalChunks - 1;
    }

    private static bool IsIncompleteSpeakerDiarizationJob(SpeakerDiarizationJobDocument job)
    {
        return string.Equals(job.Status, SpeakerDiarizationJobStatuses.Running, StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.Status, SpeakerDiarizationJobStatuses.Canceled, StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.Status, SpeakerDiarizationJobStatuses.Failed, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSpeakerDiarizationAudioFingerprint(TranscriptSessionDocument document)
    {
        return string.Join(
            "|",
            document.Audio.Sha256,
            document.Audio.FileSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            document.Audio.DurationSeconds?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
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

        HandleSelectedAudioFile(selectedFilePath);
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
        HandleSelectedAudioFile(filePath);
        return true;
    }

    public Task LoadRecentSessionAsync(TranscriptSessionSummary session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return LoadSessionByIdAsync(session.SessionId);
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

    private Task CloseAsync()
    {
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
            NotifyPropertyChanged(nameof(HasCurrentSession));
            NotifyPropertyChanged(nameof(HasPendingSessionSelection));
            NotifyPropertyChanged(nameof(ShouldShowTranscriptChooseFileAction));
            NotifyPropertyChanged(nameof(ShouldShowTranscriptTranscribeAudioAction));
            NotifyPropertyChanged(nameof(IsTranscriptEmptyStateVisible));
            NotifyPropertyChanged(nameof(CanRunDetectSpeakerPrimaryAction));
            NotifyPropertyChanged(nameof(CanRunDetectSpeakersPrimaryAction));
            NotifyPropertyChanged(nameof(LoadedAudioFileName));
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
        if (!IsAudioFileLoaded)
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
        return HasCurrentSession && !IsBusy;
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
        for (int index = RecentSessions.Count - 1; index >= 0; index--)
        {
            if (string.Equals(RecentSessions[index].SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                RecentSessions.RemoveAt(index);
            }
        }

        NotifyPropertyChanged(nameof(HasRecentSessions));
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
        return IsAudioFileLoaded && !IsAudioPlaying;
    }

    private bool CanPauseAudio()
    {
        return IsAudioFileLoaded && IsAudioPlaying;
    }

    private void RefreshCommandStates()
    {
        CloseCommand.RaiseCanExecuteChanged();
        OpenAudioFileCommand.RaiseCanExecuteChanged();
        DeleteSelectedSessionCommand.RaiseCanExecuteChanged();
        PlayAudioCommand.RaiseCanExecuteChanged();
        PauseAudioCommand.RaiseCanExecuteChanged();
    }

    private void NotifyInteractionAvailabilityChanged()
    {
        NotifyPropertyChanged(nameof(IsEngineSelectionEnabled));
        NotifyPropertyChanged(nameof(HasPremium));
        NotifyPropertyChanged(nameof(IsPremiumProductAvailable));
        NotifyPropertyChanged(nameof(PremiumProductDisplayName));
        NotifyPropertyChanged(nameof(CanUseLiveTranscription));
        NotifyPropertyChanged(nameof(CanUseSpeakerDiarization));
        NotifyPropertyChanged(nameof(PremiumStatusText));
        NotifyPropertyChanged(nameof(IsTranscribeAudioTranscriptionEnabled));
        NotifyPropertyChanged(nameof(IsTranscriptGenerationEnabled));
        NotifyPropertyChanged(nameof(CanRunLivePrimaryAction));
        NotifyPropertyChanged(nameof(CanRunTranscribeAudioPrimaryAction));
        NotifyPropertyChanged(nameof(CanRunDetectSpeakerPrimaryAction));
        NotifyPropertyChanged(nameof(CanRunDetectSpeakersPrimaryAction));
    }

    private void NotifyCurrentTranscriptStateChanged()
    {
        NotifyPropertyChanged(nameof(CurrentTranscriptLines));
        NotifyPropertyChanged(nameof(HasCurrentTranscriptLines));
        NotifyPropertyChanged(nameof(IsTranscriptEmptyStateVisible));
        NotifyPropertyChanged(nameof(ShouldShowTranscriptChooseFileAction));
        NotifyPropertyChanged(nameof(ShouldShowTranscriptTranscribeAudioAction));
        NotifyPropertyChanged(nameof(CanCopyTranscript));
        NotifyPropertyChanged(nameof(CanRunDetectSpeakerPrimaryAction));
        NotifyPropertyChanged(nameof(CanRunDetectSpeakersPrimaryAction));
        NotifyPropertyChanged(nameof(IsTranscribeAudioTranscriptViewSelected));
        NotifyPropertyChanged(nameof(HasSpeakerLabels));
        NotifyPropertyChanged(nameof(TranscriptEmptyStateTitle));
        NotifyPropertyChanged(nameof(TranscriptEmptyStateMessage));
    }

    private void SaveAppPreferences()
    {
        _appPreferencesStore.Save(new AppPreferencesSnapshot(
            CopyFinalizedWithTimeline: false,
            AutoTranscribeWithAi: true,
            ThemePreference: _selectedThemePreference,
            AutoPlayTimelineSelection: _autoPlayTimelineSelection,
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

    private void HandleSelectedAudioFile(string sourceFilePath)
    {
        try
        {
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
                LoadRecentSessions(loadResult.Document.SessionId);
                AppendLog("Selected audio matched an existing session and was loaded.");
                return;
            }

            ClearOutputCore(unloadAudioPreview: true, clearSessionContext: true);
            if (!TryLoadAudioPreview(sourceFilePath))
            {
                RaiseError("Unable to load the selected audio file for preview.");
                AppendLog("Selected audio could not be staged because preview loading failed.");
                return;
            }

            _pendingImportedAudioFilePath = sourceFilePath;
            AppendLog("Selected audio does not have an existing session. Preview loaded and session creation is deferred until Generate is clicked.");
            _uiContext.Post(_ => NewAudioFileStagedForTranscribeAudio?.Invoke(this, EventArgs.Empty), null);
        }
        catch (Exception ex)
        {
            RaiseError($"Unable to process selected audio file: {ex.Message}");
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

        try
        {
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
            _sessionAutosaveTimer.Stop();
            TrySaveCurrentSession(
                updatedTranscriptMode: null,
                showErrorDialog: false,
                successLogMessage: string.Empty);

            TranscriptSessionLoadResult loadResult = _sessionStore.LoadSession(sessionId);
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

    private void LoadSessionResult(TranscriptSessionLoadResult loadResult, bool showAudioIssueDialog)
    {
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

        if (IsCurrentSessionAudioMissing && !string.IsNullOrWhiteSpace(CurrentSessionAudioIssue))
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
                    text: timedLine.Text.Trim());
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
            if (selectedSession is not null)
            {
                sessions = sessions
                    .Where(item => !string.Equals(item.SessionId, selectSessionId, StringComparison.OrdinalIgnoreCase))
                    .Prepend(selectedSession)
                    .ToArray();
            }
        }

        string? loadedSessionId = _currentSessionDocument?.SessionId;

        RecentSessions.Clear();

        foreach (TranscriptSessionSummary session in sessions)
        {
            RecentSessions.Add(session with
            {
                IsLoaded = !string.IsNullOrWhiteSpace(loadedSessionId)
                    && string.Equals(session.SessionId, loadedSessionId, StringComparison.OrdinalIgnoreCase)
            });
        }

        NotifyPropertyChanged(nameof(HasRecentSessions));
        string? highlightedSessionId = !string.IsNullOrWhiteSpace(selectSessionId)
            ? selectSessionId
            : _currentSessionDocument?.SessionId;
        SelectedRecentSession = !string.IsNullOrWhiteSpace(highlightedSessionId)
            ? RecentSessions.FirstOrDefault(item => string.Equals(item.SessionId, highlightedSessionId, StringComparison.OrdinalIgnoreCase))
            : null;
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
        string operationName,
        TranscriptGenerationMode transcriptMode)
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
        NotifyPropertyChanged(nameof(HasCurrentSession));
        NotifyPropertyChanged(nameof(HasPendingSessionSelection));
        NotifyPropertyChanged(nameof(ShouldShowTranscriptChooseFileAction));
        NotifyPropertyChanged(nameof(ShouldShowTranscriptTranscribeAudioAction));
        NotifyPropertyChanged(nameof(IsTranscriptEmptyStateVisible));
        NotifyPropertyChanged(nameof(CanRunDetectSpeakerPrimaryAction));
        NotifyPropertyChanged(nameof(CanRunDetectSpeakersPrimaryAction));
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
                    SpeakerLabelSource = line.SpeakerLabelSource,
                    DiarizationRevision = line.DiarizationRevision,
                    LastDiarizedChunkIndex = line.LastDiarizedChunkIndex,
                })
                .ToList(),
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
            .Select(line => new TranscriptSessionLineDocument
            {
                Text = line.Text,
                StartSeconds = line.StartOffset?.TotalSeconds,
                EndSeconds = line.EndOffset?.TotalSeconds,
                IsTimestampEstimated = line.IsTimestampEstimated,
                SpeakerLabel = line.SpeakerLabel,
                IsManuallyReviewed = line.IsManuallyReviewed,
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

    private void OnAppUpdateSnapshotChanged(object? sender, AppUpdateSnapshot snapshot)
    {
        _uiContext.Post(_ =>
        {
            _appUpdateSnapshot = snapshot;
            NotifyPropertyChanged(nameof(ApplicationVersionStatusText));
            NotifyPropertyChanged(nameof(ApplicationUpdateStatusText));
            NotifyPropertyChanged(nameof(ApplicationUpdateStageText));
            NotifyPropertyChanged(nameof(ApplicationUpdateMessageText));
            NotifyPropertyChanged(nameof(ApplicationUpdateState));
            NotifyPropertyChanged(nameof(IsApplicationUpdateProgressVisible));
            NotifyPropertyChanged(nameof(ApplicationUpdateProgressPercent));
            NotifyPropertyChanged(nameof(IsApplicationUpdateActive));
            NotifyPropertyChanged(nameof(IsMandatoryApplicationUpdateAvailable));
        }, null);
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
        return AppFeatureAccess.CanInstallModel(modelId, HasPremium);
    }

    public async Task<PremiumPurchaseResult> RequestPremiumPurchaseAsync(CancellationToken cancellationToken = default)
    {
        if (_entitlementService is null)
        {
            return new PremiumPurchaseResult(
                PremiumPurchaseStatus.NotAvailable,
                $"{PremiumProductDisplayName} purchase is unavailable in this build.");
        }

        PremiumPurchaseResult result = await _entitlementService.RequestPremiumPurchaseAsync(cancellationToken);
        if (result.Status is PremiumPurchaseStatus.Succeeded or PremiumPurchaseStatus.AlreadyOwned)
        {
            RefreshEngines(_availableModelsProvider());
        }

        return result;
    }

    private IEnumerable<TranscriptionModelOption> FilterAccessibleEngines(IEnumerable<TranscriptionModelOption> models)
    {
        return models.Where(model => AppFeatureAccess.CanUseModel(model.Id, HasPremium));
    }

    private void EnsureSelectedEngineAllowed()
    {
        if (SelectedEngine is null || AppFeatureAccess.CanUseModel(SelectedEngine.Id, HasPremium))
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
            TranscriptSessionDocument? backupDocument)
        {
            Kind = kind;
            SourceAudioPath = sourceAudioPath;
            BackupDocument = backupDocument;
        }

        public TranscribeAudioWorkflowKind Kind { get; }

        public string SourceAudioPath { get; }

        public TranscriptSessionDocument? BackupDocument { get; }

        public string? CreatedSessionId { get; set; }

        public bool HasStarted { get; set; }
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




