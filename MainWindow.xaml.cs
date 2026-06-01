using System;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using AudioScript.Abstractions;
using AudioScript.Audio;
using AudioScript.Services;
using AudioScript.Services.Store;
using AudioScript.ViewModels;
using DataGridCell = System.Windows.Controls.DataGridCell;
using DataGridCellsPresenter = System.Windows.Controls.Primitives.DataGridCellsPresenter;

namespace AudioScript;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly string _storePackageVersionText;
    internal enum TranscriptContextMenuScope
    {
        OtherCell,
        SpeakerCell,
        TextCell,
    }

    private enum TranscriptProcessingWorkflowKind
    {
        None,
        TranscribeAudio,
        DetectSpeakers,
    }

    private enum LiveStageProgressMode
    {
        AudioLevel,
        DrainPendingChunks,
        CancelPendingChunks,
    }

    internal enum LiveUiState
    {
        Idle,
        Preparing,
        Running,
        StoppingDrain,
        StoppingCancel,
        Failed,
        Stopped,
    }

    private const int TimelineColumnIndex = 0;
    private const int SpeakerColumnIndex = 1;
    private const int TranscriptTextColumnIndex = 2;
    private const double ToastTopMargin = 48;
    private const double ToastRightMargin = 48;
    private const double ToastHiddenOffsetY = -14;
    private static readonly TimeSpan LiveRecordingSegmentDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RowFileTranscriptionHeadSilencePadding = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RowFileTranscriptionTailMargin = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RowFileTranscriptionContextTailMargin = TimeSpan.FromMilliseconds(500);
    public static readonly RoutedUICommand TranscribeRowCommand =
        new("Transcribe This Row", nameof(TranscribeRowCommand), typeof(MainWindow));
    public static readonly RoutedUICommand CombineToPreviousRowCommand =
        new("Combine with Previous Row", nameof(CombineToPreviousRowCommand), typeof(MainWindow));
    public static readonly RoutedUICommand RenameSpeakerCommand =
        new("Rename Speaker…", nameof(RenameSpeakerCommand), typeof(MainWindow));
    public static readonly RoutedUICommand MergeAdjacentRowsForSelectedSpeakerCommand =
        new("Merge Adjacent Rows for This Speaker", nameof(MergeAdjacentRowsForSelectedSpeakerCommand), typeof(MainWindow));
    public static readonly RoutedUICommand MergeAllAdjacentRowsBySpeakerCommand =
        new("Merge All Adjacent Rows by Speaker", nameof(MergeAllAdjacentRowsBySpeakerCommand), typeof(MainWindow));
    public static readonly RoutedUICommand CopyRowTextCommand =
        new("Copy Row Text", nameof(CopyRowTextCommand), typeof(MainWindow));
    public static readonly RoutedUICommand SeparateRowCommand =
        new("Split into Two Rows", nameof(SeparateRowCommand), typeof(MainWindow));
    private static readonly TimeSpan ToastDisplayDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PlaybackEditStopDrainDelay = TimeSpan.FromMilliseconds(400);
    private bool _isApplyingTranscriptEditLoopSeek;
    private bool _isTranscriptEditLoopRestartPending;
    private bool _suppressNextTranscriptGridEditAfterSeparate;
    private MainViewModel? _boundViewModel;
    private CancellationTokenSource? _copyToastCts;
    private CancellationTokenSource? _transcribeAudioBatchTranscriptionCts;
    private readonly Func<PlaybackTranscriptionSession>? _playbackTranscriptionSessionFactory;
    private readonly Func<AudioInputDeviceOption, LiveAudioGainOptions, LiveRecordingSession, LiveRecordingCaptureSession>? _liveRecordingCaptureSessionFactory;
    private readonly IAudioTranscriptionService? _rowAudioTranscriptionService;
    private readonly AudioStandardizer? _rowAudioStandardizer;
    private readonly WaveClipExtractor? _rowWaveClipExtractor;
    private readonly ProcessLogService? _processLogService;
    private readonly WhisperModelManager? _whisperModelManager;
    private readonly PyannoteCommunityModelManager? _pyannoteCommunityModelManager;
    private bool _isTranscribeAudioBatchTranscribing;
    private bool _isRowFileTranscriptionRunning;
    private bool _isLiveTranscribing;
    private bool _isTranscribeAudioBatchPendingStart;
    private bool _isTranscriptProcessingMuteAvailable = true;
    private bool _isTranscriptProcessingIndeterminate = true;
    private bool _isTranscriptProcessingCanceling;
    private TranscriptProcessingWorkflowKind _activeTranscriptProcessingWorkflow = TranscriptProcessingWorkflowKind.None;
    private double _transcriptProcessingPercent;
    private string _transcriptProcessingElapsedText = "Elapsed 00:00";
    private string _transcriptProcessingEtaText = "ETA calculating";
    private string _transcriptProcessingChunkText = "Progress 0%";
    private string _transcriptProcessingAudioText = "Audio 00:00 / 00:00";
    private string _transcriptProcessingSourceFileText = string.Empty;
    private string _transcriptProcessingSourceFileSizeText = string.Empty;
    private string _transcriptProcessingEngineText = string.Empty;
    private string _transcriptProcessingStartButtonText = "Start";
    private FinalizedTranscriptLineViewModel? _playbackMatchedLine;
    private FinalizedTranscriptLineViewModel? _editLoopLine;
    private PlaybackEditTranscriptionState? _activePlaybackEditTranscription;
    private TimeSpan? _editLoopStartOffset;
    private TimeSpan? _editLoopRepeatOffset;
    private bool _nonTranscriptCellEditShouldResumePlayback;
    private FinalizedTranscriptLineViewModel? _transcriptTextEditLine;
    private string _transcriptTextEditOriginalText = string.Empty;
    private FinalizedTranscriptLineViewModel? _speakerEditLine;
    private string _speakerEditOriginalLabel = string.Empty;
    private TranscriptContextMenuScope _transcriptContextMenuScope = TranscriptContextMenuScope.OtherCell;
    private FinalizedTranscriptLineViewModel? _lastPlaybackSyncedLine;
    private readonly object _liveSegmentTranscriptionSync = new();
    private readonly Queue<LiveSegmentTranscriptionCompletedEventArgs> _pendingLiveSegmentTranscriptionCompletions = new();
    private int _liveChunksGenerated;
    private int _liveChunksQueued;
    private int _liveChunksProcessing;
    private int _liveChunksTranscribed;
    private int _liveChunksFailed;
    private LiveRecordingCaptureSession? _liveRecordingCaptureSession;
    private LiveSegmentTranscriptionSession? _liveSegmentTranscriptionSession;
    private LiveRecordingSession? _liveRecordingSession;
    private LiveStageProgressMode _liveStageProgressMode = LiveStageProgressMode.AudioLevel;
    private bool _livePanelIsOperationPending;
    private bool _livePanelIsStopping;
    private int _liveDrainInitialPendingChunks;
    private bool _livePanelInitialized;
    private bool _isLiveSegmentTranscriptionDrainScheduled;
    private bool _isClosingAfterLiveTranscriptionStop;
    private bool _forceCancelLiveChunkTranscriptions;
    private bool _isLiveTranscriptionStopping;
    private bool _isTranscriptionInteractionLocked;
    private bool _deferLiveRunningStopUntilPendingChunksCleared;
    private double _lastFullyTranscribedLiveSegmentEndSeconds;
    private bool _hasConfirmedCloseWithActiveTranscription;
    private LiveUiState _liveUiState = LiveUiState.Idle;
    private DeferredUpdateInstallWindow? _updateProgressWindow;
    private readonly DispatcherTimer _liveElapsedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset? _liveElapsedStartedUtc;

    public MainWindow(
        Func<PlaybackTranscriptionSession>? playbackTranscriptionSessionFactory = null,
        Func<AudioInputDeviceOption, LiveAudioGainOptions, LiveRecordingSession, LiveRecordingCaptureSession>? liveRecordingCaptureSessionFactory = null,
        IAudioTranscriptionService? rowAudioTranscriptionService = null,
        AudioStandardizer? rowAudioStandardizer = null,
        WaveClipExtractor? rowWaveClipExtractor = null,
        ProcessLogService? processLogService = null,
        WhisperModelManager? whisperModelManager = null,
        PyannoteCommunityModelManager? pyannoteCommunityModelManager = null)
    {
        _playbackTranscriptionSessionFactory = playbackTranscriptionSessionFactory;
        _liveRecordingCaptureSessionFactory = liveRecordingCaptureSessionFactory;
        _rowAudioTranscriptionService = rowAudioTranscriptionService;
        _rowAudioStandardizer = rowAudioStandardizer;
        _rowWaveClipExtractor = rowWaveClipExtractor;
        _processLogService = processLogService;
        _whisperModelManager = whisperModelManager;
        _pyannoteCommunityModelManager = pyannoteCommunityModelManager;
        _storePackageVersionText = ResolveStorePackageVersionText();
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closing += OnMainWindowClosing;
        Closed += OnMainWindowClosed;
        PreviewMouseDown += OnWindowMouseDismissToast;
        PreviewMouseWheel += OnWindowMouseWheelDismissToast;
        Loaded += OnMainWindowLoaded;
        _liveElapsedTimer.Tick += OnLiveElapsedTimerTick;
        ResetLiveElapsedTime();
    }

    public string StorePackageVersionText => _storePackageVersionText;

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string ResolveStorePackageVersionText()
    {
        string installedVersion = new AppVersionProvider().InstalledVersion;
        if (!Version.TryParse(installedVersion, out Version? version))
        {
            return "0.0.0.0";
        }

        int major = Math.Max(0, version.Major);
        int minor = Math.Max(0, version.Minor);
        int build = Math.Max(0, version.Build);
        return $"{major}.{minor}.{build}.0";
    }

    public bool IsTranscribeAudioBatchTranscribing
    {
        get => _isTranscribeAudioBatchTranscribing;
        private set
        {
            if (_isTranscribeAudioBatchTranscribing == value)
            {
                return;
            }

            _isTranscribeAudioBatchTranscribing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShouldShowMediaPlayerPanel));
            OnPropertyChanged(nameof(ShouldShowAudioTranscriptionPanel));
            OnPropertyChanged(nameof(IsTranscribeAudioProcessingUiBusy));
            OnPropertyChanged(nameof(CanPrimeTranscribeAudioFromCurrentSession));
            UpdateLivePrimaryActionButtonState();
            UpdateTranscribeAudioBatchControlState();
            UpdateTranscriptionInteractionLockState();
        }
    }

    public bool IsLiveTranscribing
    {
        get => _isLiveTranscribing;
        private set
        {
            if (_isLiveTranscribing == value)
            {
                return;
            }

            _isLiveTranscribing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShouldShowMediaPlayerPanel));
            UpdateTranscriptionInteractionLockState();
        }
    }

    public bool IsTranscriptionInteractionLocked =>
        IsTranscribeAudioBatchTranscribing
        || IsLiveTranscribing
        || _isLiveTranscriptionStopping;

    public bool ShouldShowMediaPlayerPanel =>
        DataContext is MainViewModel vm
        && !IsLiveTranscribing
        && !IsTranscribeAudioBatchTranscribing
        && !vm.ShouldShowLiveTranscriptionPanel
        && !ShouldShowAudioTranscriptionPanel
        && vm.IsAudioFileLoaded;

    public bool ShouldShowAudioTranscriptionPanel =>
        DataContext is MainViewModel vm
        && vm.IsCurrentSessionAudioTranscriptionSession
        && (vm.IsTranscriptDataEmpty
            || IsTranscribeAudioBatchPendingStart
            || IsTranscribeAudioBatchTranscribing
            || vm.GetTranscriptProcessingPanelSessionSnapshot().ResumeAvailable);

    private void SetLiveTranscriptionStopping(bool isStopping)
    {
        if (_isLiveTranscriptionStopping == isStopping)
        {
            return;
        }

        _isLiveTranscriptionStopping = isStopping;
        UpdateTranscriptionInteractionLockState();
    }

    private void UpdateTranscriptionInteractionLockState()
    {
        bool isLocked = IsTranscriptionInteractionLocked;
        if (_isTranscriptionInteractionLocked == isLocked)
        {
            return;
        }

        _isTranscriptionInteractionLocked = isLocked;
        OnPropertyChanged(nameof(IsTranscriptionInteractionLocked));

        if (!isLocked || DataContext is not MainViewModel vm)
        {
            return;
        }

        if (_activePlaybackEditTranscription is not null)
        {
            StopActivePlaybackEditTranscription(
                vm,
                pausePlayback: false,
                "Transcription in progress",
                discardResults: true);
        }

        ClearTranscriptEditPlaybackLoop();

        if (vm.IsAudioPlaying && vm.PauseAudioCommand.CanExecute(null))
        {
            vm.PauseAudioCommand.Execute(null);
        }
    }

    public bool IsTranscribeAudioBatchPendingStart
    {
        get => _isTranscribeAudioBatchPendingStart;
        private set
        {
            if (_isTranscribeAudioBatchPendingStart == value)
            {
                return;
            }

            _isTranscribeAudioBatchPendingStart = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTranscribeAudioBatchStartEnabled));
            OnPropertyChanged(nameof(TranscriptProcessingDismissButtonText));
            OnPropertyChanged(nameof(ShouldShowAudioTranscriptionPanel));
            OnPropertyChanged(nameof(IsTranscribeAudioProcessingUiBusy));
            OnPropertyChanged(nameof(CanPrimeTranscribeAudioFromCurrentSession));
            UpdateTranscribeAudioBatchControlState();
        }
    }

    public bool IsTranscribeAudioBatchStartEnabled => IsTranscribeAudioBatchPendingStart && !IsTranscribeAudioBatchTranscribing;
    public bool IsTranscribeAudioProcessingUiBusy =>
        IsTranscribeAudioBatchPendingStart
        || IsTranscribeAudioBatchTranscribing
        || IsTranscriptProcessingCanceling;
    public bool CanPrimeTranscribeAudioFromCurrentSession =>
        DataContext is MainViewModel vm
        && !IsTranscribeAudioProcessingUiBusy
        && vm.IsCurrentSessionAudioTranscriptionSession
        && (!vm.HasCurrentTranscriptLines || vm.GetTranscriptProcessingPanelSessionSnapshot().ResumeAvailable)
        && vm.CanRunTranscribeAudioPrimaryAction;
    public bool IsTranscribeAudioRestartVisible =>
        DataContext is MainViewModel vm
        && !IsTranscribeAudioBatchTranscribing
        && vm.IsCurrentSessionAudioTranscriptionSession
        && vm.GetTranscriptProcessingPanelSessionSnapshot().ResumeAvailable
        && vm.HasCurrentTranscriptLines;

    public string TranscriptProcessingDismissButtonText => IsTranscribeAudioBatchPendingStart
        ? "Close"
        : _activeTranscriptProcessingWorkflow == TranscriptProcessingWorkflowKind.TranscribeAudio
            ? "Pause"
            : "Cancel";

    public string TranscriptProcessingStartButtonText
    {
        get => _transcriptProcessingStartButtonText;
        private set
        {
            if (string.Equals(_transcriptProcessingStartButtonText, value, StringComparison.Ordinal))
            {
                return;
            }

            _transcriptProcessingStartButtonText = value;
            OnPropertyChanged();
        }
    }

    public bool IsTranscriptProcessingMuteAvailable
    {
        get => _isTranscriptProcessingMuteAvailable;
        private set
        {
            if (_isTranscriptProcessingMuteAvailable == value)
            {
                return;
            }

            _isTranscriptProcessingMuteAvailable = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTranscriptProcessingMuteEnabled));
        }
    }

    private void Window_PreviewDragEnter(object sender, System.Windows.DragEventArgs e)
    {
        UpdateAudioFileDropState(e);
    }

    private void Window_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        UpdateAudioFileDropState(e);
    }

    private void Window_PreviewDragLeave(object sender, System.Windows.DragEventArgs e)
    {
        ResetAudioFileDropState();
    }

    private void Window_PreviewDrop(object sender, System.Windows.DragEventArgs e)
    {
        string? filePath = GetDroppedAudioFilePath(e);
        ResetAudioFileDropState();

        if (_boundViewModel is null || string.IsNullOrWhiteSpace(filePath))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = _boundViewModel.TryImportAudioFileFromPath(filePath)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.ContextMenu is not System.Windows.Controls.ContextMenu menu)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private async void LiveTranscriptionPrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if (vm.IsLiveTranscriptionRunning || IsTranscribeAudioBatchTranscribing)
        {
            return;
        }

        var confirmDialog = new ConfirmationDialogWindow(
            "Start live recording session?",
            "AudioScript will create a new live recording session. You can start capture later from the live panel.",
            "Create Session",
            "Cancel")
        {
            Owner = this,
        };
        if (confirmDialog.ShowDialog() != true)
        {
            return;
        }

        if (!vm.InitializeNewLiveTranscriptSession("Live Recording"))
        {
            return;
        }

        await EnsureLiveTranscriptionPanelReadyAsync(vm);
        SetLiveSessionReadyToStartActivity();
        UpdateLiveControlState();
        ShowCopyToast(
            "Live recording session started",
            "Session ready. Select audio source, then click Start to begin live transcription.",
            ToastNotificationType.Info);
    }

    private async void TranscribeAudioPrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || IsTranscribeAudioBatchTranscribing || IsTranscribeAudioBatchPendingStart)
        {
            return;
        }

        OpenTranscribeAudioBatchDialog(vm);
    }

    private async void TranscribeAudio_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || IsTranscribeAudioBatchTranscribing || IsTranscribeAudioBatchPendingStart)
        {
            return;
        }

        OpenTranscribeAudioBatchDialog(vm);
    }

    private async void DetectSpeakerPrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || IsTranscribeAudioBatchTranscribing || IsTranscribeAudioBatchPendingStart)
        {
            return;
        }

        if (!vm.CanUseSpeakerDiarization)
        {
            if (!vm.IsSpeakerDiarizationRuntimeAvailable)
            {
                ShowBlockingMessage(
                    "Speaker detection unavailable",
                    vm.SpeakerDiarizationRuntimeStatusMessage);
                return;
            }

            if (vm.IsDevelopmentUnpackagedMode)
            {
                ShowBlockingMessage(
                    "Feature unavailable in local debug run",
                    "Speaker diarization upgrade/purchase prompts are disabled outside the Microsoft Store package.");
                return;
            }

            if (!await PromptPremiumFeatureAsync(
                "Detect Speaker",
                $"Detect Speaker is available with {vm.PremiumProductDisplayName}. Upgrade in Microsoft Store to unlock this feature."))
            {
                return;
            }
        }

        OpenDetectSpeakersDialog(vm);
    }

    private void OpenTranscribeAudioBatchDialog(MainViewModel vm, bool forceRestart = false)
    {
        if (!EnsureSelectedEngineReady(vm, "Transcribe Audio"))
        {
            return;
        }

        if (!vm.TryPrepareTranscribeAudioWorkflow(forceRestart))
        {
            return;
        }

        ConfigureTranscriptProcessingUi(allowMute: false);
        _activeTranscriptProcessingWorkflow = TranscriptProcessingWorkflowKind.TranscribeAudio;
        TranscriptProcessingStartButtonText = vm.IsPreparedTranscribeAudioResumeRequested ? "Resume" : "Start";
        TranscriptProcessingSourceFileText = vm.LoadedAudioFileName;
        TranscriptProcessingSourceFileSizeText = FormatFileSizeText(vm.LoadedAudioFilePath);
        TranscriptProcessingEngineText = ResolveCurrentEngineLabel(vm);
        TranscriptProcessingChunkText = "Progress 0%";
        TranscriptProcessingAudioText = "Audio 00:00 / 00:00";
        TranscriptProcessingElapsedText = "Elapsed 00:00";
        TranscriptProcessingEtaText = "ETA calculating";
        IsTranscriptProcessingIndeterminate = false;
        IsTranscribeAudioBatchPendingStart = true;
        ApplyTranscribeAudioBatchInteractionLock();
    }

    private void OpenDetectSpeakersDialog(MainViewModel vm)
    {
        if (!vm.CanRunDetectSpeakerPrimaryAction)
        {
            return;
        }

        if (_pyannoteCommunityModelManager is null)
        {
            ShowBlockingMessage(
                "Speaker detection unavailable",
                "Speaker detection is not configured in this build.");
            return;
        }

        if (!_pyannoteCommunityModelManager.IsSupportedOnCurrentArchitecture)
        {
            ShowBlockingMessage(
                "Speaker detection unavailable",
                "Speaker diarization requires an x64 AudioScript build.");
            return;
        }

        if (!vm.ConfirmSpeakerLabelOverwrite())
        {
            return;
        }

        ConfigureTranscriptProcessingUi(allowMute: false);
        _activeTranscriptProcessingWorkflow = TranscriptProcessingWorkflowKind.DetectSpeakers;
        TranscriptProcessingStartButtonText = "Start";
        TranscriptProcessingSourceFileText = vm.LoadedAudioFileName;
        TranscriptProcessingSourceFileSizeText = FormatFileSizeText(vm.LoadedAudioFilePath);
        TranscriptProcessingEngineText = "Pyannote Community-1";
        TranscriptProcessingChunkText = "Progress 0%";
        TranscriptProcessingAudioText = "Audio 00:00 / 00:00";
        TranscriptProcessingElapsedText = "Elapsed 00:00";
        TranscriptProcessingEtaText = "ETA calculating";
        IsTranscriptProcessingIndeterminate = false;
        IsTranscribeAudioBatchPendingStart = true;
        ApplyTranscribeAudioBatchInteractionLock();
    }

    private async Task RunTranscribeAudioAsync(MainViewModel vm)
    {
        if (!IsTranscribeAudioBatchPendingStart)
        {
            return;
        }

        IsTranscribeAudioBatchPendingStart = false;
        IsTranscriptProcessingIndeterminate = true;
        IsTranscribeAudioBatchTranscribing = true;
        vm.SetGenerationRunning(isRunning: true);
        _transcribeAudioBatchTranscriptionCts = new CancellationTokenSource();
        CancellationToken cancellationToken = _transcribeAudioBatchTranscriptionCts.Token;
        await Dispatcher.Yield(DispatcherPriority.Background);

        try
        {
            _processLogService?.UpdateCrashContext(
                "transcribe_audio.batch.run_started",
                $"engine='{vm.SelectedEngineId}', source='{vm.LoadedAudioFilePath}'");
            LogTranscribeAudioBatch("Transcribe Audio requested.");
            var transcriptionProgress = new Progress<TranscriptionProgressSnapshot>(ApplyTranscriptionProgress);

            bool completed = await vm.RunPreparedTranscribeAudioWorkflowAsync(cancellationToken, transcriptionProgress);
            if (cancellationToken.IsCancellationRequested)
            {
                vm.PausePreparedTranscribeAudioWorkflow();
                ShowCopyToast(
                    "Transcription paused",
                    "Completed chunks were kept. Click Transcribe Audio to resume.",
                    ToastNotificationType.Warning);
                return;
            }

            if (!completed)
            {
                ShowTranscribeAudioErrorDialog("Transcribe Audio did not complete.");
                vm.FailPreparedTranscribeAudioWorkflow();
                return;
            }

            vm.CompletePreparedTranscribeAudioWorkflow();
            _processLogService?.UpdateCrashContext("transcribe_audio.batch.completed");
            ShowCopyToast(
                "Transcribe Audio completed",
                "Transcript rows are ready.",
                ToastNotificationType.Success);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _processLogService?.UpdateCrashContext("transcribe_audio.batch.canceled");
            vm.PausePreparedTranscribeAudioWorkflow();
        }
        catch (Exception ex)
        {
            _processLogService?.UpdateCrashContext("transcribe_audio.batch.failed", ex.GetType().FullName);
            vm.LogHandledException("Transcribe Audio", ex);
            LogTranscribeAudioBatch($"Transcribe Audio failed: {ex.Message}");
            ShowTranscribeAudioErrorDialog(BuildTranscribeAudioFailureMessage(vm, ex));
            vm.FailPreparedTranscribeAudioWorkflow();
        }
        finally
        {
            _transcribeAudioBatchTranscriptionCts?.Dispose();
            _transcribeAudioBatchTranscriptionCts = null;
            IsTranscribeAudioBatchTranscribing = false;
            vm.SetGenerationRunning(isRunning: false);
            RestoreTranscribeAudioBatchInteractionLock();
        }
    }

    private async Task RunDetectSpeakersAsync(MainViewModel vm)
    {
        if (!IsTranscribeAudioBatchPendingStart)
        {
            return;
        }

        IsTranscribeAudioBatchPendingStart = false;
        IsTranscriptProcessingIndeterminate = true;
        IsTranscribeAudioBatchTranscribing = true;
        vm.SetGenerationRunning(isRunning: true);
        _transcribeAudioBatchTranscriptionCts = new CancellationTokenSource();
        CancellationToken cancellationToken = _transcribeAudioBatchTranscriptionCts.Token;
        await Dispatcher.Yield(DispatcherPriority.Background);

        try
        {
            LogTranscribeAudioBatch("Detect Speaker requested.");
            if (!await EnsureSpeakerDetectionAssetsReadyAsync(cancellationToken))
            {
                return;
            }

            var progress = new Progress<TranscriptionProgressSnapshot>(ApplyTranscriptionProgress);

            bool completed = await vm.RunSpeakerDetectionAsync(cancellationToken, progress);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!completed)
            {
                ShowTranscribeAudioErrorDialog("Detect Speaker did not complete.");
                return;
            }

            ShowCopyToast(
                vm.LastSpeakerDetectionUsedHeuristicFallback
                    ? "Speaker labels applied"
                    : "Speaker detection completed",
                vm.LastSpeakerDetectionUsedHeuristicFallback
                    ? "Pyannote Community-1 is unavailable, so heuristic speaker labels were applied."
                    : "Speaker labels are ready.",
                vm.LastSpeakerDetectionUsedHeuristicFallback
                    ? ToastNotificationType.Warning
                    : ToastNotificationType.Success);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            vm.LogHandledException("Detect Speaker", ex);
            LogTranscribeAudioBatch($"Detect Speaker failed: {ex.Message}");
            ShowErrorDialog(BuildDetectSpeakerFailureMessage(vm, ex), title: "Detect Speaker failed");
        }
        finally
        {
            _transcribeAudioBatchTranscriptionCts?.Dispose();
            _transcribeAudioBatchTranscriptionCts = null;
            IsTranscribeAudioBatchTranscribing = false;
            vm.SetGenerationRunning(isRunning: false);
            RestoreTranscribeAudioBatchInteractionLock();
        }
    }

    public bool IsTranscriptProcessingIndeterminate
    {
        get => _isTranscriptProcessingIndeterminate;
        private set
        {
            if (_isTranscriptProcessingIndeterminate == value)
            {
                return;
            }

            _isTranscriptProcessingIndeterminate = value;
            OnPropertyChanged();
        }
    }

    public bool IsTranscriptProcessingCanceling
    {
        get => _isTranscriptProcessingCanceling;
        private set
        {
            if (_isTranscriptProcessingCanceling == value)
            {
                return;
            }

            _isTranscriptProcessingCanceling = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTranscriptProcessingCancelEnabled));
            OnPropertyChanged(nameof(IsTranscriptProcessingMuteEnabled));
            OnPropertyChanged(nameof(IsTranscribeAudioProcessingUiBusy));
            OnPropertyChanged(nameof(CanPrimeTranscribeAudioFromCurrentSession));
            UpdateTranscribeAudioBatchControlState();
        }
    }

    public bool IsTranscriptProcessingCancelEnabled => !IsTranscriptProcessingCanceling;

    public bool IsTranscriptProcessingMuteEnabled =>
        IsTranscriptProcessingMuteAvailable && !IsTranscriptProcessingCanceling;

    public double TranscriptProcessingPercent
    {
        get => _transcriptProcessingPercent;
        private set
        {
            double normalized = Math.Clamp(value, 0, 100);
            if (Math.Abs(_transcriptProcessingPercent - normalized) < 0.01)
            {
                return;
            }

            _transcriptProcessingPercent = normalized;
            OnPropertyChanged();
        }
    }

    public string TranscriptProcessingElapsedText
    {
        get => _transcriptProcessingElapsedText;
        private set
        {
            if (string.Equals(_transcriptProcessingElapsedText, value, StringComparison.Ordinal))
            {
                return;
            }

            _transcriptProcessingElapsedText = value;
            OnPropertyChanged();
        }
    }

    public string TranscriptProcessingEtaText
    {
        get => _transcriptProcessingEtaText;
        private set
        {
            if (string.Equals(_transcriptProcessingEtaText, value, StringComparison.Ordinal))
            {
                return;
            }

            _transcriptProcessingEtaText = value;
            OnPropertyChanged();
        }
    }

    public string TranscriptProcessingChunkText
    {
        get => _transcriptProcessingChunkText;
        private set
        {
            if (string.Equals(_transcriptProcessingChunkText, value, StringComparison.Ordinal))
            {
                return;
            }

            _transcriptProcessingChunkText = value;
            OnPropertyChanged();
        }
    }

    public string TranscriptProcessingAudioText
    {
        get => _transcriptProcessingAudioText;
        private set
        {
            if (string.Equals(_transcriptProcessingAudioText, value, StringComparison.Ordinal))
            {
                return;
            }

            _transcriptProcessingAudioText = value;
            OnPropertyChanged();
        }
    }

    public string TranscriptProcessingSourceFileText
    {
        get => _transcriptProcessingSourceFileText;
        private set
        {
            if (string.Equals(_transcriptProcessingSourceFileText, value, StringComparison.Ordinal))
            {
                return;
            }

            _transcriptProcessingSourceFileText = value;
            OnPropertyChanged();
        }
    }

    public string TranscriptProcessingSourceFileSizeText
    {
        get => _transcriptProcessingSourceFileSizeText;
        private set
        {
            if (string.Equals(_transcriptProcessingSourceFileSizeText, value, StringComparison.Ordinal))
            {
                return;
            }

            _transcriptProcessingSourceFileSizeText = value;
            OnPropertyChanged();
        }
    }

    public string TranscriptProcessingEngineText
    {
        get => _transcriptProcessingEngineText;
        private set
        {
            if (string.Equals(_transcriptProcessingEngineText, value, StringComparison.Ordinal))
            {
                return;
            }

            _transcriptProcessingEngineText = value;
            OnPropertyChanged();
        }
    }

    private async Task EnsureLiveTranscriptionPanelReadyAsync(MainViewModel vm)
    {
        if (!vm.CanUseLiveTranscription)
        {
            if (vm.IsDevelopmentUnpackagedMode)
            {
                ShowBlockingMessage(
                    "Feature unavailable in local debug run",
                    "Live transcription upgrade/purchase prompts are disabled outside the Microsoft Store package.");
                return;
            }

            if (!await PromptPremiumFeatureAsync(
                "Live Transcription",
                $"Live Transcription is available with {vm.PremiumProductDisplayName}. Upgrade in Microsoft Store to unlock this feature."))
            {
                return;
            }
        }

        IAudioTranscriptionService? audioTranscriptionService = _rowAudioTranscriptionService;
        ProcessLogService? processLogService = _processLogService;
        if (_liveRecordingCaptureSessionFactory is null || audioTranscriptionService is null || processLogService is null)
        {
            ShowCopyToast(
                "Live transcription unavailable",
                "Live transcription is not configured.",
                ToastNotificationType.Warning);
            return;
        }

        if (!EnsureSelectedEngineReady(vm, "Live transcription"))
        {
            return;
        }

        IReadOnlyList<AudioInputDeviceOption> devices = BuildLiveAudioSourceOptions();
        if (devices.Count == 0)
        {
            ShowCopyToast(
                "No input devices",
                "No recording input devices were found.",
                ToastNotificationType.Warning);
            return;
        }

        DeviceComboBox.ItemsSource = devices;
        AudioInputDeviceOption initialDevice = ResolveInitialLiveAudioSource(devices, vm);
        DeviceComboBox.SelectedItem = initialDevice;
        if (!_livePanelInitialized)
        {
            SetLiveIdleActivity();
            _livePanelInitialized = true;
        }
        UpdateLiveControlState();
    }

    private static AudioInputDeviceOption ResolveInitialLiveAudioSource(
        IReadOnlyList<AudioInputDeviceOption> devices,
        MainViewModel vm)
    {
        return devices.FirstOrDefault(device =>
                device.Kind == vm.PreferredLiveAudioSourceKind
                && device.DeviceNumber == vm.PreferredLiveAudioDeviceNumber)
            ?? devices[0];
    }

    private async Task<bool> StartLiveTranscriptionAsync(
        MainViewModel vm,
        AudioInputDeviceOption selectedDevice)
    {
        IAudioTranscriptionService? audioTranscriptionService = _rowAudioTranscriptionService;
        ProcessLogService? processLogService = _processLogService;
        if (_liveRecordingCaptureSessionFactory is null || audioTranscriptionService is null || processLogService is null)
        {
            ShowCopyToast(
                "Live transcription unavailable",
                "Live transcription is not configured.",
                ToastNotificationType.Warning);
            return false;
        }

        if (IsLiveTranscribing)
        {
            return true;
        }

        if (!EnsureSelectedEngineReady(vm, "Live transcription"))
        {
            return false;
        }

        vm.SetPreferredLiveAudioSource(selectedDevice);
        if (!vm.EnsureLiveTranscriptSession(selectedDevice.Name))
        {
            return false;
        }

        ConfigureTranscriptProcessingUi(allowMute: false);

        LiveRecordingSession recordingSession;
        try
        {
            recordingSession = vm.CreateLiveRecordingSession(selectedDevice.Name, LiveRecordingSegmentDuration);
        }
        catch (Exception ex)
        {
            vm.LogHandledException("live recording start", ex);
            ShowCopyToast(
                "Live recording failed",
                $"Live transcription was not started because recording could not be prepared: {ex.Message}",
                ToastNotificationType.Error);
            return false;
        }

        LiveAudioGainOptions captureGainOptions = new(
            IsAutomaticGainEnabled: false,
            ManualGainLevel: LiveAudioGainOptions.DefaultManualGainLevel);
        LiveRecordingCaptureSession captureSession = _liveRecordingCaptureSessionFactory(
            selectedDevice,
            captureGainOptions,
            recordingSession);
        var segmentTranscriptionSession = new LiveSegmentTranscriptionSession(
            recordingSession,
            audioTranscriptionService,
            processLogService);
        _liveRecordingCaptureSession = captureSession;
        _liveSegmentTranscriptionSession = segmentTranscriptionSession;
        _liveRecordingSession = recordingSession;
        _lastFullyTranscribedLiveSegmentEndSeconds = 0;
        ResetLiveChunkCounts();
        captureSession.AudioLevelChanged += OnLiveAudioLevelChanged;
        captureSession.Faulted += OnLiveTranscriptionFaulted;
        segmentTranscriptionSession.SegmentTranscriptionQueued += OnLiveSegmentTranscriptionQueued;
        segmentTranscriptionSession.SegmentTranscriptionStarted += OnLiveSegmentTranscriptionStarted;
        segmentTranscriptionSession.SegmentTranscriptionCompleted += OnLiveSegmentTranscriptionCompleted;
        segmentTranscriptionSession.SegmentTranscriptionFailed += OnLiveSegmentTranscriptionFailed;
        recordingSession.Faulted += OnLiveRecordingFaulted;

        try
        {
            string model = TranscriptionModelCatalog.SupportsPlaybackTranscription(vm.SelectedEngine?.Id ?? string.Empty)
                ? vm.SelectedEngine!.Id
                : TranscriptionModelCatalog.WhisperSmall;
            IsLiveTranscribing = true;
            _deferLiveRunningStopUntilPendingChunksCleared = false;
            vm.SetGenerationRunning(isRunning: true, isLiveTranscriptionRunning: true);
            LogLiveTranscription(
                $"Live transcription started source='{selectedDevice.Name}', kind={selectedDevice.Kind}, deviceNumber={selectedDevice.DeviceNumber}, model='{model}'.");
            segmentTranscriptionSession.Start(model);
            captureSession.Start();
            SetLiveTranscribing(true);
            SetLiveListeningActivity(ResolveTranscriptionEngineLabel(model));
            await Dispatcher.Yield(DispatcherPriority.Background);
            return true;
        }
        catch (Exception ex)
        {
            DetachLiveRecordingCaptureSession(captureSession);
            DetachLiveSegmentTranscriptionSession(segmentTranscriptionSession);
            recordingSession.Faulted -= OnLiveRecordingFaulted;
            _liveRecordingCaptureSession = null;
            _liveSegmentTranscriptionSession = null;
            _liveRecordingSession = null;
            IsLiveTranscribing = false;
            vm.SetGenerationRunning(isRunning: false);
            await recordingSession.InterruptAsync(ex.Message);
            await DisposeLiveSegmentTranscriptionSessionAsync(segmentTranscriptionSession);
            await DisposeLiveRecordingCaptureSessionAsync(captureSession);
            vm.LogHandledException("live transcription start", ex);
            SetLiveFailureActivity(ex.Message);
            ShowCopyToast(
                "Live transcription failed",
                ex.Message,
                ToastNotificationType.Error);
            return false;
        }
    }

    private static IReadOnlyList<AudioInputDeviceOption> BuildLiveAudioSourceOptions()
    {
        var options = new List<AudioInputDeviceOption>();
        IReadOnlyList<AudioInputDeviceOption> microphones = MicrophoneAudioCaptureService.GetInputDevices();
        AudioInputDeviceOption? defaultMicrophone = microphones.FirstOrDefault();

        if (defaultMicrophone is not null)
        {
            options.Add(new AudioInputDeviceOption(
                LiveAudioSourceKind.Microphone,
                defaultMicrophone.DeviceNumber,
                "default microphone"));
        }

        options.Add(new AudioInputDeviceOption(
            LiveAudioSourceKind.DefaultPlayback,
            -1,
            "default audio playback"));

        if (defaultMicrophone is not null)
        {
            options.Add(new AudioInputDeviceOption(
                LiveAudioSourceKind.MicrophoneAndDefaultPlayback,
                defaultMicrophone.DeviceNumber,
                "default microphone and default audio playback"));
        }

        return options;
    }

    private async void LiveTranscriptionStartStop_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || _livePanelIsOperationPending)
        {
            return;
        }

        if (IsLiveTranscribing)
        {
            _livePanelIsStopping = true;
            _livePanelIsOperationPending = true;
            UpdateLiveControlState();
            try
            {
                await StopLiveTranscriptionAsync(vm, showToast: true);
            }
            finally
            {
                if (_deferLiveRunningStopUntilPendingChunksCleared)
                {
                    _livePanelIsOperationPending = false;
                    _livePanelIsStopping = true;
                }
                else
                {
                    _livePanelIsOperationPending = false;
                    _livePanelIsStopping = false;
                }

                UpdateLiveControlState();
            }

            return;
        }

        await EnsureLiveTranscriptionPanelReadyAsync(vm);
        if (DeviceComboBox.SelectedItem is not AudioInputDeviceOption selectedDevice)
        {
            return;
        }

        _livePanelIsOperationPending = true;
        SetLiveStartingActivity(selectedDevice.Name);
        UpdateLiveControlState();
        try
        {
            _ = await StartLiveTranscriptionAsync(vm, selectedDevice);
        }
        finally
        {
            _livePanelIsOperationPending = false;
            UpdateLiveControlState();
        }
    }

    private void DeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateLiveControlState();
    }

    private void SetLiveAudioLevel(double peakLevel, double gainMultiplier = 1, bool automaticGainApplied = false)
    {
        if (_liveStageProgressMode != LiveStageProgressMode.AudioLevel)
        {
            return;
        }

        double normalized = Math.Max(0, Math.Min(1, peakLevel));
        VolumeMeter.Value = normalized * 100;
        _ = gainMultiplier;
        _ = automaticGainApplied;
    }

    private void SetLiveTranscribing(bool isTranscribing)
    {
        IsLiveTranscribing = isTranscribing;
        if (isTranscribing)
        {
            _liveUiState = LiveUiState.Running;
            _liveStageProgressMode = LiveStageProgressMode.AudioLevel;
            _liveDrainInitialPendingChunks = 0;
            VolumeMeter.IsIndeterminate = false;
            VolumeMeter.Value = 0;
        }
        else
        {
            // Ensure stop/finalize lock is cleared once live mode is no longer transcribing.
            SetLiveTranscriptionStopping(false);
            if (_liveStageProgressMode == LiveStageProgressMode.AudioLevel)
            {
                _liveUiState = LiveUiState.Stopped;
                VolumeMeter.IsIndeterminate = false;
                VolumeMeter.Value = 0;
            }
        }

        UpdateLiveControlState();
    }

    private void SetLiveIdleActivity()
    {
        _liveUiState = LiveUiState.Idle;
        ResetLiveElapsedTime();
        ActivitySummaryText.Text = "Ready. Start live transcription to begin recording and speech processing.";
        RecordingActivityText.Text = "Recorder: idle";
        TranscriptionActivityText.Text = "Transcriber: idle";
        SetLiveChunkCounts(generated: 0, queued: 0, processing: 0, transcribed: 0, failed: 0);
    }

    private void SetLiveSessionReadyToStartActivity()
    {
        _liveUiState = LiveUiState.Idle;
        ResetLiveElapsedTime();
        ActivitySummaryText.Text = "Session ready. Select an audio source, then click Start.";
        RecordingActivityText.Text = "Recorder: session initialized and idle";
        TranscriptionActivityText.Text = "Transcriber: waiting for Start";
        SetLiveChunkCounts(generated: 0, queued: 0, processing: 0, transcribed: 0, failed: 0);
    }

    private void SetLiveStartingActivity(string sourceName)
    {
        _liveUiState = LiveUiState.Preparing;
        ResetLiveElapsedTime();
        ActivitySummaryText.Text = "Preparing live capture, recording, and the transcription pipeline.";
        RecordingActivityText.Text = "Recorder: preparing manifest and output segment path";
        TranscriptionActivityText.Text = "Transcriber: validating engine and opening capture source";
        SetLiveChunkCounts(generated: 0, queued: 0, processing: 0, transcribed: 0, failed: 0);
    }

    private void SetLiveListeningActivity(string modelDisplayName)
    {
        _liveUiState = LiveUiState.Running;
        StartLiveElapsedTime();
        ActivitySummaryText.Text = "Live recording and transcription are active.";
        RecordingActivityText.Text = "Recorder: writing standardized PCM audio into rotating WAV segments";
        TranscriptionActivityText.Text = $"Transcriber: listening for speech with {modelDisplayName}";
    }

    private void SetLiveFinalTranscriptionActivity(string preview)
    {
        if (_liveStageProgressMode != LiveStageProgressMode.AudioLevel)
        {
            return;
        }

        ActivitySummaryText.Text = "A transcript segment was finalized and saved into the session.";
        RecordingActivityText.Text = "Recorder: continuing segmented session recording";
        TranscriptionActivityText.Text = "Transcriber: finalized the latest speech chunk";
    }

    private void SetLiveStoppingActivity()
    {
        StopLiveElapsedTime();
        ActivitySummaryText.Text = "Stopped listening and recording. Completing pending chunk transcription.";
        RecordingActivityText.Text = "Recorder: finalizing the current WAV segment";
        TranscriptionActivityText.Text = "Transcriber: preparing pending chunk drain";
    }

    private void SetLiveDrainPendingChunkProgress(int initialPendingChunks)
    {
        _liveUiState = LiveUiState.StoppingDrain;
        _liveStageProgressMode = LiveStageProgressMode.DrainPendingChunks;
        _liveDrainInitialPendingChunks = Math.Max(0, initialPendingChunks);
        VolumeMeter.IsIndeterminate = false;
        VolumeMeter.Minimum = 0;
        VolumeMeter.Maximum = Math.Max(1, _liveChunksGenerated);
        VolumeMeter.Value = Math.Max(0, Math.Min(_liveChunksGenerated, _liveChunksTranscribed));
        TranscriptionActivityText.Text = _liveDrainInitialPendingChunks == 0
            ? "Transcriber: no pending chunks to transcribe"
            : $"Transcriber: transcribing pending chunks 0/{_liveDrainInitialPendingChunks:N0}";
    }

    private void SetLiveCancelPendingChunkProgress()
    {
        _liveUiState = LiveUiState.StoppingCancel;
        _liveStageProgressMode = LiveStageProgressMode.CancelPendingChunks;
        _liveDrainInitialPendingChunks = 0;
        VolumeMeter.IsIndeterminate = true;
        TranscriptionActivityText.Text = "Transcriber: canceling pending and active chunk transcription";
    }

    private void SetLiveStoppedActivity(bool recordingInterrupted)
    {
        _liveUiState = LiveUiState.Stopped;
        StopLiveElapsedTime();
        if (_liveStageProgressMode == LiveStageProgressMode.DrainPendingChunks)
        {
            VolumeMeter.IsIndeterminate = false;
            VolumeMeter.Minimum = 0;
            VolumeMeter.Maximum = Math.Max(1, _liveChunksGenerated);
            VolumeMeter.Value = Math.Max(0, Math.Min(_liveChunksGenerated, _liveChunksTranscribed));
        }

        ActivitySummaryText.Text = recordingInterrupted
            ? "Live transcription stopped, but the recording was interrupted."
            : "Live transcription stopped. The captured transcript and audio remain in the session.";
        RecordingActivityText.Text = recordingInterrupted
            ? "Recorder: interrupted before clean completion"
            : "Recorder: completed and saved session audio";
        TranscriptionActivityText.Text = "Transcriber: stopped";
    }

    private void SetLiveRecordingSavedForClose()
    {
        UpdateLiveControlState();
    }

    private void SetLiveRecordingInterruptedActivity(string reason)
    {
        ActivitySummaryText.Text = "Recording was interrupted, but live transcription is still running.";
        RecordingActivityText.Text = $"Recorder: interrupted ({reason})";
        TranscriptionActivityText.Text = "Transcriber: continuing without full session audio coverage";
    }

    private void SetLiveFailureActivity(string detail)
    {
        _liveUiState = LiveUiState.Failed;
        StopLiveElapsedTime();
        ActivitySummaryText.Text = "Live transcription encountered a failure.";
        RecordingActivityText.Text = "Recorder: stopped";
        TranscriptionActivityText.Text = "Transcriber: stopped";
    }

    private void StartLiveElapsedTime()
    {
        _liveElapsedStartedUtc = DateTimeOffset.UtcNow;
        UpdateLiveElapsedText();
        _liveElapsedTimer.Start();
    }

    private void StopLiveElapsedTime()
    {
        _liveElapsedTimer.Stop();
    }

    private void ResetLiveElapsedTime()
    {
        StopLiveElapsedTime();
        _liveElapsedStartedUtc = null;
        LiveElapsedText.Text = "00:00:00";
    }

    private void OnLiveElapsedTimerTick(object? sender, EventArgs e)
    {
        UpdateLiveElapsedText();
    }

    private void UpdateLiveElapsedText()
    {
        if (_liveElapsedStartedUtc is not DateTimeOffset startedUtc)
        {
            LiveElapsedText.Text = "00:00:00";
            return;
        }

        TimeSpan elapsed = DateTimeOffset.UtcNow - startedUtc;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        int totalHours = (int)elapsed.TotalHours;
        LiveElapsedText.Text = $"{totalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private void SetLiveChunkCounts(int generated, int queued, int processing, int transcribed, int failed)
    {
        int safeGenerated = Math.Max(0, generated);
        int safeTranscribed = Math.Max(0, transcribed);
        int pending = Math.Max(0, queued) + Math.Max(0, processing);
        string text =
            $"Chunks: generated {safeGenerated:N0} | in queue {Math.Max(0, queued):N0} | " +
            $"processing {Math.Max(0, processing):N0} | transcribed {safeTranscribed:N0} | " +
            $"pending {pending:N0}";
        if (failed > 0)
        {
            text += $" | failed {failed:N0}";
        }

        ChunkActivityText.Text = text;

        if (_liveStageProgressMode == LiveStageProgressMode.DrainPendingChunks)
        {
            VolumeMeter.IsIndeterminate = false;
            VolumeMeter.Minimum = 0;
            VolumeMeter.Maximum = Math.Max(1, safeGenerated);
            VolumeMeter.Value = Math.Max(0, Math.Min(safeGenerated, safeTranscribed));
            if (_liveDrainInitialPendingChunks > 0)
            {
                int completed = Math.Max(0, Math.Min(_liveDrainInitialPendingChunks, _liveDrainInitialPendingChunks - pending));
                TranscriptionActivityText.Text =
                    $"Transcriber: transcribing pending chunks {completed:N0}/{_liveDrainInitialPendingChunks:N0}";
            }
        }

        TryCompleteLiveStopIfDrained();
    }

    private void TryCompleteLiveStopIfDrained()
    {
        if (!_livePanelIsStopping && !_deferLiveRunningStopUntilPendingChunksCleared)
        {
            return;
        }

        if (GetLivePendingChunkCount() > 0)
        {
            return;
        }

        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        _deferLiveRunningStopUntilPendingChunksCleared = false;
        _livePanelIsStopping = false;
        _livePanelIsOperationPending = false;
        vm.SetGenerationRunning(isRunning: false);
        SetLiveTranscribing(false);
        UpdateLiveControlState();
    }

    private void UpdateLiveControlState()
    {
        DeviceComboBox.IsEnabled = !IsLiveTranscribing && !_livePanelIsOperationPending;
        StartStopButton.IsEnabled = !_livePanelIsOperationPending && !_livePanelIsStopping &&
                                    (DeviceComboBox.SelectedItem is AudioInputDeviceOption || IsLiveTranscribing);
        LiveUiState effectiveState = ResolveLiveUiState(
            isLiveTranscribing: IsLiveTranscribing,
            isPanelOperationPending: _livePanelIsOperationPending,
            isPanelStopping: _livePanelIsStopping,
            isCancelPendingMode: _liveStageProgressMode == LiveStageProgressMode.CancelPendingChunks,
            selectedDeviceAvailable: DeviceComboBox.SelectedItem is AudioInputDeviceOption,
            lastKnownState: _liveUiState);
        _liveUiState = effectiveState;
        StartStopButton.Content = effectiveState switch
        {
            LiveUiState.Preparing => "Starting...",
            LiveUiState.Running => "Stop",
            LiveUiState.StoppingDrain or LiveUiState.StoppingCancel => "Finalizing... please wait",
            _ => "Start",
        };
        bool isStopState = effectiveState == LiveUiState.Running;
        StartStopButton.Tag = isStopState ? "\uE711" : "\uE768";
        if (isStopState)
        {
            StartStopButton.Background = System.Windows.Media.Brushes.Red;
            StartStopButton.Foreground = System.Windows.Media.Brushes.White;
        }
        else
        {
            StartStopButton.ClearValue(BackgroundProperty);
            StartStopButton.ClearValue(ForegroundProperty);
        }
    }

    internal static LiveUiState ResolveLiveUiState(
        bool isLiveTranscribing,
        bool isPanelOperationPending,
        bool isPanelStopping,
        bool isCancelPendingMode,
        bool selectedDeviceAvailable,
        LiveUiState lastKnownState)
    {
        if (isPanelStopping)
        {
            return isCancelPendingMode
                ? LiveUiState.StoppingCancel
                : LiveUiState.StoppingDrain;
        }

        if (isLiveTranscribing)
        {
            return LiveUiState.Running;
        }

        if (isPanelOperationPending)
        {
            return LiveUiState.Preparing;
        }

        if (!selectedDeviceAvailable)
        {
            return lastKnownState == LiveUiState.Failed
                ? LiveUiState.Failed
                : LiveUiState.Idle;
        }

        return lastKnownState == LiveUiState.Failed
            ? LiveUiState.Failed
            : LiveUiState.Stopped;
    }

    private async Task StopLiveTranscriptionAsync(
        MainViewModel vm,
        bool showToast,
        bool cancelPendingTranscriptions = false,
        bool pruneAudioToLastFullyTranscribedSegment = false)
    {
        LiveRecordingCaptureSession? captureSession = _liveRecordingCaptureSession;
        LiveSegmentTranscriptionSession? segmentTranscriptionSession = _liveSegmentTranscriptionSession;
        LiveRecordingSession? recordingSession = _liveRecordingSession;
        if (captureSession is null)
        {
            IsLiveTranscribing = false;
            if (GetLivePendingChunkCount() == 0)
            {
                _deferLiveRunningStopUntilPendingChunksCleared = false;
                vm.SetGenerationRunning(isRunning: false);
                SetLiveTranscribing(false);
            }
            else
            {
                _deferLiveRunningStopUntilPendingChunksCleared = true;
                vm.SetGenerationRunning(isRunning: true, isLiveTranscriptionRunning: true);
            }
            return;
        }

        try
        {
            SetLiveTranscriptionStopping(true);
            bool shouldCancelPendingTranscriptions = cancelPendingTranscriptions || _forceCancelLiveChunkTranscriptions;
            int initialPendingChunks = GetLivePendingChunkCount();
            (int generated, int queued, int processing, int transcribed, int failed) = GetLiveChunkSnapshot();
            SetLiveStoppingActivity();
            if (shouldCancelPendingTranscriptions)
            {
                SetLiveCancelPendingChunkProgress();
            }
            else
            {
                SetLiveDrainPendingChunkProgress(initialPendingChunks);
            }
            SetLiveChunkCounts(generated, queued, processing, transcribed, failed);
            await captureSession.StopAsync();
            if (recordingSession is not null)
            {
                if (recordingSession.IsFaulted)
                {
                    await recordingSession.InterruptAsync("Live recording was interrupted.");
                }
                else
                {
                    await recordingSession.CompleteAsync();
                }

                vm.RefreshLiveRecordingMetadata();
                if (pruneAudioToLastFullyTranscribedSegment)
                {
                    vm.PruneCurrentLiveRecordingAudioAfterTime(_lastFullyTranscribedLiveSegmentEndSeconds);
                    vm.RefreshLiveRecordingMetadata();
                }
                vm.LoadCurrentSessionAudioPreview();
                SetLiveRecordingSavedForClose();
            }

            if (segmentTranscriptionSession is not null)
            {
                if (shouldCancelPendingTranscriptions)
                {
                    await segmentTranscriptionSession.CancelAsync();
                    ClearPendingLiveChunkCounts();
                }
                else
                {
                    await segmentTranscriptionSession.StopAsync();
                }
            }

            DrainPendingLiveSegmentTranscriptionResults();
            TryCompleteLiveStopIfDrained();
            vm.ClearLiveInterimTranscriptionBlock();
            vm.SaveLiveTranscriptSession();
            LogLiveTranscription("Live transcription stopped.");
            SetLiveStoppedActivity(recordingSession?.IsFaulted == true);

            if (showToast)
            {
                ShowCopyToast(
                    "Live transcription stopped",
                    "Captured transcript rows were kept.",
                    ToastNotificationType.Info);
            }
        }
        catch (Exception ex)
        {
            vm.LogHandledException("live transcription stop", ex);
            if (recordingSession is not null)
            {
                try
                {
                    await recordingSession.InterruptAsync(ex.Message);
                    vm.RefreshLiveRecordingMetadata();
                }
                catch (Exception recordingEx)
                {
                    vm.LogHandledException("live recording stop", recordingEx);
                }
            }

            SetLiveFailureActivity(ex.Message);
            ShowCopyToast(
                "Live transcription stop failed",
                ex.Message,
                ToastNotificationType.Error);
        }
        finally
        {
            SetLiveTranscriptionStopping(false);
            _forceCancelLiveChunkTranscriptions = false;
            DetachLiveRecordingCaptureSession(captureSession);
            if (segmentTranscriptionSession is not null)
            {
                DetachLiveSegmentTranscriptionSession(segmentTranscriptionSession);
            }
            if (recordingSession is not null)
            {
                recordingSession.Faulted -= OnLiveRecordingFaulted;
            }

            _liveRecordingCaptureSession = null;
            _liveSegmentTranscriptionSession = null;
            _liveRecordingSession = null;
            IsLiveTranscribing = false;
            if (GetLivePendingChunkCount() == 0)
            {
                _deferLiveRunningStopUntilPendingChunksCleared = false;
                vm.SetGenerationRunning(isRunning: false);
                SetLiveTranscribing(false);
            }
            else
            {
                _deferLiveRunningStopUntilPendingChunksCleared = true;
                vm.SetGenerationRunning(isRunning: true, isLiveTranscriptionRunning: true);
            }
            if (segmentTranscriptionSession is not null)
            {
                await DisposeLiveSegmentTranscriptionSessionAsync(segmentTranscriptionSession);
            }
            await DisposeLiveRecordingCaptureSessionAsync(captureSession);
            TryCompleteLiveStopIfDrained();
            _lastFullyTranscribedLiveSegmentEndSeconds = 0;
        }
    }


    private void ResetLiveChunkCounts()
    {
        lock (_liveSegmentTranscriptionSync)
        {
            _liveChunksGenerated = 0;
            _liveChunksQueued = 0;
            _liveChunksProcessing = 0;
            _liveChunksTranscribed = 0;
            _liveChunksFailed = 0;
        }

        ScheduleLiveChunkCountUpdate();
    }

    private void ClearPendingLiveChunkCounts()
    {
        lock (_liveSegmentTranscriptionSync)
        {
            _liveChunksQueued = 0;
            _liveChunksProcessing = 0;
        }

        ScheduleLiveChunkCountUpdate();
    }

    private void MarkLiveChunkTranscribed()
    {
        lock (_liveSegmentTranscriptionSync)
        {
            if (_liveChunksProcessing > 0)
            {
                _liveChunksProcessing--;
            }

            _liveChunksTranscribed++;
        }

        ScheduleLiveChunkCountUpdate();
    }

    private bool HasPendingLiveChunkTranscriptions()
    {
        lock (_liveSegmentTranscriptionSync)
        {
            return _liveChunksQueued > 0 || _liveChunksProcessing > 0;
        }
    }

    private int GetLivePendingChunkCount()
    {
        lock (_liveSegmentTranscriptionSync)
        {
            return Math.Max(0, _liveChunksQueued) + Math.Max(0, _liveChunksProcessing);
        }
    }

    private (int Generated, int Queued, int Processing, int Transcribed, int Failed) GetLiveChunkSnapshot()
    {
        lock (_liveSegmentTranscriptionSync)
        {
            return (
                Generated: _liveChunksGenerated,
                Queued: _liveChunksQueued,
                Processing: _liveChunksProcessing,
                Transcribed: _liveChunksTranscribed,
                Failed: _liveChunksFailed);
        }
    }

    private void ScheduleLiveChunkCountUpdate()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(UpdateLiveChunkCountDisplay), DispatcherPriority.Background);
    }

    private void UpdateLiveChunkCountDisplay()
    {
        int generated;
        int queued;
        int processing;
        int transcribed;
        int failed;
        lock (_liveSegmentTranscriptionSync)
        {
            generated = _liveChunksGenerated;
            queued = _liveChunksQueued;
            processing = _liveChunksProcessing;
            transcribed = _liveChunksTranscribed;
            failed = _liveChunksFailed;
        }

        SetLiveChunkCounts(
            generated,
            queued,
            processing,
            transcribed,
            failed);
    }

    private void OnLiveAudioLevelChanged(object? sender, PlaybackAudioLevelChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(
            new Action(() => SetLiveAudioLevel(
                e.PeakLevel,
                e.GainMultiplier,
                e.AutomaticGainApplied)),
            DispatcherPriority.Background);
    }

    private void OnLiveSegmentTranscriptionQueued(object? sender, LiveSegmentTranscriptionQueuedEventArgs e)
    {
        lock (_liveSegmentTranscriptionSync)
        {
            _liveChunksGenerated++;
            _liveChunksQueued++;
        }

        ScheduleLiveChunkCountUpdate();
    }

    private void OnLiveSegmentTranscriptionStarted(object? sender, LiveSegmentTranscriptionStartedEventArgs e)
    {
        lock (_liveSegmentTranscriptionSync)
        {
            if (_liveChunksQueued > 0)
            {
                _liveChunksQueued--;
            }

            _liveChunksProcessing++;
        }

        ScheduleLiveChunkCountUpdate();
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                TimeSpan start = TimeSpan.FromSeconds(e.Segment.StartSeconds);
                LogLiveTranscription(
                    $"Transcribing live recording segment starting at {start:mm\\:ss}.");
                if (!_isLiveTranscriptionStopping)
                {
                    ActivitySummaryText.Text = "A recorded segment is being transcribed.";
                    RecordingActivityText.Text = "Recorder: appending audio frames to the active segment";
                    TranscriptionActivityText.Text = "Transcriber: processing recorded segment";
                }
            }),
            DispatcherPriority.Background);
    }

    private void OnLiveSegmentTranscriptionCompleted(object? sender, LiveSegmentTranscriptionCompletedEventArgs e)
    {
        lock (_liveSegmentTranscriptionSync)
        {
            _pendingLiveSegmentTranscriptionCompletions.Enqueue(e);
        }

        ScheduleLiveSegmentTranscriptionDrain();
    }

    private void ScheduleLiveSegmentTranscriptionDrain()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            DrainPendingLiveSegmentTranscriptionResults();
            return;
        }

        lock (_liveSegmentTranscriptionSync)
        {
            if (_isLiveSegmentTranscriptionDrainScheduled)
            {
                return;
            }

            _isLiveSegmentTranscriptionDrainScheduled = true;
        }

        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                lock (_liveSegmentTranscriptionSync)
                {
                    _isLiveSegmentTranscriptionDrainScheduled = false;
                }

                DrainPendingLiveSegmentTranscriptionResults();
            }),
            DispatcherPriority.Background);
    }

    private void DrainPendingLiveSegmentTranscriptionResults()
    {
        if (!Dispatcher.CheckAccess())
        {
            ScheduleLiveSegmentTranscriptionDrain();
            return;
        }

        while (true)
        {
            LiveSegmentTranscriptionCompletedEventArgs? item;
            lock (_liveSegmentTranscriptionSync)
            {
                item = _pendingLiveSegmentTranscriptionCompletions.Count == 0
                    ? null
                    : _pendingLiveSegmentTranscriptionCompletions.Dequeue();
            }

            if (item is null)
            {
                TryCompleteLiveStopIfDrained();
                return;
            }

            ApplyLiveSegmentTranscriptionResult(item);
        }
    }

    private void OnLiveSegmentTranscriptionFailed(object? sender, LiveSegmentTranscriptionFailedEventArgs e)
    {
        lock (_liveSegmentTranscriptionSync)
        {
            if (_liveChunksProcessing > 0)
            {
                _liveChunksProcessing--;
            }

            _liveChunksFailed++;
        }

        ScheduleLiveChunkCountUpdate();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.LogHandledException("live segment transcription", e.Exception);
                ShowCopyToast(
                    "Live segment transcription failed",
                    "Recording is still running. The failed segment can be transcribed again later.",
                    ToastNotificationType.Warning);
            }
        }), DispatcherPriority.Background);
    }

    private void ApplyLiveSegmentTranscriptionResult(LiveSegmentTranscriptionCompletedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        _lastFullyTranscribedLiveSegmentEndSeconds = Math.Max(
            _lastFullyTranscribedLiveSegmentEndSeconds,
            Math.Max(0, e.Segment.StartSeconds + e.Segment.DurationSeconds));

        MarkLiveChunkTranscribed();
        int rowCount = vm.AppendLiveTranscriptionResult(e.Result);

        vm.SaveLiveTranscriptSession();
        string preview = BuildLogPreview(e.Result.Text);
        TimeSpan start = TimeSpan.FromSeconds(e.Segment.StartSeconds);
        LogLiveTranscription(
            $"Applied live recording segment at {start:mm\\:ss} " +
            $"with {rowCount:N0} row(s), preview='{preview}'.");
        if (rowCount > 0)
        {
            if (!_isLiveTranscriptionStopping)
            {
                SetLiveFinalTranscriptionActivity(preview);
            }
        }
    }

    private void OnLiveTranscriptionFaulted(object? sender, Exception ex)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.LogHandledException("live transcription", ex);
                SetLiveFailureActivity(ex.Message);
                ShowCopyToast(
                    "Live transcription failed",
                    ex.Message,
                    ToastNotificationType.Error);
                await StopLiveTranscriptionAsync(vm, showToast: false);
            }
        }), DispatcherPriority.Background);
    }

    private void OnLiveRecordingFaulted(object? sender, Exception ex)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.LogHandledException("live recording", ex);
                SetLiveRecordingInterruptedActivity(ex.Message);
                ShowCopyToast(
                    "Live recording interrupted",
                    "Live transcription is still running, but the audio recording is incomplete.",
                    ToastNotificationType.Warning);
            }
        }), DispatcherPriority.Background);
    }

    private void DetachLiveRecordingCaptureSession(LiveRecordingCaptureSession session)
    {
        session.AudioLevelChanged -= OnLiveAudioLevelChanged;
        session.Faulted -= OnLiveTranscriptionFaulted;
    }

    private void DetachLiveSegmentTranscriptionSession(LiveSegmentTranscriptionSession session)
    {
        session.SegmentTranscriptionQueued -= OnLiveSegmentTranscriptionQueued;
        session.SegmentTranscriptionStarted -= OnLiveSegmentTranscriptionStarted;
        session.SegmentTranscriptionCompleted -= OnLiveSegmentTranscriptionCompleted;
        session.SegmentTranscriptionFailed -= OnLiveSegmentTranscriptionFailed;
    }

    private void CancelProcessing_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if (IsLiveTranscribing)
        {
            _ = StopLiveTranscriptionAsync(vm, showToast: true);
            return;
        }

        if (IsTranscribeAudioBatchTranscribing)
        {
            if (_activeTranscriptProcessingWorkflow == TranscriptProcessingWorkflowKind.DetectSpeakers)
            {
                CancelDetectSpeakers(vm);
            }
            else
            {
                CancelTranscribeAudioBatch(vm);
            }
            return;
        }

        if (IsTranscribeAudioBatchPendingStart)
        {
            if (_activeTranscriptProcessingWorkflow == TranscriptProcessingWorkflowKind.TranscribeAudio)
            {
                vm.ClosePendingTranscribeAudioWorkflow();
            }

            RestoreTranscribeAudioBatchInteractionLock();
        }
    }

    private async void StartProcessing_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !IsTranscribeAudioBatchPendingStart || IsTranscribeAudioBatchTranscribing)
        {
            return;
        }

        if (_activeTranscriptProcessingWorkflow == TranscriptProcessingWorkflowKind.DetectSpeakers)
        {
            await RunDetectSpeakersAsync(vm);
            return;
        }

        await RunTranscribeAudioAsync(vm);
    }

    private async void TranscribeAudioBatchStartStop_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if (IsTranscribeAudioBatchTranscribing)
        {
            if (_activeTranscriptProcessingWorkflow == TranscriptProcessingWorkflowKind.DetectSpeakers)
            {
                CancelDetectSpeakers(vm);
            }
            else
            {
                CancelTranscribeAudioBatch(vm);
            }

            return;
        }

        if (!IsTranscribeAudioBatchPendingStart)
        {
            if (!CanPrimeTranscribeAudioFromCurrentSession)
            {
                return;
            }

            OpenTranscribeAudioBatchDialog(vm);
            if (!IsTranscribeAudioBatchPendingStart)
            {
                return;
            }
        }

        if (_activeTranscriptProcessingWorkflow == TranscriptProcessingWorkflowKind.DetectSpeakers)
        {
            await RunDetectSpeakersAsync(vm);
            return;
        }

        await RunTranscribeAudioAsync(vm);
    }

    private async void TranscribeAudioRestart_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm
            || IsTranscribeAudioBatchTranscribing
            || IsTranscriptProcessingCanceling
            || !vm.IsCurrentSessionAudioTranscriptionSession)
        {
            return;
        }

        OpenTranscribeAudioBatchDialog(vm, forceRestart: true);
        if (!IsTranscribeAudioBatchPendingStart)
        {
            return;
        }

        await RunTranscribeAudioAsync(vm);
    }

    private void CopyFinalizedToClipboard_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        try
        {
            string plainText = vm.BuildClipboardTranscriptText();

            System.Windows.Clipboard.SetText(plainText);
            ShowCopyToast(
                "Copied to clipboard",
                "Transcript is ready to paste.",
                ToastNotificationType.Success);
        }
        catch (Exception ex)
        {
            vm.LogHandledException("copy finalized transcript", ex);
            var dialog = new ErrorDialogWindow($"Unable to copy transcript to clipboard: {ex.Message}")
            {
                Owner = this,
            };
            dialog.ShowDialog();
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || _whisperModelManager is null)
        {
            return;
        }

        OpenSettingsDialog(vm, null);
    }

    private bool EnsureSelectedEngineReady(MainViewModel vm, string operationName)
    {
        string selectedEngineId = vm.SelectedEngine?.Id ?? string.Empty;
        if (!TranscriptionModelCatalog.SupportsFileTranscription(selectedEngineId)
            && !TranscriptionModelCatalog.SupportsPlaybackTranscription(selectedEngineId))
        {
            ShowBlockingMessage(
                "Transcription engine required",
                $"{operationName} was stopped because no supported transcription engine is selected.");
            return false;
        }

        if (_whisperModelManager is not null
            && TranscriptionModelCatalog.IsLocalWhisper(selectedEngineId)
            && !_whisperModelManager.IsModelInstalled(selectedEngineId))
        {
            ShowBlockingMessage(
                "Transcription model required",
                $"{operationName} was stopped because {ResolveCurrentEngineLabel(vm)} is not installed yet. Install it in Settings and try again.");
            OpenSettingsDialog(vm, selectedEngineId);
            return false;
        }

        return true;
    }

    private bool OpenSettingsDialog(MainViewModel vm, string? preferredModelId)
    {
        if (_whisperModelManager is null)
        {
            return false;
        }

        var dialog = new SettingsWindow(
            vm,
            _whisperModelManager,
            _processLogService ?? new ProcessLogService())
        {
            Owner = this,
        };

        dialog.ShowDialog();
        vm.RefreshEngines(
            _whisperModelManager.GetSelectableTranscriptionModels(),
            dialog.LastInstalledModelId ?? preferredModelId);
        if (dialog.HasModelChanges)
        {
            ShowCopyToast(
                "Engine models updated",
                "Installed engine choices were refreshed.",
                ToastNotificationType.Success);
        }

        return dialog.HasModelChanges;
    }

    private void ShowBlockingMessage(string title, string message)
    {
        System.Windows.MessageBox.Show(
            this,
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private async Task<bool> PromptPremiumFeatureAsync(string featureName, string message)
    {
        if (DataContext is not MainViewModel vm)
        {
            return false;
        }

        if (vm.IsPremiumEntitlementChecking || vm.IsPremiumEntitlementVerificationFailed)
        {
            await vm.RefreshPremiumEntitlementAsync();
            if (vm.HasPremium)
            {
                return true;
            }
        }

        if (vm.IsPremiumEntitlementChecking)
        {
            ShowCopyToast(
                "Checking license",
                "AudioScript is still verifying your Microsoft Store entitlement. Try again in a moment.",
                ToastNotificationType.Info);
            return false;
        }

        if (vm.IsPremiumEntitlementVerificationFailed)
        {
            ShowErrorDialog(
                "AudioScript could not verify your Microsoft Store entitlement right now. Please ensure Microsoft Store is signed in and try Re-check Premium.",
                title: $"{featureName} unavailable");
            return false;
        }

        if (!vm.CanPromptPremiumPurchase)
        {
            ShowCopyToast(
                "Premium status updating",
                "AudioScript is updating premium status. Please try again.",
                ToastNotificationType.Info);
            return false;
        }

        var dialog = new ConfirmationDialogWindow(
            "Premium feature",
            message,
            "Get Premium",
            "Not now")
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true)
        {
            return false;
        }
        Window initiatingWindow = GetInitiatingWindow();
        PremiumPurchaseResult result;
        IntPtr initiatingWindowHandle = IntPtr.Zero;
        try
        {
            initiatingWindowHandle = new System.Windows.Interop.WindowInteropHelper(initiatingWindow).Handle;
        }
        catch (Exception ex)
        {
            _boundViewModel?.LogHandledException("premium owner handle resolve", ex);
        }

        try
        {
            using IDisposable scope = StorePurchaseOwnerWindowBinding.BeginScope(initiatingWindowHandle);
            result = await vm.RequestPremiumPurchaseAsync();
        }
        finally
        {
            RecoverWindowAccessibility(initiatingWindow);
        }

        switch (result.Status)
        {
            case PremiumPurchaseStatus.Succeeded:
            case PremiumPurchaseStatus.AlreadyOwned:
                ShowCopyToast(
                    "Premium unlocked",
                    result.Message,
                    ToastNotificationType.Success);
                return true;

            case PremiumPurchaseStatus.Canceled:
                ShowCopyToast(
                    "Purchase canceled",
                    result.Message,
                    ToastNotificationType.Info);
                return false;

            default:
                ShowErrorDialog(result.Message, title: $"{featureName} unavailable");
                return false;
        }
    }

    private Window GetInitiatingWindow()
    {
        return System.Windows.Application.Current.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive)
            ?? this;
    }

    private void RecoverWindowAccessibility(Window? initiatingWindow)
    {
        try
        {
            if (initiatingWindow is not null)
            {
                initiatingWindow.IsEnabled = true;
                initiatingWindow.Activate();
                _ = initiatingWindow.Focus();
            }
        }
        catch (Exception ex)
        {
            _boundViewModel?.LogHandledException("premium initiating window recovery", ex);
        }

        try
        {
            if (!IsLoaded)
            {
                return;
            }

            IsEnabled = true;
            Activate();
            _ = Focus();
        }
        catch (Exception ex)
        {
            _boundViewModel?.LogHandledException("premium main window recovery", ex);
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundViewModel is not null)
        {
            StopActivePlaybackEditTranscription(
                _boundViewModel,
                pausePlayback: false,
                reason: "view model changed",
                discardResults: true);
            _boundViewModel.ErrorOccurred -= OnErrorOccurred;
            _boundViewModel.ConfirmationRequested -= OnConfirmationRequested;
            _boundViewModel.ToastRequested -= OnToastRequested;
            _boundViewModel.NewAudioFileStagedForTranscribeAudio -= OnNewAudioFileStagedForTranscribeAudio;
            _boundViewModel.PremiumUpsellRequested -= OnPremiumUpsellRequested;
            _boundViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _boundViewModel.FinalizedTranscriptLines.CollectionChanged -= OnFinalizedTranscriptLinesCollectionChanged;
            _boundViewModel = null;
        }

        if (e.NewValue is MainViewModel vm)
        {
            _boundViewModel = vm;
            _boundViewModel.ErrorOccurred += OnErrorOccurred;
            _boundViewModel.ConfirmationRequested += OnConfirmationRequested;
            _boundViewModel.ToastRequested += OnToastRequested;
            _boundViewModel.NewAudioFileStagedForTranscribeAudio += OnNewAudioFileStagedForTranscribeAudio;
            _boundViewModel.PremiumUpsellRequested += OnPremiumUpsellRequested;
            _boundViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _boundViewModel.FinalizedTranscriptLines.CollectionChanged += OnFinalizedTranscriptLinesCollectionChanged;
            ApplyApplicationUpdateTaskbarProgress(vm);
            UpdateTranscriptGridPresentation();
            UpdatePlaybackTimelineHighlight();
            UpdateTranscriptRowActionsVisibility();
            SetLiveIdleActivity();
            UpdateLiveControlState();
            UpdateLivePrimaryActionButtonState();
            SyncTranscriptProcessingPanelFromSession(vm);
            OnPropertyChanged(nameof(ShouldShowMediaPlayerPanel));
            OnPropertyChanged(nameof(ShouldShowAudioTranscriptionPanel));
            OnPropertyChanged(nameof(CanPrimeTranscribeAudioFromCurrentSession));
            OnPropertyChanged(nameof(IsTranscribeAudioRestartVisible));
        }
        else
        {
            StopActivePlaybackEditTranscription(
                _boundViewModel,
                pausePlayback: false,
                reason: "view model cleared",
                discardResults: true);
            ClearTranscriptEditPlaybackLoop();
            SetPlaybackTimelineMatch(null);
            SetTranscriptRowActionsLine(null);
            UpdateLivePrimaryActionButtonState();
            OnPropertyChanged(nameof(ShouldShowMediaPlayerPanel));
            OnPropertyChanged(nameof(ShouldShowAudioTranscriptionPanel));
            OnPropertyChanged(nameof(CanPrimeTranscribeAudioFromCurrentSession));
            OnPropertyChanged(nameof(IsTranscribeAudioRestartVisible));
        }
    }

    private void UpdateAudioFileDropState(System.Windows.DragEventArgs e)
    {
        string? filePath = GetDroppedAudioFilePath(e);
        bool canAccept = !string.IsNullOrWhiteSpace(filePath);

        e.Effects = canAccept ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;

        SetDropTargetVisualState(TranscriptEmptyStateBorder);
    }

    private void ResetAudioFileDropState()
    {
        SetDropTargetVisualState(TranscriptEmptyStateBorder);
    }

    private static void SetDropTargetVisualState(Border border)
    {
        if (border is null)
        {
            return;
        }

        border.ClearValue(Border.BackgroundProperty);
        border.ClearValue(Border.BorderBrushProperty);
    }

    private static string? GetDroppedAudioFilePath(System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            return null;
        }

        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return null;
        }

        return files.FirstOrDefault(MainViewModel.IsSupportedAudioFilePath);
    }

    private void OnErrorOccurred(object? sender, string message)
    {
        var dialog = new ErrorDialogWindow(message)
        {
            Owner = this,
        };
        dialog.ShowDialog();
    }

    private void OnConfirmationRequested(object? sender, ConfirmationRequest request)
    {
        if (request is null)
        {
            return;
        }

        var dialog = new ConfirmationDialogWindow(
            request.Title,
            request.Message,
            request.ConfirmButtonText,
            request.CancelButtonText)
        {
            Owner = this,
        };

        request.IsConfirmed = dialog.ShowDialog() == true;
    }

    private async void OnPremiumUpsellRequested(object? sender, PremiumUpsellRequest request)
    {
        if (request is null)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(async () => await PromptPremiumFeatureAsync(request.FeatureName, request.Message));
            return;
        }

        await PromptPremiumFeatureAsync(request.FeatureName, request.Message);
    }

    private async void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        bool resumingAfterLiveTranscriptionStop = _isClosingAfterLiveTranscriptionStop;
        bool hasActiveTranscription =
            _liveRecordingCaptureSession is not null
            || IsLiveTranscribing
            || _isLiveTranscriptionStopping
            || IsTranscribeAudioBatchTranscribing
            || IsTranscribeAudioBatchPendingStart
            || _isRowFileTranscriptionRunning;

        if (!resumingAfterLiveTranscriptionStop
            && hasActiveTranscription
            && !_hasConfirmedCloseWithActiveTranscription)
        {
            var confirmDialog = new ConfirmationDialogWindow(
                "Close AudioScript?",
                "Live/audio transcription is still active. Closing now will stop the active process. Continue?",
                "Close App",
                "Cancel")
            {
                Owner = this,
            };

            if (confirmDialog.ShowDialog() != true)
            {
                e.Cancel = true;
                return;
            }

            _hasConfirmedCloseWithActiveTranscription = true;
        }

        if (!resumingAfterLiveTranscriptionStop
            && _liveRecordingCaptureSession is not null
            && _boundViewModel is not null)
        {
            e.Cancel = true;
            IsEnabled = false;
            try
            {
                await StopLiveTranscriptionAsync(
                    _boundViewModel,
                    showToast: false,
                    cancelPendingTranscriptions: true,
                    pruneAudioToLastFullyTranscribedSegment: true);
            }
            finally
            {
                _isClosingAfterLiveTranscriptionStop = true;
                Close();
            }
            return;
        }

        _hasConfirmedCloseWithActiveTranscription = false;
        _ = DataContext as MainViewModel;
    }

    private async Task RunDeferredUpdateInstallAsync(IAppUpdateService appUpdateService)
    {
        var progressWindow = new DeferredUpdateInstallWindow(appUpdateService)
        {
            Owner = this,
        };

        Task<StoreUpdateOperationResult?> installTask = appUpdateService.RunExitTimeInstallAsync();
        _ = installTask.ContinueWith(
            _ => progressWindow.Dispatcher.BeginInvoke(new Action(progressWindow.CloseAfterOperation)),
            TaskScheduler.Default);

        progressWindow.ShowDialog();
        StoreUpdateOperationResult? result = null;
        Exception? capturedException = null;

        try
        {
            result = await installTask.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            capturedException = ex;
        }

        if (capturedException is not null)
        {
            _processLogService?.LogException(
                nameof(MainWindow),
                "deferred_exit_update_install_failed",
                capturedException);
            ShowDeferredUpdateInstallFailureMessageSafe();
            return;
        }

        if (result is not null && !result.Succeeded)
        {
            ShowDeferredUpdateInstallFailureMessageSafe();
        }
    }

    private void ShowDeferredUpdateInstallFailureMessageSafe()
    {
        try
        {
            System.Windows.MessageBox.Show(
                this,
                "The update could not be installed. The app will close normally. You can check for updates again later.",
                "Update installation failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _processLogService?.LogException(
                nameof(MainWindow),
                "deferred_exit_update_install_message_failed",
                ex);
        }
    }

    private void ExportTranscriptToDocument_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        try
        {
            TranscriptDocumentFormat? selectedFormat = PromptTranscriptDocumentFormat();
            if (selectedFormat is null)
            {
                return;
            }

            string sourceFileName = string.IsNullOrWhiteSpace(vm.LoadedAudioFileName)
                ? "transcript"
                : vm.LoadedAudioFileName;
            string initialFileName = $"{SanitizeFileName(Path.GetFileNameWithoutExtension(sourceFileName))}-transcript.docx";
            string documentsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string persistedDirectory = vm.TranscriptExportDirectory;
            string initialDirectory = Directory.Exists(persistedDirectory)
                ? persistedDirectory
                : documentsDirectory;
            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Word Document|*.docx",
                DefaultExt = ".docx",
                AddExtension = true,
                FileName = initialFileName,
                InitialDirectory = initialDirectory,
                OverwritePrompt = true,
                Title = "Export Transcript to Document",
            };

            bool? saveResult = saveDialog.ShowDialog(this);
            if (saveResult != true || string.IsNullOrWhiteSpace(saveDialog.FileName))
            {
                return;
            }

            vm.SetTranscriptExportDirectory(Path.GetDirectoryName(saveDialog.FileName));
            TranscriptDocumentExporter.ExportDocx(
                saveDialog.FileName,
                vm.FinalizedTranscriptLines,
                new TranscriptDocumentExportMetadata(
                    Title: "Transcript Export",
                    SourceAudioFileName: vm.LoadedAudioFileName,
                    ExportedAt: DateTimeOffset.Now),
                new TranscriptDocumentExportOptions(
                    IncludeTimestamps: selectedFormat == TranscriptDocumentFormat.TabDelimited,
                    IncludeSpeakerLabels: true,
                    Format: selectedFormat.Value));

            ShowCopyToast(
                "Transcript exported",
                $"Saved to {saveDialog.FileName}.",
                ToastNotificationType.Success);

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = saveDialog.FileName,
                    UseShellExecute = true,
                });
            }
            catch (Exception openEx)
            {
                vm.LogHandledException("open exported transcript document", openEx);
                ShowCopyToast(
                    "Document saved",
                    "Transcript was exported, but no app is available to open .docx files.",
                    ToastNotificationType.Warning);
            }
        }
        catch (Exception ex)
        {
            vm.LogHandledException("export transcript document", ex);
            var dialog = new ErrorDialogWindow($"Unable to export transcript document: {ex.Message}")
            {
                Owner = this,
            };
            dialog.ShowDialog();
        }
    }

    private TranscriptDocumentFormat? PromptTranscriptDocumentFormat()
    {
        var dialog = new ExportFormatDialogWindow
        {
            Owner = this,
        };

        bool? result = dialog.ShowDialog();
        if (result != true)
        {
            return null;
        }

        return dialog.SelectedFormat;
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        Closing -= OnMainWindowClosing;
        Loaded -= OnMainWindowLoaded;
        _liveElapsedTimer.Tick -= OnLiveElapsedTimerTick;
        _liveElapsedTimer.Stop();
        CancelCopyToast();
        PreviewMouseDown -= OnWindowMouseDismissToast;
        PreviewMouseWheel -= OnWindowMouseWheelDismissToast;
        StopActivePlaybackEditTranscription(
            _boundViewModel,
            pausePlayback: false,
            reason: "window closed",
            discardResults: true);
        if (_boundViewModel is null)
        {
            return;
        }

        _boundViewModel.ErrorOccurred -= OnErrorOccurred;
        _boundViewModel.ConfirmationRequested -= OnConfirmationRequested;
        _boundViewModel.ToastRequested -= OnToastRequested;
        _boundViewModel.NewAudioFileStagedForTranscribeAudio -= OnNewAudioFileStagedForTranscribeAudio;
        _boundViewModel.PremiumUpsellRequested -= OnPremiumUpsellRequested;
        _boundViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _boundViewModel.FinalizedTranscriptLines.CollectionChanged -= OnFinalizedTranscriptLinesCollectionChanged;
        _boundViewModel = null;
        ClearTranscriptEditPlaybackLoop();
        SetPlaybackTimelineMatch(null);
        SetTranscriptRowActionsLine(null);
    }

    private void OnToastRequested(object? sender, ToastNotification notification)
    {
        if (notification is null)
        {
            return;
        }

        ShowCopyToast(notification.Title, notification.Message, notification.Type);
    }

    private void OnNewAudioFileStagedForTranscribeAudio(object? sender, EventArgs e)
    {
        if (sender is not MainViewModel vm || IsTranscribeAudioBatchTranscribing)
        {
            return;
        }

        if (IsTranscribeAudioBatchPendingStart)
        {
            // Replace any pending workflow with the newly selected file route.
            vm.ClosePendingTranscribeAudioWorkflow();
            RestoreTranscribeAudioBatchInteractionLock();
        }

        OpenTranscribeAudioBatchDialog(vm);
    }

    private static (string Title, string Message) BuildTranscribeAudioFailureToast(MainViewModel vm, Exception ex)
    {
        string fileName = string.IsNullOrWhiteSpace(vm.LoadedAudioFileName)
            ? "the selected audio file"
            : vm.LoadedAudioFileName;
        Exception root = GetRootException(ex);
        string[] typeNames = EnumerateExceptionChain(ex)
            .Select(candidate => candidate.GetType().Name)
            .ToArray();
        string combinedMessages = string.Join(
            " | ",
            EnumerateExceptionChain(ex)
                .Select(candidate => candidate.Message)
                .Where(message => !string.IsNullOrWhiteSpace(message)));

        if (typeNames.Any(name => string.Equals(name, "WhisperProcessingException", StringComparison.Ordinal)))
        {
            return (
                "Transcription engine failed",
                $"The selected engine could not process {fileName}. Try another engine model or a different audio file.");
        }

        if (root is FileNotFoundException
            || combinedMessages.Contains("missing bundled pyannote asset", StringComparison.OrdinalIgnoreCase)
            || combinedMessages.Contains("model", StringComparison.OrdinalIgnoreCase)
            || combinedMessages.Contains("asset", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "Transcription files missing",
                "Required engine files are missing or unavailable. Reinstall the engine in Settings and try again.");
        }

        if (root is IOException)
        {
            return (
                "Audio file unavailable",
                $"The app could not read {fileName}. Check that the file still exists and is not locked by another app.");
        }

        return (
            "Transcribe Audio failed",
            $"The app could not finish transcribing {fileName}. Check the selected engine and try again.");
    }

    private static string BuildTranscribeAudioFailureMessage(MainViewModel vm, Exception ex)
    {
        return BuildTranscribeAudioFailureToast(vm, ex).Message;
    }

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "transcript";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[fileName.Length];
        int index = 0;

        foreach (char c in fileName)
        {
            buffer[index++] = invalid.Contains(c) ? '_' : c;
        }

        string sanitized = new string(buffer).Trim('_', ' ');
        return string.IsNullOrWhiteSpace(sanitized) ? "transcript" : sanitized;
    }

    private static string BuildDetectSpeakerFailureMessage(MainViewModel vm, Exception ex)
    {
        string fileName = string.IsNullOrWhiteSpace(vm.LoadedAudioFileName)
            ? "the selected audio file"
            : vm.LoadedAudioFileName;
        Exception root = GetRootException(ex);
        string combinedMessages = string.Join(
            " | ",
            EnumerateExceptionChain(ex)
                .Select(candidate => candidate.Message)
                .Where(message => !string.IsNullOrWhiteSpace(message)));

        if (combinedMessages.Contains("x64", StringComparison.OrdinalIgnoreCase)
            || ex is PlatformNotSupportedException)
        {
            return "Speaker diarization requires an x64 AudioScript build.";
        }

        if (combinedMessages.Contains("torchcodec", StringComparison.OrdinalIgnoreCase)
            || combinedMessages.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
        {
            return "Speaker detection runtime is installed but audio decoding dependencies are not compatible yet. The app will continue with fallback speaker labeling.";
        }

        if (combinedMessages.Contains("DiarizeOutput", StringComparison.OrdinalIgnoreCase)
            || combinedMessages.Contains("itertracks", StringComparison.OrdinalIgnoreCase))
        {
            return "Speaker detection runtime is installed but the diarization runtime API changed. The app will continue with fallback speaker labeling.";
        }

        if (combinedMessages.Contains("pyannote", StringComparison.OrdinalIgnoreCase)
            || combinedMessages.Contains("model", StringComparison.OrdinalIgnoreCase)
            || combinedMessages.Contains("asset", StringComparison.OrdinalIgnoreCase))
        {
            return "Required speaker detection files are missing or unavailable. Download the speaker detection files and try again.";
        }

        if (root is FileNotFoundException or IOException)
        {
            return $"The app could not read {fileName}. Check that the session audio still exists and is not locked by another app.";
        }

        return $"The app could not finish detecting speakers for {fileName}. Check the session audio and try again.";
    }

    private void ShowTranscribeAudioErrorDialog(string message)
    {
        var dialog = new TranscribeAudioErrorDialogWindow(message)
        {
            Owner = this,
        };
        dialog.ShowDialog();
    }

    private void ShowErrorDialog(string message, string title)
    {
        var dialog = new ErrorDialogWindow(message)
        {
            Title = title,
            Owner = this,
        };
        dialog.ShowDialog();
    }

    private static Exception GetRootException(Exception ex)
    {
        Exception current = ex;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current;
    }

    private async Task<bool> EnsureSpeakerDetectionAssetsReadyAsync(CancellationToken cancellationToken)
    {
        if (_pyannoteCommunityModelManager is null)
        {
            ShowTranscribeAudioErrorDialog("Speaker detection is not configured in this build.");
            return false;
        }

        if (!_pyannoteCommunityModelManager.IsSupportedOnCurrentArchitecture)
        {
            ShowTranscribeAudioErrorDialog("Speaker diarization requires an x64 AudioScript build.");
            return false;
        }

        if (_pyannoteCommunityModelManager.IsInstalled())
        {
            return true;
        }

        TranscriptProcessingEngineText = "Pyannote Community-1";
        TranscriptProcessingChunkText = "Progress 0%";
        TranscriptProcessingAudioText = "Download 0 B / unknown size";
        TranscriptProcessingElapsedText = "Elapsed 00:00";
        TranscriptProcessingEtaText = "ETA calculating";
        IsTranscriptProcessingIndeterminate = true;
        TranscriptProcessingPercent = 0;

        var progress = new Progress<AssetProvisioningProgress>(assetProgress =>
        {
            TranscriptProcessingPercent = assetProgress.Percent;
            IsTranscriptProcessingIndeterminate = assetProgress.TotalBytes is not > 0;
            TranscriptProcessingChunkText = $"Progress {assetProgress.Percent:0}%";
            TranscriptProcessingAudioText = $"Download {FormatDownloadBytes(assetProgress.BytesReceived)} / {FormatDownloadBytes(assetProgress.TotalBytes)}";
        });

        try
        {
            await _pyannoteCommunityModelManager.EnsureProvisionedAsync(progress, cancellationToken);
            TranscriptProcessingChunkText = "Progress 100%";
            TranscriptProcessingAudioText = "Download completed";
            TranscriptProcessingPercent = 100;
            IsTranscriptProcessingIndeterminate = false;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _boundViewModel?.LogHandledException("speaker detection asset provisioning", ex);
            string message = DataContext is MainViewModel vm
                ? BuildDetectSpeakerFailureMessage(vm, ex)
                : ex.Message;
            ShowTranscribeAudioErrorDialog(message);
            return false;
        }
    }

    private static string FormatDownloadBytes(long? bytes)
    {
        if (bytes is null || bytes <= 0)
        {
            return "unknown size";
        }

        if (bytes >= 1_000_000_000)
        {
            return $"{bytes.Value / 1_000_000_000d:F2} GB";
        }

        if (bytes >= 1_000_000)
        {
            return $"{bytes.Value / 1_000_000d:F1} MB";
        }

        if (bytes >= 1_000)
        {
            return $"{bytes.Value / 1_000d:F1} KB";
        }

        return $"{bytes:N0} B";
    }

    private static IEnumerable<Exception> EnumerateExceptionChain(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    private void ShowCopyToast(string title, string message, ToastNotificationType type = ToastNotificationType.Info)
    {
        double startOpacity = CopyToastHost.Visibility == Visibility.Visible
            ? CopyToastHost.Opacity
            : 0;
        double startOffset = CopyToastHost.Visibility == Visibility.Visible
            ? CopyToastTransform.Y
            : ToastHiddenOffsetY;

        CancelCopyToast();

        ApplyToastVisuals(type);
        CopyToastTitleText.Text = title;
        CopyToastMessageText.Text = message;
        CopyToastHost.Visibility = Visibility.Visible;
        CopyToastHost.Opacity = startOpacity;
        CopyToastTransform.Y = startOffset;
        CopyToastHost.Margin = new Thickness(0, ToastTopMargin, ToastRightMargin, 0);

        CopyToastHost.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(startOpacity, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut,
                },
            });
        CopyToastTransform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(startOffset, 0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut,
                },
            });

        _copyToastCts = new CancellationTokenSource();
        _ = HideCopyToastAfterDelayAsync(_copyToastCts);
    }

    private async Task HideCopyToastAfterDelayAsync(CancellationTokenSource toastCts)
    {
        CancellationToken token;
        try
        {
            token = toastCts.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            await Task.Delay(ToastDisplayDuration, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (IsCancellationRequestedOrDisposed(toastCts))
        {
            return;
        }

        var opacityAnimation = new DoubleAnimation(CopyToastHost.Opacity, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase
            {
                EasingMode = EasingMode.EaseIn,
            },
        };
        opacityAnimation.Completed += (_, _) =>
        {
            if (IsCancellationRequestedOrDisposed(toastCts))
            {
                return;
            }

            CopyToastHost.Visibility = Visibility.Collapsed;
            CopyToastHost.Opacity = 0;
            CopyToastTransform.Y = ToastHiddenOffsetY;
        };

        CopyToastHost.BeginAnimation(OpacityProperty, opacityAnimation);
        CopyToastTransform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(CopyToastTransform.Y, -10, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseIn,
                },
            });
    }

    private static bool IsCancellationRequestedOrDisposed(CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            return cancellationTokenSource.IsCancellationRequested;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private void ApplyToastVisuals(ToastNotificationType type)
    {
        ToastAppearance appearance = type switch
        {
            ToastNotificationType.Success => new ToastAppearance(
                IconData: "M2.5,8.5 L6.2,12.2 L13.5,4.5"),
            ToastNotificationType.Warning => new ToastAppearance(
                IconData: "M8,1.7 L15.1,14.8 H0.9 Z M8,5.2 V9.1 M8,11.6 V12.2"),
            ToastNotificationType.Error => new ToastAppearance(
                IconData: "M8,1.8 A6.2,6.2 0 1 1 7.99,1.8 M5.1,5.1 L10.9,10.9 M10.9,5.1 L5.1,10.9"),
            _ => new ToastAppearance(
                IconData: "M8,1.8 A6.2,6.2 0 1 1 7.99,1.8 M8,6.2 V10.8 M8,4.2 V4.3"),
        };

        CopyToastIconHost.ClearValue(Border.BackgroundProperty);
        CopyToastIconHost.ClearValue(Border.BorderBrushProperty);
        CopyToastIconPath.Stroke = CopyToastTitleText.Foreground;
        CopyToastIconPath.Data = Geometry.Parse(appearance.IconData);
    }

    private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        ApplyFloatingSurfaceTheme();
        ApplySelectionControlTheme();
        ApplySessionsCardTheme();
        UpdateTranscribeAudioBatchControlState();
    }

    private void CancelCopyToast()
    {
        if (_copyToastCts is not null)
        {
            try
            {
                _copyToastCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _copyToastCts.Dispose();
            _copyToastCts = null;
        }

        CopyToastHost.BeginAnimation(OpacityProperty, null);
        CopyToastTransform.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private void OnWindowMouseDismissToast(object sender, MouseButtonEventArgs e)
    {
        DismissToastImmediate();
    }

    private void OnWindowMouseWheelDismissToast(object sender, MouseWheelEventArgs e)
    {
        DismissToastImmediate();
    }

    private void DismissToastImmediate()
    {
        if (CopyToastHost.Visibility != Visibility.Visible)
        {
            return;
        }

        CancelCopyToast();
        CopyToastHost.Visibility = Visibility.Collapsed;
        CopyToastHost.Opacity = 0;
        CopyToastTransform.Y = ToastHiddenOffsetY;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsLiveTranscriptionRunning)
            or nameof(MainViewModel.IsGenerationRunning))
        {
            UpdateLivePrimaryActionButtonState();
        }

        if (e.PropertyName == nameof(MainViewModel.SelectedThemePreference))
        {
            // The view-model property changed event is raised before Application.ThemeMode settles.
            // Re-apply after bindings and theme updates complete to keep floating surfaces in sync.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplyFloatingSurfaceTheme();
                ApplySelectionControlTheme();
                ApplySessionsCardTheme();
            }), DispatcherPriority.Background);
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.ApplicationUpdateState)
            or nameof(MainViewModel.ApplicationUpdateProgressPercent)
            or nameof(MainViewModel.IsApplicationUpdateProgressVisible))
        {
            if (sender is MainViewModel vm)
            {
                ApplyApplicationUpdateTaskbarProgress(vm);
                SyncUpdateProgressWindow(vm);
            }

            return;
        }

        if (e.PropertyName == nameof(MainViewModel.HasSpeakerLabels))
        {
            UpdateTranscriptGridPresentation();
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.SelectedTranscriptViewIndex))
        {
            StopActivePlaybackEditTranscription(
                _boundViewModel,
                pausePlayback: false,
                reason: "transcript view changed",
                discardResults: true);
            ClearTranscriptTextEditState();
            ClearTranscriptEditPlaybackLoop();
            UpdateTranscriptRowActionsVisibility();
        }

        if (e.PropertyName is nameof(MainViewModel.AudioSeekPositionSeconds)
            or nameof(MainViewModel.LoadedAudioFilePath)
            or nameof(MainViewModel.IsAudioFileLoaded))
        {
            OnPropertyChanged(nameof(ShouldShowMediaPlayerPanel));
            OnPropertyChanged(nameof(CanPrimeTranscribeAudioFromCurrentSession));
            OnPropertyChanged(nameof(IsTranscribeAudioRestartVisible));
            UpdateTranscribeAudioBatchControlState();
            if (_boundViewModel is not null && !_boundViewModel.IsAudioFileLoaded)
            {
                StopActivePlaybackEditTranscription(
                    _boundViewModel,
                    pausePlayback: false,
                    reason: "audio preview unloaded",
                    discardResults: true);
                ClearTranscriptEditPlaybackLoop();
            }

            UpdatePlaybackEditTranscriptionProgress();
            EnforceTranscriptEditPlaybackLoop();
            EnforcePlaybackEditTranscriptionStop();
            UpdatePlaybackTimelineHighlight();
        }

        if (e.PropertyName is nameof(MainViewModel.IsTranscriptDataEmpty)
            or nameof(MainViewModel.IsCurrentSessionAudioTranscriptionSession))
        {
            OnPropertyChanged(nameof(ShouldShowAudioTranscriptionPanel));
            OnPropertyChanged(nameof(CanPrimeTranscribeAudioFromCurrentSession));
            OnPropertyChanged(nameof(IsTranscribeAudioRestartVisible));
            UpdateTranscribeAudioBatchControlState();
        }

        if (e.PropertyName is nameof(MainViewModel.HasCurrentTranscriptLines)
            or nameof(MainViewModel.CanRunTranscribeAudioPrimaryAction)
            or nameof(MainViewModel.IsGenerationRunning))
        {
            OnPropertyChanged(nameof(CanPrimeTranscribeAudioFromCurrentSession));
            OnPropertyChanged(nameof(IsTranscribeAudioRestartVisible));
            UpdateTranscribeAudioBatchControlState();
        }

        if (sender is MainViewModel vmForPanel
            && e.PropertyName is nameof(MainViewModel.LoadedAudioFilePath)
                or nameof(MainViewModel.LoadedAudioFileName)
                or nameof(MainViewModel.SelectedEngine)
                or nameof(MainViewModel.SelectedEngineId)
                or nameof(MainViewModel.IsTranscriptDataEmpty)
                or nameof(MainViewModel.IsCurrentSessionAudioTranscriptionSession)
                or nameof(MainViewModel.CurrentSessionDisplayName)
                or nameof(MainViewModel.IsCurrentSessionAudioMissing))
        {
            SyncTranscriptProcessingPanelFromSession(vmForPanel);
            UpdateTranscribeAudioBatchControlState();
        }
    }

    private void ApplyApplicationUpdateTaskbarProgress(MainViewModel vm)
    {
        TaskbarItemInfo ??= new TaskbarItemInfo();
        TaskbarItemInfo.ProgressValue = Math.Clamp(vm.ApplicationUpdateProgressPercent / 100d, 0, 1);
        TaskbarItemInfo.ProgressState = vm.ApplicationUpdateState switch
        {
            AppUpdateState.Checking => TaskbarItemProgressState.Indeterminate,
            AppUpdateState.Downloading or AppUpdateState.Installing => TaskbarItemProgressState.Normal,
            AppUpdateState.Deferred => TaskbarItemProgressState.Paused,
            AppUpdateState.Failed => TaskbarItemProgressState.Error,
            _ => TaskbarItemProgressState.None,
        };
    }

    private void UpdateLivePrimaryActionButtonState()
    {
        if (LiveTranscriptionPrimaryActionButton is null)
        {
            return;
        }

        bool isEnabled = DataContext is MainViewModel vm
            && !vm.IsLiveTranscriptionRunning
            && !IsTranscribeAudioBatchTranscribing;
        LiveTranscriptionPrimaryActionButton.IsEnabled = isEnabled;
    }

    private void SyncUpdateProgressWindow(MainViewModel vm)
    {
        bool shouldOpen = vm.ApplicationUpdateState is AppUpdateState.Checking
            or AppUpdateState.Downloading
            or AppUpdateState.Installing;

        if (shouldOpen)
        {
            if (_updateProgressWindow is null)
            {
                _updateProgressWindow = new DeferredUpdateInstallWindow(vm.AppUpdateService!)
                {
                    Owner = this,
                };
                _updateProgressWindow.Closed += (_, _) => _updateProgressWindow = null;
                _updateProgressWindow.Show();
            }
            return;
        }

        if (_updateProgressWindow is not null
            && vm.ApplicationUpdateState is AppUpdateState.Completed or AppUpdateState.Failed or AppUpdateState.Idle)
        {
            _updateProgressWindow.CloseAfterOperation();
            _updateProgressWindow = null;
        }
    }

    private void ApplyFloatingSurfaceTheme()
    {
        System.Windows.Media.Color backgroundColor;
        System.Windows.Media.Color borderColor;
        bool darkTheme = IsDarkThemeActive();

        if (darkTheme)
        {
            backgroundColor = System.Windows.Media.Color.FromRgb(41, 33, 47);
            borderColor = System.Windows.Media.Color.FromRgb(93, 84, 105);
        }
        else
        {
            backgroundColor = System.Windows.Media.Color.FromRgb(255, 255, 255);
            borderColor = System.Windows.Media.Color.FromRgb(199, 199, 204);
        }

        var backgroundBrush = new SolidColorBrush(backgroundColor);
        var borderBrush = new SolidColorBrush(borderColor);
        if (backgroundBrush.CanFreeze)
        {
            backgroundBrush.Freeze();
        }

        if (borderBrush.CanFreeze)
        {
            borderBrush.Freeze();
        }

        TranscribeAudioBatchCard.Background = System.Windows.Media.Brushes.Transparent;
        TranscribeAudioBatchCard.BorderBrush = null;
        CopyToastCard.Background = backgroundBrush;
        CopyToastCard.BorderBrush = borderBrush;
    }

    private void ApplySelectionControlTheme()
    {
        if (System.Windows.Application.Current is null)
        {
            return;
        }

        var selectionBrush = new SolidColorBrush(
            IsDarkThemeActive()
            ? Colors.White
            : System.Windows.Media.Color.FromRgb(26, 26, 26));
        if (selectionBrush.CanFreeze)
        {
            selectionBrush.Freeze();
        }

        System.Windows.Application.Current.Resources["SelectionControlForegroundBrush"] = selectionBrush;
    }

    private void ApplySessionsCardTheme()
    {
        if (System.Windows.Application.Current is null)
        {
            return;
        }

        bool darkTheme = IsDarkThemeActive();
        var backgroundBrush = new SolidColorBrush(
            darkTheme
                ? System.Windows.Media.Color.FromRgb(217, 217, 217)
                : System.Windows.Media.Color.FromRgb(64, 64, 64));
        var foregroundBrush = new SolidColorBrush(
            darkTheme
                ? System.Windows.Media.Colors.Black
                : System.Windows.Media.Colors.White);

        if (backgroundBrush.CanFreeze)
        {
            backgroundBrush.Freeze();
        }

        if (foregroundBrush.CanFreeze)
        {
            foregroundBrush.Freeze();
        }

        System.Windows.Application.Current.Resources["SessionsCardLoadedBackgroundBrush"] = backgroundBrush;
        System.Windows.Application.Current.Resources["SessionsCardLoadedForegroundBrush"] = foregroundBrush;
    }

    private bool IsDarkThemeActive()
    {
        AppThemePreference preference = _boundViewModel?.SelectedThemePreference ?? AppThemePreference.System;
        if (preference == AppThemePreference.Dark)
        {
            return true;
        }

        if (preference == AppThemePreference.Light)
        {
            return false;
        }

        return !IsSystemUsingLightTheme();
    }

    private static bool IsSystemUsingLightTheme()
    {
        const string personalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        const string appsUseLightThemeValue = "AppsUseLightTheme";

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(personalizeKeyPath, writable: false);
            object? value = key?.GetValue(appsUseLightThemeValue);
            return value is int intValue
                ? intValue != 0
                : true;
        }
        catch
        {
            return true;
        }
    }

    private void OnFinalizedTranscriptLinesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateTranscriptGridPresentation();
        UpdatePlaybackTimelineHighlight();
        UpdateTranscriptRowActionsVisibility();

        if ((_isLiveTranscribing || _isLiveTranscriptionStopping)
            && e.Action == NotifyCollectionChangedAction.Add
            && e.NewItems is { Count: > 0 })
        {
            Dispatcher.BeginInvoke(new Action(ScrollTranscriptGridToLastRow), DispatcherPriority.Background);
        }
    }

    private void ScrollTranscriptGridToLastRow()
    {
        if (FinalizedTranscriptGrid.Items.Count == 0)
        {
            return;
        }

        object? lastItem = FinalizedTranscriptGrid.Items[FinalizedTranscriptGrid.Items.Count - 1];
        if (lastItem is null)
        {
            return;
        }

        DataGridColumn? anchorColumn = TranscriptTextColumn
            ?? TimelineTranscriptColumn
            ?? FinalizedTranscriptGrid.Columns.FirstOrDefault();
        if (anchorColumn is null)
        {
            return;
        }

        FinalizedTranscriptGrid.ScrollIntoView(lastItem, anchorColumn);
    }

    private void FinalizedTranscriptGrid_CurrentCellChanged(object sender, EventArgs e)
    {
        SyncPlaybackToCurrentTranscriptRow();
        Dispatcher.BeginInvoke(new Action(UpdateTranscriptRowActionsVisibility), DispatcherPriority.Background);
    }

    private void FinalizedTranscriptGrid_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(UpdateTranscriptRowActionsVisibility), DispatcherPriority.Background);
    }

    private void FinalizedTranscriptGrid_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(UpdateTranscriptRowActionsVisibility), DispatcherPriority.Background);
    }

    private void RecentSessionsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox listBox)
        {
            return;
        }

        DependencyObject? source = e.OriginalSource as DependencyObject;
        if (source is null)
        {
            return;
        }

        if (FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is not null)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(listBox, source) is not ListBoxItem item)
        {
            return;
        }

        if (e.ClickCount < 2 && !item.IsSelected)
        {
            item.IsSelected = true;
            if (item.DataContext is TranscriptSessionSummary session)
            {
                _processLogService?.Log(
                    "SessionDelete",
                    $"List single-click selected session. sessionId='{session.SessionId}'.",
                    ProcessLogLevel.Debug);
            }

            e.Handled = true;
        }
    }

    private async void RecentSessionsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox listBox || DataContext is not MainViewModel vm)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(listBox, e.OriginalSource as DependencyObject) is not ListBoxItem item
            || item.DataContext is not TranscriptSessionSummary session)
        {
            return;
        }

        _processLogService?.Log(
            "SessionDelete",
            $"List double-click open session requested. sessionId='{session.SessionId}'.",
            ProcessLogLevel.Debug);
        await vm.LoadRecentSessionAsync(session);
        await PrepareOpenedLiveSessionForNewRecordingStartAsync(vm);
        e.Handled = true;
    }

    private async void RecentSessionOpenButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TranscriptSessionSummary session
            || DataContext is not MainViewModel vm)
        {
            return;
        }

        _processLogService?.Log(
            "SessionDelete",
            $"Open button clicked for session. sessionId='{session.SessionId}'.",
            ProcessLogLevel.Debug);
        await vm.LoadRecentSessionAsync(session);
        await PrepareOpenedLiveSessionForNewRecordingStartAsync(vm);
        e.Handled = true;
    }

    private async Task PrepareOpenedLiveSessionForNewRecordingStartAsync(MainViewModel vm)
    {
        if (!vm.PrepareOpenedLiveSessionForNewRecordingStart())
        {
            return;
        }

        await EnsureLiveTranscriptionPanelReadyAsync(vm);
        SetLiveSessionReadyToStartActivity();
        UpdateLiveControlState();
        OnPropertyChanged(nameof(ShouldShowMediaPlayerPanel));
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void ConfigureTranscriptProcessingUi(bool allowMute)
    {
        TranscriptProcessingStartButtonText = "Start";
        IsTranscriptProcessingMuteAvailable = allowMute;
        TranscriptProcessingSourceFileText = string.Empty;
        TranscriptProcessingSourceFileSizeText = string.Empty;
        TranscriptProcessingEngineText = string.Empty;
        ResetTranscriptProcessingProgress();
    }

    private void ResetTranscriptProcessingProgress()
    {
        IsTranscriptProcessingCanceling = false;
        IsTranscriptProcessingIndeterminate = true;
        TranscriptProcessingPercent = 0;
        TranscriptProcessingChunkText = "Progress 0%";
        TranscriptProcessingAudioText = "Audio 00:00 / 00:00";
        TranscriptProcessingElapsedText = "Elapsed 00:00";
        TranscriptProcessingEtaText = "ETA calculating";
    }

    private void ApplyTranscriptionProgress(TranscriptionProgressSnapshot snapshot)
    {
        if (snapshot.Phase == TranscriptionProgressPhase.Canceling)
        {
            ApplyTranscriptProcessingCancelingState();
            return;
        }

        TranscriptProcessingPercent = snapshot.Percent;
        IsTranscriptProcessingIndeterminate = ShouldShowIndeterminateTranscriptProgress(snapshot);
        TranscriptProcessingChunkText = FormatProgressChunkText(snapshot);
        TranscriptProcessingAudioText =
            $"Audio {FormatProgressDuration(snapshot.ProcessedAudio)} / {FormatProgressDuration(snapshot.TotalAudio)}";
        TranscriptProcessingElapsedText = $"Elapsed {FormatProgressDuration(snapshot.Elapsed)}";
        TranscriptProcessingEtaText = snapshot.EstimatedRemaining is null
            ? "ETA calculating"
            : $"ETA {FormatProgressDuration(snapshot.EstimatedRemaining.Value)}";
    }

    private void ApplyTranscriptProcessingCancelingState()
    {
        ApplyTranscriptProcessingStoppingState("ETA canceled");
    }

    private void ApplyTranscriptProcessingPausingState()
    {
        ApplyTranscriptProcessingStoppingState("Resume available");
    }

    private void ApplyTranscriptProcessingStoppingState(string etaText)
    {
        IsTranscriptProcessingCanceling = true;
        IsTranscriptProcessingIndeterminate = true;
        TranscriptProcessingEtaText = etaText;
    }

    private static string FormatProgressChunkText(TranscriptionProgressSnapshot snapshot)
    {
        if (snapshot.Phase == TranscriptionProgressPhase.RunningSpeakerDiarization
            && snapshot.CurrentChunk is int currentChunk
            && snapshot.TotalChunks is int totalChunks
            && totalChunks > 1)
        {
            string chunkState = snapshot.Percent < 1
                ? "analyzing"
                : $"{snapshot.Percent:0}% complete";
            return $"Chunk {currentChunk:N0} of {totalChunks:N0} - {chunkState}";
        }

        if (snapshot.Phase == TranscriptionProgressPhase.RunningSpeakerDiarization && snapshot.Percent < 1)
        {
            return "Analyzing speakers";
        }

        string percentText = $"Progress {snapshot.Percent:0}%";
        if (snapshot.CurrentChunk is null || snapshot.TotalChunks is null || snapshot.TotalChunks <= 1)
        {
            return percentText;
        }

        return $"{percentText} - chunk {snapshot.CurrentChunk:N0} of {snapshot.TotalChunks:N0}";
    }

    private static string FormatProgressDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalHours >= 1
            ? duration.ToString(@"hh\:mm\:ss")
            : duration.ToString(@"mm\:ss");
    }

    private static bool ShouldShowIndeterminateTranscriptProgress(TranscriptionProgressSnapshot snapshot)
    {
        if (snapshot.Phase == TranscriptionProgressPhase.Completed)
        {
            return false;
        }

        if (snapshot.TotalAudio <= TimeSpan.Zero)
        {
            return true;
        }

        return snapshot.Phase == TranscriptionProgressPhase.RunningSpeakerDiarization
            && snapshot.Percent < 1;
    }

    private static string FormatFileSizeText(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return string.Empty;
        }

        try
        {
            var fileInfo = new System.IO.FileInfo(filePath);
            if (!fileInfo.Exists || fileInfo.Length < 0)
            {
                return string.Empty;
            }

            return FormatFileSize(fileInfo.Length);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FormatFileSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int suffixIndex = 0;

        while (size >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            size /= 1024;
            suffixIndex++;
        }

        return suffixIndex == 0
            ? $"{bytes:N0} {suffixes[suffixIndex]}"
            : $"{size:0.##} {suffixes[suffixIndex]}";
    }

    private static string FormatTranscriptionPhase(TranscriptionProgressPhase phase)
    {
        return phase switch
        {
            TranscriptionProgressPhase.PreparingAudio => "Preparing audio.",
            TranscriptionProgressPhase.Chunking => "Preparing audio chunks.",
            TranscriptionProgressPhase.TranscribingChunk => "Transcribing audio.",
            TranscriptionProgressPhase.RunningSpeakerDiarization => "Running speaker diarization.",
            TranscriptionProgressPhase.MergingSpeakerLabels => "Applying speaker labels.",
            TranscriptionProgressPhase.MergingResults => "Merging transcript rows.",
            TranscriptionProgressPhase.Completed => "Completed.",
            TranscriptionProgressPhase.Canceling => "Canceling...",
            _ => "Working...",
        };
    }

    private void ApplyTranscribeAudioBatchInteractionLock()
    {
        TranscribeAudioBatchOverlay.UpdateLayout();

        try
        {
            FinalizedTranscriptGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            FinalizedTranscriptGrid.CommitEdit(DataGridEditingUnit.Row, true);
        }
        catch
        {
            // Best-effort edit shutdown.
        }

        ClearTranscriptEditPlaybackLoop();
        FinalizedTranscriptGrid.SelectedCells.Clear();
        FinalizedTranscriptGrid.UnselectAllCells();
        FinalizedTranscriptGrid.CurrentCell = default;
        Keyboard.ClearFocus();

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsTranscribeAudioBatchPendingStart)
            {
                TranscribeAudioBatchStartStopButton.Focus();
            }
        }), DispatcherPriority.Input);
    }

    private void RestoreTranscribeAudioBatchInteractionLock()
    {
        IsTranscribeAudioBatchPendingStart = false;
        _activeTranscriptProcessingWorkflow = TranscriptProcessingWorkflowKind.None;
        IsTranscriptProcessingMuteAvailable = true;
        IsTranscriptProcessingCanceling = false;
        if (DataContext is MainViewModel vm)
        {
            SyncTranscriptProcessingPanelFromSession(vm);
        }
        else
        {
            TranscriptProcessingSourceFileText = string.Empty;
            TranscriptProcessingSourceFileSizeText = string.Empty;
            TranscriptProcessingEngineText = string.Empty;
            ResetTranscriptProcessingProgress();
        }

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            FinalizedTranscriptGrid.SelectedCells.Clear();
            FinalizedTranscriptGrid.UnselectAllCells();
            FinalizedTranscriptGrid.CurrentCell = default;
        }), DispatcherPriority.Background);
    }

    private void UpdateTranscribeAudioBatchControlState()
    {
        if (TranscribeAudioBatchStartStopButton is null)
        {
            return;
        }

        OnPropertyChanged(nameof(IsTranscribeAudioRestartVisible));

        bool isStopState = IsTranscribeAudioBatchTranscribing;
        bool isCanceling = IsTranscriptProcessingCanceling;
        TranscribeAudioBatchStartStopButton.Content = isCanceling
            ? "Finalizing... please wait"
            : isStopState ? "Stop" : TranscriptProcessingStartButtonText;
        TranscribeAudioBatchStartStopButton.Tag = isStopState ? "\uE711" : "\uE768";
        TranscribeAudioBatchStartStopButton.IsEnabled = isStopState
            ? IsTranscriptProcessingCancelEnabled
            : IsTranscribeAudioBatchStartEnabled || CanPrimeTranscribeAudioFromCurrentSession;

        if (isStopState)
        {
            TranscribeAudioBatchStartStopButton.Background = System.Windows.Media.Brushes.Red;
            TranscribeAudioBatchStartStopButton.Foreground = System.Windows.Media.Brushes.White;
        }
        else
        {
            TranscribeAudioBatchStartStopButton.ClearValue(BackgroundProperty);
            TranscribeAudioBatchStartStopButton.ClearValue(ForegroundProperty);
        }
    }

    private void CancelTranscribeAudioBatch(MainViewModel vm)
    {
        if (!IsTranscribeAudioBatchTranscribing)
        {
            return;
        }

        if (_transcribeAudioBatchTranscriptionCts is not null && !_transcribeAudioBatchTranscriptionCts.IsCancellationRequested)
        {
            ApplyTranscriptProcessingPausingState();
            _transcribeAudioBatchTranscriptionCts.Cancel();
            LogTranscribeAudioBatch("Transcribe Audio pause requested.");
        }

        StopActivePlaybackEditTranscription(
            vm,
            pausePlayback: true,
            reason: "transcribe audio batch canceled",
            discardResults: true);
    }

    private void SyncTranscriptProcessingPanelFromSession(MainViewModel vm)
    {
        if (IsTranscribeAudioBatchPendingStart || IsTranscribeAudioBatchTranscribing)
        {
            // Keep engine display synced with the latest selection even while panel is staged/running.
            TranscriptProcessingEngineText = ResolveCurrentEngineLabel(vm);
            return;
        }

        TranscriptProcessingPanelSessionSnapshot snapshot = vm.GetTranscriptProcessingPanelSessionSnapshot();
        bool shouldShowFreshStartState =
            vm.IsCurrentSessionAudioTranscriptionSession
            && !vm.HasCurrentTranscriptLines
            && !snapshot.ResumeAvailable;

        TranscriptProcessingStartButtonText = shouldShowFreshStartState
            ? "Start"
            : snapshot.ResumeAvailable ? "Resume" : "Start";
        TranscriptProcessingSourceFileText = snapshot.SourceFileName;
        TranscriptProcessingSourceFileSizeText = snapshot.SourceFileSizeBytes > 0
            ? FormatFileSize(snapshot.SourceFileSizeBytes)
            : FormatFileSizeText(vm.LoadedAudioFilePath);
        // Audio transcription always starts with the current selected engine.
        // Show the active selection as the source of truth across panels.
        TranscriptProcessingEngineText = ResolveCurrentEngineLabel(vm);

        if (shouldShowFreshStartState)
        {
            TranscriptProcessingPercent = 0;
            IsTranscriptProcessingIndeterminate = false;
            TranscriptProcessingChunkText = "Progress 0%";
            TranscriptProcessingAudioText = "Audio 00:00 / 00:00";
            TranscriptProcessingElapsedText = "Elapsed 00:00";
            TranscriptProcessingEtaText = "ETA calculating";
            return;
        }

        TranscriptProcessingPercent = snapshot.ProgressPercent;
        IsTranscriptProcessingIndeterminate = false;
        TranscriptProcessingChunkText = $"Progress {snapshot.ProgressPercent:0}%";

        if (snapshot.TotalAudioDuration is TimeSpan totalAudio && totalAudio > TimeSpan.Zero)
        {
            TimeSpan processedAudio = TimeSpan.FromTicks((long)(totalAudio.Ticks * (snapshot.ProgressPercent / 100d)));
            TranscriptProcessingAudioText = $"Audio {FormatProgressDuration(processedAudio)} / {FormatProgressDuration(totalAudio)}";
        }
        else
        {
            TranscriptProcessingAudioText = "Audio 00:00 / 00:00";
        }

        TranscriptProcessingElapsedText = snapshot.Elapsed is TimeSpan elapsed
            ? $"Elapsed {FormatProgressDuration(elapsed)}"
            : "Elapsed 00:00";
        TranscriptProcessingEtaText = snapshot.EstimatedRemaining is TimeSpan remaining
            ? $"ETA {FormatProgressDuration(remaining)}"
            : "ETA calculating";
    }

    private void CancelDetectSpeakers(MainViewModel vm)
    {
        if (!IsTranscribeAudioBatchTranscribing)
        {
            return;
        }

        if (_transcribeAudioBatchTranscriptionCts is not null && !_transcribeAudioBatchTranscriptionCts.IsCancellationRequested)
        {
            ApplyTranscriptProcessingCancelingState();
            _transcribeAudioBatchTranscriptionCts.Cancel();
            LogTranscribeAudioBatch("Detect Speaker cancellation requested.");
        }
    }

    private void InsertTranscriptRowBelow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button
            || button.DataContext is not FinalizedTranscriptLineViewModel currentLine
            || DataContext is not MainViewModel vm)
        {
            return;
        }

        HandleInsertTranscriptRow(vm, currentLine, insertBelow: true, duplicateText: false);
    }

    private void TranscriptContext_InsertRowAbove_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuLine(sender, out FinalizedTranscriptLineViewModel currentLine)
            || DataContext is not MainViewModel vm)
        {
            return;
        }

        HandleInsertTranscriptRow(vm, currentLine, insertBelow: false, duplicateText: false);
    }

    private void TranscriptContext_InsertRowBelow_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuLine(sender, out FinalizedTranscriptLineViewModel currentLine)
            || DataContext is not MainViewModel vm)
        {
            return;
        }

        HandleInsertTranscriptRow(vm, currentLine, insertBelow: true, duplicateText: false);
    }

    private void TranscriptContext_DuplicateRow_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuLine(sender, out FinalizedTranscriptLineViewModel currentLine)
            || DataContext is not MainViewModel vm)
        {
            return;
        }

        HandleInsertTranscriptRow(vm, currentLine, insertBelow: true, duplicateText: true);
    }

    private void TranscriptContext_DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuLine(sender, out FinalizedTranscriptLineViewModel currentLine)
            || DataContext is not MainViewModel vm)
        {
            return;
        }

        HandleCombineToPreviousRow(vm, currentLine);
    }

    private void TranscriptContext_CopyText_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuLine(sender, out FinalizedTranscriptLineViewModel currentLine))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(currentLine.Text ?? string.Empty);
            ShowCopyToast("Copied", "Row text copied.", ToastNotificationType.Success);
        }
        catch (Exception ex)
        {
            _boundViewModel?.LogHandledException("copy row text", ex);
            ShowCopyToast("Copy failed", "Unable to copy row text.", ToastNotificationType.Error);
        }
    }

    private void TranscriptContext_CopyTimelineAndText_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuLine(sender, out FinalizedTranscriptLineViewModel currentLine))
        {
            return;
        }

        string lineText = string.IsNullOrWhiteSpace(currentLine.Timeline)
            ? (currentLine.Text ?? string.Empty)
            : $"{currentLine.Timeline} {currentLine.Text}".TrimEnd();

        try
        {
            System.Windows.Clipboard.SetText(lineText);
            ShowCopyToast("Copied", "Timeline and row text copied.", ToastNotificationType.Success);
        }
        catch (Exception ex)
        {
            _boundViewModel?.LogHandledException("copy row timeline and text", ex);
            ShowCopyToast("Copy failed", "Unable to copy timeline and row text.", ToastNotificationType.Error);
        }
    }

    private bool TryGetContextMenuLine(object sender, out FinalizedTranscriptLineViewModel line)
    {
        line = null!;

        if (sender is FinalizedTranscriptLineViewModel rowLine)
        {
            line = rowLine;
            return true;
        }

        if (sender is System.Windows.Controls.MenuItem menuItem)
        {
            if (menuItem.DataContext is FinalizedTranscriptLineViewModel menuLine)
            {
                line = menuLine;
                return true;
            }

            if (menuItem.Parent is System.Windows.Controls.ContextMenu contextMenu
                && contextMenu.DataContext is FinalizedTranscriptLineViewModel contextLine)
            {
                line = contextLine;
                return true;
            }
        }

        return false;
    }

    private void TranscriptContextCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command == TranscribeRowCommand)
        {
            e.CanExecute =
                DataContext is MainViewModel vm
                && TryGetContextMenuLine(e.Parameter, out FinalizedTranscriptLineViewModel currentLine)
                && !_isRowFileTranscriptionRunning
                && CanRunExplicitRowTranscription(vm, currentLine, out _)
                && EnsureSegmentRowActionAvailable(vm, "Transcribe row", showMessage: false);
            e.Handled = true;
            return;
        }

        if (e.Command == RenameSpeakerCommand)
        {
            e.CanExecute =
                DataContext is MainViewModel vm
                && TryGetContextMenuLine(e.Parameter, out FinalizedTranscriptLineViewModel currentLine)
                && !string.IsNullOrWhiteSpace(currentLine.SpeakerLabel)
                && EnsureSegmentRowActionAvailable(vm, "Rename Speaker", showMessage: false);
            e.Handled = true;
            return;
        }

        if (e.Command == CombineToPreviousRowCommand)
        {
            e.CanExecute =
                DataContext is MainViewModel vm
                && TryGetContextMenuLine(e.Parameter, out FinalizedTranscriptLineViewModel currentLine)
                && TryResolveCombineToPreviousRowMerge(vm.CurrentTranscriptLines.ToList(), currentLine, out _, out _)
                && EnsureSegmentRowActionAvailable(vm, "Combine to previous row", showMessage: false);
            e.Handled = true;
            return;
        }

        if (e.Command == MergeAdjacentRowsForSelectedSpeakerCommand)
        {
            e.CanExecute =
                DataContext is MainViewModel vm
                && TryGetContextMenuLine(e.Parameter, out FinalizedTranscriptLineViewModel currentLine)
                && !string.IsNullOrWhiteSpace(currentLine.SpeakerLabel)
                && CanMergeAdjacentRowsAroundSelectedRow(
                    GetDisplayedTranscriptLines(),
                    currentLine)
                && EnsureSegmentRowActionAvailable(vm, "Merge selected speaker rows", showMessage: false);
            e.Handled = true;
            return;
        }

        if (e.Command == MergeAllAdjacentRowsBySpeakerCommand)
        {
            e.CanExecute =
                DataContext is MainViewModel vm
                && TryGetContextMenuLine(e.Parameter, out FinalizedTranscriptLineViewModel currentLine)
                && !string.IsNullOrWhiteSpace(currentLine.SpeakerLabel)
                && CanMergeAnyAdjacentRowsBySpeaker(
                    GetDisplayedTranscriptLines())
                && EnsureSegmentRowActionAvailable(vm, "Merge all adjacent rows by speaker", showMessage: false);
            e.Handled = true;
            return;
        }

        if (e.Command == SeparateRowCommand)
        {
            e.CanExecute =
                DataContext is MainViewModel vm
                && TryGetContextMenuLine(e.Parameter, out _)
                && EnsureSegmentRowActionAvailable(vm, "Separate row", showMessage: false);
            e.Handled = true;
            return;
        }

        e.CanExecute = DataContext is MainViewModel;
        e.Handled = true;
    }

    private void TranscribeRowCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!TryGetContextMenuLine(e.Parameter, out FinalizedTranscriptLineViewModel currentLine)
            || DataContext is not MainViewModel vm)
        {
            return;
        }

        TryStartRowFileTranscription(vm, currentLine);
    }

    private void CombineToPreviousRowCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!TryGetContextMenuLine(e.Parameter, out FinalizedTranscriptLineViewModel currentLine)
            || DataContext is not MainViewModel vm)
        {
            return;
        }

        HandleCombineToPreviousRow(vm, currentLine);
    }

    private void RenameSpeakerCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!TryGetContextMenuLine(e.Parameter, out FinalizedTranscriptLineViewModel currentLine)
            || DataContext is not MainViewModel vm)
        {
            return;
        }

        HandleRenameSpeaker(vm, currentLine);
    }

    private void MergeAdjacentRowsForSelectedSpeakerCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!TryGetContextMenuLine(e.Parameter, out FinalizedTranscriptLineViewModel currentLine)
            || DataContext is not MainViewModel vm)
        {
            return;
        }

        HandleMergeAdjacentRowsForSelectedSpeaker(vm, currentLine);
    }

    private void MergeAllAdjacentRowsBySpeakerCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!TryGetContextMenuLine(e.Parameter, out FinalizedTranscriptLineViewModel currentLine)
            || DataContext is not MainViewModel vm)
        {
            return;
        }

        HandleMergeAllAdjacentRowsBySpeaker(vm, currentLine);
    }

    private void CopyRowTextCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!TryGetContextMenuLine(e.Parameter, out FinalizedTranscriptLineViewModel currentLine))
        {
            return;
        }

        string clipboardText = BuildRowClipboardText(currentLine);

        try
        {
            System.Windows.Clipboard.SetText(clipboardText);
            ShowCopyToast("Copied", "Row text copied.", ToastNotificationType.Success);
        }
        catch (Exception ex)
        {
            _boundViewModel?.LogHandledException("copy row text", ex);
            ShowCopyToast("Copy failed", "Unable to copy row text.", ToastNotificationType.Error);
        }
    }

    private void FinalizedTranscriptGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DataGridCell? clickedCell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        _transcriptContextMenuScope = ResolveTranscriptContextMenuScope(clickedCell?.Column);

        if (clickedCell?.DataContext is FinalizedTranscriptLineViewModel line
            && clickedCell.Column is DataGridColumn column)
        {
            var clickedInfo = new DataGridCellInfo(line, column);
            FinalizedTranscriptGrid.SelectedCells.Clear();
            FinalizedTranscriptGrid.CurrentCell = clickedInfo;
            FinalizedTranscriptGrid.SelectedCells.Add(clickedInfo);
        }
    }

    private void FinalizedTranscriptGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        DataGridColumn? targetColumn = null;

        if (e.OriginalSource is DependencyObject originalSource)
        {
            DataGridCell? clickedCell = FindAncestor<DataGridCell>(originalSource);
            targetColumn = clickedCell?.Column;
        }

        if (targetColumn is null)
        {
            targetColumn = FinalizedTranscriptGrid.CurrentCell.Column;
        }

        _transcriptContextMenuScope = ResolveTranscriptContextMenuScope(targetColumn);

        if (FinalizedTranscriptGrid.CurrentCell.Item is not FinalizedTranscriptLineViewModel currentLine)
        {
            return;
        }

        DataGridRow? row = FinalizedTranscriptGrid.ItemContainerGenerator.ContainerFromItem(currentLine) as DataGridRow;
        if (row?.ContextMenu is not System.Windows.Controls.ContextMenu contextMenu)
        {
            return;
        }

        bool isSpeakerCellMenu = _transcriptContextMenuScope == TranscriptContextMenuScope.SpeakerCell;
        bool isTextCellMenu = _transcriptContextMenuScope == TranscriptContextMenuScope.TextCell;
        bool canRenameSpeaker = !string.IsNullOrWhiteSpace(currentLine.SpeakerLabel);
        foreach (object item in contextMenu.Items)
        {
            if (item is System.Windows.Controls.Separator separator)
            {
                string separatorTag = separator.Tag as string ?? string.Empty;
                separator.Visibility = separatorTag switch
                {
                    "SpeakerDivider" => isSpeakerCellMenu ? Visibility.Visible : Visibility.Collapsed,
                    "CopyDivider" => Visibility.Visible,
                    _ => Visibility.Visible,
                };
                continue;
            }

            if (item is not System.Windows.Controls.MenuItem menuItem)
            {
                continue;
            }

            string header = menuItem.Header as string ?? string.Empty;
            menuItem.Visibility = ResolveTranscriptContextMenuItemVisibility(
                header,
                isSpeakerCellMenu,
                isTextCellMenu,
                canRenameSpeaker);
        }
    }

    private void SeparateRowCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!TryGetContextMenuLine(e.Parameter, out FinalizedTranscriptLineViewModel currentLine)
            || DataContext is not MainViewModel vm)
        {
            return;
        }

        HandleSeparateTranscriptRow(vm, currentLine);
    }

    internal static string BuildRowClipboardText(FinalizedTranscriptLineViewModel line)
    {
        ArgumentNullException.ThrowIfNull(line);

        string start = line.StartOffset is TimeSpan startOffset
            ? FormatRowTimelineOffset(startOffset)
            : string.Empty;
        string end = line.EndOffset is TimeSpan endOffset
            ? FormatRowTimelineOffset(endOffset)
            : string.Empty;
        string speaker = line.SpeakerLabel?.Trim() ?? string.Empty;
        string text = line.Text ?? string.Empty;

        return string.Join('\t', start, end, speaker, text);
    }

    internal static bool TryResolveSeparateRowRange(
        FinalizedTranscriptLineViewModel line,
        out TimeSpan startOffset,
        out TimeSpan endOffset)
    {
        startOffset = TimeSpan.Zero;
        endOffset = TimeSpan.Zero;

        if (line.StartOffset is not TimeSpan start
            || line.EndOffset is not TimeSpan end
            || end <= start)
        {
            return false;
        }

        startOffset = start;
        endOffset = end;
        return true;
    }

    internal static (string FirstText, string SecondText) SplitRowTextForSeparate(string? text)
    {
        string source = text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source))
        {
            return (string.Empty, string.Empty);
        }

        int splitIndex = FindLineFeedSplitIndex(source);
        if (splitIndex >= 0)
        {
            return (
                source[..splitIndex].Trim(),
                source[(splitIndex + 1)..].Trim());
        }

        splitIndex = FindPeriodSplitIndex(source);
        if (splitIndex >= 0)
        {
            return (
                source[..(splitIndex + 1)].Trim(),
                source[(splitIndex + 1)..].Trim());
        }

        splitIndex = FindPunctuationSplitIndex(source);
        if (splitIndex >= 0)
        {
            return (
                source[..(splitIndex + 1)].Trim(),
                source[(splitIndex + 1)..].Trim());
        }

        splitIndex = FindNearestWhitespaceToMidpoint(source);
        if (splitIndex < 0)
        {
            splitIndex = source.Length / 2;
        }

        return (
            source[..splitIndex].Trim(),
            source[splitIndex..].Trim());
    }

    internal static (string FirstText, string SecondText) SplitRowTextAtIndex(string? text, int splitIndex)
    {
        string source = text ?? string.Empty;
        int normalizedIndex = Math.Clamp(splitIndex, 0, source.Length);
        return (
            source[..normalizedIndex].Trim(),
            source[normalizedIndex..].Trim());
    }

    internal static bool TryValidateSeparateRowTextSplit(string? text, int splitIndex, out string errorMessage)
    {
        string source = text ?? string.Empty;
        if (splitIndex <= 0 || splitIndex >= source.Length)
        {
            errorMessage = "Place the split cursor inside the text.";
            return false;
        }

        (string firstText, string secondText) = SplitRowTextAtIndex(source, splitIndex);
        if (string.IsNullOrWhiteSpace(firstText) || string.IsNullOrWhiteSpace(secondText))
        {
            errorMessage = "Both split text parts must contain visible text.";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    internal static int ResolveInitialSeparateSplitIndex(string? text)
    {
        string source = text ?? string.Empty;
        if (source.Length <= 1)
        {
            return Math.Clamp(source.Length / 2, 0, source.Length);
        }

        int midpoint = source.Length / 2;
        int nearestWhitespace = FindNearestWhitespaceToMidpoint(source);
        if (nearestWhitespace > 0 && nearestWhitespace < source.Length)
        {
            return nearestWhitespace;
        }

        return Math.Clamp(midpoint, 1, source.Length - 1);
    }

    internal static TimeSpan ResolveSeparateRowSplitOffsetFromTextBoundary(
        TimeSpan rowStartOffset,
        TimeSpan rowEndOffset,
        string originalText,
        int splitIndex)
    {
        double totalSeconds = Math.Max((rowEndOffset - rowStartOffset).TotalSeconds, 0d);
        if (totalSeconds <= 0d)
        {
            return rowStartOffset;
        }

        int safeTextLength = Math.Max(originalText.Length, 1);
        double ratio = Math.Clamp(splitIndex / (double)safeTextLength, 0d, 1d);
        TimeSpan candidate = rowStartOffset + TimeSpan.FromSeconds(totalSeconds * ratio);

        TimeSpan minHalf = TimeSpan.FromMilliseconds(500);
        TimeSpan minOffset = rowStartOffset + minHalf;
        TimeSpan maxOffset = rowEndOffset - minHalf;
        if (maxOffset < minOffset)
        {
            minOffset = rowStartOffset + TimeSpan.FromMilliseconds(100);
            maxOffset = rowEndOffset - TimeSpan.FromMilliseconds(100);
        }

        if (candidate < minOffset)
        {
            return minOffset;
        }

        if (candidate > maxOffset)
        {
            return maxOffset;
        }

        return candidate;
    }

    internal static TimeSpan ResolveInitialSeparateSplitOffset(TimeSpan startOffset, TimeSpan endOffset)
    {
        TimeSpan minOffset = startOffset + TimeSpan.FromSeconds(1);
        TimeSpan maxOffset = endOffset - TimeSpan.FromSeconds(1);
        if (maxOffset < minOffset)
        {
            return minOffset;
        }

        TimeSpan midpoint = startOffset + TimeSpan.FromSeconds(
            Math.Floor((endOffset - startOffset).TotalSeconds / 2d));
        if (midpoint < minOffset)
        {
            return minOffset;
        }

        if (midpoint > maxOffset)
        {
            return maxOffset;
        }

        return midpoint;
    }

    internal static bool TryValidateSeparateRowInput(
        TimeSpan rowStartOffset,
        TimeSpan rowEndOffset,
        TimeSpan splitOffset,
        string? firstText,
        string? secondText,
        out string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(firstText) || string.IsNullOrWhiteSpace(secondText))
        {
            errorMessage = "Both row texts are required.";
            return false;
        }

        if (splitOffset <= rowStartOffset || splitOffset >= rowEndOffset)
        {
            errorMessage = "Timeline split point must stay inside the original row range.";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    internal static FinalizedTranscriptLineViewModel CreateSecondSeparatedRow(
        FinalizedTranscriptLineViewModel sourceLine,
        TimeSpan splitOffset,
        TimeSpan rowEndOffset,
        string secondRowText)
    {
        ArgumentNullException.ThrowIfNull(sourceLine);

        bool hasSpeaker = !string.IsNullOrWhiteSpace(sourceLine.SpeakerLabel);
        return new FinalizedTranscriptLineViewModel(
            startOffset: splitOffset,
            endOffset: rowEndOffset,
            isTimestampEstimated: sourceLine.IsTimestampEstimated,
            text: secondRowText,
            speakerLabel: sourceLine.SpeakerLabel,
            isManuallyReviewed: true,
            speakerLabelSource: hasSpeaker ? sourceLine.SpeakerLabelSource : string.Empty,
            diarizationRevision: hasSpeaker ? sourceLine.DiarizationRevision : null,
            lastDiarizedChunkIndex: hasSpeaker ? sourceLine.LastDiarizedChunkIndex : null);
    }

    private static int FindLineFeedSplitIndex(string text)
    {
        int index = text.IndexOf('\n');
        return IsSplitIndexInMiddle(text, index) ? index : -1;
    }

    private static int FindPeriodSplitIndex(string text)
    {
        int index = text.IndexOf('.');
        return IsSplitIndexInMiddle(text, index) ? index : -1;
    }

    private static int FindPunctuationSplitIndex(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (char.IsPunctuation(text[index]) && IsSplitIndexInMiddle(text, index))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindNearestWhitespaceToMidpoint(string text)
    {
        int midpoint = text.Length / 2;
        int bestIndex = -1;
        int bestDistance = int.MaxValue;

        for (int index = 1; index < text.Length - 1; index++)
        {
            if (!char.IsWhiteSpace(text[index]))
            {
                continue;
            }

            int distance = Math.Abs(index - midpoint);
            if (distance < bestDistance)
            {
                bestIndex = index;
                bestDistance = distance;
            }
        }

        return bestIndex;
    }

    private static bool IsSplitIndexInMiddle(string text, int index)
    {
        return index > 0 && index < text.Length - 1;
    }

    private bool EnsureSegmentRowActionAvailable(MainViewModel vm, string actionTitle, bool showMessage = true)
    {
        if (IsTranscriptionInteractionLocked)
        {
            if (showMessage)
            {
                ShowCopyToast(
                    "Transcription in progress",
                    "Wait for active transcription to finish before editing transcript rows.",
                    ToastNotificationType.Info);
            }
            return false;
        }

        if (!vm.IsTranscribeAudioTranscriptViewSelected)
        {
            if (showMessage)
            {
                ShowCopyToast(
                    actionTitle,
                    "Switch to Transcribe Audio mode to edit timeline rows.",
                    ToastNotificationType.Info);
            }
            return false;
        }

        return true;
    }

    private void HandleRenameSpeaker(MainViewModel vm, FinalizedTranscriptLineViewModel currentLine)
    {
        if (!EnsureSegmentRowActionAvailable(vm, "Rename Speaker"))
        {
            return;
        }

        string fromSpeaker = currentLine.SpeakerLabel?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fromSpeaker))
        {
            ShowCopyToast("Rename Speaker", "The selected row does not have a speaker label.", ToastNotificationType.Warning);
            return;
        }

        var dialog = new RenameSpeakerWindow(fromSpeaker)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        int renamedRows;
        try
        {
            renamedRows = vm.RenameSpeakerAcrossTranscript(fromSpeaker, dialog.ToSpeaker);
        }
        catch (ArgumentException ex)
        {
            ShowCopyToast("Rename Speaker", ex.Message, ToastNotificationType.Warning);
            return;
        }

        if (renamedRows <= 0)
        {
            ShowCopyToast("Speaker unchanged", "No matching speaker labels were found.", ToastNotificationType.Warning);
            return;
        }

        ShowCopyToast("Speaker renamed", $"{renamedRows:N0} row(s) updated.", ToastNotificationType.Success);
    }

    private void HandleSeparateTranscriptRow(MainViewModel vm, FinalizedTranscriptLineViewModel currentLine)
    {
        if (!EnsureSegmentRowActionAvailable(vm, "Separate row"))
        {
            return;
        }

        if (!TryResolveSeparateRowRange(currentLine, out TimeSpan rowStartOffset, out TimeSpan rowEndOffset))
        {
            ShowCopyToast(
                "Separate row",
                "Cannot separate row because the timeline values are invalid.",
                ToastNotificationType.Warning);
            return;
        }

        if (rowEndOffset - rowStartOffset <= TimeSpan.FromSeconds(1))
        {
            ShowCopyToast(
                "Separate row",
                "Cannot separate a row with a 1-second timeline.",
                ToastNotificationType.Warning);
            return;
        }

        string originalText = currentLine.Text ?? string.Empty;
        int initialSplitIndex = ResolveInitialSeparateSplitIndex(originalText);
        var dialog = new SeparateRowWindow(
            originalText,
            initialSplitIndex)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!TryValidateSeparateRowTextSplit(
            originalText,
            dialog.SplitIndex,
            out string validationError))
        {
            ShowCopyToast("Separate row", validationError, ToastNotificationType.Warning);
            return;
        }

        int currentIndex = vm.FinalizedTranscriptLines.IndexOf(currentLine);
        if (currentIndex < 0)
        {
            ShowCopyToast("Separate row", "Unable to locate the selected row.", ToastNotificationType.Error);
            return;
        }

        TimeSpan? originalStart = currentLine.StartOffset;
        TimeSpan? originalEnd = currentLine.EndOffset;
        (string firstRowText, string secondRowText) = SplitRowTextAtIndex(originalText, dialog.SplitIndex);
        TimeSpan splitOffset = ResolveSeparateRowSplitOffsetFromTextBoundary(
            rowStartOffset,
            rowEndOffset,
            originalText,
            dialog.SplitIndex);

        FinalizedTranscriptLineViewModel secondRow = CreateSecondSeparatedRow(
            currentLine,
            splitOffset,
            rowEndOffset,
            secondRowText);

        currentLine.SetTimelineOffsets(rowStartOffset, splitOffset);
        currentLine.Text = firstRowText;
        currentLine.IsManuallyReviewed = true;

        if (!vm.InsertFinalizedTranscriptLine(currentIndex + 1, secondRow))
        {
            currentLine.SetTimelineOffsets(originalStart, originalEnd);
            currentLine.Text = originalText;
            ShowCopyToast("Separate row", "Unable to create the second row.", ToastNotificationType.Error);
            return;
        }

        ShowCopyToast("Row separated", "The selected row was separated into two rows.", ToastNotificationType.Success);
        _suppressNextTranscriptGridEditAfterSeparate = true;
        Dispatcher.BeginInvoke(new Action(() =>
            FocusGridCell(secondRow, TranscriptTextColumnIndex, beginEdit: false)), DispatcherPriority.Background);
    }

    private void HandleInsertTranscriptRow(
        MainViewModel vm,
        FinalizedTranscriptLineViewModel currentLine,
        bool insertBelow,
        bool duplicateText)
    {
        if (!EnsureSegmentRowActionAvailable(vm, "Row action unavailable"))
        {
            return;
        }

        try
        {
            List<FinalizedTranscriptLineViewModel> displayedLines = GetDisplayedTranscriptLines();
            int currentIndex = displayedLines.IndexOf(currentLine);

            if (currentIndex < 0 || !TryGetLineTimelineOffset(currentLine, out TimeSpan currentOffset))
            {
                ShowCopyToast(
                    "Row not added",
                    "Cannot add new row because of the timeline values.",
                    ToastNotificationType.Warning);
                return;
            }

            int neighborIndex = insertBelow ? currentIndex + 1 : currentIndex - 1;
            if (neighborIndex < 0 || neighborIndex >= displayedLines.Count)
            {
                ShowCopyToast(
                    "Row not added",
                    insertBelow
                        ? "Cannot add below the last row."
                        : "Cannot add above the first row.",
                    ToastNotificationType.Warning);
                return;
            }

            if (!TryGetLineTimelineOffset(displayedLines[neighborIndex], out TimeSpan neighborOffset))
            {
                ShowCopyToast(
                    "Row not added",
                    "Cannot add new row because of the timeline values.",
                    ToastNotificationType.Warning);
                return;
            }

            TimeSpan lowerOffset = insertBelow ? currentOffset : neighborOffset;
            TimeSpan upperOffset = insertBelow ? neighborOffset : currentOffset;
            if (upperOffset - lowerOffset <= TimeSpan.FromSeconds(1))
            {
                ShowCopyToast(
                    "Row not added",
                    "Cannot add new row because of the timeline values.",
                    ToastNotificationType.Warning);
                return;
            }

            int insertIndex = vm.FinalizedTranscriptLines.IndexOf(currentLine);
            if (insertIndex < 0)
            {
                ShowCopyToast(
                    "Row not added",
                    "Cannot add new row because of the timeline values.",
                    ToastNotificationType.Warning);
                return;
            }

            if (!insertBelow)
            {
                insertIndex -= 1;
            }

            TimeSpan newOffset = insertBelow
                ? currentOffset + TimeSpan.FromSeconds(1)
                : currentOffset - TimeSpan.FromSeconds(1);
            var newLine = new FinalizedTranscriptLineViewModel(
                startOffset: newOffset,
                endOffset: newOffset,
                isTimestampEstimated: true,
                text: duplicateText ? (currentLine.Text ?? string.Empty) : string.Empty);

            if (!vm.InsertFinalizedTranscriptLine(insertIndex + 1, newLine))
            {
                ShowCopyToast(
                    "Row not added",
                    "Unable to insert a new row right now.",
                    ToastNotificationType.Error);
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                FocusGridCell(
                    newLine,
                    TranscriptTextColumnIndex,
                    beginEdit: !duplicateText);
            }), DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            vm.LogHandledException("insert transcript row below", ex);
            ShowCopyToast(
                "Row not added",
                "Unable to insert a new row right now.",
                ToastNotificationType.Error);
        }
    }

    private void CombineToPreviousRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button
            || button.DataContext is not FinalizedTranscriptLineViewModel currentLine
            || DataContext is not MainViewModel vm)
        {
            return;
        }

        HandleCombineToPreviousRow(vm, currentLine);
    }

    private void HandleCombineToPreviousRow(MainViewModel vm, FinalizedTranscriptLineViewModel currentLine)
    {
        if (!EnsureSegmentRowActionAvailable(vm, "Row action unavailable"))
        {
            return;
        }

        if (IsTranscribeAudioBatchTranscribing || _isRowFileTranscriptionRunning)
        {
            ShowCopyToast(
                "Transcribe Audio in progress",
                "Wait for the current transcription process to finish.",
                ToastNotificationType.Info);
            return;
        }

        List<FinalizedTranscriptLineViewModel> displayedLines = GetDisplayedTranscriptLines();
        if (!TryResolveCombineToPreviousRowMerge(displayedLines, currentLine, out FinalizedTranscriptLineViewModel mergeTargetLine, out TimeSpan mergeEndOffset))
        {
            ShowCopyToast(
                "Row not combined",
                "The first row cannot be combined, or the neighboring timelines are invalid.",
                ToastNotificationType.Warning);
            return;
        }

        var dialog = new ConfirmationDialogWindow(
            title: "Combine this row into previous row?",
            message: "This transcript row will be removed, the row above will be extended to keep the timeline continuous, and the text will be combined.",
            confirmButtonText: "Yes",
            cancelButtonText: "No")
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            SetTranscriptRowActionsLine(null);

            if (!vm.RemoveFinalizedTranscriptLine(currentLine))
            {
                ShowCopyToast(
                    "Row not combined",
                    "Unable to remove the selected row.",
                    ToastNotificationType.Error);
                return;
            }

            mergeTargetLine.SetTimelineOffsets(mergeTargetLine.StartOffset, mergeEndOffset);
            MergeDeletedRowTextIntoPreviousRow(mergeTargetLine, currentLine);
            FocusGridCell(mergeTargetLine, TranscriptTextColumnIndex, beginEdit: false);
            ShowCopyToast(
                "Row combined",
                "The previous row timeline was extended and the text was combined.",
                ToastNotificationType.Success);

            Dispatcher.BeginInvoke(new Action(UpdateTranscriptRowActionsVisibility), DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            vm.LogHandledException("combine to previous row", ex);
            ShowCopyToast(
                "Row not combined",
                "Unable to remove the selected row.",
                ToastNotificationType.Error);
        }
    }

    internal static void MergeDeletedRowTextIntoPreviousRow(
        FinalizedTranscriptLineViewModel previousLine,
        FinalizedTranscriptLineViewModel deletedLine)
    {
        ArgumentNullException.ThrowIfNull(previousLine);
        ArgumentNullException.ThrowIfNull(deletedLine);

        previousLine.Text = BuildMergedDeletedRowText(previousLine.Text, deletedLine.Text);
        previousLine.IsManuallyReviewed = true;
    }

    internal static string BuildMergedDeletedRowText(string? previousText, string? deletedText)
    {
        string[] parts = [
            NormalizeMergedRowParagraphPart(previousText),
            NormalizeMergedRowParagraphPart(deletedText),
        ];

        return string.Join(
            " ",
            parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string NormalizeMergedRowParagraphPart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = value
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');

        return string.Join(
            " ",
            normalized
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    internal static bool CanCombineToPreviousRow(
        IEnumerable<FinalizedTranscriptLineViewModel> lines,
        FinalizedTranscriptLineViewModel? line)
    {
        if (line is null)
        {
            return false;
        }

        List<FinalizedTranscriptLineViewModel> displayedLines = lines.ToList();
        return displayedLines.IndexOf(line) > 0;
    }

    internal static bool CanMergeAdjacentRowsAroundSelectedRow(
        IReadOnlyList<FinalizedTranscriptLineViewModel> lines,
        FinalizedTranscriptLineViewModel selectedLine)
    {
        ArgumentNullException.ThrowIfNull(selectedLine);

        string normalizedSpeaker = selectedLine.SpeakerLabel?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedSpeaker))
        {
            return false;
        }

        int selectedIndex = -1;
        for (int index = 0; index < lines.Count; index++)
        {
            if (ReferenceEquals(lines[index], selectedLine))
            {
                selectedIndex = index;
                break;
            }
        }

        if (selectedIndex < 0)
        {
            return false;
        }

        bool sameAsPrevious = selectedIndex > 0
            && string.Equals(lines[selectedIndex - 1].SpeakerLabel?.Trim(), normalizedSpeaker, StringComparison.OrdinalIgnoreCase);
        bool sameAsNext = selectedIndex + 1 < lines.Count
            && string.Equals(lines[selectedIndex + 1].SpeakerLabel?.Trim(), normalizedSpeaker, StringComparison.OrdinalIgnoreCase);

        return sameAsPrevious || sameAsNext;
    }

    internal static bool TryResolveSelectedSpeakerMergeRange(
        IReadOnlyList<FinalizedTranscriptLineViewModel> lines,
        FinalizedTranscriptLineViewModel selectedLine,
        out int rangeStartIndex,
        out int rangeEndIndex)
    {
        rangeStartIndex = -1;
        rangeEndIndex = -1;

        if (!CanMergeAdjacentRowsAroundSelectedRow(lines, selectedLine))
        {
            return false;
        }

        string normalizedSpeaker = selectedLine.SpeakerLabel?.Trim() ?? string.Empty;
        int selectedIndex = -1;
        for (int index = 0; index < lines.Count; index++)
        {
            if (ReferenceEquals(lines[index], selectedLine))
            {
                selectedIndex = index;
                break;
            }
        }

        if (selectedIndex < 0)
        {
            return false;
        }

        rangeStartIndex = selectedIndex;
        while (rangeStartIndex > 0
            && string.Equals(lines[rangeStartIndex - 1].SpeakerLabel?.Trim(), normalizedSpeaker, StringComparison.OrdinalIgnoreCase))
        {
            rangeStartIndex--;
        }

        rangeEndIndex = selectedIndex;
        while (rangeEndIndex + 1 < lines.Count
            && string.Equals(lines[rangeEndIndex + 1].SpeakerLabel?.Trim(), normalizedSpeaker, StringComparison.OrdinalIgnoreCase))
        {
            rangeEndIndex++;
        }

        if (rangeEndIndex <= rangeStartIndex)
        {
            rangeStartIndex = -1;
            rangeEndIndex = -1;
            return false;
        }

        return true;
    }

    internal static bool CanMergeAnyAdjacentRowsBySpeaker(IReadOnlyList<FinalizedTranscriptLineViewModel> lines)
    {
        for (int index = 1; index < lines.Count; index++)
        {
            string previousSpeaker = lines[index - 1].SpeakerLabel?.Trim() ?? string.Empty;
            string currentSpeaker = lines[index].SpeakerLabel?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(previousSpeaker) || string.IsNullOrWhiteSpace(currentSpeaker))
            {
                continue;
            }

            if (string.Equals(previousSpeaker, currentSpeaker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool TryResolveCombineToPreviousRowMerge(
        IReadOnlyList<FinalizedTranscriptLineViewModel> displayedLines,
        FinalizedTranscriptLineViewModel currentLine,
        out FinalizedTranscriptLineViewModel mergeTargetLine,
        out TimeSpan mergeEndOffset)
    {
        mergeTargetLine = null!;
        mergeEndOffset = TimeSpan.Zero;

        int currentIndex = -1;
        for (int index = 0; index < displayedLines.Count; index++)
        {
            if (ReferenceEquals(displayedLines[index], currentLine))
            {
                currentIndex = index;
                break;
            }
        }

        if (currentIndex <= 0)
        {
            return false;
        }

        mergeTargetLine = displayedLines[currentIndex - 1];
        if (mergeTargetLine.StartOffset is not TimeSpan mergeStartOffset)
        {
            return false;
        }

        if (currentIndex + 1 < displayedLines.Count)
        {
            FinalizedTranscriptLineViewModel nextLine = displayedLines[currentIndex + 1];
            if (nextLine.StartOffset is not TimeSpan nextStartOffset || nextStartOffset <= mergeStartOffset)
            {
                return false;
            }

            mergeEndOffset = nextStartOffset;
            return true;
        }

        if (currentLine.EndOffset is not TimeSpan deletedEndOffset || deletedEndOffset <= mergeStartOffset)
        {
            return false;
        }

        mergeEndOffset = deletedEndOffset;
        return true;
    }

    private void HandleMergeAdjacentRowsForSelectedSpeaker(
        MainViewModel vm,
        FinalizedTranscriptLineViewModel currentLine)
    {
        if (!EnsureSegmentRowActionAvailable(vm, "Merge selected speaker rows"))
        {
            return;
        }

        if (IsTranscribeAudioBatchTranscribing || _isRowFileTranscriptionRunning)
        {
            ShowCopyToast(
                "Transcribe Audio in progress",
                "Wait for the current transcription process to finish.",
                ToastNotificationType.Info);
            return;
        }

        string selectedSpeaker = currentLine.SpeakerLabel?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(selectedSpeaker))
        {
            ShowCopyToast(
                "Merge selected speaker rows",
                "The selected row does not have a speaker label.",
                ToastNotificationType.Warning);
            return;
        }

        List<FinalizedTranscriptLineViewModel> displayedLines = GetDisplayedTranscriptLines();
        if (!TryResolveSelectedSpeakerMergeRange(displayedLines, currentLine, out int rangeStartIndex, out int rangeEndIndex))
        {
            ShowCopyToast(
                "No adjacent rows merged",
                "Only rows adjacent to the selected row can be merged for this speaker.",
                ToastNotificationType.Info);
            return;
        }

        var dialog = new ConfirmationDialogWindow(
            title: "Merge adjacent rows for selected speaker?",
            message: "Adjacent rows with this speaker label will be merged. Timeline ranges will be checked and fixed if needed.",
            confirmButtonText: "Yes",
            cancelButtonText: "No")
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            SetTranscriptRowActionsLine(null);
            int mergedRows = MergeAdjacentRowsInRange(vm, displayedLines, rangeStartIndex, rangeEndIndex);
            int mergedRowIndex = rangeStartIndex;
            int fixedTimelineRows = NormalizeMergedRowTimelineNeighborhood(GetDisplayedTranscriptLines(), mergedRowIndex);

            if (mergedRows <= 0)
            {
                ShowCopyToast(
                    "No adjacent rows merged",
                    "Only rows adjacent to the selected row can be merged for this speaker.",
                    ToastNotificationType.Info);
                return;
            }

            ShowCopyToast(
                "Speaker rows merged",
                fixedTimelineRows > 0
                    ? $"{mergedRows:N0} row(s) merged. Timeline fixed for {fixedTimelineRows:N0} row(s)."
                    : $"{mergedRows:N0} row(s) merged.",
                ToastNotificationType.Success);

            Dispatcher.BeginInvoke(new Action(UpdateTranscriptRowActionsVisibility), DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            vm.LogHandledException("merge adjacent rows for selected speaker", ex);
            ShowCopyToast(
                "Merge failed",
                "Unable to merge adjacent rows for the selected speaker right now.",
                ToastNotificationType.Error);
        }
    }

    private void HandleMergeAllAdjacentRowsBySpeaker(
        MainViewModel vm,
        FinalizedTranscriptLineViewModel currentLine)
    {
        if (!EnsureSegmentRowActionAvailable(vm, "Merge all adjacent rows by speaker"))
        {
            return;
        }

        if (IsTranscribeAudioBatchTranscribing || _isRowFileTranscriptionRunning)
        {
            ShowCopyToast(
                "Transcribe Audio in progress",
                "Wait for the current transcription process to finish.",
                ToastNotificationType.Info);
            return;
        }

        if (string.IsNullOrWhiteSpace(currentLine.SpeakerLabel))
        {
            ShowCopyToast(
                "Merge all adjacent rows by speaker",
                "The selected row does not have a speaker label.",
                ToastNotificationType.Warning);
            return;
        }

        List<FinalizedTranscriptLineViewModel> displayedLines = GetDisplayedTranscriptLines();
        if (!CanMergeAnyAdjacentRowsBySpeaker(displayedLines))
        {
            ShowCopyToast(
                "No adjacent rows merged",
                "No adjacent speaker rows were found to merge.",
                ToastNotificationType.Info);
            return;
        }

        var dialog = new ConfirmationDialogWindow(
            title: "Merge all adjacent rows by speaker?",
            message: "Rows will be scanned from top to bottom and adjacent rows with the same speaker will be merged until none remain.",
            confirmButtonText: "Yes",
            cancelButtonText: "No")
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            SetTranscriptRowActionsLine(null);

            int mergedRows = MergeAllAdjacentRowsBySpeaker(vm, displayedLines);
            int fixedTimelineRows = NormalizeTranscriptTimelineRanges(GetDisplayedTranscriptLines());

            if (mergedRows <= 0)
            {
                ShowCopyToast(
                    "No adjacent rows merged",
                    "No adjacent speaker rows were found to merge.",
                    ToastNotificationType.Info);
                return;
            }

            ShowCopyToast(
                "Speaker rows merged",
                fixedTimelineRows > 0
                    ? $"{mergedRows:N0} row(s) merged. Timeline fixed for {fixedTimelineRows:N0} row(s)."
                    : $"{mergedRows:N0} row(s) merged.",
                ToastNotificationType.Success);

            Dispatcher.BeginInvoke(new Action(UpdateTranscriptRowActionsVisibility), DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            vm.LogHandledException("merge all adjacent rows by speaker", ex);
            ShowCopyToast(
                "Merge failed",
                "Unable to merge all adjacent rows by speaker right now.",
                ToastNotificationType.Error);
        }
    }

    private static int MergeAdjacentRowsInRange(
        MainViewModel vm,
        List<FinalizedTranscriptLineViewModel> displayedLines,
        int rangeStartIndex,
        int rangeEndIndex)
    {
        if (rangeStartIndex < 0
            || rangeEndIndex >= displayedLines.Count
            || rangeEndIndex <= rangeStartIndex)
        {
            return 0;
        }

        FinalizedTranscriptLineViewModel mergedLine = displayedLines[rangeStartIndex];
        int removedRows = 0;

        for (int index = rangeStartIndex + 1; index <= rangeEndIndex; index++)
        {
            FinalizedTranscriptLineViewModel lineToMerge = displayedLines[index];
            TimeSpan? mergedEndOffset = lineToMerge.EndOffset ?? lineToMerge.StartOffset ?? mergedLine.EndOffset;
            if (mergedLine.StartOffset is TimeSpan mergedStart
                && mergedEndOffset is TimeSpan mergedEnd
                && mergedEnd < mergedStart)
            {
                mergedEndOffset = mergedStart;
            }

            mergedLine.SetTimelineOffsets(mergedLine.StartOffset, mergedEndOffset);
            MergeDeletedRowTextIntoPreviousRow(mergedLine, lineToMerge);

            if (!vm.RemoveFinalizedTranscriptLine(lineToMerge))
            {
                throw new InvalidOperationException("Unable to remove an adjacent row while merging selected speaker rows.");
            }

            removedRows++;
        }

        displayedLines.RemoveRange(rangeStartIndex + 1, removedRows);
        return removedRows;
    }

    private static int MergeAllAdjacentRowsBySpeaker(
        MainViewModel vm,
        List<FinalizedTranscriptLineViewModel> displayedLines)
    {
        int mergedRows = 0;
        int index = 1;

        while (index < displayedLines.Count)
        {
            FinalizedTranscriptLineViewModel previousLine = displayedLines[index - 1];
            FinalizedTranscriptLineViewModel currentLine = displayedLines[index];
            string previousSpeaker = previousLine.SpeakerLabel?.Trim() ?? string.Empty;
            string currentSpeaker = currentLine.SpeakerLabel?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(previousSpeaker)
                || string.IsNullOrWhiteSpace(currentSpeaker)
                || !string.Equals(previousSpeaker, currentSpeaker, StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            TimeSpan? mergedEndOffset = currentLine.EndOffset ?? currentLine.StartOffset ?? previousLine.EndOffset;
            if (previousLine.StartOffset is TimeSpan mergedStart
                && mergedEndOffset is TimeSpan mergedEnd
                && mergedEnd < mergedStart)
            {
                mergedEndOffset = mergedStart;
            }

            previousLine.SetTimelineOffsets(previousLine.StartOffset, mergedEndOffset);
            MergeDeletedRowTextIntoPreviousRow(previousLine, currentLine);

            if (!vm.RemoveFinalizedTranscriptLine(currentLine))
            {
                throw new InvalidOperationException("Unable to remove an adjacent row while merging all adjacent speaker rows.");
            }

            displayedLines.RemoveAt(index);
            mergedRows++;
        }

        return mergedRows;
    }

    internal static int NormalizeTranscriptTimelineRanges(IReadOnlyList<FinalizedTranscriptLineViewModel> displayedLines)
    {
        int fixedRows = 0;

        for (int index = 0; index < displayedLines.Count; index++)
        {
            FinalizedTranscriptLineViewModel currentLine = displayedLines[index];
            TimeSpan? currentStart = currentLine.StartOffset;
            TimeSpan? currentEnd = currentLine.EndOffset;

            if (currentStart is TimeSpan resolvedStart
                && currentEnd is TimeSpan resolvedEnd
                && resolvedEnd < resolvedStart)
            {
                currentLine.SetTimelineOffsets(currentStart, resolvedStart);
                currentEnd = resolvedStart;
                fixedRows++;
            }

            if (index + 1 < displayedLines.Count
                && displayedLines[index + 1].StartOffset is TimeSpan nextStart)
            {
                TimeSpan clampedNextStart = currentStart is TimeSpan start && nextStart < start
                    ? start
                    : nextStart;

                if (currentEnd != clampedNextStart)
                {
                    currentLine.SetTimelineOffsets(currentStart, clampedNextStart);
                    fixedRows++;
                }
            }
        }

        return fixedRows;
    }

    internal static int NormalizeMergedRowTimelineNeighborhood(
        IReadOnlyList<FinalizedTranscriptLineViewModel> displayedLines,
        int mergedRowIndex)
    {
        int fixedRows = 0;
        if (mergedRowIndex < 0 || mergedRowIndex >= displayedLines.Count)
        {
            return 0;
        }

        FinalizedTranscriptLineViewModel mergedLine = displayedLines[mergedRowIndex];
        TimeSpan? mergedStart = mergedLine.StartOffset;
        TimeSpan? mergedEnd = mergedLine.EndOffset;

        if (mergedStart is TimeSpan resolvedMergedStart
            && mergedEnd is TimeSpan resolvedMergedEnd
            && resolvedMergedEnd < resolvedMergedStart)
        {
            mergedLine.SetTimelineOffsets(mergedStart, resolvedMergedStart);
            mergedEnd = resolvedMergedStart;
            fixedRows++;
        }

        if (mergedRowIndex + 1 < displayedLines.Count
            && displayedLines[mergedRowIndex + 1].StartOffset is TimeSpan nextStart)
        {
            TimeSpan clampedNextStart = mergedStart is TimeSpan resolvedStart && nextStart < resolvedStart
                ? resolvedStart
                : nextStart;

            if (mergedEnd != clampedNextStart)
            {
                mergedLine.SetTimelineOffsets(mergedStart, clampedNextStart);
                fixedRows++;
            }
        }

        return fixedRows;
    }

    private void FinalizedTranscriptGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (_suppressNextTranscriptGridEditAfterSeparate)
        {
            _suppressNextTranscriptGridEditAfterSeparate = false;
            ClearTranscriptTextEditState();
            e.Cancel = true;
            return;
        }

        if (IsTranscriptionInteractionLocked)
        {
            ClearTranscriptTextEditState();
            e.Cancel = true;
            return;
        }

        if (DataContext is not MainViewModel vm
            || e.Row?.Item is not FinalizedTranscriptLineViewModel line)
        {
            ClearTranscriptTextEditState();
            ClearTranscriptEditPlaybackLoop();
            return;
        }

        if (e.Column?.IsReadOnly == true)
        {
            ClearTranscriptTextEditState();
            e.Cancel = true;
            return;
        }

        if (line.IsPlaybackEditTranscribing)
        {
            ClearTranscriptTextEditState();
            e.Cancel = true;
            Dispatcher.BeginInvoke(new Action(UpdateTranscriptRowActionsVisibility), DispatcherPriority.Background);
            return;
        }

        if (IsTimelineColumn(e.Column))
        {
            ClearTranscriptTextEditState();
            e.Cancel = true;
            return;
        }

        bool isTranscriptTextColumn = IsTranscriptTextColumn(e.Column);
        bool isAuxiliaryEditableColumn = !isTranscriptTextColumn && !IsTimelineColumn(e.Column);

        if (!vm.IsTranscribeAudioTranscriptViewSelected)
        {
            if (!isTranscriptTextColumn)
            {
                ClearTranscriptTextEditState();
                CaptureAuxiliaryCellOriginalValue(e.Column, line);
                BeginNonTranscriptCellEdit(vm);
                return;
            }

            _transcriptTextEditLine = line;
            _transcriptTextEditOriginalText = line.Text ?? string.Empty;

            SyncPlaybackForTranscriptEdit(vm, line, "speaker transcript edit playback sync");
            return;
        }

        if (isAuxiliaryEditableColumn)
        {
            ClearTranscriptTextEditState();
            CaptureAuxiliaryCellOriginalValue(e.Column, line);
            BeginNonTranscriptCellEdit(vm);
            return;
        }

        if (string.IsNullOrWhiteSpace(line.Text)
            && TryStartPlaybackEditTranscription(vm, line))
        {
            ClearTranscriptTextEditState();
            e.Cancel = true;
            Dispatcher.BeginInvoke(new Action(UpdateTranscriptRowActionsVisibility), DispatcherPriority.Background);
            return;
        }

        _transcriptTextEditLine = line;
        _transcriptTextEditOriginalText = line.Text ?? string.Empty;

        SyncPlaybackForTranscriptEdit(vm, line, "transcript edit playback sync");
    }

    private void FinalizedTranscriptGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (!IsTranscriptTextColumn(e.Column))
        {
            HandleNonTranscriptCellEditEnding(e);
            return;
        }

        if (DataContext is not MainViewModel vm)
        {
            ClearTranscriptTextEditState();
            ClearTranscriptEditPlaybackLoop();
            Dispatcher.BeginInvoke(new Action(UpdateTranscriptRowActionsVisibility), DispatcherPriority.Background);
            return;
        }

        if (e.Row?.Item is FinalizedTranscriptLineViewModel line
            && ReferenceEquals(line, _transcriptTextEditLine))
        {
            if (e.EditAction == DataGridEditAction.Commit
                && !string.Equals(line.Text, _transcriptTextEditOriginalText, StringComparison.Ordinal))
            {
                line.IsManuallyReviewed = true;
            }

            ClearTranscriptTextEditState();
        }

        if (!vm.IsTranscribeAudioTranscriptViewSelected)
        {
            if (e.Row?.Item is FinalizedTranscriptLineViewModel speakerEditLoopLine
                && ReferenceEquals(speakerEditLoopLine, _editLoopLine))
            {
                PausePlaybackAfterTranscriptEdit(vm);
                return;
            }

            Dispatcher.BeginInvoke(new Action(UpdateTranscriptRowActionsVisibility), DispatcherPriority.Background);
            return;
        }

        if (e.Row?.Item is FinalizedTranscriptLineViewModel editLoopLine
            && ReferenceEquals(editLoopLine, _editLoopLine))
        {
            PausePlaybackAfterTranscriptEdit(vm);
            return;
        }

        if (e.EditAction is DataGridEditAction.Cancel or DataGridEditAction.Commit)
        {
            PausePlaybackAfterTranscriptEdit(vm);
        }

        Dispatcher.BeginInvoke(new Action(UpdateTranscriptRowActionsVisibility), DispatcherPriority.Background);
    }

    private void FinalizedTranscriptGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (FinalizedTranscriptGrid.Items.Count == 0)
        {
            return;
        }

        if (TryGetActiveTranscriptCellEditor(e, out System.Windows.Controls.TextBox activeEditor)
            && ShouldLetTranscriptCellEditorHandleKey(
                ResolveKey(e),
                Keyboard.Modifiers,
                activeEditor.AcceptsReturn))
        {
            return;
        }

        if (DataContext is MainViewModel vm
            && IsManualSegmentKeyboardFlowEnabled(vm)
            && HandleManualSegmentCommandShortcuts(vm, e))
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (IsCurrentGridCellEditing())
            {
                RestoreCurrentCellOriginalValue();
                FinalizedTranscriptGrid.CancelEdit(DataGridEditingUnit.Cell);
                FinalizedTranscriptGrid.CancelEdit(DataGridEditingUnit.Row);
            }

            return;
        }

        if (e.Key == Key.F2)
        {
            e.Handled = true;
            if (!IsCurrentGridCellEditing())
            {
                EnsureCurrentGridCellFocused(beginEdit: true);
            }

            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;

            if (!IsCurrentGridCellEditing())
            {
                EnsureCurrentGridCellFocused(beginEdit: true);
                return;
            }

            FinalizedTranscriptGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            FinalizedTranscriptGrid.CommitEdit(DataGridEditingUnit.Row, true);

            int activeColumnIndex = GetActiveColumnIndex(defaultColumnIndex: TranscriptTextColumnIndex);
            if (activeColumnIndex == TranscriptTextColumnIndex)
            {
                MoveCurrentGridCellFocusByRow(
                    delta: 1,
                    preferredColumnIndex: TranscriptTextColumnIndex,
                    beginEdit: false);
                return;
            }

            MoveCurrentGridCellFocusByColumn(delta: 1, beginEdit: false);
            return;
        }

        if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
        {
            e.Handled = true;
            FinalizedTranscriptGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            FinalizedTranscriptGrid.CommitEdit(DataGridEditingUnit.Row, true);

            if (e.Key == Key.Up)
            {
                MoveCurrentGridCellFocusByRow(delta: -1, beginEdit: false);
                return;
            }

            if (e.Key == Key.Down)
            {
                MoveCurrentGridCellFocusByRow(delta: 1, beginEdit: false);
                return;
            }

            MoveCurrentGridCellFocusByColumn(
                delta: e.Key == Key.Left ? -1 : 1,
                beginEdit: false);
        }
    }

    private bool TryGetActiveTranscriptCellEditor(
        System.Windows.Input.KeyEventArgs e,
        out System.Windows.Controls.TextBox textBox)
    {
        textBox = null!;

        if (e.OriginalSource is not DependencyObject source)
        {
            return false;
        }

        textBox = FindAncestor<System.Windows.Controls.TextBox>(source)!;
        if (textBox is null || !textBox.IsKeyboardFocusWithin)
        {
            return false;
        }

        DataGridCell? cell = FindAncestor<DataGridCell>(textBox);
        if (cell?.IsEditing != true)
        {
            return false;
        }

        return ReferenceEquals(FindAncestor<System.Windows.Controls.DataGrid>(cell), FinalizedTranscriptGrid);
    }

    private static Key ResolveKey(System.Windows.Input.KeyEventArgs e)
    {
        return e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };
    }

    internal static bool ShouldLetTranscriptCellEditorHandleKey(
        Key key,
        ModifierKeys modifiers,
        bool acceptsReturn)
    {
        if (key is Key.Left
            or Key.Right
            or Key.Up
            or Key.Down
            or Key.Home
            or Key.End
            or Key.PageUp
            or Key.PageDown
            or Key.Back
            or Key.Delete)
        {
            return true;
        }

        if (key == Key.Enter)
        {
            return acceptsReturn
                && modifiers.HasFlag(ModifierKeys.Shift)
                && !modifiers.HasFlag(ModifierKeys.Control);
        }

        if (modifiers.HasFlag(ModifierKeys.Control)
            && key is Key.A
                or Key.C
                or Key.X
                or Key.V
                or Key.Z
                or Key.Y
                or Key.I
                or Key.D)
        {
            return true;
        }

        return false;
    }

    private bool HandleManualSegmentCommandShortcuts(MainViewModel vm, System.Windows.Input.KeyEventArgs e)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        bool isCtrlPressed = modifiers.HasFlag(ModifierKeys.Control);
        bool isShiftPressed = modifiers.HasFlag(ModifierKeys.Shift);

        if (isCtrlPressed && e.Key == Key.I)
        {
            if (TryGetCurrentSegmentLine(vm, out FinalizedTranscriptLineViewModel currentLine))
            {
                e.Handled = true;
                HandleInsertTranscriptRow(
                    vm,
                    currentLine,
                    insertBelow: !isShiftPressed,
                    duplicateText: false);
            }

            return e.Handled;
        }

        if (isCtrlPressed && e.Key == Key.D)
        {
            if (TryGetCurrentSegmentLine(vm, out FinalizedTranscriptLineViewModel currentLine))
            {
                e.Handled = true;
                HandleInsertTranscriptRow(
                    vm,
                    currentLine,
                    insertBelow: true,
                    duplicateText: true);
            }

            return e.Handled;
        }

        if (isCtrlPressed && e.Key == Key.Delete)
        {
            if (TryGetCurrentSegmentLine(vm, out FinalizedTranscriptLineViewModel currentLine))
            {
                e.Handled = true;
                HandleCombineToPreviousRow(vm, currentLine);
            }

            return e.Handled;
        }

        return false;
    }

    private void MoveCurrentGridCellFocusByRow(int delta, int? preferredColumnIndex = null, bool beginEdit = false)
    {
        IList<object> rowItems = GetTranscriptRowItems();
        if (rowItems.Count == 0)
        {
            return;
        }

        object? currentItem = FinalizedTranscriptGrid.CurrentCell.Item;
        if (!IsDataItem(currentItem))
        {
            currentItem = FinalizedTranscriptGrid.CurrentItem ?? FinalizedTranscriptGrid.SelectedItem;
        }

        int currentIndex = currentItem is null ? 0 : rowItems.IndexOf(currentItem);

        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        int targetIndex = Math.Min(
            Math.Max(currentIndex + delta, 0),
            rowItems.Count - 1);

        object targetItem = rowItems[targetIndex];
        FocusGridCell(
            targetItem,
            preferredColumnIndex ?? GetActiveColumnIndex(defaultColumnIndex: TranscriptTextColumnIndex),
            beginEdit);
    }

    private void EnsureCurrentGridCellFocused(bool beginEdit)
    {
        if (!FinalizedTranscriptGrid.IsKeyboardFocusWithin)
        {
            return;
        }

        IList<object> rowItems = GetTranscriptRowItems();
        if (rowItems.Count == 0)
        {
            return;
        }

        object? targetItem = FinalizedTranscriptGrid.CurrentCell.Item;

        if (!IsDataItem(targetItem))
        {
            targetItem = FinalizedTranscriptGrid.CurrentItem ?? FinalizedTranscriptGrid.SelectedItem;
        }

        if (!IsDataItem(targetItem) || !rowItems.Contains(targetItem))
        {
            targetItem = rowItems[0];
        }

        FocusGridCell(
            targetItem,
            GetActiveColumnIndex(defaultColumnIndex: TranscriptTextColumnIndex),
            beginEdit);
    }

    private void FocusGridCell(object targetItem, int columnIndex, bool beginEdit)
    {
        if (columnIndex < 0 || columnIndex >= FinalizedTranscriptGrid.Columns.Count)
        {
            return;
        }

        try
        {
            DataGridColumn targetColumn = FinalizedTranscriptGrid.Columns[columnIndex];
            var cellInfo = new DataGridCellInfo(targetItem, targetColumn);

            if (!cellInfo.IsValid)
            {
                return;
            }

            FinalizedTranscriptGrid.SelectedCells.Clear();
            FinalizedTranscriptGrid.CurrentCell = cellInfo;
            FinalizedTranscriptGrid.SelectedCells.Add(cellInfo);
            FinalizedTranscriptGrid.ScrollIntoView(targetItem, targetColumn);
            FinalizedTranscriptGrid.UpdateLayout();

            DataGridCell? targetCell = TryGetDataCell(targetItem, columnIndex);
            if (targetCell is not null)
            {
                targetCell.Focus();
            }
            else
            {
                FinalizedTranscriptGrid.Focus();
            }

            if (beginEdit)
            {
                FinalizedTranscriptGrid.BeginEdit();
                Dispatcher.BeginInvoke(
                    new Action(() => FocusCellEditor(targetItem, columnIndex)),
                    DispatcherPriority.Input);
            }
        }
        catch (Exception ex)
        {
            _boundViewModel?.LogHandledException("transcript focus correction", ex);
        }
    }

    private void ScrollTranscriptRowIntoView(object targetItem, int columnIndex)
    {
        if (columnIndex < 0 || columnIndex >= FinalizedTranscriptGrid.Columns.Count)
        {
            return;
        }

        try
        {
            DataGridColumn targetColumn = FinalizedTranscriptGrid.Columns[columnIndex];
            FinalizedTranscriptGrid.ScrollIntoView(targetItem, targetColumn);
            FinalizedTranscriptGrid.UpdateLayout();
        }
        catch (Exception ex)
        {
            _boundViewModel?.LogHandledException("Transcribe Audio row scroll", ex);
        }
    }

    private bool IsCurrentGridCellEditing()
    {
        DataGridCellInfo current = FinalizedTranscriptGrid.CurrentCell;

        if (current.Column is null || current.Item is null)
        {
            return false;
        }

        int columnIndex = FinalizedTranscriptGrid.Columns.IndexOf(current.Column);
        DataGridCell? cell = TryGetDataCell(current.Item, columnIndex);
        return cell?.IsEditing ?? false;
    }

    private int GetActiveColumnIndex(int defaultColumnIndex)
    {
        DataGridColumn? currentColumn = FinalizedTranscriptGrid.CurrentCell.Column;
        int currentColumnIndex = currentColumn is null
            ? -1
            : FinalizedTranscriptGrid.Columns.IndexOf(currentColumn);

        if (currentColumnIndex >= 0
            && currentColumnIndex < FinalizedTranscriptGrid.Columns.Count
            && currentColumn?.Visibility == Visibility.Visible)
        {
            return currentColumnIndex;
        }

        if (defaultColumnIndex >= 0
            && defaultColumnIndex < FinalizedTranscriptGrid.Columns.Count
            && FinalizedTranscriptGrid.Columns[defaultColumnIndex].Visibility == Visibility.Visible)
        {
            return defaultColumnIndex;
        }

        for (int index = 0; index < FinalizedTranscriptGrid.Columns.Count; index++)
        {
            if (FinalizedTranscriptGrid.Columns[index].Visibility == Visibility.Visible)
            {
                return index;
            }
        }

        return 0;
    }

    private void FocusCurrentGridRowColumn(int targetColumnIndex, bool beginEdit)
    {
        DataGridCellInfo currentCell = FinalizedTranscriptGrid.CurrentCell;
        object? currentItem = currentCell.Item;

        if (!IsDataItem(currentItem))
        {
            currentItem = FinalizedTranscriptGrid.CurrentItem ?? FinalizedTranscriptGrid.SelectedItem;
        }

        if (!IsDataItem(currentItem))
        {
            IList<object> rowItems = GetTranscriptRowItems();
            if (rowItems.Count == 0)
            {
                return;
            }

            currentItem = rowItems[0];
        }

        FocusGridCell(
            currentItem!,
            targetColumnIndex,
            beginEdit);
    }

    private void MoveCurrentGridCellFocusByColumn(int delta, bool beginEdit)
    {
        if (delta == 0)
        {
            return;
        }

        if (!TryGetCurrentGridItem(out object currentItem))
        {
            return;
        }

        List<int> visibleColumnIndexes = GetVisibleColumnIndexes();
        if (visibleColumnIndexes.Count == 0)
        {
            return;
        }

        int activeColumnIndex = GetActiveColumnIndex(defaultColumnIndex: TranscriptTextColumnIndex);
        int visiblePosition = visibleColumnIndexes.IndexOf(activeColumnIndex);
        if (visiblePosition < 0)
        {
            visiblePosition = 0;
        }

        int targetPosition = Math.Min(
            Math.Max(visiblePosition + delta, 0),
            visibleColumnIndexes.Count - 1);

        FocusGridCell(
            currentItem,
            visibleColumnIndexes[targetPosition],
            beginEdit);
    }

    private bool TryGetCurrentGridItem(out object item)
    {
        item = null!;

        object? currentItem = FinalizedTranscriptGrid.CurrentCell.Item;
        if (!IsDataItem(currentItem))
        {
            currentItem = FinalizedTranscriptGrid.CurrentItem ?? FinalizedTranscriptGrid.SelectedItem;
        }

        if (IsDataItem(currentItem))
        {
            item = currentItem!;
            return true;
        }

        IList<object> rowItems = GetTranscriptRowItems();
        if (rowItems.Count == 0)
        {
            return false;
        }

        item = rowItems[0];
        return true;
    }

    private List<int> GetVisibleColumnIndexes()
    {
        var indexes = new List<int>();

        for (int index = 0; index < FinalizedTranscriptGrid.Columns.Count; index++)
        {
            if (FinalizedTranscriptGrid.Columns[index].Visibility == Visibility.Visible)
            {
                indexes.Add(index);
            }
        }

        return indexes;
    }

    private bool IsManualSegmentKeyboardFlowEnabled(MainViewModel vm)
    {
        return !IsTranscriptionInteractionLocked
            && vm.IsTranscribeAudioTranscriptViewSelected;
    }

    private bool TryGetCurrentSegmentLine(MainViewModel vm, out FinalizedTranscriptLineViewModel line)
    {
        line = null!;

        if (!vm.IsTranscribeAudioTranscriptViewSelected)
        {
            return false;
        }

        object? currentItem = FinalizedTranscriptGrid.CurrentCell.Item;
        if (currentItem is FinalizedTranscriptLineViewModel currentLine)
        {
            line = currentLine;
            return true;
        }

        if (FinalizedTranscriptGrid.CurrentItem is FinalizedTranscriptLineViewModel currentItemLine)
        {
            line = currentItemLine;
            return true;
        }

        if (FinalizedTranscriptGrid.SelectedItem is FinalizedTranscriptLineViewModel selectedItemLine)
        {
            line = selectedItemLine;
            return true;
        }

        IList<object> rowItems = GetTranscriptRowItems();
        line = rowItems.OfType<FinalizedTranscriptLineViewModel>().FirstOrDefault()!;
        return line is not null;
    }

    private System.Windows.Controls.DataGridCell? TryGetDataCell(object item, int columnIndex)
    {
        if (columnIndex < 0 || columnIndex >= FinalizedTranscriptGrid.Columns.Count)
        {
            return null;
        }

        DataGridColumn targetColumn = FinalizedTranscriptGrid.Columns[columnIndex];
        DataGridRow? row = FinalizedTranscriptGrid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
        if (row is null)
        {
            FinalizedTranscriptGrid.ScrollIntoView(item, targetColumn);
            FinalizedTranscriptGrid.UpdateLayout();
            row = FinalizedTranscriptGrid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
        }

        if (row is null)
        {
            return null;
        }

        DataGridCellsPresenter? presenter = FindVisualChild<DataGridCellsPresenter>(row);
        if (presenter is null)
        {
            row.ApplyTemplate();
            presenter = FindVisualChild<DataGridCellsPresenter>(row);
        }

        if (presenter is null)
        {
            return null;
        }

        System.Windows.Controls.DataGridCell? cell =
            presenter.ItemContainerGenerator.ContainerFromIndex(columnIndex) as System.Windows.Controls.DataGridCell;
        if (cell is null)
        {
            FinalizedTranscriptGrid.ScrollIntoView(item, targetColumn);
            FinalizedTranscriptGrid.UpdateLayout();
            cell =
                presenter.ItemContainerGenerator.ContainerFromIndex(columnIndex) as System.Windows.Controls.DataGridCell;
        }

        return cell;
    }

    private void FocusCellEditor(object item, int columnIndex)
    {
        try
        {
            DataGridCell? cell = TryGetDataCell(item, columnIndex);
            if (cell is null)
            {
                return;
            }

            if (FindVisualChild<System.Windows.Controls.TextBox>(cell) is not System.Windows.Controls.TextBox textBox)
            {
                return;
            }

            if (!textBox.IsKeyboardFocusWithin)
            {
                textBox.Focus();
            }

            textBox.CaretIndex = textBox.Text?.Length ?? 0;
            textBox.SelectionLength = 0;
        }
        catch (Exception ex)
        {
            _boundViewModel?.LogHandledException("cell editor focus", ex);
        }
    }

    private bool IsTimelineColumn(DataGridColumn? column)
    {
        return column is not null
            && FinalizedTranscriptGrid.Columns.IndexOf(column) == TimelineColumnIndex;
    }

    private bool IsTranscriptTextColumn(DataGridColumn? column)
    {
        return column is not null
            && FinalizedTranscriptGrid.Columns.IndexOf(column) == TranscriptTextColumnIndex;
    }

    private bool IsSpeakerColumn(DataGridColumn? column)
    {
        return column is not null
            && FinalizedTranscriptGrid.Columns.IndexOf(column) == SpeakerColumnIndex;
    }

    private TranscriptContextMenuScope ResolveTranscriptContextMenuScope(DataGridColumn? column)
    {
        if (IsSpeakerColumn(column))
        {
            return TranscriptContextMenuScope.SpeakerCell;
        }

        if (IsTranscriptTextColumn(column))
        {
            return TranscriptContextMenuScope.TextCell;
        }

        return TranscriptContextMenuScope.OtherCell;
    }

    internal static Visibility ResolveTranscriptContextMenuItemVisibility(
        string header,
        bool isSpeakerCellMenu,
        bool isTextCellMenu,
        bool canRenameSpeaker)
    {
        return header switch
        {
            "Rename Speaker…" => isSpeakerCellMenu && canRenameSpeaker
                ? Visibility.Visible
                : Visibility.Collapsed,
            "Merge Adjacent Rows for This Speaker" => isSpeakerCellMenu && canRenameSpeaker
                ? Visibility.Visible
                : Visibility.Collapsed,
            "Merge All Adjacent Rows by Speaker" => isSpeakerCellMenu && canRenameSpeaker
                ? Visibility.Visible
                : Visibility.Collapsed,
            "Transcribe This Row" or "Split into Two Rows" or "Combine with Previous Row" => Visibility.Visible,
            _ => Visibility.Visible,
        };
    }

    private void UpdateTranscriptGridPresentation()
    {
        bool showSpeakerColumn = _boundViewModel?.HasSpeakerLabels == true;

        if (SpeakerColumn is not null)
        {
            SpeakerColumn.Visibility = showSpeakerColumn
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void UpdateTranscriptRowActionsVisibility()
    {
        // Row actions are now provided via row context menu.
    }

    private void SetTranscriptRowActionsLine(FinalizedTranscriptLineViewModel? targetLine)
    {
        // Row actions are now provided via row context menu.
    }

    private void SyncPlaybackForTranscriptEdit(
        MainViewModel vm,
        FinalizedTranscriptLineViewModel line,
        string operationName)
    {
        if (!vm.IsAudioFileLoaded || line.StartOffset is null)
        {
            ClearTranscriptEditPlaybackLoop();
            return;
        }

        try
        {
            vm.SeekAudioPreview(line.StartOffset.Value);
            ConfigureTranscriptEditPlaybackLoop(vm, line);

            if (!IsTranscriptionInteractionLocked
                && !vm.IsAudioPlaying
                && vm.PlayAudioCommand.CanExecute(null))
            {
                vm.PlayAudioCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            vm.LogHandledException(operationName, ex);
        }
    }

    private void BeginNonTranscriptCellEdit(MainViewModel vm)
    {
        ClearTranscriptEditPlaybackLoop();
        _nonTranscriptCellEditShouldResumePlayback = vm.AutoPlayTimelineSelection && vm.IsAudioPlaying;

        if (vm.IsAudioPlaying)
        {
            vm.EnsureAudioPreviewPaused();
        }
    }

    private void HandleNonTranscriptCellEditEnding(DataGridCellEditEndingEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            ClearNonTranscriptCellEditState();
            return;
        }

        if (e.EditAction is DataGridEditAction.Cancel or DataGridEditAction.Commit)
        {
            ResumePlaybackAfterNonTranscriptCellEdit(vm);
        }

        ClearNonTranscriptCellEditState();
    }

    private void ResumePlaybackAfterNonTranscriptCellEdit(MainViewModel vm)
    {
        if (!IsTranscriptionInteractionLocked
            && _nonTranscriptCellEditShouldResumePlayback
            && vm.PlayAudioCommand.CanExecute(null))
        {
            vm.PlayAudioCommand.Execute(null);
        }
    }

    private void ClearNonTranscriptCellEditState()
    {
        _nonTranscriptCellEditShouldResumePlayback = false;
        _speakerEditLine = null;
        _speakerEditOriginalLabel = string.Empty;
    }

    private void ClearTranscriptTextEditState()
    {
        _transcriptTextEditLine = null;
        _transcriptTextEditOriginalText = string.Empty;
    }

    private void CaptureAuxiliaryCellOriginalValue(DataGridColumn? column, FinalizedTranscriptLineViewModel line)
    {
        _speakerEditLine = null;
        _speakerEditOriginalLabel = string.Empty;

        if (!IsSpeakerColumn(column))
        {
            return;
        }

        _speakerEditLine = line;
        _speakerEditOriginalLabel = line.SpeakerLabel ?? string.Empty;
    }

    private void RestoreCurrentCellOriginalValue()
    {
        if (FinalizedTranscriptGrid.CurrentCell.Column is not DataGridColumn column
            || FinalizedTranscriptGrid.CurrentCell.Item is not FinalizedTranscriptLineViewModel currentLine)
        {
            return;
        }

        if (IsTranscriptTextColumn(column) && ReferenceEquals(currentLine, _transcriptTextEditLine))
        {
            currentLine.Text = _transcriptTextEditOriginalText;
            return;
        }

        if (IsSpeakerColumn(column) && ReferenceEquals(currentLine, _speakerEditLine))
        {
            currentLine.SpeakerLabel = _speakerEditOriginalLabel;
        }
    }

    private bool TryStartPlaybackEditTranscription(
        MainViewModel vm,
        FinalizedTranscriptLineViewModel line,
        TaskCompletionSource<PlaybackEditAutomationResult>? completionSource = null)
    {
        if (!vm.IsTranscribeAudioTranscriptViewSelected)
        {
            LogPlaybackEdit("Playback transcription for transcript editing is disabled outside Transcribe Audio mode.");
            return false;
        }

        if (_playbackTranscriptionSessionFactory is null)
        {
            LogPlaybackEdit("Playback transcription is unavailable for transcript editing.");
            return false;
        }

        if (!vm.IsAudioFileLoaded || line.StartOffset is null)
        {
            return false;
        }

        string selectedModel = TranscriptionModelCatalog.SupportsPlaybackTranscription(vm.SelectedEngine?.Id ?? string.Empty)
            ? vm.SelectedEngine!.Id
            : TranscriptionModelCatalog.WhisperSmall;

        if (!TryResolvePlaybackEditStopOffset(vm.AudioSeekMaximumSeconds, line, out TimeSpan stopOffset))
        {
            LogPlaybackEdit(
                $"Playback transcription for row '{line.Timeline}' was skipped because the row stop offset could not be resolved.");
            return false;
        }

        StopActivePlaybackEditTranscription(
            vm,
            pausePlayback: false,
            reason: "starting another transcript edit",
            discardResults: true);

        PlaybackTranscriptionSession session;
        try
        {
            session = _playbackTranscriptionSessionFactory();
        }
        catch (Exception ex)
        {
            vm.LogHandledException("playback edit transcription session", ex);
            LogPlaybackEdit($"Unable to create playback transcription session: {ex.Message}");
            return false;
        }

        var state = new PlaybackEditTranscriptionState(
            session,
            line,
            line.StartOffset.Value,
            stopOffset,
            completionSource);

        EventHandler<PlaybackTranscriptionUpdate> finalHandler = (_, update) =>
            BufferPlaybackEditTranscriptionFinal(state, update);
        EventHandler<Exception> faultHandler = (_, ex) =>
            Dispatcher.BeginInvoke(new Action(() => HandlePlaybackEditTranscriptionFault(state, ex)));
        state.FinalHandler = finalHandler;
        state.FaultHandler = faultHandler;

        session.PlaybackFinalTranscriptionAvailable += finalHandler;
        session.Faulted += faultHandler;

        try
        {
            _activePlaybackEditTranscription = state;
            SetPlaybackEditTranscriptionVisualState(
                line,
                isActive: true,
                progressPercent: 0,
                isIndeterminate: false);
            vm.SeekAudioPreview(state.StartOffset);
            ConfigureTranscriptEditPlaybackLoop(vm, line);

            if (!IsTranscriptionInteractionLocked
                && !vm.IsAudioPlaying
                && vm.PlayAudioCommand.CanExecute(null))
            {
                vm.PlayAudioCommand.Execute(null);
            }

            session.StartPlaybackTranscription(selectedModel);
            LogPlaybackEdit(
                $"Started playback transcription for row '{line.Timeline}' from {state.StartOffset} to {state.StopOffset}.");
            return true;
        }
        catch (Exception ex)
        {
            DetachPlaybackEditTranscriptionState(state);
            if (ReferenceEquals(_activePlaybackEditTranscription, state))
            {
                _activePlaybackEditTranscription = null;
            }

            SetPlaybackEditTranscriptionVisualState(
                line,
                isActive: false,
                progressPercent: 0,
                isIndeterminate: false);
            _ = DisposePlaybackTranscriptionSessionAsync(session);
            vm.LogHandledException("playback edit transcription start", ex);
            LogPlaybackEdit($"Unable to start playback transcription for row '{line.Timeline}': {ex.Message}");
            return false;
        }
    }

    internal static bool TryResolvePlaybackEditStopOffset(
        double audioDurationSeconds,
        FinalizedTranscriptLineViewModel line,
        out TimeSpan stopOffset)
    {
        stopOffset = TimeSpan.Zero;

        if (line.StartOffset is not TimeSpan startOffset
            || line.EndOffset is not TimeSpan endOffset
            || endOffset <= startOffset)
        {
            return false;
        }

        stopOffset = endOffset;
        if (audioDurationSeconds > 0)
        {
            TimeSpan audioDuration = TimeSpan.FromSeconds(audioDurationSeconds);
            if (audioDuration <= startOffset)
            {
                return false;
            }

            if (stopOffset > audioDuration)
            {
                stopOffset = audioDuration;
            }
        }

        return stopOffset > startOffset;
    }

    internal static bool CanRunExplicitRowTranscription(
        MainViewModel? vm,
        FinalizedTranscriptLineViewModel? line,
        out string failureMessage)
    {
        failureMessage = string.Empty;

        if (vm is null || line is null)
        {
            failureMessage = "Select a transcript row before transcribing.";
            return false;
        }

        if (!vm.IsAudioFileLoaded || string.IsNullOrWhiteSpace(vm.LoadedAudioFilePath) || !File.Exists(vm.LoadedAudioFilePath))
        {
            failureMessage = "Load or restore the session audio before transcribing this row.";
            return false;
        }

        if (line.StartOffset is not TimeSpan startOffset
            || line.EndOffset is not TimeSpan endOffset
            || endOffset <= startOffset)
        {
            failureMessage = "The selected row does not have a usable timeline.";
            return false;
        }

        string selectedEngineId = vm.SelectedEngine?.Id ?? string.Empty;
        if (!TranscriptionModelCatalog.SupportsFileTranscription(selectedEngineId))
        {
            failureMessage = "The selected engine does not support file transcription.";
            return false;
        }

        return true;
    }

    private bool TryStartRowFileTranscription(
        MainViewModel vm,
        FinalizedTranscriptLineViewModel line,
        bool showStartedToast = true)
    {
        if (_isRowFileTranscriptionRunning)
        {
            ShowCopyToast(
                "Transcribe row in progress",
                "Wait for the current row transcription to finish.",
                ToastNotificationType.Info);
            return false;
        }

        if (_rowAudioTranscriptionService is null
            || _rowAudioStandardizer is null
            || _rowWaveClipExtractor is null)
        {
            ShowCopyToast(
                "Transcribe row unavailable",
                "Row transcription services are not available.",
                ToastNotificationType.Error);
            return false;
        }

        if (!CanRunExplicitRowTranscription(vm, line, out string failureMessage))
        {
            ShowCopyToast("Transcribe row unavailable", failureMessage, ToastNotificationType.Warning);
            return false;
        }

        if (!EnsureSelectedEngineReady(vm, "Transcribe row"))
        {
            return false;
        }

        _isRowFileTranscriptionRunning = true;
        SetPlaybackEditTranscriptionVisualState(line, isActive: true, progressPercent: 0, isIndeterminate: true);
        CommandManager.InvalidateRequerySuggested();
        Dispatcher.BeginInvoke(new Action(UpdateTranscriptRowActionsVisibility), DispatcherPriority.Background);

        if (showStartedToast)
        {
            ShowCopyToast(
                "Transcribing row",
                "The selected row is being transcribed from its timeline range.",
                ToastNotificationType.Info);
        }

        _ = RunRowFileTranscriptionAsync(vm, line);
        return true;
    }

    private async Task RunRowFileTranscriptionAsync(MainViewModel vm, FinalizedTranscriptLineViewModel line)
    {
        string? standardizedPath = null;
        string? clipPath = null;

        try
        {
            string loadedAudioFilePath = vm.LoadedAudioFilePath;
            string selectedEngineId = vm.SelectedEngineId;
            string lineTimeline = line.Timeline;
            TimeSpan rowStartOffset = line.StartOffset ?? TimeSpan.Zero;
            TimeSpan rowEndOffset = line.EndOffset ?? rowStartOffset;
            double audioSeekMaximumSeconds = vm.AudioSeekMaximumSeconds;

            (standardizedPath, clipPath) = await Task.Run(() =>
            {
                TimeSpan clipStartOffset = rowStartOffset;
                TimeSpan clipEndOffset = rowEndOffset + RowFileTranscriptionTailMargin;
                if (audioSeekMaximumSeconds > 0)
                {
                    TimeSpan audioDuration = TimeSpan.FromSeconds(audioSeekMaximumSeconds);
                    if (clipEndOffset > audioDuration)
                    {
                        clipEndOffset = audioDuration;
                    }
                }

                string resolvedStandardizedPath = _rowAudioStandardizer!.ConvertFileToEngineWav(loadedAudioFilePath);
                string resolvedClipPath = _rowWaveClipExtractor!.ExtractTemporaryWaveFile(
                    resolvedStandardizedPath,
                    clipStartOffset,
                    clipEndOffset,
                    $"row-{lineTimeline}",
                    RowFileTranscriptionHeadSilencePadding);

                return (resolvedStandardizedPath, resolvedClipPath);
            }).ConfigureAwait(false);

            var progress = new Progress<TranscriptionProgressSnapshot>(snapshot =>
            {
                double percent = double.IsFinite(snapshot.Percent) ? snapshot.Percent : 0;
                SetPlaybackEditTranscriptionVisualState(
                    line,
                    isActive: true,
                    progressPercent: percent,
                    isIndeterminate: snapshot.Percent <= 0);
            });

            TranscriptionResult result = await Task.Run(
                () => _rowAudioTranscriptionService!.TranscribeAudioFileAsync(
                    clipPath,
                    selectedEngineId,
                    CancellationToken.None,
                    progress)).ConfigureAwait(false);

            TimeSpan rowStartInClip = RowFileTranscriptionHeadSilencePadding;
            TimeSpan rowEndInClip = rowStartInClip + (rowEndOffset - rowStartOffset) + RowFileTranscriptionContextTailMargin;
            string transcribedText = BuildRowFileTranscriptionText(result, rowStartInClip, rowEndInClip);
            string reconciledText = ReconcileRowFileTranscriptionText(line.Text, transcribedText);

            Dispatcher.Invoke(() =>
            {
                ApplyPlaybackEditTranscriptionText(line, reconciledText);
                ShowCopyToast(
                    "Row transcribed",
                    "The selected row text was updated.",
                    ToastNotificationType.Success);
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                vm.LogHandledException("row transcription", ex);
                ShowCopyToast(
                    "Transcribe row failed",
                    ex.Message,
                    ToastNotificationType.Error);
            });
        }
        finally
        {
            DeleteTemporaryRowTranscriptionFile(clipPath);
            DeleteTemporaryRowTranscriptionFile(standardizedPath);

            Dispatcher.Invoke(() =>
            {
                _isRowFileTranscriptionRunning = false;
                SetPlaybackEditTranscriptionVisualState(line, isActive: false, progressPercent: 0, isIndeterminate: false);
                CommandManager.InvalidateRequerySuggested();
                UpdateTranscriptRowActionsVisibility();
            });
        }
    }

    internal static void ApplyPlaybackEditTranscriptionText(
        FinalizedTranscriptLineViewModel line,
        string? text)
    {
        ArgumentNullException.ThrowIfNull(line);

        line.Text = text ?? string.Empty;
        line.IsManuallyReviewed = false;
    }

    internal static string BuildRowFileTranscriptionText(
        TranscriptionResult result,
        TimeSpan rowStartOffset,
        TimeSpan rowEndOffset)
    {
        ArgumentNullException.ThrowIfNull(result);

        IReadOnlyList<TranscriptionTimedLine> timedLines =
            result.TimedLines ?? Array.Empty<TranscriptionTimedLine>();

        string[] timedLineTexts = timedLines
            .Where(line =>
            {
                TimeSpan lineStart = line.StartOffset;
                TimeSpan lineEnd = line.EndOffset ?? lineStart;
                return lineEnd > rowStartOffset && lineStart < rowEndOffset;
            })
            .Select(line => line.Text.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();

        if (timedLineTexts.Length > 0)
        {
            return string.Join(Environment.NewLine, timedLineTexts);
        }

        return (result.Text ?? string.Empty).Trim();
    }

    internal static string ReconcileRowFileTranscriptionText(string? existingText, string? transcribedText)
    {
        string trimmedTranscribedText = (transcribedText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedTranscribedText))
        {
            return string.Empty;
        }

        string trimmedExistingText = (existingText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedExistingText))
        {
            return trimmedTranscribedText;
        }

        List<TextMergeToken> existingTokens = TokenizeForTextMerge(trimmedExistingText);
        List<TextMergeToken> transcribedTokens = TokenizeForTextMerge(trimmedTranscribedText);
        if (existingTokens.Count == 0 || transcribedTokens.Count == 0)
        {
            return trimmedTranscribedText;
        }

        int suffixPrefixOverlap = FindBoundaryOverlap(
            existingTokens,
            transcribedTokens,
            existingSuffixToTranscribedPrefix: true);
        int prefixSuffixOverlap = FindBoundaryOverlap(
            existingTokens,
            transcribedTokens,
            existingSuffixToTranscribedPrefix: false);

        if (suffixPrefixOverlap > 0 && suffixPrefixOverlap >= prefixSuffixOverlap)
        {
            string transcribedRemainder = GetTextFromToken(trimmedTranscribedText, transcribedTokens, suffixPrefixOverlap);
            return JoinTextMergeParts(trimmedExistingText, transcribedRemainder);
        }

        if (prefixSuffixOverlap > 0)
        {
            string existingRemainder = GetTextFromToken(trimmedExistingText, existingTokens, prefixSuffixOverlap);
            return JoinTextMergeParts(trimmedTranscribedText, existingRemainder);
        }

        int nearSuffixPrefixOverlap = FindNearBoundaryOverlap(
            existingTokens,
            transcribedTokens,
            existingSuffixToTranscribedPrefix: true);
        if (nearSuffixPrefixOverlap > 0)
        {
            int overlapStart = existingTokens.Count - nearSuffixPrefixOverlap;
            string existingPrefix = overlapStart > 0
                ? trimmedExistingText[..existingTokens[overlapStart].Start].Trim()
                : string.Empty;
            return JoinTextMergeParts(existingPrefix, trimmedTranscribedText);
        }

        int nearPrefixSuffixOverlap = FindNearBoundaryOverlap(
            existingTokens,
            transcribedTokens,
            existingSuffixToTranscribedPrefix: false);
        if (nearPrefixSuffixOverlap > 0)
        {
            string existingRemainder = GetTextFromToken(trimmedExistingText, existingTokens, nearPrefixSuffixOverlap);
            return JoinTextMergeParts(trimmedTranscribedText, existingRemainder);
        }

        if (TryFindTokenSequence(existingTokens, transcribedTokens, out int containedStart))
        {
            int containedEnd = existingTokens[containedStart + transcribedTokens.Count - 1].RawEnd;
            return trimmedExistingText[..containedEnd].Trim();
        }

        if (TryFindTokenSequence(transcribedTokens, existingTokens, out _))
        {
            return trimmedTranscribedText;
        }

        if (TryFindBestInternalOverlap(
            existingTokens,
            transcribedTokens,
            out int existingStart,
            out int existingLength,
            out int transcribedStart,
            out int transcribedLength))
        {
            string existingPrefix = existingStart > 0
                ? trimmedExistingText[..existingTokens[existingStart].Start].Trim()
                : string.Empty;

            return JoinTextMergeParts(existingPrefix, trimmedTranscribedText);
        }

        return trimmedTranscribedText;
    }

    private static List<TextMergeToken> TokenizeForTextMerge(string text)
    {
        var tokens = new List<TextMergeToken>();
        int index = 0;

        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            int rawStart = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            int rawEnd = index;
            int tokenStart = rawStart;
            int tokenEnd = rawEnd;

            while (tokenStart < tokenEnd && !char.IsLetterOrDigit(text[tokenStart]))
            {
                tokenStart++;
            }

            while (tokenEnd > tokenStart && !char.IsLetterOrDigit(text[tokenEnd - 1]))
            {
                tokenEnd--;
            }

            if (tokenStart >= tokenEnd)
            {
                continue;
            }

            string normalized = text[tokenStart..tokenEnd].ToLowerInvariant();
            tokens.Add(new TextMergeToken(normalized, tokenStart, tokenEnd, rawEnd));
        }

        return tokens;
    }

    private static int FindBoundaryOverlap(
        IReadOnlyList<TextMergeToken> existingTokens,
        IReadOnlyList<TextMergeToken> transcribedTokens,
        bool existingSuffixToTranscribedPrefix)
    {
        int maxOverlap = Math.Min(existingTokens.Count, transcribedTokens.Count);

        for (int length = maxOverlap; length > 0; length--)
        {
            bool matches = true;
            for (int offset = 0; offset < length; offset++)
            {
                string existing = existingSuffixToTranscribedPrefix
                    ? existingTokens[existingTokens.Count - length + offset].Normalized
                    : existingTokens[offset].Normalized;
                string transcribed = existingSuffixToTranscribedPrefix
                    ? transcribedTokens[offset].Normalized
                    : transcribedTokens[transcribedTokens.Count - length + offset].Normalized;

                if (!string.Equals(existing, transcribed, StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return length;
            }
        }

        return 0;
    }

    private static int FindNearBoundaryOverlap(
        IReadOnlyList<TextMergeToken> existingTokens,
        IReadOnlyList<TextMergeToken> transcribedTokens,
        bool existingSuffixToTranscribedPrefix)
    {
        int maxOverlap = Math.Min(existingTokens.Count, transcribedTokens.Count);

        for (int length = maxOverlap; length >= 2; length--)
        {
            int exactMatches = 0;
            int nearMatches = 0;
            int longestExactTokenLength = 0;
            bool matches = true;

            for (int offset = 0; offset < length; offset++)
            {
                TextMergeToken existing = existingSuffixToTranscribedPrefix
                    ? existingTokens[existingTokens.Count - length + offset]
                    : existingTokens[offset];
                TextMergeToken transcribed = existingSuffixToTranscribedPrefix
                    ? transcribedTokens[offset]
                    : transcribedTokens[transcribedTokens.Count - length + offset];

                if (string.Equals(existing.Normalized, transcribed.Normalized, StringComparison.Ordinal))
                {
                    exactMatches++;
                    longestExactTokenLength = Math.Max(longestExactTokenLength, existing.Normalized.Length);
                    continue;
                }

                if (AreNearTextMergeTokens(existing.Normalized, transcribed.Normalized))
                {
                    nearMatches++;
                    continue;
                }

                matches = false;
                break;
            }

            if (!matches || nearMatches != 1 || exactMatches != length - 1)
            {
                continue;
            }

            if (length > 2 || longestExactTokenLength >= 4)
            {
                return length;
            }
        }

        return 0;
    }

    private static bool AreNearTextMergeTokens(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return false;
        }

        if (!char.Equals(left[0], right[0]))
        {
            return false;
        }

        int lengthDifference = Math.Abs(left.Length - right.Length);
        if (lengthDifference > 1)
        {
            return false;
        }

        int leftIndex = 0;
        int rightIndex = 0;
        int edits = 0;

        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            if (left[leftIndex] == right[rightIndex])
            {
                leftIndex++;
                rightIndex++;
                continue;
            }

            edits++;
            if (edits > 1)
            {
                return false;
            }

            if (left.Length == right.Length)
            {
                leftIndex++;
                rightIndex++;
            }
            else if (left.Length > right.Length)
            {
                leftIndex++;
            }
            else
            {
                rightIndex++;
            }
        }

        return edits + (left.Length - leftIndex) + (right.Length - rightIndex) <= 1;
    }

    private static bool TryFindTokenSequence(
        IReadOnlyList<TextMergeToken> haystack,
        IReadOnlyList<TextMergeToken> needle,
        out int startIndex)
    {
        startIndex = -1;
        if (needle.Count == 0 || needle.Count > haystack.Count)
        {
            return false;
        }

        for (int candidate = 0; candidate <= haystack.Count - needle.Count; candidate++)
        {
            bool matches = true;
            for (int offset = 0; offset < needle.Count; offset++)
            {
                if (!string.Equals(
                    haystack[candidate + offset].Normalized,
                    needle[offset].Normalized,
                    StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                startIndex = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindBestInternalOverlap(
        IReadOnlyList<TextMergeToken> existingTokens,
        IReadOnlyList<TextMergeToken> transcribedTokens,
        out int existingStart,
        out int existingLength,
        out int transcribedStart,
        out int transcribedLength)
    {
        existingStart = -1;
        existingLength = 0;
        transcribedStart = -1;
        transcribedLength = 0;

        for (int existingIndex = 0; existingIndex < existingTokens.Count; existingIndex++)
        {
            for (int transcribedIndex = 0; transcribedIndex < transcribedTokens.Count; transcribedIndex++)
            {
                int length = 0;
                while (existingIndex + length < existingTokens.Count
                    && transcribedIndex + length < transcribedTokens.Count
                    && string.Equals(
                        existingTokens[existingIndex + length].Normalized,
                        transcribedTokens[transcribedIndex + length].Normalized,
                        StringComparison.Ordinal))
                {
                    length++;
                }

                if (length >= 2 && length > existingLength)
                {
                    existingStart = existingIndex;
                    existingLength = length;
                    transcribedStart = transcribedIndex;
                    transcribedLength = length;
                }
            }
        }

        return existingLength > 0;
    }

    private static string GetTextFromToken(
        string text,
        IReadOnlyList<TextMergeToken> tokens,
        int tokenIndex)
    {
        return tokenIndex >= tokens.Count
            ? string.Empty
            : text[tokens[tokenIndex].Start..].Trim();
    }

    private static string JoinTextMergeParts(params string[] parts)
    {
        return string.Join(
            " ",
            parts
                .Select(part => part?.Trim() ?? string.Empty)
                .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static void DeleteTemporaryRowTranscriptionFile(string? filePath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Best-effort cleanup for temporary row transcription files.
        }
    }

    private void UpdatePlaybackEditTranscriptionProgress()
    {
        if (_boundViewModel is null || _activePlaybackEditTranscription is null)
        {
            return;
        }

        PlaybackEditTranscriptionState state = _activePlaybackEditTranscription;
        if (Volatile.Read(ref state.StopRequestedFlag) != 0)
        {
            return;
        }

        double totalSeconds = Math.Max((state.StopOffset - state.StartOffset).TotalSeconds, 0.001d);
        double currentSeconds = Math.Max(_boundViewModel.AudioSeekPositionSeconds, 0);
        double elapsedSeconds = Math.Clamp(
            currentSeconds - state.StartOffset.TotalSeconds,
            0,
            totalSeconds);
        double progressPercent = (elapsedSeconds / totalSeconds) * 100d;

        SetPlaybackEditTranscriptionVisualState(
            state.Line,
            isActive: true,
            progressPercent: progressPercent,
            isIndeterminate: false);
    }

    private void EnforcePlaybackEditTranscriptionStop()
    {
        if (_boundViewModel is null || _activePlaybackEditTranscription is null)
        {
            return;
        }

        if (!_boundViewModel.IsAudioFileLoaded)
        {
            StopActivePlaybackEditTranscription(
                _boundViewModel,
                pausePlayback: false,
                reason: "audio preview unavailable",
                discardResults: true);
            return;
        }

        double currentSeconds = Math.Max(_boundViewModel.AudioSeekPositionSeconds, 0);
        if (currentSeconds < _activePlaybackEditTranscription.StopOffset.TotalSeconds)
        {
            return;
        }

        StopActivePlaybackEditTranscription(
            _boundViewModel,
            pausePlayback: true,
            reason: "playback reached row capture boundary");
    }

    private void StopActivePlaybackEditTranscription(
        MainViewModel? vm,
        bool pausePlayback,
        string reason,
        bool discardResults = false)
    {
        PlaybackEditTranscriptionState? state = _activePlaybackEditTranscription;
        if (state is null)
        {
            return;
        }

        _ = StopPlaybackEditTranscriptionAsync(
            state,
            vm,
            pausePlayback,
            reason,
            discardResults);
    }

    private async Task StopPlaybackEditTranscriptionAsync(
        PlaybackEditTranscriptionState state,
        MainViewModel? vm,
        bool pausePlayback,
        string reason,
        bool discardResults)
    {
        if (Interlocked.Exchange(ref state.StopRequestedFlag, 1) != 0)
        {
            return;
        }

        state.IgnoreResults = discardResults;
        LogPlaybackEdit($"Stopping playback transcription for row '{state.Line.Timeline}' ({reason}).");

        try
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
                SetPlaybackEditTranscriptionVisualState(
                    state.Line,
                    isActive: true,
                    progressPercent: 100,
                    isIndeterminate: true)));

            if (pausePlayback && vm is not null)
            {
                PausePlaybackForPlaybackEditStop(vm);
                await Task.Delay(PlaybackEditStopDrainDelay).ConfigureAwait(false);
            }

            await state.Session.StopPlaybackTranscriptionAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                vm?.LogHandledException("playback edit transcription stop", ex);
                LogPlaybackEdit($"Playback transcription stop failed for row '{state.Line.Timeline}': {ex.Message}");
            }));
        }
        finally
        {
            await DisposePlaybackTranscriptionSessionAsync(state.Session).ConfigureAwait(false);

            _ = Dispatcher.BeginInvoke(new Action(() =>
                CompletePlaybackEditTranscriptionStop(state, vm, pausePlayback, discardResults)));
        }
    }

    private void BufferPlaybackEditTranscriptionFinal(
        PlaybackEditTranscriptionState state,
        PlaybackTranscriptionUpdate update)
    {
        string text = update.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (state.SyncRoot)
        {
            if (state.IgnoreResults)
            {
                return;
            }

            state.FinalSegments.Add(text);
        }

        LogPlaybackEdit(
            $"Buffered playback transcription final chunk {update.SequenceIndex ?? state.FinalSegments.Count - 1} " +
            $"for row '{state.Line.Timeline}'.");

        _ = Dispatcher.BeginInvoke(
            new Action(() => ApplyBufferedPlaybackEditTranscription(state)),
            DispatcherPriority.Background);
    }

    private void ApplyBufferedPlaybackEditTranscription(PlaybackEditTranscriptionState state)
    {
        if (state.IgnoreResults)
        {
            return;
        }

        string mergedText;
        lock (state.SyncRoot)
        {
            if (state.FinalSegments.Count == 0)
            {
                return;
            }

            mergedText = string.Join(Environment.NewLine, state.FinalSegments);
        }

        ApplyPlaybackEditTranscriptionText(state.Line, mergedText);
        LogPlaybackEdit(
            $"Applied buffered playback transcription text to row '{state.Line.Timeline}' " +
            $"({mergedText.Length:N0} chars).");
    }

    private void HandlePlaybackEditTranscriptionFault(
        PlaybackEditTranscriptionState state,
        Exception ex)
    {
        if (!ReferenceEquals(_activePlaybackEditTranscription, state))
        {
            return;
        }

        state.Failure = ex;
        _boundViewModel?.LogHandledException("playback edit transcription", ex);
        LogPlaybackEdit($"Playback transcription failed for row '{state.Line.Timeline}': {ex.Message}");

        StopActivePlaybackEditTranscription(
            _boundViewModel,
            pausePlayback: true,
            reason: "playback transcription fault");
    }

    private void CompletePlaybackEditTranscriptionStop(
        PlaybackEditTranscriptionState state,
        MainViewModel? vm,
        bool pausePlayback,
        bool discardResults)
    {
        if (!discardResults)
        {
            ApplyBufferedPlaybackEditTranscription(state);
        }

        DetachPlaybackEditTranscriptionState(state);

        if (ReferenceEquals(_activePlaybackEditTranscription, state))
        {
            _activePlaybackEditTranscription = null;
        }

        SetPlaybackEditTranscriptionVisualState(
            state.Line,
            isActive: false,
            progressPercent: 0,
            isIndeterminate: false);
        Dispatcher.BeginInvoke(new Action(UpdateTranscriptRowActionsVisibility), DispatcherPriority.Background);

        state.CompletionSource?.TrySetResult(new PlaybackEditAutomationResult(
            state.Line,
            WasDiscarded: discardResults,
            HadFinalText: state.FinalSegments.Count > 0,
            Failure: state.Failure));

        if (pausePlayback && vm is not null)
        {
            try
            {
                vm.EnsureAudioPreviewPaused();
            }
            catch (Exception ex)
            {
                vm.LogHandledException("playback edit transcription pause", ex);
            }
        }

        if (discardResults)
        {
            LogPlaybackEdit($"Discarded playback transcription results for row '{state.Line.Timeline}'.");
            return;
        }

        if (state.FinalSegments.Count > 0)
        {
            LogPlaybackEdit(
                $"Playback transcription completed for row '{state.Line.Timeline}' " +
                $"and inserted {state.FinalSegments.Count:N0} finalized segment(s).");
            return;
        }

        LogPlaybackEdit($"Playback transcription completed for row '{state.Line.Timeline}' with no finalized text.");
    }

    private static void PausePlaybackForPlaybackEditStop(MainViewModel vm)
    {
        try
        {
            if (vm.PauseAudioCommand.CanExecute(null))
            {
                vm.PauseAudioCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            vm.LogHandledException("playback edit transcription pause", ex);
        }
    }

    private static void DetachPlaybackEditTranscriptionState(PlaybackEditTranscriptionState state)
    {
        if (state.FinalHandler is not null)
        {
            state.Session.PlaybackFinalTranscriptionAvailable -= state.FinalHandler;
            state.FinalHandler = null;
        }

        if (state.FaultHandler is not null)
        {
            state.Session.Faulted -= state.FaultHandler;
            state.FaultHandler = null;
        }
    }

    private static async Task DisposePlaybackTranscriptionSessionAsync(PlaybackTranscriptionSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static async Task DisposeLiveSegmentTranscriptionSessionAsync(LiveSegmentTranscriptionSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static async Task DisposeLiveRecordingCaptureSessionAsync(LiveRecordingCaptureSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static void SetPlaybackEditTranscriptionVisualState(
        FinalizedTranscriptLineViewModel line,
        bool isActive,
        double progressPercent,
        bool isIndeterminate)
    {
        line.IsPlaybackEditTranscribing = isActive;
        line.PlaybackEditProgressPercent = isActive ? progressPercent : 0;
        line.IsPlaybackEditProgressIndeterminate = isActive && isIndeterminate;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void LogTranscribeAudioBatch(string message)
    {
        _processLogService?.Log("TranscribeAudioBatch", message);
    }

    private void LogLiveTranscription(string message)
    {
        _processLogService?.Log("LiveTranscription", message);
    }

    private void LogPlaybackEdit(string message)
    {
        _processLogService?.Log("PlaybackEdit", message);
    }

    private static string ResolveTranscriptionEngineLabel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return "the selected model";
        }

        return TranscriptionModelCatalog.Models
            .FirstOrDefault(model => string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName
            ?? modelId;
    }

    private static string ResolveCurrentEngineLabel(MainViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);

        return !string.IsNullOrWhiteSpace(vm.SelectedEngine?.DisplayName)
            ? vm.SelectedEngine.DisplayName
            : ResolveTranscriptionEngineLabel(vm.SelectedEngineId);
    }

    private static string BuildLogPreview(string text)
    {
        string normalized = string.Join(" ", (text ?? string.Empty).Split(
            Array.Empty<char>(),
            StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 80
            ? normalized
            : $"{normalized[..80]}...";
    }

    private static string FormatRowTimelineOffset(TimeSpan value)
    {
        if (value.TotalHours >= 1)
        {
            return value.ToString(@"hh\:mm\:ss");
        }

        return value.ToString(@"mm\:ss");
    }

    private List<FinalizedTranscriptLineViewModel> GetDisplayedTranscriptLines()
    {
        return GetTranscriptRowItems()
            .OfType<FinalizedTranscriptLineViewModel>()
            .ToList();
    }

    private static bool TryGetLineTimelineOffset(FinalizedTranscriptLineViewModel? line, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;

        if (line is null)
        {
            return false;
        }

        if (FinalizedTranscriptLineViewModel.TryParseTimeline(line.Timeline, out offset))
        {
            return true;
        }

        if (line.StartOffset is TimeSpan startOffset)
        {
            offset = startOffset;
            return true;
        }

        return false;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);

            if (child is T match)
            {
                return match;
            }

            T? descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        DependencyObject? current = start;

        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = GetVisualOrLogicalParent(current);
        }

        return null;
    }

    private static DependencyObject? GetVisualOrLogicalParent(DependencyObject current)
    {
        if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
        {
            DependencyObject? visualParent = VisualTreeHelper.GetParent(current);
            if (visualParent is not null)
            {
                return visualParent;
            }
        }

        return current switch
        {
            FrameworkElement frameworkElement => frameworkElement.Parent,
            FrameworkContentElement frameworkContentElement => frameworkContentElement.Parent,
            _ => null,
        };
    }

    private IList<object> GetTranscriptRowItems()
    {
        return FinalizedTranscriptGrid.Items
            .Cast<object>()
            .Where(IsDataItem)
            .ToList();
    }

    private void UpdatePlaybackTimelineHighlight()
    {
        if (_boundViewModel is null || !_boundViewModel.IsAudioFileLoaded)
        {
            SetPlaybackTimelineMatch(null);
            return;
        }

        if (!_boundViewModel.IsAudioPlaying || IsCurrentGridCellEditing())
        {
            SetPlaybackTimelineMatch(null);
            return;
        }

        List<FinalizedTranscriptLineViewModel> activeLines = _boundViewModel.CurrentTranscriptLines.ToList();
        if (activeLines.Count == 0)
        {
            SetPlaybackTimelineMatch(null);
            return;
        }

        TimeSpan playbackPosition = TimeSpan.FromSeconds(Math.Max(_boundViewModel.AudioSeekPositionSeconds, 0));
        FinalizedTranscriptLineViewModel? matchedLine = FindPlaybackTimelineMatch(
            activeLines,
            playbackPosition);
        SetPlaybackTimelineMatch(matchedLine);
    }

    private void SetPlaybackTimelineMatch(FinalizedTranscriptLineViewModel? matchedLine)
    {
        if (ReferenceEquals(_playbackMatchedLine, matchedLine))
        {
            return;
        }

        bool shouldClearSelectionForPlaybackFollow =
            matchedLine is not null
            && !ReferenceEquals(_playbackMatchedLine, matchedLine);

        if (shouldClearSelectionForPlaybackFollow)
        {
            ClearTranscriptGridCellSelectionForPlaybackFollow();
        }

        if (_playbackMatchedLine is not null)
        {
            _playbackMatchedLine.IsPlaybackTimelineMatch = false;
        }

        _playbackMatchedLine = matchedLine;

        if (_playbackMatchedLine is not null)
        {
            _playbackMatchedLine.IsPlaybackTimelineMatch = true;
            EnsurePlaybackTimelineMatchVisible(_playbackMatchedLine);
        }
    }

    private void ClearTranscriptGridCellSelectionForPlaybackFollow()
    {
        if (IsCurrentGridCellEditing())
        {
            return;
        }

        FinalizedTranscriptGrid.SelectedCells.Clear();
        FinalizedTranscriptGrid.UnselectAllCells();
        FinalizedTranscriptGrid.CurrentCell = default;
    }

    private void EnsurePlaybackTimelineMatchVisible(FinalizedTranscriptLineViewModel matchedLine)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(_playbackMatchedLine, matchedLine)
                || !FinalizedTranscriptGrid.IsLoaded
                || !FinalizedTranscriptGrid.IsVisible)
            {
                return;
            }

            ScrollViewer? scrollViewer = FindVisualChild<ScrollViewer>(FinalizedTranscriptGrid);
            if (scrollViewer is null || scrollViewer.ViewportHeight <= 0)
            {
                return;
            }

            DataGridRow? row =
                FinalizedTranscriptGrid.ItemContainerGenerator.ContainerFromItem(matchedLine) as DataGridRow;
            if (row is not null)
            {
                RevealPlaybackTimelineRowIfNeeded(row, scrollViewer);
                return;
            }

            int itemIndex = FinalizedTranscriptGrid.Items.IndexOf(matchedLine);
            if (itemIndex < 0)
            {
                return;
            }

            if (scrollViewer.CanContentScroll)
            {
                double viewportTop = scrollViewer.VerticalOffset;
                double viewportBottom = scrollViewer.VerticalOffset + scrollViewer.ViewportHeight;
                if (itemIndex < viewportTop)
                {
                    scrollViewer.ScrollToVerticalOffset(itemIndex);
                }
                else if (itemIndex >= viewportBottom)
                {
                    double targetOffset = Math.Max(0, itemIndex - scrollViewer.ViewportHeight + 1);
                    scrollViewer.ScrollToVerticalOffset(targetOffset);
                }

                return;
            }

            DataGridColumn targetColumn = TimelineTranscriptColumn ?? FinalizedTranscriptGrid.Columns[0];
            FinalizedTranscriptGrid.ScrollIntoView(matchedLine, targetColumn);
            FinalizedTranscriptGrid.UpdateLayout();

            row = FinalizedTranscriptGrid.ItemContainerGenerator.ContainerFromItem(matchedLine) as DataGridRow;
            if (row is not null)
            {
                RevealPlaybackTimelineRowIfNeeded(row, scrollViewer);
            }
        }), DispatcherPriority.Background);
    }

    private void RevealPlaybackTimelineRowIfNeeded(DataGridRow row, ScrollViewer scrollViewer)
    {
        try
        {
            Rect rowBounds = row.TransformToAncestor(scrollViewer)
                .TransformBounds(new Rect(new System.Windows.Point(0, 0), row.RenderSize));

            if (rowBounds.Top >= 0 && rowBounds.Bottom <= scrollViewer.ViewportHeight)
            {
                return;
            }

            if (rowBounds.Top < 0)
            {
                row.BringIntoView(new Rect(
                    new System.Windows.Point(0, 0),
                    new System.Windows.Size(Math.Max(row.ActualWidth, 1), 1)));
                return;
            }

            row.BringIntoView(new Rect(
                new System.Windows.Point(0, Math.Max(row.ActualHeight - 1, 0)),
                new System.Windows.Size(Math.Max(row.ActualWidth, 1), 1)));
        }
        catch (Exception ex)
        {
            _boundViewModel?.LogHandledException("playback timeline auto-scroll", ex);
        }
    }

    private static FinalizedTranscriptLineViewModel? FindPlaybackTimelineMatch(
        IEnumerable<FinalizedTranscriptLineViewModel> lines,
        TimeSpan playbackPosition)
    {
        List<FinalizedTranscriptLineViewModel> candidates = lines
            .Where(line => line.StartOffset is not null)
            .OrderBy(line => line.StartOffset)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        for (int index = 0; index < candidates.Count; index++)
        {
            FinalizedTranscriptLineViewModel current = candidates[index];
            TimeSpan start = current.StartOffset ?? TimeSpan.Zero;
            TimeSpan end = current.EndOffset ?? start;

            if (end < start)
            {
                end = start;
            }

            if (playbackPosition >= start && playbackPosition <= end)
            {
                return current;
            }

            if (index < candidates.Count - 1)
            {
                TimeSpan nextStart = candidates[index + 1].StartOffset ?? end;
                if (playbackPosition >= start && playbackPosition < nextStart)
                {
                    return current;
                }
            }
            else if (playbackPosition >= start)
            {
                return current;
            }
        }

        return candidates
            .OrderBy(line => Abs((line.StartOffset ?? TimeSpan.Zero) - playbackPosition))
            .FirstOrDefault();
    }

    private void ConfigureTranscriptEditPlaybackLoop(
        MainViewModel viewModel,
        FinalizedTranscriptLineViewModel currentLine)
    {
        ClearTranscriptEditPlaybackLoop();

        if (currentLine.StartOffset is null)
        {
            return;
        }

        List<FinalizedTranscriptLineViewModel> activeLines = viewModel.CurrentTranscriptLines.ToList();

        int currentIndex = activeLines.IndexOf(currentLine);
        if (currentIndex < 0)
        {
            return;
        }

        TimeSpan startOffset = currentLine.StartOffset.Value;
        TimeSpan? repeatOffset = null;

        for (int index = currentIndex + 1; index < activeLines.Count; index++)
        {
            FinalizedTranscriptLineViewModel candidate = activeLines[index];
            if (candidate.StartOffset is null)
            {
                continue;
            }

            if (candidate.StartOffset.Value > startOffset)
            {
                repeatOffset = candidate.StartOffset.Value;
                break;
            }
        }

        if (repeatOffset is null)
        {
            double audioDurationSeconds = Math.Max(viewModel.AudioSeekMaximumSeconds, 0);
            if (audioDurationSeconds > startOffset.TotalSeconds + 0.001d)
            {
                repeatOffset = TimeSpan.FromSeconds(audioDurationSeconds);
            }
        }

        if (repeatOffset is null)
        {
            return;
        }

        _editLoopLine = currentLine;
        _editLoopStartOffset = startOffset;
        _editLoopRepeatOffset = repeatOffset;
    }

    private void ClearTranscriptEditPlaybackLoop()
    {
        _editLoopLine = null;
        _editLoopStartOffset = null;
        _editLoopRepeatOffset = null;
        _isTranscriptEditLoopRestartPending = false;
    }

    private bool IsTranscriptCellEditingActive()
    {
        return _transcriptTextEditLine is not null;
    }

    private void SyncPlaybackToCurrentTranscriptRow()
    {
        if (DataContext is not MainViewModel vm
            || !vm.IsTranscribeAudioTranscriptViewSelected
            || !vm.IsAudioFileLoaded
            || IsTranscriptionInteractionLocked
            || _activePlaybackEditTranscription is not null)
        {
            _lastPlaybackSyncedLine = null;
            return;
        }

        if (!TryGetCurrentTranscriptGridLine(out FinalizedTranscriptLineViewModel currentLine)
            || currentLine.StartOffset is not TimeSpan rowStartOffset)
        {
            _lastPlaybackSyncedLine = null;
            return;
        }

        if (ReferenceEquals(_lastPlaybackSyncedLine, currentLine))
        {
            return;
        }

        try
        {
            _lastPlaybackSyncedLine = currentLine;
            if (!vm.AutoPlayTimelineSelection)
            {
                vm.EnsureAudioPreviewPaused();
            }

            vm.SeekAudioPreview(rowStartOffset);
            ConfigureTranscriptEditPlaybackLoop(vm, currentLine);

            bool shouldAutoPlay = vm.AutoPlayTimelineSelection;
            if (!IsTranscriptionInteractionLocked
                && shouldAutoPlay
                && !vm.IsAudioPlaying
                && vm.PlayAudioCommand.CanExecute(null))
            {
                vm.PlayAudioCommand.Execute(null);
            }
            else if (!shouldAutoPlay)
            {
                vm.EnsureAudioPreviewPaused();
            }
        }
        catch (Exception ex)
        {
            vm.LogHandledException("row transfer playback sync", ex);
        }
    }

    private bool TryGetCurrentTranscriptGridLine(out FinalizedTranscriptLineViewModel line)
    {
        line = null!;

        if (FinalizedTranscriptGrid.CurrentCell.Item is FinalizedTranscriptLineViewModel currentCellLine)
        {
            line = currentCellLine;
            return true;
        }

        if (FinalizedTranscriptGrid.CurrentItem is FinalizedTranscriptLineViewModel currentItemLine)
        {
            line = currentItemLine;
            return true;
        }

        if (FinalizedTranscriptGrid.SelectedItem is FinalizedTranscriptLineViewModel selectedItemLine)
        {
            line = selectedItemLine;
            return true;
        }

        return false;
    }

    private void PausePlaybackAfterTranscriptEdit(MainViewModel vm)
    {
        ClearTranscriptEditPlaybackLoop();

        try
        {
            if (vm.PauseAudioCommand.CanExecute(null))
            {
                vm.PauseAudioCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            vm.LogHandledException("transcript edit playback pause", ex);
        }
    }

    private void EnforceTranscriptEditPlaybackLoop()
    {
        if (_isApplyingTranscriptEditLoopSeek
            || _boundViewModel is null
            || _editLoopLine is null
            || _editLoopStartOffset is null
            || _editLoopRepeatOffset is null
            || !_boundViewModel.IsAudioFileLoaded)
        {
            return;
        }

        if (!IsTranscriptCellEditingActive())
        {
            _isTranscriptEditLoopRestartPending = false;
            return;
        }

        double currentSeconds = Math.Max(_boundViewModel.AudioSeekPositionSeconds, 0);

        if (_isTranscriptEditLoopRestartPending)
        {
            double repeatSeconds = _editLoopRepeatOffset.Value.TotalSeconds;

            if (currentSeconds < repeatSeconds - 0.05d)
            {
                _isTranscriptEditLoopRestartPending = false;
            }

            return;
        }

        if (currentSeconds < _editLoopRepeatOffset.Value.TotalSeconds)
        {
            return;
        }

        _isTranscriptEditLoopRestartPending = true;
        Dispatcher.BeginInvoke(
            new Action(ApplyTranscriptEditPlaybackLoopRestart),
            DispatcherPriority.Background);
    }

    private void ApplyTranscriptEditPlaybackLoopRestart()
    {
        if (_boundViewModel is null
            || _editLoopLine is null
            || _editLoopStartOffset is null
            || _editLoopRepeatOffset is null
            || !_boundViewModel.IsAudioFileLoaded)
        {
            _isTranscriptEditLoopRestartPending = false;
            return;
        }

        if (!IsTranscriptCellEditingActive())
        {
            _isTranscriptEditLoopRestartPending = false;
            return;
        }

        try
        {
            _isApplyingTranscriptEditLoopSeek = true;
            _boundViewModel.RestartAudioPreviewSegment(_editLoopStartOffset.Value);
        }
        catch (Exception ex)
        {
            _boundViewModel.LogHandledException("transcript edit playback loop", ex);
            ClearTranscriptEditPlaybackLoop();
        }
        finally
        {
            _isApplyingTranscriptEditLoopSeek = false;
        }
    }

    private static TimeSpan Abs(TimeSpan value) => value < TimeSpan.Zero ? value.Negate() : value;

    private static bool IsDataItem(object? item)
    {
        return item is not null
            && !ReferenceEquals(item, CollectionView.NewItemPlaceholder)
            && !ReferenceEquals(item, DependencyProperty.UnsetValue);
    }

    private sealed record ToastAppearance(
        string IconData
    );

    private readonly record struct TextMergeToken(
        string Normalized,
        int Start,
        int End,
        int RawEnd
    );

    private sealed class PlaybackEditTranscriptionState
    {
        public PlaybackEditTranscriptionState(
            PlaybackTranscriptionSession session,
            FinalizedTranscriptLineViewModel line,
            TimeSpan startOffset,
            TimeSpan stopOffset,
            TaskCompletionSource<PlaybackEditAutomationResult>? completionSource)
        {
            Session = session;
            Line = line;
            StartOffset = startOffset;
            StopOffset = stopOffset;
            CompletionSource = completionSource;
        }

        public PlaybackTranscriptionSession Session { get; }

        public FinalizedTranscriptLineViewModel Line { get; }

        public TimeSpan StartOffset { get; }

        public TimeSpan StopOffset { get; }

        public object SyncRoot { get; } = new();

        public List<string> FinalSegments { get; } = new();

        public TaskCompletionSource<PlaybackEditAutomationResult>? CompletionSource { get; }

        public int StopRequestedFlag;

        public bool IgnoreResults { get; set; }

        public Exception? Failure { get; set; }

        public EventHandler<PlaybackTranscriptionUpdate>? FinalHandler { get; set; }

        public EventHandler<Exception>? FaultHandler { get; set; }
    }

    private sealed record PlaybackEditAutomationResult(
        FinalizedTranscriptLineViewModel Line,
        bool WasDiscarded,
        bool HadFinalText,
        Exception? Failure
    );

}







