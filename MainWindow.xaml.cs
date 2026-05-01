using System;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
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
using System.Windows.Threading;
using AudioScript.Abstractions;
using AudioScript.Audio;
using AudioScript.Services;
using AudioScript.ViewModels;
using DataGridCell = System.Windows.Controls.DataGridCell;
using DataGridCellsPresenter = System.Windows.Controls.Primitives.DataGridCellsPresenter;
using NAudio.CoreAudioApi;

namespace AudioScript;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const int TimelineColumnIndex = 0;
    private const int TranscriptTextColumnIndex = 2;
    private const double ToastBottomMargin = 48;
    private const double ToastHiddenOffsetY = -14;
    public static readonly RoutedUICommand InsertRowAboveCommand =
        new("Insert Row Above", nameof(InsertRowAboveCommand), typeof(MainWindow));
    public static readonly RoutedUICommand InsertRowBelowCommand =
        new("Insert Row Below", nameof(InsertRowBelowCommand), typeof(MainWindow));
    public static readonly RoutedUICommand DuplicateRowCommand =
        new("Duplicate Row", nameof(DuplicateRowCommand), typeof(MainWindow));
    public static readonly RoutedUICommand DeleteRowCommand =
        new("Delete Row", nameof(DeleteRowCommand), typeof(MainWindow));
    public static readonly RoutedUICommand CopyTextCommand =
        new("Copy Text", nameof(CopyTextCommand), typeof(MainWindow));
    public static readonly RoutedUICommand CopyTimelineAndTextCommand =
        new("Copy Timeline + Text", nameof(CopyTimelineAndTextCommand), typeof(MainWindow));
    private static readonly TimeSpan ToastDisplayDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PlaybackEditSegmentDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PlaybackEditStopDrainDelay = TimeSpan.Zero;
    private bool _isApplyingTranscriptEditLoopSeek;
    private bool _isTranscriptEditLoopRestartPending;
    private MainViewModel? _boundViewModel;
    private CancellationTokenSource? _copyToastCts;
    private CancellationTokenSource? _transcribeAudioBatchTranscriptionCts;
    private readonly Func<PlaybackTranscriptionSession>? _playbackTranscriptionSessionFactory;
    private readonly Func<AudioInputDeviceOption, PlaybackTranscriptionSession>? _liveTranscriptionSessionFactory;
    private readonly ProcessLogService? _processLogService;
    private readonly WhisperModelManager? _whisperModelManager;
    private bool _isTranscribeAudioBatchTranscribing;
    private bool _isLiveTranscribing;
    private bool _isTranscriptProcessingMuteAvailable = true;
    private bool _isTranscriptProcessingIndeterminate = true;
    private bool _isTranscriptProcessingCanceling;
    private double _transcriptProcessingPercent;
    private string _transcriptProcessingDetail = "Preparing...";
    private string _transcriptProcessingElapsedText = "Elapsed 00:00";
    private string _transcriptProcessingEtaText = "ETA calculating";
    private string _transcriptProcessingChunkText = "Progress 0%";
    private string _transcriptProcessingAudioText = "Audio 00:00 / 00:00";
    private string _transcriptProcessingTitle = "Generating Transcript";
    private FinalizedTranscriptLineViewModel? _playbackMatchedLine;
    private FinalizedTranscriptLineViewModel? _editLoopLine;
    private FinalizedTranscriptLineViewModel? _timelineEditLine;
    private PlaybackEditTranscriptionState? _activePlaybackEditTranscription;
    private TimeSpan? _editLoopStartOffset;
    private TimeSpan? _editLoopRepeatOffset;
    private string _timelineEditOriginalTimeline = string.Empty;
    private bool _timelineEditShouldResumePlayback;
    private bool _nonTranscriptCellEditShouldResumePlayback;
    private FinalizedTranscriptLineViewModel? _transcriptTextEditLine;
    private string _transcriptTextEditOriginalText = string.Empty;
    private FinalizedTranscriptLineViewModel? _lastPlaybackSyncedLine;
    private PlaybackTranscriptionSession? _liveTranscriptionSession;
    private FinalizedTranscriptLineViewModel? _liveInterimLine;
    private DateTimeOffset _liveTranscriptionStartedUtc;
    private int _requiredUpdateShutdownStarted;

    public MainWindow(
        Func<PlaybackTranscriptionSession>? playbackTranscriptionSessionFactory = null,
        Func<AudioInputDeviceOption, PlaybackTranscriptionSession>? liveTranscriptionSessionFactory = null,
        ProcessLogService? processLogService = null,
        WhisperModelManager? whisperModelManager = null)
    {
        _playbackTranscriptionSessionFactory = playbackTranscriptionSessionFactory;
        _liveTranscriptionSessionFactory = liveTranscriptionSessionFactory;
        _processLogService = processLogService;
        _whisperModelManager = whisperModelManager;
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += OnMainWindowClosed;
        PreviewMouseDown += OnWindowMouseDismissToast;
        PreviewMouseWheel += OnWindowMouseWheelDismissToast;
        Loaded += OnMainWindowLoaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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
        }
    }

    public string TranscriptProcessingTitle
    {
        get => _transcriptProcessingTitle;
        private set
        {
            if (string.Equals(_transcriptProcessingTitle, value, StringComparison.Ordinal))
            {
                return;
            }

            _transcriptProcessingTitle = value;
            OnPropertyChanged();
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

    private async void GenerateTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if (vm.IsGenerationRunning)
        {
            return;
        }

        if (vm.IsLiveTranscriptionModeSelected)
        {
            await StartLiveTranscriptionAsync(vm);
            return;
        }

        await RunTranscribeAudioAsync(vm);
    }

    private async void StartLivePrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if (vm.IsGenerationRunning)
        {
            return;
        }

        SelectTranscriptMode(vm, TranscriptGenerationMode.Live);
        await StartLiveTranscriptionAsync(vm);
    }

    private async void TranscribeAudioPrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || IsTranscribeAudioBatchTranscribing)
        {
            return;
        }

        SelectTranscriptMode(vm, TranscriptGenerationMode.TranscribeAudio);
        await RunTranscribeAudioAsync(vm);
    }

    private async void TranscribeAudio_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || IsTranscribeAudioBatchTranscribing)
        {
            return;
        }

        await RunTranscribeAudioAsync(vm);
    }

    private static void SelectTranscriptMode(MainViewModel vm, TranscriptGenerationMode mode)
    {
        TranscriptModeOptionViewModel? option = vm.TranscriptModes
            .FirstOrDefault(candidate => candidate.Mode == mode);

        if (option is not null && !ReferenceEquals(vm.SelectedTranscriptMode, option))
        {
            vm.SelectedTranscriptMode = option;
        }
    }

    private async Task RunTranscribeAudioAsync(MainViewModel vm)
    {
        ConfigureTranscriptProcessingUi(
            title: "Transcribing Audio",
            allowMute: false);

        if (!EnsureSelectedEngineReady(vm, "Transcribe Audio"))
        {
            vm.StatusMessage = "Transcribe Audio requires a configured transcription engine.";
            return;
        }

        IsTranscribeAudioBatchTranscribing = true;
        vm.SetGenerationRunning(isRunning: true);
        _transcribeAudioBatchTranscriptionCts = new CancellationTokenSource();
        CancellationToken cancellationToken = _transcribeAudioBatchTranscriptionCts.Token;
        ApplyTranscribeAudioBatchInteractionLock();
        await Dispatcher.Yield(DispatcherPriority.Background);

        try
        {
            LogTranscribeAudioBatch("Transcribe Audio requested.");
            var transcriptionProgress = new Progress<TranscriptionProgressSnapshot>(ApplyTranscriptionProgress);

            bool completed = await vm.GenerateTranscribeAudioTranscriptAsync(cancellationToken, transcriptionProgress);
            if (cancellationToken.IsCancellationRequested)
            {
                ShowCopyToast(
                    "Transcribe Audio canceled",
                    "The automated Transcribe Audio was canceled.",
                    ToastNotificationType.Info);
                return;
            }

            if (!completed)
            {
                return;
            }
            ShowCopyToast(
                "Transcribe Audio completed",
                "Transcript rows are ready.",
                ToastNotificationType.Success);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            vm.StatusMessage = "Transcribe Audio canceled.";
            ShowCopyToast(
                "Transcribe Audio canceled",
                "The Transcribe Audio request was canceled.",
                ToastNotificationType.Info);
        }
        catch (Exception ex)
        {
            vm.LogHandledException("Transcribe Audio", ex);
            LogTranscribeAudioBatch($"Transcribe Audio failed: {ex.Message}");
            ShowCopyToast(
                "Transcribe Audio failed",
                ex.Message,
                ToastNotificationType.Error);
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
        }
    }

    public bool IsTranscriptProcessingCancelEnabled => !IsTranscriptProcessingCanceling;

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

    public string TranscriptProcessingDetail
    {
        get => _transcriptProcessingDetail;
        private set
        {
            if (string.Equals(_transcriptProcessingDetail, value, StringComparison.Ordinal))
            {
                return;
            }

            _transcriptProcessingDetail = value;
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

    private async Task StartLiveTranscriptionAsync(MainViewModel vm)
    {
        if (_liveTranscriptionSessionFactory is null)
        {
            ShowCopyToast(
                "Live transcription unavailable",
                "Live transcription is not configured.",
                ToastNotificationType.Warning);
            return;
        }

        if (!EnsureSelectedEngineReady(vm, "Live transcription"))
        {
            vm.StatusMessage = "Live transcription requires a configured transcription engine.";
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

        var picker = new LiveAudioDevicePickerWindow(devices)
        {
            Owner = this,
        };
        picker.SelectPreferredDevice(vm.PreferredLiveAudioSourceKind, vm.PreferredLiveAudioDeviceNumber);

        if (picker.ShowDialog() != true || picker.SelectedDevice is null)
        {
            return;
        }

        AudioInputDeviceOption selectedDevice = picker.SelectedDevice;
        vm.SetPreferredLiveAudioSource(selectedDevice);
        if (!vm.EnsureLiveTranscriptSession(selectedDevice.Name))
        {
            return;
        }

        ConfigureTranscriptProcessingUi(
            title: "Live Transcription",
            allowMute: false);

        PlaybackTranscriptionSession session = _liveTranscriptionSessionFactory(selectedDevice);
        _liveTranscriptionSession = session;
        _liveInterimLine = null;
        _liveTranscriptionStartedUtc = DateTimeOffset.Now;
        session.PlaybackInterimTranscriptionUpdated += OnLiveInterimTranscriptionUpdated;
        session.PlaybackFinalTranscriptionAvailable += OnLiveFinalTranscriptionAvailable;
        session.Faulted += OnLiveTranscriptionFaulted;

        try
        {
            string model = TranscriptionModelCatalog.SupportsPlaybackTranscription(vm.SelectedEngine?.Id ?? string.Empty)
                ? vm.SelectedEngine!.Id
                : TranscriptionModelCatalog.WhisperSmall;
            IsLiveTranscribing = true;
            vm.SetGenerationRunning(isRunning: true, isLiveTranscriptionRunning: true);
            vm.StatusMessage = $"Live transcription started: {selectedDevice.Name}.";
            LogLiveTranscription(
                $"Live transcription started source='{selectedDevice.Name}', kind={selectedDevice.Kind}, deviceNumber={selectedDevice.DeviceNumber}, model='{model}'.");
            session.StartPlaybackTranscription(model);
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            DetachLiveTranscriptionSession(session);
            _liveTranscriptionSession = null;
            IsLiveTranscribing = false;
            vm.SetGenerationRunning(isRunning: false);
            await DisposePlaybackEditTranscriptionSessionAsync(session);
            vm.LogHandledException("live transcription start", ex);
            ShowCopyToast(
                "Live transcription failed",
                ex.Message,
                ToastNotificationType.Error);
        }
    }

    private static IReadOnlyList<AudioInputDeviceOption> BuildLiveAudioSourceOptions()
    {
        var options = new List<AudioInputDeviceOption>();
        IReadOnlyList<AudioInputDeviceOption> microphones = MicrophoneAudioCaptureService.GetInputDevices();
        AudioInputDeviceOption? defaultMicrophone = microphones.FirstOrDefault();
        string defaultPlaybackName = GetDefaultPlaybackDeviceName();

        if (defaultMicrophone is not null)
        {
            options.Add(new AudioInputDeviceOption(
                LiveAudioSourceKind.Microphone,
                defaultMicrophone.DeviceNumber,
                $"Microphone ({defaultMicrophone.Name})"));
        }

        options.Add(new AudioInputDeviceOption(
            LiveAudioSourceKind.DefaultPlayback,
            -1,
            $"Default playback device ({defaultPlaybackName})"));

        if (defaultMicrophone is not null)
        {
            options.Add(new AudioInputDeviceOption(
                LiveAudioSourceKind.MicrophoneAndDefaultPlayback,
                defaultMicrophone.DeviceNumber,
                $"Microphone + default playback ({defaultMicrophone.Name} + {defaultPlaybackName})"));
        }

        return options;
    }

    private static string GetDefaultPlaybackDeviceName()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return string.IsNullOrWhiteSpace(device.FriendlyName)
                ? "Default Playback"
                : device.FriendlyName;
        }
        catch
        {
            return "Default Playback";
        }
    }

    private async Task StopLiveTranscriptionAsync(MainViewModel vm, bool showToast)
    {
        PlaybackTranscriptionSession? session = _liveTranscriptionSession;
        if (session is null)
        {
            IsLiveTranscribing = false;
            vm.SetGenerationRunning(isRunning: false);
            return;
        }

        try
        {
            vm.StatusMessage = "Stopping live transcription...";
            await session.StopPlaybackTranscriptionAsync();
            vm.SaveLiveTranscriptSession();
            vm.StatusMessage = "Live transcription stopped.";
            LogLiveTranscription("Live transcription stopped.");

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
            ShowCopyToast(
                "Live transcription stop failed",
                ex.Message,
                ToastNotificationType.Error);
        }
        finally
        {
            DetachLiveTranscriptionSession(session);
            _liveTranscriptionSession = null;
            _liveInterimLine = null;
            IsLiveTranscribing = false;
            vm.SetGenerationRunning(isRunning: false);
            await DisposePlaybackEditTranscriptionSessionAsync(session);
        }
    }

    private void OnLiveInterimTranscriptionUpdated(object? sender, PlaybackTranscriptionUpdate update)
    {
        Dispatcher.BeginInvoke(
            new Action(() => ApplyLiveTranscriptionUpdate(update.Text, isFinal: false)),
            DispatcherPriority.Background);
    }

    private void OnLiveFinalTranscriptionAvailable(object? sender, PlaybackTranscriptionUpdate update)
    {
        Dispatcher.BeginInvoke(
            new Action(() => ApplyLiveTranscriptionUpdate(update.Text, isFinal: true)),
            DispatcherPriority.Background);
    }

    private void OnLiveTranscriptionFaulted(object? sender, Exception ex)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.LogHandledException("live transcription", ex);
                ShowCopyToast(
                    "Live transcription failed",
                    ex.Message,
                    ToastNotificationType.Error);
                await StopLiveTranscriptionAsync(vm, showToast: false);
            }
        }), DispatcherPriority.Background);
    }

    private void ApplyLiveTranscriptionUpdate(string text, bool isFinal)
    {
        if (DataContext is not MainViewModel vm || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        TimeSpan startOffset = DateTimeOffset.Now - _liveTranscriptionStartedUtc;
        TimeSpan endOffset = startOffset + TimeSpan.FromSeconds(1);

        if (_liveInterimLine is null)
        {
            _liveInterimLine = vm.AddLiveTranscriptLine(startOffset, endOffset, text);
        }
        else
        {
            vm.UpdateLiveTranscriptLine(_liveInterimLine, endOffset, text);
        }

        if (isFinal)
        {
            LogLiveTranscription(
                $"Final update applied at {startOffset:mm\\:ss}, chars={text.Trim().Length:N0}, preview='{BuildLogPreview(text)}'.");
            _liveInterimLine = null;
            vm.SaveLiveTranscriptSession();
        }
        else
        {
            LogLiveTranscription(
                $"Interim update applied at {startOffset:mm\\:ss}, chars={text.Trim().Length:N0}, preview='{BuildLogPreview(text)}'.");
        }
    }

    private void DetachLiveTranscriptionSession(PlaybackTranscriptionSession session)
    {
        session.PlaybackInterimTranscriptionUpdated -= OnLiveInterimTranscriptionUpdated;
        session.PlaybackFinalTranscriptionAvailable -= OnLiveFinalTranscriptionAvailable;
        session.Faulted -= OnLiveTranscriptionFaulted;
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
            CancelTranscribeAudioBatch(vm);
        }
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

    private void SetupEngineModels_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || _whisperModelManager is null)
        {
            return;
        }

        var dialog = new EngineModelSetupWindow(
            _whisperModelManager,
            _processLogService ?? new ProcessLogService())
        {
            Owner = this,
        };

        dialog.ShowDialog();
        vm.RefreshEngines(_whisperModelManager.GetSelectableTranscriptionModels());
        if (dialog.HasModelChanges)
        {
            ShowCopyToast(
                "Engine models updated",
                "Installed engine choices were refreshed.",
                ToastNotificationType.Success);
        }
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

        return true;
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
            _boundViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _boundViewModel.FinalizedTranscriptLines.CollectionChanged += OnFinalizedTranscriptLinesCollectionChanged;
            UpdateTranscriptGridPresentation();
            UpdatePlaybackTimelineHighlight();
            UpdateTranscriptRowActionsVisibility();
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

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        Loaded -= OnMainWindowLoaded;
        CancelCopyToast();
        PreviewMouseDown -= OnWindowMouseDismissToast;
        PreviewMouseWheel -= OnWindowMouseWheelDismissToast;
        StopActivePlaybackEditTranscription(
            _boundViewModel,
            pausePlayback: false,
            reason: "window closed",
            discardResults: true);
        if (_liveTranscriptionSession is not null && _boundViewModel is not null)
        {
            StopLiveTranscriptionAsync(_boundViewModel, showToast: false).GetAwaiter().GetResult();
        }
        if (_boundViewModel is null)
        {
            return;
        }

        _boundViewModel.ErrorOccurred -= OnErrorOccurred;
        _boundViewModel.ConfirmationRequested -= OnConfirmationRequested;
        _boundViewModel.ToastRequested -= OnToastRequested;
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
        CopyToastHost.Margin = new Thickness(0, 0, 0, ToastBottomMargin);

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
        try
        {
            await Task.Delay(ToastDisplayDuration, toastCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (toastCts.Token.IsCancellationRequested)
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
            if (toastCts.Token.IsCancellationRequested)
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
        if (e.PropertyName == nameof(MainViewModel.SelectedThemePreference))
        {
            // The view-model property changed event is raised before Application.ThemeMode settles.
            // Re-apply after bindings and theme updates complete to keep floating surfaces in sync.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplyFloatingSurfaceTheme();
                ApplySelectionControlTheme();
            }), DispatcherPriority.Background);
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.SelectedTranscriptMode))
        {
            StopActivePlaybackEditTranscription(
                _boundViewModel,
                pausePlayback: false,
                reason: "transcript mode changed",
                discardResults: true);
            ClearTranscriptTextEditState();
            ClearTimelineEditState();
            ClearTranscriptEditPlaybackLoop();

            UpdateTranscriptGridPresentation();
            UpdatePlaybackTimelineHighlight();
            UpdateTranscriptRowActionsVisibility();

            return;
        }

        if (e.PropertyName == nameof(MainViewModel.SelectedTranscriptViewIndex))
        {
            UpdateTranscriptRowActionsVisibility();
        }

        if (e.PropertyName is nameof(MainViewModel.AudioSeekPositionSeconds)
            or nameof(MainViewModel.LoadedAudioFilePath)
            or nameof(MainViewModel.IsAudioFileLoaded))
        {
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

        TranscribeAudioBatchCard.Background = backgroundBrush;
        TranscribeAudioBatchCard.BorderBrush = borderBrush;
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
        UpdatePlaybackTimelineHighlight();
        UpdateTranscriptRowActionsVisibility();
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

    private void RecentSessionsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox listBox || DataContext is not MainViewModel vm)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(listBox, e.OriginalSource as DependencyObject) is not ListBoxItem)
        {
            return;
        }

        if (!vm.OpenSelectedSessionCommand.CanExecute(null))
        {
            return;
        }

        vm.OpenSelectedSessionCommand.Execute(null);
        e.Handled = true;
    }

    private void ConfigureTranscriptProcessingUi(string title, bool allowMute)
    {
        TranscriptProcessingTitle = title;
        IsTranscriptProcessingMuteAvailable = allowMute;
        ResetTranscriptProcessingProgress();
    }

    private void ResetTranscriptProcessingProgress()
    {
        IsTranscriptProcessingCanceling = false;
        IsTranscriptProcessingIndeterminate = true;
        TranscriptProcessingPercent = 0;
        TranscriptProcessingDetail = "Preparing...";
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
        IsTranscriptProcessingIndeterminate = snapshot.TotalAudio <= TimeSpan.Zero
            && snapshot.Phase != TranscriptionProgressPhase.Completed;
        TranscriptProcessingDetail = string.IsNullOrWhiteSpace(snapshot.DetailMessage)
            ? FormatTranscriptionPhase(snapshot.Phase)
            : snapshot.DetailMessage;
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
        IsTranscriptProcessingCanceling = true;
        IsTranscriptProcessingIndeterminate = true;
        TranscriptProcessingDetail = "Canceling...";
        TranscriptProcessingEtaText = "ETA canceled";
    }

    private static string FormatProgressChunkText(TranscriptionProgressSnapshot snapshot)
    {
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
        TranscribeAudioBatchOverlay.Visibility = Visibility.Visible;
        TranscribeAudioBatchOverlay.IsHitTestVisible = true;
        TranscribeAudioBatchOverlay.UpdateLayout();

        MainContentHost.IsHitTestVisible = false;

        try
        {
            FinalizedTranscriptGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            FinalizedTranscriptGrid.CommitEdit(DataGridEditingUnit.Row, true);
        }
        catch
        {
            // Best-effort edit shutdown.
        }

        ClearTimelineEditState();
        ClearTranscriptEditPlaybackLoop();
        FinalizedTranscriptGrid.SelectedCells.Clear();
        FinalizedTranscriptGrid.UnselectAllCells();
        FinalizedTranscriptGrid.CurrentCell = default;
        Keyboard.ClearFocus();

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            TranscribeAudioBatchCancelButton.Focus();
            TranscribeAudioBatchCancelButton.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            TranscribeAudioBatchCancelButton.Focus();
        }), DispatcherPriority.Input);
    }

    private void RestoreTranscribeAudioBatchInteractionLock()
    {
        TranscribeAudioBatchOverlay.IsHitTestVisible = false;
        TranscribeAudioBatchOverlay.Visibility = Visibility.Collapsed;
        IsTranscriptProcessingMuteAvailable = true;
        TranscriptProcessingTitle = "Generating Transcript";

        MainContentHost.IsHitTestVisible = true;

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            FinalizedTranscriptGrid.SelectedCells.Clear();
            FinalizedTranscriptGrid.UnselectAllCells();
            FinalizedTranscriptGrid.CurrentCell = default;
        }), DispatcherPriority.Background);
    }

    private void CancelTranscribeAudioBatch(MainViewModel vm)
    {
        if (!IsTranscribeAudioBatchTranscribing)
        {
            return;
        }

        if (_transcribeAudioBatchTranscriptionCts is not null && !_transcribeAudioBatchTranscriptionCts.IsCancellationRequested)
        {
            ApplyTranscriptProcessingCancelingState();
            _transcribeAudioBatchTranscriptionCts.Cancel();
            LogTranscribeAudioBatch("Transcribe Audio cancellation requested.");
        }

        StopActivePlaybackEditTranscription(
            vm,
            pausePlayback: true,
            reason: "transcribe audio batch canceled",
            discardResults: true);
    }

    public async Task PrepareForRequiredUpdateShutdownAsync()
    {
        if (Interlocked.Exchange(ref _requiredUpdateShutdownStarted, 1) != 0)
        {
            return;
        }

        MainViewModel? vm = _boundViewModel ?? DataContext as MainViewModel;
        LogPlaybackEdit("Preparing for required update shutdown.");
        LogTranscribeAudioBatch("Preparing for required update shutdown.");

        if (_liveTranscriptionSession is not null && vm is not null)
        {
            await StopLiveTranscriptionAsync(vm, showToast: false);
        }

        if (_transcribeAudioBatchTranscriptionCts is not null && !_transcribeAudioBatchTranscriptionCts.IsCancellationRequested)
        {
            ApplyTranscriptProcessingCancelingState();
            _transcribeAudioBatchTranscriptionCts.Cancel();
        }

        PlaybackEditTranscriptionState? state = _activePlaybackEditTranscription;
        if (state is not null)
        {
            await StopPlaybackEditTranscriptionAsync(
                state,
                vm,
                pausePlayback: true,
                reason: "application update required",
                discardResults: true);
        }

        vm?.PrepareForRequiredUpdateShutdown();

        TranscribeAudioBatchOverlay.IsHitTestVisible = false;
        TranscribeAudioBatchOverlay.Visibility = Visibility.Collapsed;
        MainContentHost.IsEnabled = false;
        MainContentHost.IsHitTestVisible = false;

        ClearTimelineEditState();
        ClearTranscriptEditPlaybackLoop();

        try
        {
            FinalizedTranscriptGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            FinalizedTranscriptGrid.CommitEdit(DataGridEditingUnit.Row, true);
        }
        catch
        {
            // Best-effort edit shutdown.
        }

        FinalizedTranscriptGrid.SelectedCells.Clear();
        FinalizedTranscriptGrid.UnselectAllCells();
        FinalizedTranscriptGrid.CurrentCell = default;
        Keyboard.ClearFocus();
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

        HandleDeleteTranscriptRow(vm, currentLine);
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
        e.CanExecute = DataContext is MainViewModel;
        e.Handled = true;
    }

    private void InsertRowAboveCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!TryGetContextMenuLine(e.Parameter, out FinalizedTranscriptLineViewModel currentLine)
            || DataContext is not MainViewModel vm)
        {
            return;
        }

        HandleInsertTranscriptRow(vm, currentLine, insertBelow: false, duplicateText: false);
    }

    private void InsertRowBelowCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!TryGetContextMenuLine(e.Parameter, out FinalizedTranscriptLineViewModel currentLine)
            || DataContext is not MainViewModel vm)
        {
            return;
        }

        HandleInsertTranscriptRow(vm, currentLine, insertBelow: true, duplicateText: false);
    }

    private void DuplicateRowCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!TryGetContextMenuLine(e.Parameter, out FinalizedTranscriptLineViewModel currentLine)
            || DataContext is not MainViewModel vm)
        {
            return;
        }

        HandleInsertTranscriptRow(vm, currentLine, insertBelow: true, duplicateText: true);
    }

    private void DeleteRowCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!TryGetContextMenuLine(e.Parameter, out FinalizedTranscriptLineViewModel currentLine)
            || DataContext is not MainViewModel vm)
        {
            return;
        }

        HandleDeleteTranscriptRow(vm, currentLine);
    }

    private void CopyTextCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!TryGetContextMenuLine(e.Parameter, out FinalizedTranscriptLineViewModel currentLine))
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

    private void CopyTimelineAndTextCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!TryGetContextMenuLine(e.Parameter, out FinalizedTranscriptLineViewModel currentLine))
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

    private bool EnsureSegmentRowActionAvailable(MainViewModel vm, string actionTitle)
    {
        if (IsTranscribeAudioBatchTranscribing)
        {
            ShowCopyToast(
                "Transcribe Audio in progress",
                "Wait for the automated row transcription to finish.",
                ToastNotificationType.Info);
            return false;
        }

        if (!vm.IsTranscribeAudioTranscriptViewSelected)
        {
            ShowCopyToast(
                actionTitle,
                "Switch to Transcribe Audio mode to edit timeline rows.",
                ToastNotificationType.Info);
            return false;
        }

        return true;
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

    private void DeleteTranscriptRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button
            || button.DataContext is not FinalizedTranscriptLineViewModel currentLine
            || DataContext is not MainViewModel vm)
        {
            return;
        }

        HandleDeleteTranscriptRow(vm, currentLine);
    }

    private void HandleDeleteTranscriptRow(MainViewModel vm, FinalizedTranscriptLineViewModel currentLine)
    {
        if (!EnsureSegmentRowActionAvailable(vm, "Row action unavailable"))
        {
            return;
        }

        if (IsTranscribeAudioBatchTranscribing)
        {
            ShowCopyToast(
                "Transcribe Audio in progress",
                "Wait for the automated row transcription to finish.",
                ToastNotificationType.Info);
            return;
        }

        var dialog = new ConfirmationDialogWindow(
            title: "Delete this Row?",
            message: "This transcript row will be removed.",
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
            List<FinalizedTranscriptLineViewModel> displayedLines = GetDisplayedTranscriptLines();
            int currentIndex = displayedLines.IndexOf(currentLine);
            FinalizedTranscriptLineViewModel? nextFocusLine = null;

            if (currentIndex >= 0)
            {
                if (currentIndex + 1 < displayedLines.Count)
                {
                    nextFocusLine = displayedLines[currentIndex + 1];
                }
                else if (currentIndex - 1 >= 0)
                {
                    nextFocusLine = displayedLines[currentIndex - 1];
                }
            }

            SetTranscriptRowActionsLine(null);

            if (!vm.RemoveFinalizedTranscriptLine(currentLine))
            {
                ShowCopyToast(
                    "Row not deleted",
                    "Unable to remove the selected row.",
                    ToastNotificationType.Error);
                return;
            }

            if (nextFocusLine is not null && vm.FinalizedTranscriptLines.Contains(nextFocusLine))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    FocusGridCell(nextFocusLine, TranscriptTextColumnIndex, beginEdit: false);
                }), DispatcherPriority.Background);
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(UpdateTranscriptRowActionsVisibility), DispatcherPriority.Background);
            }
        }
        catch (Exception ex)
        {
            vm.LogHandledException("delete transcript row", ex);
            ShowCopyToast(
                "Row not deleted",
                "Unable to remove the selected row.",
                ToastNotificationType.Error);
        }
    }

    private void FinalizedTranscriptGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (IsTranscribeAudioBatchTranscribing)
        {
            ClearTranscriptTextEditState();
            e.Cancel = true;
            return;
        }

        if (DataContext is not MainViewModel vm
            || e.Row?.Item is not FinalizedTranscriptLineViewModel line)
        {
            ClearTranscriptTextEditState();
            StopActivePlaybackEditTranscription(
                _boundViewModel,
                pausePlayback: false,
                reason: "transcript edit unavailable",
                discardResults: true);
            ClearTimelineEditState();
            ClearTranscriptEditPlaybackLoop();
            return;
        }

        if (line.IsPlaybackEditTranscribing)
        {
            ClearTranscriptTextEditState();
            e.Cancel = true;
            Dispatcher.BeginInvoke(new Action(UpdateTranscriptRowActionsVisibility), DispatcherPriority.Background);
            return;
        }

        if (e.Column?.IsReadOnly == true)
        {
            ClearTranscriptTextEditState();
            e.Cancel = true;
            return;
        }

        bool isTranscriptTextColumn = IsTranscriptTextColumn(e.Column);
        bool isAuxiliaryEditableColumn = !isTranscriptTextColumn && !IsTimelineColumn(e.Column);

        if (IsTimelineColumn(e.Column))
        {
            ClearTranscriptTextEditState();
            BeginTimelineEdit(vm, line);
            return;
        }

        if (!vm.IsTranscribeAudioTranscriptViewSelected)
        {
            ClearTimelineEditState();
            StopActivePlaybackEditTranscription(
                vm,
                pausePlayback: false,
                reason: "speaker transcript edit started",
                discardResults: true);

            if (!isTranscriptTextColumn)
            {
                ClearTranscriptTextEditState();
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
            StopActivePlaybackEditTranscription(
                vm,
                pausePlayback: false,
                reason: "starting another non-transcript edit",
                discardResults: true);
            ClearTimelineEditState();
            BeginNonTranscriptCellEdit(vm);
            return;
        }

        ClearTimelineEditState();
        StopActivePlaybackEditTranscription(
            vm,
            pausePlayback: false,
            reason: "starting another transcript edit",
            discardResults: true);

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
        if (IsTimelineColumn(e.Column))
        {
            HandleTimelineCellEditEnding(e);
            return;
        }

        if (!IsTranscriptTextColumn(e.Column))
        {
            HandleNonTranscriptCellEditEnding(e);
            return;
        }

        if (_activePlaybackEditTranscription is not null
            && e.Row?.Item is FinalizedTranscriptLineViewModel activeLine
            && ReferenceEquals(activeLine, _activePlaybackEditTranscription.Line))
        {
            StopActivePlaybackEditTranscription(
                DataContext as MainViewModel,
                pausePlayback: true,
                reason: e.EditAction == DataGridEditAction.Cancel
                    ? "transcript edit canceled"
                    : "transcript edit completed",
                discardResults: e.EditAction == DataGridEditAction.Cancel);
            ClearTranscriptTextEditState();
            Dispatcher.BeginInvoke(new Action(UpdateTranscriptRowActionsVisibility), DispatcherPriority.Background);
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

        if (DataContext is MainViewModel vm
            && IsManualSegmentKeyboardFlowEnabled(vm)
            && HandleManualSegmentCommandShortcuts(vm, e))
        {
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
                HandleDeleteTranscriptRow(vm, currentLine);
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
        return !IsTranscribeAudioBatchTranscribing
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

            if (!textBox.AcceptsReturn)
            {
                textBox.CaretIndex = textBox.Text?.Length ?? 0;
                textBox.SelectionLength = 0;
            }
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

    private void UpdateTranscriptGridPresentation()
    {
        bool showSpeakerColumn = _boundViewModel?.IsTranscribeAudioModeSelected == true;

        if (TimelineTranscriptColumn is not null)
        {
            TimelineTranscriptColumn.IsReadOnly = false;
        }

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

            if (!vm.IsAudioPlaying
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

    private void BeginTimelineEdit(MainViewModel vm, FinalizedTranscriptLineViewModel line)
    {
        ClearNonTranscriptCellEditState();
        ClearTranscriptEditPlaybackLoop();
        _timelineEditLine = line;
        _timelineEditOriginalTimeline = line.Timeline ?? string.Empty;
        _timelineEditShouldResumePlayback = vm.AutoPlayTimelineSelection && vm.IsAudioPlaying;

        if (vm.IsAudioPlaying)
        {
            vm.EnsureAudioPreviewPaused();
        }
    }

    private void HandleTimelineCellEditEnding(DataGridCellEditEndingEventArgs e)
    {
        if (DataContext is not MainViewModel vm
            || e.Row?.Item is not FinalizedTranscriptLineViewModel line)
        {
            ClearTimelineEditState();
            return;
        }

        System.Windows.Controls.TextBox? textBox = TryGetTimelineEditingTextBox(e.EditingElement);

        if (e.EditAction == DataGridEditAction.Cancel)
        {
            RestoreTimelineEditDisplay(textBox);
            ResumePlaybackAfterTimelineEdit(vm);
            ClearTimelineEditState();
            return;
        }

        if (textBox is null)
        {
            e.Cancel = true;
            ShowCopyToast(
                "Timeline not updated",
                "The timeline editor could not be validated. Try editing the value again.",
                ToastNotificationType.Warning);
            return;
        }

        if (!TryValidateTimelineEdit(vm, line, textBox.Text, out string normalized, out string validationMessage))
        {
            e.Cancel = true;
            ShowCopyToast("Timeline not updated", validationMessage, ToastNotificationType.Warning);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                textBox.Focus();
                textBox.SelectAll();
            }), DispatcherPriority.Input);
            return;
        }

        textBox.Text = normalized;
        textBox.Tag = normalized;

        BindingExpression? binding = textBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty);
        binding?.UpdateSource();

        if (!string.Equals(_timelineEditOriginalTimeline, normalized, StringComparison.Ordinal))
        {
            line.IsManuallyReviewed = true;
        }

        ResumePlaybackAfterTimelineEdit(vm);
        ClearTimelineEditState();
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

    private System.Windows.Controls.TextBox? TryGetTimelineEditingTextBox(FrameworkElement? editingElement)
    {
        if (editingElement is null)
        {
            return null;
        }

        if (editingElement is System.Windows.Controls.TextBox textBox)
        {
            return textBox;
        }

        return FindVisualChild<System.Windows.Controls.TextBox>(editingElement);
    }

    private void RestoreTimelineEditDisplay(System.Windows.Controls.TextBox? textBox)
    {
        if (textBox is null)
        {
            return;
        }

        string fallbackTimeline = string.IsNullOrWhiteSpace(_timelineEditOriginalTimeline)
            ? "00:00"
            : _timelineEditOriginalTimeline;
        textBox.Text = fallbackTimeline;
        textBox.Tag = fallbackTimeline;
        textBox.SelectionLength = 0;
        textBox.CaretIndex = 0;
    }

    private void ResumePlaybackAfterTimelineEdit(MainViewModel vm)
    {
        if (_timelineEditShouldResumePlayback && vm.PlayAudioCommand.CanExecute(null))
        {
            vm.PlayAudioCommand.Execute(null);
        }
    }

    private void ResumePlaybackAfterNonTranscriptCellEdit(MainViewModel vm)
    {
        if (_nonTranscriptCellEditShouldResumePlayback && vm.PlayAudioCommand.CanExecute(null))
        {
            vm.PlayAudioCommand.Execute(null);
        }
    }

    private void ClearTimelineEditState()
    {
        _timelineEditLine = null;
        _timelineEditOriginalTimeline = string.Empty;
        _timelineEditShouldResumePlayback = false;
    }

    private void ClearNonTranscriptCellEditState()
    {
        _nonTranscriptCellEditShouldResumePlayback = false;
    }

    private void ClearTranscriptTextEditState()
    {
        _transcriptTextEditLine = null;
        _transcriptTextEditOriginalText = string.Empty;
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

        if (!TryResolvePlaybackEditStopOffset(vm, line, out TimeSpan stopOffset))
        {
            LogPlaybackEdit(
                $"Playback transcription for row '{line.Timeline}' was skipped because the 10-second capture window could not be resolved.");
            return false;
        }

        var session = _playbackTranscriptionSessionFactory.Invoke();
        var state = new PlaybackEditTranscriptionState(
            session,
            line,
            line.StartOffset.Value,
            stopOffset,
            completionSource);

        SetPlaybackEditTranscriptionVisualState(line, isActive: true, progressPercent: 0, isIndeterminate: false);
        state.FinalHandler = (_, update) => BufferPlaybackEditTranscriptionFinal(state, update);
        state.FaultHandler = (_, ex) => Dispatcher.BeginInvoke(
            new Action(() => HandlePlaybackEditTranscriptionFault(state, ex)),
            DispatcherPriority.Background);

        session.PlaybackFinalTranscriptionAvailable += state.FinalHandler;
        session.Faulted += state.FaultHandler;

        _activePlaybackEditTranscription = state;
        ClearTranscriptEditPlaybackLoop();

        try
        {
            vm.SeekAudioPreview(state.StartOffset);
            UpdatePlaybackEditTranscriptionProgress();
            session.StartPlaybackTranscription(selectedModel);

            if (!vm.IsAudioPlaying && vm.PlayAudioCommand.CanExecute(null))
            {
                vm.PlayAudioCommand.Execute(null);
            }

            LogPlaybackEdit(
                $"Started playback transcription for empty row '{line.Timeline}' " +
                $"for exactly {PlaybackEditSegmentDuration.TotalSeconds:F0} seconds " +
                $"(stop at {FormatPlaybackEditOffset(stopOffset)}) using model '{selectedModel}'.");
            return true;
        }
        catch (Exception ex)
        {
            SetPlaybackEditTranscriptionVisualState(line, isActive: false, progressPercent: 0, isIndeterminate: false);
            _activePlaybackEditTranscription = null;
            DetachPlaybackEditTranscriptionState(state);
            _ = DisposePlaybackEditTranscriptionSessionAsync(session);
            vm.LogHandledException("playback edit transcription start", ex);
            LogPlaybackEdit(
                $"Unable to start playback transcription for row '{line.Timeline}': {ex.Message}");
            return false;
        }
    }

    private bool TryResolvePlaybackEditStopOffset(
        MainViewModel vm,
        FinalizedTranscriptLineViewModel line,
        out TimeSpan stopOffset)
    {
        stopOffset = TimeSpan.Zero;

        if (line.StartOffset is not TimeSpan startOffset)
        {
            return false;
        }

        stopOffset = startOffset + PlaybackEditSegmentDuration;

        double audioDurationSeconds = Math.Max(vm.AudioSeekMaximumSeconds, 0);
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

            return true;
        }

        return stopOffset > startOffset;
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
            reason: "playback reached 10-second capture boundary");
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
        LogPlaybackEdit(
            $"Stopping playback transcription for row '{state.Line.Timeline}' ({reason}).");

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
                LogPlaybackEdit(
                    $"Playback transcription stop failed for row '{state.Line.Timeline}': {ex.Message}");
            }));
        }
        finally
        {
            await DisposePlaybackEditTranscriptionSessionAsync(state.Session).ConfigureAwait(false);

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

        state.Line.Text = mergedText;
        state.Line.IsManuallyReviewed = false;

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
        LogPlaybackEdit(
            $"Playback transcription failed for row '{state.Line.Timeline}': {ex.Message}");

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

        if (discardResults)
        {
            LogPlaybackEdit(
                $"Discarded playback transcription results for row '{state.Line.Timeline}'.");
            return;
        }

        if (state.FinalSegments.Count > 0)
        {
            LogPlaybackEdit(
                $"Playback transcription completed for row '{state.Line.Timeline}' " +
                $"and inserted {state.FinalSegments.Count:N0} finalized segment(s).");
            return;
        }

        LogPlaybackEdit(
            $"Playback transcription completed for row '{state.Line.Timeline}' with no finalized text.");
    }

    private void DetachPlaybackEditTranscriptionState(PlaybackEditTranscriptionState state)
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

    private static async Task DisposePlaybackEditTranscriptionSessionAsync(PlaybackTranscriptionSession session)
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

    private void LogPlaybackEdit(string message)
    {
        _processLogService?.Log("PlaybackEdit", message);
    }

    private void LogTranscribeAudioBatch(string message)
    {
        _processLogService?.Log("TranscribeAudioBatch", message);
    }

    private void LogLiveTranscription(string message)
    {
        _processLogService?.Log("LiveTranscription", message);
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

    private static string FormatPlaybackEditOffset(TimeSpan value)
    {
        if (value.TotalHours >= 1)
        {
            return value.ToString(@"hh\:mm\:ss");
        }

        return value.ToString(@"mm\:ss");
    }

    private bool TryValidateTimelineEdit(
        MainViewModel vm,
        FinalizedTranscriptLineViewModel line,
        string? candidateTimeline,
        out string normalized,
        out string validationMessage)
    {
        normalized = string.Empty;
        validationMessage = string.Empty;

        if (!FinalizedTranscriptLineViewModel.TryNormalizeTimeline(candidateTimeline, out normalized)
            || !FinalizedTranscriptLineViewModel.TryParseTimeline(normalized, out TimeSpan candidateOffset))
        {
            validationMessage = "Timeline must use the strict 00:00 format, and seconds must stay within 00 to 59.";
            return false;
        }

        List<FinalizedTranscriptLineViewModel> displayedLines = GetDisplayedTranscriptLines();
        int currentIndex = displayedLines.IndexOf(line);
        if (currentIndex < 0)
        {
            validationMessage = "The timeline row could not be validated. Try editing the row again.";
            return false;
        }

        FinalizedTranscriptLineViewModel? previousLine = FindNeighborTimelineLine(displayedLines, currentIndex, searchBackward: true);
        if (TryGetLineTimelineOffset(previousLine, out TimeSpan previousOffset) && candidateOffset <= previousOffset)
        {
            string previousTimeline = previousLine?.Timeline ?? "the previous row";
            validationMessage = $"Timeline must be later than the previous row ({previousTimeline}).";
            return false;
        }

        FinalizedTranscriptLineViewModel? nextLine = FindNeighborTimelineLine(displayedLines, currentIndex, searchBackward: false);
        if (TryGetLineTimelineOffset(nextLine, out TimeSpan nextOffset) && candidateOffset >= nextOffset)
        {
            string nextTimeline = nextLine?.Timeline ?? "the next row";
            validationMessage = $"Timeline must be earlier than the next row ({nextTimeline}).";
            return false;
        }

        return true;
    }

    private static FinalizedTranscriptLineViewModel? FindNeighborTimelineLine(
        IReadOnlyList<FinalizedTranscriptLineViewModel> lines,
        int currentIndex,
        bool searchBackward)
    {
        if (searchBackward)
        {
            for (int index = currentIndex - 1; index >= 0; index--)
            {
                if (lines[index].StartOffset is not null)
                {
                    return lines[index];
                }
            }

            return null;
        }

        for (int index = currentIndex + 1; index < lines.Count; index++)
        {
            if (lines[index].StartOffset is not null)
            {
                return lines[index];
            }
        }

        return null;
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

    private void TimelineEditingTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox)
        {
            return;
        }

        string normalized = NormalizeTimelineEditingText(textBox.Text, textBox.Tag as string);
        textBox.Tag = normalized;
        textBox.Text = normalized;
        textBox.CaretIndex = 0;
        textBox.SelectionLength = 0;
    }

    private void TimelineEditingTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!textBox.IsKeyboardFocusWithin)
            {
                return;
            }

            int caretIndex = GetEditableTimelineIndexAtOrAfter(textBox.CaretIndex);
            textBox.CaretIndex = caretIndex >= 0 ? caretIndex : 0;
            textBox.SelectionLength = 0;
        }), DispatcherPriority.Input);
    }

    private void TimelineEditingTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(e.Text) || e.Text.Any(character => !char.IsAsciiDigit(character)))
        {
            e.Handled = true;
            return;
        }

        ReplaceTimelineDigits(textBox, e.Text);
        e.Handled = true;
    }

    private void TimelineEditingTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox)
        {
            return;
        }

        if (e.Key == Key.Space)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back)
        {
            ZeroTimelineDigits(textBox, deleteForward: false);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete)
        {
            ZeroTimelineDigits(textBox, deleteForward: true);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Left)
        {
            MoveTimelineCaret(textBox, moveForward: false);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Right)
        {
            MoveTimelineCaret(textBox, moveForward: true);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Home)
        {
            textBox.CaretIndex = 0;
            textBox.SelectionLength = 0;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.End)
        {
            textBox.CaretIndex = 5;
            textBox.SelectionLength = 0;
            e.Handled = true;
        }
    }

    private void TimelineEditingTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox)
        {
            e.CancelCommand();
            return;
        }

        string pastedText = e.SourceDataObject.GetData(System.Windows.DataFormats.UnicodeText) as string
            ?? e.SourceDataObject.GetData(System.Windows.DataFormats.Text) as string
            ?? string.Empty;

        if (!TryNormalizePastedTimeline(pastedText, out string normalized))
        {
            e.CancelCommand();
            return;
        }

        textBox.Text = normalized;
        textBox.CaretIndex = 5;
        textBox.SelectionLength = 0;
        e.CancelCommand();
    }

    private void ReplaceTimelineDigits(System.Windows.Controls.TextBox textBox, string digits)
    {
        char[] characters = EnsureMaskedTimelineEditingText(textBox.Text, textBox.Tag as string).ToCharArray();
        int replacementIndex = ResolveTimelineReplacementIndex(textBox);

        foreach (char digit in digits)
        {
            if (replacementIndex < 0 || replacementIndex >= characters.Length)
            {
                break;
            }

            characters[replacementIndex] = digit;
            replacementIndex = GetEditableTimelineIndexAfter(replacementIndex);
        }

        textBox.Text = new string(characters);
        textBox.SelectionLength = 0;
        textBox.CaretIndex = replacementIndex >= 0 ? replacementIndex : 5;
    }

    private void ZeroTimelineDigits(System.Windows.Controls.TextBox textBox, bool deleteForward)
    {
        char[] characters = EnsureMaskedTimelineEditingText(textBox.Text, textBox.Tag as string).ToCharArray();

        if (textBox.SelectionLength > 0)
        {
            bool updated = false;

            for (int index = textBox.SelectionStart; index < textBox.SelectionStart + textBox.SelectionLength && index < characters.Length; index++)
            {
                if (!IsEditableTimelineIndex(index))
                {
                    continue;
                }

                characters[index] = '0';
                updated = true;
            }

            if (!updated)
            {
                return;
            }

            textBox.Text = new string(characters);
            textBox.CaretIndex = GetEditableTimelineIndexAtOrAfter(textBox.SelectionStart) is int selectionIndex && selectionIndex >= 0
                ? selectionIndex
                : 5;
            textBox.SelectionLength = 0;
            return;
        }

        int targetIndex = deleteForward
            ? GetEditableTimelineIndexAtOrAfter(textBox.CaretIndex)
            : GetEditableTimelineIndexBefore(textBox.CaretIndex);

        if (targetIndex < 0)
        {
            return;
        }

        characters[targetIndex] = '0';
        textBox.Text = new string(characters);
        textBox.CaretIndex = deleteForward ? targetIndex : targetIndex + 1;
        textBox.SelectionLength = 0;
    }

    private void MoveTimelineCaret(System.Windows.Controls.TextBox textBox, bool moveForward)
    {
        int nextIndex = moveForward
            ? GetEditableTimelineIndexAtOrAfter(textBox.CaretIndex + 1)
            : GetEditableTimelineIndexBefore(textBox.CaretIndex);

        if (nextIndex < 0)
        {
            nextIndex = moveForward ? 5 : 0;
        }

        textBox.CaretIndex = nextIndex;
        textBox.SelectionLength = 0;
    }

    private static string NormalizeTimelineEditingText(string? currentText, string? fallbackText)
    {
        if (TryNormalizePastedTimeline(currentText, out string normalized))
        {
            return normalized;
        }

        if (TryNormalizePastedTimeline(fallbackText, out normalized))
        {
            return normalized;
        }

        return "00:00";
    }

    private static string EnsureMaskedTimelineEditingText(string? currentText, string? fallbackText)
    {
        string trimmed = currentText?.Trim() ?? string.Empty;

        if (trimmed.Length == 5
            && trimmed[2] == ':'
            && char.IsAsciiDigit(trimmed[0])
            && char.IsAsciiDigit(trimmed[1])
            && char.IsAsciiDigit(trimmed[3])
            && char.IsAsciiDigit(trimmed[4]))
        {
            return trimmed;
        }

        return NormalizeTimelineEditingText(currentText, fallbackText);
    }

    private static bool TryNormalizePastedTimeline(string? value, out string normalized)
    {
        normalized = string.Empty;

        if (FinalizedTranscriptLineViewModel.TryNormalizeTimeline(value, out normalized))
        {
            return true;
        }

        string digitsOnly = new string((value ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (digitsOnly.Length != 4)
        {
            return false;
        }

        return FinalizedTranscriptLineViewModel.TryNormalizeTimeline(
            $"{digitsOnly[..2]}:{digitsOnly[2..]}",
            out normalized);
    }

    private static int ResolveTimelineReplacementIndex(System.Windows.Controls.TextBox textBox)
    {
        if (textBox.SelectionLength > 0)
        {
            for (int index = textBox.SelectionStart; index < textBox.SelectionStart + textBox.SelectionLength; index++)
            {
                if (IsEditableTimelineIndex(index))
                {
                    return index;
                }
            }
        }

        return GetEditableTimelineIndexAtOrAfter(textBox.CaretIndex);
    }

    private static int GetEditableTimelineIndexAtOrAfter(int index)
    {
        if (index <= 0)
        {
            return 0;
        }

        if (index <= 1)
        {
            return index;
        }

        if (index <= 3)
        {
            return 3;
        }

        if (index <= 4)
        {
            return 4;
        }

        return -1;
    }

    private static int GetEditableTimelineIndexAfter(int index)
    {
        return index switch
        {
            < 0 => 0,
            0 => 1,
            1 => 3,
            2 => 3,
            3 => 4,
            _ => -1,
        };
    }

    private static int GetEditableTimelineIndexBefore(int index)
    {
        return index switch
        {
            <= 0 => -1,
            1 => 0,
            2 => 1,
            3 => 1,
            4 => 3,
            _ => 4,
        };
    }

    private static bool IsEditableTimelineIndex(int index)
    {
        return index is 0 or 1 or 3 or 4;
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
        return _transcriptTextEditLine is not null || _timelineEditLine is not null;
    }

    private void SyncPlaybackToCurrentTranscriptRow()
    {
        if (DataContext is not MainViewModel vm
            || !vm.IsTranscribeAudioTranscriptViewSelected
            || !vm.IsAudioFileLoaded
            || IsTranscribeAudioBatchTranscribing
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
            if (shouldAutoPlay && !vm.IsAudioPlaying && vm.PlayAudioCommand.CanExecute(null))
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






