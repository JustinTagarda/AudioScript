using System.Windows;
using System.ComponentModel;
using AudioScript.Audio;

namespace AudioScript;

public partial class LiveTranscriptionWindow : Window
{
    private readonly Func<AudioInputDeviceOption, Task<bool>> _startTranscriptionAsync;
    private readonly Func<Task> _stopTranscriptionAsync;
    private bool _isTranscribing;
    private bool _isOperationPending;
    private bool _allowClose;

    public LiveTranscriptionWindow(
        IReadOnlyList<AudioInputDeviceOption> devices,
        string transcriptionEngineDisplayName,
        Func<AudioInputDeviceOption, Task<bool>> startTranscriptionAsync,
        Func<Task> stopTranscriptionAsync)
    {
        _startTranscriptionAsync = startTranscriptionAsync;
        _stopTranscriptionAsync = stopTranscriptionAsync;
        InitializeComponent();
        DeviceComboBox.ItemsSource = devices;
        DeviceComboBox.SelectedIndex = devices.Count > 0 ? 0 : -1;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        SetTranscriptionEngine(transcriptionEngineDisplayName);
        SetIdleActivity();
    }

    public AudioInputDeviceOption? SelectedDevice =>
        DeviceComboBox.SelectedItem as AudioInputDeviceOption;

    public bool IsTranscribing => _isTranscribing;

    public LiveAudioGainOptions CurrentGainOptions =>
        LiveAudioGainOptions.Default;

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

        UpdateSourceDetail();
    }

    public void SetGainOptions(LiveAudioGainOptions options)
    {
        _ = options.Validate();
        UpdateGainSummary(1);
        UpdateControlState();
    }

    public void SetAudioLevel(double peakLevel, double gainMultiplier = 1, bool automaticGainApplied = false)
    {
        double normalized = Math.Max(0, Math.Min(1, peakLevel));
        VolumeMeter.Value = normalized * 100;
        UpdateGainSummary(gainMultiplier);
    }

    public void SetTranscribing(bool isTranscribing)
    {
        _isTranscribing = isTranscribing;
        if (!isTranscribing)
        {
            SetAudioLevel(0);
        }

        UpdateControlState();
    }

    public void SetIdleActivity()
    {
        ActivitySummaryText.Text = "Ready. Start live transcription to begin recording and speech processing.";
        RecordingActivityText.Text = "Recorder: idle";
        TranscriptionActivityText.Text = "Transcriber: idle";
        LatestActivityText.Text = "Latest event: waiting to start";
    }

    public void SetTranscriptionEngine(string displayName)
    {
        string normalized = string.IsNullOrWhiteSpace(displayName)
            ? "not selected"
            : displayName.Trim();
        TranscriptionEngineText.Text = $"Engine: {normalized}";
    }

    public void SetStartingActivity(string sourceName)
    {
        ActivitySummaryText.Text = "Preparing live capture, recording, and the transcription pipeline.";
        RecordingActivityText.Text = "Recorder: preparing manifest and output segment path";
        TranscriptionActivityText.Text = "Transcriber: validating engine and opening capture source";
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
        ActivitySummaryText.Text = "Speech detected. Interim text is being updated.";
        RecordingActivityText.Text = "Recorder: appending audio frames to the active segment";
        TranscriptionActivityText.Text = "Transcriber: processing the current live speech chunk";
        LatestActivityText.Text = "Latest event: interim transcription update received";
    }

    public void SetFinalTranscriptionActivity(string preview)
    {
        ActivitySummaryText.Text = "A transcript segment was finalized and saved into the session.";
        RecordingActivityText.Text = "Recorder: continuing segmented session recording";
        TranscriptionActivityText.Text = "Transcriber: finalized the latest speech chunk";
        LatestActivityText.Text = "Latest event: final transcription segment saved";
    }

    public void SetStoppingActivity()
    {
        ActivitySummaryText.Text = "Stopping live transcription and finalizing the session.";
        RecordingActivityText.Text = "Recorder: finalizing the current WAV segment";
        TranscriptionActivityText.Text = "Transcriber: draining buffered work and stopping capture";
        LatestActivityText.Text = "Latest event: stopping live session";
    }

    public void SetStoppedActivity(bool recordingInterrupted)
    {
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

    private async void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_isOperationPending)
        {
            return;
        }

        if (_isTranscribing)
        {
            await StopTranscriptionAsync();
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

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void DeviceComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateSourceDetail();
        UpdateControlState();
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !_isTranscribing)
        {
            return;
        }

        e.Cancel = true;
        if (_isOperationPending)
        {
            return;
        }

        var confirmation = new ConfirmationDialogWindow(
            "Stop live transcription?",
            "Closing Live Transcription will stop the active live transcription.",
            "Stop and close",
            "Keep open")
        {
            Owner = this,
        };

        if (confirmation.ShowDialog() != true)
        {
            return;
        }

        await StopTranscriptionAsync();
        if (!_isTranscribing)
        {
            _allowClose = true;
            Close();
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        Closing -= OnWindowClosing;
        Closed -= OnWindowClosed;
    }

    private async Task StopTranscriptionAsync()
    {
        _isOperationPending = true;
        UpdateControlState();
        try
        {
            await _stopTranscriptionAsync();
            SetTranscribing(false);
        }
        finally
        {
            _isOperationPending = false;
            UpdateControlState();
        }
    }

    private void UpdateControlState()
    {
        if (DeviceComboBox is null
            || StartStopButton is null
            || CloseButton is null)
        {
            return;
        }

        DeviceComboBox.IsEnabled = !_isTranscribing && !_isOperationPending;
        StartStopButton.IsEnabled = !_isOperationPending && (SelectedDevice is not null || _isTranscribing);
        StartStopButton.Content = _isTranscribing ? "Stop" : "Start";
        CloseButton.IsEnabled = !_isOperationPending;
    }

    private void UpdateGainSummary(double activeGainMultiplier)
    {
        if (GainSummaryText is null)
        {
            return;
        }

        string state = _isTranscribing ? "active" : "ready";
        GainSummaryText.Text = $"Auto gain {state}: {Math.Max(activeGainMultiplier, 0):0.00}x";
    }

    private void UpdateSourceDetail()
    {
        if (SourceDetailText is null)
        {
            return;
        }

        SourceDetailText.Text = SelectedDevice?.Kind switch
        {
            LiveAudioSourceKind.AudioScriptPlayback =>
                "Source: AudioScript preview audio before output volume is applied.",
            LiveAudioSourceKind.MicrophoneAndAudioScriptPlayback =>
                "Source: microphone plus AudioScript preview audio before output volume is applied.",
            LiveAudioSourceKind.DefaultPlayback =>
                "Source: Windows default playback loopback; endpoint volume and mute can affect captured audio.",
            LiveAudioSourceKind.MicrophoneAndDefaultPlayback =>
                "Source: microphone plus Windows default playback loopback.",
            LiveAudioSourceKind.Microphone =>
                "Source: selected microphone input.",
            _ =>
                "Source: waiting for selection",
        };
    }
}
