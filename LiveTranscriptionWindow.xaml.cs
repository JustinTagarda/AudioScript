using System.Windows;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioScript.Audio;

namespace AudioScript;

public partial class LiveTranscriptionWindow : Window
{
    private enum StageProgressMode
    {
        AudioLevel,
        DrainPendingChunks,
        CancelPendingChunks,
    }

    private readonly Func<AudioInputDeviceOption, Task<bool>> _startTranscriptionAsync;
    private readonly Func<Task> _stopTranscriptionAsync;
    private readonly Func<Task<bool>> _closeTranscriptionAsync;
    private readonly Func<Task>? _escalateCloseAsync;
    private readonly Action<LiveAudioGainOptions>? _persistGainOptions;
    private bool _isTranscribing;
    private bool _isStopping;
    private bool _isOperationPending;
    private bool _allowClose;
    private bool _autoCloseAfterStop;
    private bool _closeRequestedWhileStopping;
    private bool _closeEscalationRequested;
    private bool _recordingSavedForClose;
    private bool _isSystemCloseEnabled = true;
    private StageProgressMode _stageProgressMode = StageProgressMode.AudioLevel;
    private int _drainInitialPendingChunks;
    private int _drainCompletedChunks;
    private int _latestGeneratedChunks;
    private int _latestTranscribedChunks;
    private int _latestPendingChunks;
    private readonly DispatcherTimer _autoClosePollTimer;
    private LiveAudioGainOptions _gainOptions = LiveAudioGainOptions.Default;

    public LiveTranscriptionWindow(
        IReadOnlyList<AudioInputDeviceOption> devices,
        Func<AudioInputDeviceOption, Task<bool>> startTranscriptionAsync,
        Func<Task> stopTranscriptionAsync,
        Action<LiveAudioGainOptions>? persistGainOptions = null,
        Func<Task<bool>>? closeTranscriptionAsync = null,
        Func<Task>? escalateCloseAsync = null)
    {
        _startTranscriptionAsync = startTranscriptionAsync;
        _stopTranscriptionAsync = stopTranscriptionAsync;
        _closeTranscriptionAsync = closeTranscriptionAsync ?? StopAndAllowCloseAsync;
        _escalateCloseAsync = escalateCloseAsync;
        _persistGainOptions = persistGainOptions;
        _autoClosePollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _autoClosePollTimer.Tick += AutoClosePollTimer_Tick;
        InitializeComponent();
        DeviceComboBox.ItemsSource = devices;
        DeviceComboBox.SelectedIndex = devices.Count > 0 ? 0 : -1;
        SourceInitialized += OnSourceInitialized;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        SetIdleActivity();
    }

    public AudioInputDeviceOption? SelectedDevice =>
        DeviceComboBox.SelectedItem as AudioInputDeviceOption;

    public bool IsTranscribing => _isTranscribing;

    public LiveAudioGainOptions CurrentGainOptions =>
        _gainOptions;

    public void SelectPreferredDevice(LiveAudioSourceKind preferredKind, int preferredDeviceNumber)
    {
        AudioInputDeviceOption? preferred = DeviceComboBox.Items
            .OfType<AudioInputDeviceOption>()
            .FirstOrDefault(item =>
                item.Kind == preferredKind
                && item.DeviceNumber == preferredDeviceNumber);

        if (preferred is not null)
        {
            DeviceComboBox.SelectedItem = preferred;
        }
    }

    public void SetGainOptions(LiveAudioGainOptions options)
    {
        _gainOptions = options.Validate();
        if (AutomaticGainCheckBox is not null)
        {
            AutomaticGainCheckBox.IsChecked = _gainOptions.IsAutomaticGainEnabled;
        }

        _persistGainOptions?.Invoke(_gainOptions);
        UpdateGainSummary(1, automaticGainApplied: _gainOptions.IsAutomaticGainEnabled);
        UpdateControlState();
    }

    public void SetAudioLevel(double peakLevel, double gainMultiplier = 1, bool automaticGainApplied = false)
    {
        if (_stageProgressMode != StageProgressMode.AudioLevel)
        {
            return;
        }

        double normalized = Math.Max(0, Math.Min(1, peakLevel));
        VolumeMeter.Value = normalized * 100;
        UpdateGainSummary(gainMultiplier, automaticGainApplied);
    }

    public void SetTranscribing(bool isTranscribing)
    {
        _isTranscribing = isTranscribing;
        if (isTranscribing)
        {
            _recordingSavedForClose = false;
            _stageProgressMode = StageProgressMode.AudioLevel;
            _drainInitialPendingChunks = 0;
            _drainCompletedChunks = 0;
            _latestGeneratedChunks = 0;
            _latestTranscribedChunks = 0;
            _latestPendingChunks = 0;
            VolumeMeter.IsIndeterminate = false;
            VolumeMeter.Value = 0;
            UpdateProgressMeterLabel();
        }
        if (!isTranscribing)
        {
            if (_stageProgressMode == StageProgressMode.AudioLevel)
            {
                VolumeMeter.IsIndeterminate = false;
                VolumeMeter.Value = 0;
                UpdateGainSummary(1, automaticGainApplied: false);
                UpdateProgressMeterLabel();
            }
        }

        UpdateControlState();
    }

    public void SetIdleActivity()
    {
        ActivitySummaryText.Text = "Ready. Start live transcription to begin recording and speech processing.";
        RecordingActivityText.Text = "Recorder: idle";
        TranscriptionActivityText.Text = "Transcriber: idle";
        SetChunkCounts(generated: 0, queued: 0, processing: 0, transcribed: 0, failed: 0);
        LatestActivityText.Text = "Latest event: waiting to start";
    }

    public void SetStartingActivity(string sourceName)
    {
        _recordingSavedForClose = false;
        ActivitySummaryText.Text = "Preparing live capture, recording, and the transcription pipeline.";
        RecordingActivityText.Text = "Recorder: preparing manifest and output segment path";
        TranscriptionActivityText.Text = "Transcriber: validating engine and opening capture source";
        SetChunkCounts(generated: 0, queued: 0, processing: 0, transcribed: 0, failed: 0);
        LatestActivityText.Text = $"Latest event: preparing source {sourceName}";
    }

    public void SetListeningActivity(string modelDisplayName)
    {
        ActivitySummaryText.Text = "Live recording and transcription are active.";
        RecordingActivityText.Text = "Recorder: writing standardized PCM audio into rotating WAV segments";
        TranscriptionActivityText.Text = $"Transcriber: listening for speech with {modelDisplayName}";
        LatestActivityText.Text = "Latest event: capture started and waiting for speech";
    }

    public void SetInterimTranscriptionActivity(string preview)
    {
        if (_stageProgressMode != StageProgressMode.AudioLevel)
        {
            return;
        }

        ActivitySummaryText.Text = "Speech detected. Interim text is being updated.";
        RecordingActivityText.Text = "Recorder: appending audio frames to the active segment";
        TranscriptionActivityText.Text = "Transcriber: processing the current live speech chunk";
        LatestActivityText.Text = "Latest event: interim transcription update received";
    }

    public void SetFinalTranscriptionActivity(string preview)
    {
        if (_stageProgressMode != StageProgressMode.AudioLevel)
        {
            return;
        }

        ActivitySummaryText.Text = "A transcript segment was finalized and saved into the session.";
        RecordingActivityText.Text = "Recorder: continuing segmented session recording";
        TranscriptionActivityText.Text = "Transcriber: finalized the latest speech chunk";
        LatestActivityText.Text = "Latest event: final transcription segment saved";
    }

    public void SetStoppingActivity()
    {
        ActivitySummaryText.Text = "Stopped listening and recording. Completing pending chunk transcription.";
        RecordingActivityText.Text = "Recorder: finalizing the current WAV segment";
        TranscriptionActivityText.Text = "Transcriber: preparing pending chunk drain";
        LatestActivityText.Text = "Latest event: stopping live session";
    }

    public void SetDrainPendingChunkProgress(int initialPendingChunks)
    {
        _stageProgressMode = StageProgressMode.DrainPendingChunks;
        _drainInitialPendingChunks = Math.Max(0, initialPendingChunks);
        _drainCompletedChunks = 0;
        VolumeMeter.IsIndeterminate = false;
        VolumeMeter.Minimum = 0;
        VolumeMeter.Maximum = Math.Max(1, _latestGeneratedChunks);
        VolumeMeter.Value = Math.Max(0, Math.Min(_latestGeneratedChunks, _latestTranscribedChunks));
        UpdateProgressMeterLabel();
        TranscriptionActivityText.Text = _drainInitialPendingChunks == 0
            ? "Transcriber: no pending chunks to transcribe"
            : $"Transcriber: transcribing pending chunks 0/{_drainInitialPendingChunks:N0}";
        LatestActivityText.Text = _drainInitialPendingChunks == 0
            ? "Latest event: no pending chunk transcription after stop"
            : "Latest event: draining pending chunk transcription";
    }

    public void SetCancelPendingChunkProgress()
    {
        _stageProgressMode = StageProgressMode.CancelPendingChunks;
        _drainInitialPendingChunks = 0;
        _drainCompletedChunks = 0;
        VolumeMeter.IsIndeterminate = true;
        UpdateProgressMeterLabel();
        TranscriptionActivityText.Text = "Transcriber: canceling pending and active chunk transcription";
        LatestActivityText.Text = "Latest event: canceling live chunk transcription work";
    }

    public void SetStoppedActivity(bool recordingInterrupted)
    {
        _recordingSavedForClose = true;
        if (_stageProgressMode == StageProgressMode.DrainPendingChunks)
        {
            VolumeMeter.IsIndeterminate = false;
            VolumeMeter.Minimum = 0;
            VolumeMeter.Maximum = Math.Max(1, _latestGeneratedChunks);
            VolumeMeter.Value = Math.Max(0, Math.Min(_latestGeneratedChunks, _latestTranscribedChunks));
            UpdateProgressMeterLabel();
        }
        ActivitySummaryText.Text = recordingInterrupted
            ? "Live transcription stopped, but the recording was interrupted."
            : "Live transcription stopped. The captured transcript and audio remain in the session.";
        RecordingActivityText.Text = recordingInterrupted
            ? "Recorder: interrupted before clean completion"
            : "Recorder: completed and saved session audio";
        TranscriptionActivityText.Text = "Transcriber: stopped";
        LatestActivityText.Text = recordingInterrupted
            ? "Latest event: stopped with incomplete audio recording"
            : "Latest event: live session completed";
    }

    public void SetRecordingSavedForClose()
    {
        _recordingSavedForClose = true;
        TryAutoCloseAfterDrainCompletion();
        UpdateControlState();
    }

    public void SetRecordingInterruptedActivity(string reason)
    {
        ActivitySummaryText.Text = "Recording was interrupted, but live transcription is still running.";
        RecordingActivityText.Text = $"Recorder: interrupted ({reason})";
        TranscriptionActivityText.Text = "Transcriber: continuing without full session audio coverage";
        LatestActivityText.Text = "Latest event: recording interruption detected";
    }

    public void SetFailureActivity(string detail)
    {
        ActivitySummaryText.Text = "Live transcription encountered a failure.";
        RecordingActivityText.Text = "Recorder: stopped";
        TranscriptionActivityText.Text = "Transcriber: stopped";
        LatestActivityText.Text = $"Latest event: {detail}";
    }

    public void SetChunkCounts(int generated, int queued, int processing, int transcribed, int failed)
    {
        _latestGeneratedChunks = Math.Max(0, generated);
        _latestTranscribedChunks = Math.Max(0, transcribed);
        int pending = Math.Max(0, queued) + Math.Max(0, processing);
        _latestPendingChunks = pending;
        string text =
            $"Chunks: generated {Math.Max(0, generated):N0} | in queue {Math.Max(0, queued):N0} | " +
            $"processing {Math.Max(0, processing):N0} | transcribed {Math.Max(0, transcribed):N0} | " +
            $"pending {pending:N0}";
        if (failed > 0)
        {
            text += $" | failed {failed:N0}";
        }

        ChunkActivityText.Text = text;

        if (_stageProgressMode == StageProgressMode.DrainPendingChunks)
        {
            if (_drainInitialPendingChunks <= 0)
            {
                VolumeMeter.IsIndeterminate = false;
                VolumeMeter.Minimum = 0;
                VolumeMeter.Maximum = Math.Max(1, _latestGeneratedChunks);
                VolumeMeter.Value = Math.Max(0, Math.Min(_latestGeneratedChunks, _latestTranscribedChunks));
                UpdateProgressMeterLabel();
                return;
            }

            _drainCompletedChunks = Math.Max(0, Math.Min(_drainInitialPendingChunks, _drainInitialPendingChunks - pending));
            VolumeMeter.Minimum = 0;
            VolumeMeter.Maximum = Math.Max(1, _latestGeneratedChunks);
            VolumeMeter.IsIndeterminate = false;
            VolumeMeter.Value = Math.Max(0, Math.Min(_latestGeneratedChunks, _latestTranscribedChunks));
            UpdateProgressMeterLabel();
            TranscriptionActivityText.Text =
                $"Transcriber: transcribing pending chunks {_drainCompletedChunks:N0}/{_drainInitialPendingChunks:N0}";
            LatestActivityText.Text = pending > 0
                ? $"Latest event: pending chunk transcription remaining {pending:N0}"
                : "Latest event: pending chunk transcription completed";
            if (pending == 0)
            {
                TryAutoCloseAfterDrainCompletion();
            }
        }
    }

    private async void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_isOperationPending)
        {
            return;
        }

        if (_isTranscribing)
        {
            RequestStopTranscription();
            return;
        }

        if (SelectedDevice is null)
        {
            return;
        }

        _isOperationPending = true;
        SetStartingActivity(SelectedDevice.Name);
        UpdateControlState();
        try
        {
            bool started = await _startTranscriptionAsync(SelectedDevice);
            SetTranscribing(started);
        }
        finally
        {
            _isOperationPending = false;
            UpdateControlState();
        }
    }

    private void DeviceComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateControlState();
    }

    private void AutomaticGainCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (AutomaticGainCheckBox is null)
        {
            return;
        }

        bool isEnabled = AutomaticGainCheckBox.IsChecked == true;
        _gainOptions = _gainOptions with { IsAutomaticGainEnabled = isEnabled };
        _persistGainOptions?.Invoke(_gainOptions);
        UpdateGainSummary(1, automaticGainApplied: isEnabled);
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !_isTranscribing)
        {
            return;
        }

        e.Cancel = true;
        if (_isOperationPending)
        {
            if (_isStopping)
            {
                _closeRequestedWhileStopping = true;
                LatestActivityText.Text = "Latest event: canceling active and queued chunk transcription before close";
                SetCancelPendingChunkProgress();
                RequestCloseEscalation();
            }
            else
            {
                LatestActivityText.Text = "Latest event: operation in progress; close will be available when it finishes";
            }
            return;
        }

        _isOperationPending = true;
        _isStopping = true;
        UpdateControlState();
        _ = CloseAfterStopAsync();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        StopAutoClosePolling();
        _autoClosePollTimer.Tick -= AutoClosePollTimer_Tick;
        SourceInitialized -= OnSourceInitialized;
        Closing -= OnWindowClosing;
        Closed -= OnWindowClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplySystemCloseEnabledState();
    }

    private async Task StopTranscriptionAsync()
    {
        bool shouldAutoClose = false;
        _isOperationPending = true;
        UpdateControlState();
        try
        {
            await _stopTranscriptionAsync();
            SetTranscribing(false);
            shouldAutoClose = _autoCloseAfterStop && !_isTranscribing;
        }
        finally
        {
            _isOperationPending = false;
            _isStopping = false;
            UpdateControlState();
            CloseIfDeferredAndSafe();
        }

        if (shouldAutoClose)
        {
            _autoCloseAfterStop = false;
            StopAutoClosePolling();
            _allowClose = true;
            Close();
        }
    }

    private async Task CloseAfterStopAsync()
    {
        bool shouldClose = false;
        try
        {
            shouldClose = await _closeTranscriptionAsync();
            if (shouldClose)
            {
                SetTranscribing(false);
            }
        }
        finally
        {
            _isOperationPending = false;
            _isStopping = false;
            UpdateControlState();
            CloseIfDeferredAndSafe();
        }

        if (shouldClose)
        {
            _allowClose = true;
            Close();
        }
    }

    private async Task<bool> StopAndAllowCloseAsync()
    {
        await _stopTranscriptionAsync();
        return true;
    }

    private void RequestStopTranscription()
    {
        if (_isOperationPending || !_isTranscribing || _isStopping)
        {
            return;
        }

        _autoCloseAfterStop = true;
        StartAutoClosePolling();
        _isStopping = true;
        UpdateControlState();
        _ = StopTranscriptionAsync();
    }

    private void TryAutoCloseAfterDrainCompletion()
    {
        if (!_autoCloseAfterStop
            || !_isStopping
            || _stageProgressMode != StageProgressMode.DrainPendingChunks)
        {
            return;
        }

        if (_latestPendingChunks > 0)
        {
            return;
        }

        bool canCloseNow = _recordingSavedForClose || _drainInitialPendingChunks == 0;
        if (!canCloseNow)
        {
            return;
        }

        _autoCloseAfterStop = false;
        StopAutoClosePolling();
        _allowClose = true;
        Close();
    }

    private void StartAutoClosePolling()
    {
        if (_autoClosePollTimer.IsEnabled)
        {
            return;
        }

        _autoClosePollTimer.Start();
    }

    private void StopAutoClosePolling()
    {
        if (_autoClosePollTimer.IsEnabled)
        {
            _autoClosePollTimer.Stop();
        }
    }

    private void AutoClosePollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_autoCloseAfterStop || !_isStopping)
        {
            StopAutoClosePolling();
            return;
        }

        if (_latestPendingChunks == 0)
        {
            TryAutoCloseAfterDrainCompletion();
        }
    }

    private void CloseIfDeferredAndSafe()
    {
        if (!_closeRequestedWhileStopping || _isTranscribing || _isOperationPending)
        {
            return;
        }

        _closeRequestedWhileStopping = false;
        _allowClose = true;
        Close();
    }

    private void RequestCloseEscalation()
    {
        if (_closeEscalationRequested || _escalateCloseAsync is null)
        {
            return;
        }

        _closeEscalationRequested = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await _escalateCloseAsync();
            }
            finally
            {
                _closeEscalationRequested = false;
            }
        });
    }

    private void UpdateControlState()
    {
        if (DeviceComboBox is null
            || StartStopButton is null
            || AutomaticGainCheckBox is null)
        {
            return;
        }

        DeviceComboBox.IsEnabled = !_isTranscribing && !_isOperationPending;
        AutomaticGainCheckBox.IsEnabled = !_isTranscribing && !_isOperationPending;
        StartStopButton.IsEnabled = !_isOperationPending && !_isStopping && (SelectedDevice is not null || _isTranscribing);
        StartStopButton.Content = _isTranscribing
            ? (_isStopping ? "Finalizing... please wait" : "Stop")
            : "Start";
        bool isCloseEnabled = (!_isTranscribing && !_isOperationPending)
            || (_isStopping && _recordingSavedForClose);
        SetSystemCloseEnabled(isCloseEnabled);
    }

    private void SetSystemCloseEnabled(bool enabled)
    {
        if (_isSystemCloseEnabled == enabled)
        {
            return;
        }

        _isSystemCloseEnabled = enabled;
        ApplySystemCloseEnabledState();
    }

    private void ApplySystemCloseEnabledState()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        IntPtr menu = GetSystemMenu(handle, bRevert: false);
        if (menu == IntPtr.Zero)
        {
            return;
        }

        uint flags = MF_BYCOMMAND | (_isSystemCloseEnabled ? MF_ENABLED : MF_GRAYED);
        _ = EnableMenuItem(menu, SC_CLOSE, flags);
    }

    private void UpdateGainSummary(double activeGainMultiplier, bool automaticGainApplied = false)
    {
        _ = activeGainMultiplier;
        _ = automaticGainApplied;
    }

    private void UpdateProgressMeterLabel()
    {
        if (ProgressMeterLabel is null)
        {
            return;
        }

        ProgressMeterLabel.Text = _stageProgressMode switch
        {
            StageProgressMode.DrainPendingChunks =>
                $"Chunk transcription progress (transcribed/generated: {_latestTranscribedChunks:N0}/{_latestGeneratedChunks:N0})",
            StageProgressMode.CancelPendingChunks =>
                "Chunk transcription progress (canceling pending tasks)",
            _ => "Processed input level",
        };
    }

    private const uint MF_BYCOMMAND = 0x00000000;
    private const uint MF_ENABLED = 0x00000000;
    private const uint MF_GRAYED = 0x00000001;
    private const uint SC_CLOSE = 0xF060;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);
}
