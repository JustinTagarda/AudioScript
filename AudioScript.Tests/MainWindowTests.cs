using System.Text;
using System.Windows.Input;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Threading;
using AudioScript.Abstractions;
using AudioScript.Audio;
using AudioScript.Services;
using AudioScript.ViewModels;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace AudioScript.Tests;

public sealed class MainWindowTests
{
    [Fact]
    public void TranscriptToolbar_ListsReTranscribeBeforeDetectSpeaker()
    {
        string xamlPath = FindRepoFile("MainWindow.xaml");
        string xaml = File.ReadAllText(xamlPath);

        Assert.Contains("<Button Content=\"Re-transcribe\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"ReTranscribe_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"&#xE768;\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CanRunReTranscribePrimaryAction", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"TranscribeAudioReTranscribeCancel_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsTranscribeAudioCancelVisible", xaml, StringComparison.Ordinal);
        Assert.True(
            xaml.IndexOf("Content=\"Re-transcribe\"", StringComparison.Ordinal)
            < xaml.IndexOf("Content=\"Detect Speaker\"", StringComparison.Ordinal));
    }

    [Fact]
    public void TranscriptProcessingPanel_DoesNotShowMuteControl()
    {
        string xamlPath = FindRepoFile("MainWindow.xaml");
        string xaml = File.ReadAllText(xamlPath);

        Assert.DoesNotContain("Content=\"Mute\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsPlaybackMuted", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ReTranscribeClick_HandlerStagesOnly()
    {
        string codePath = FindRepoFile("MainWindow.xaml.cs");
        string code = File.ReadAllText(codePath);

        int handlerStart = code.IndexOf("private void ReTranscribe_Click", StringComparison.Ordinal);
        int nextHandlerStart = code.IndexOf("private void CopyFinalizedToClipboard_Click", handlerStart, StringComparison.Ordinal);
        string handlerBlock = nextHandlerStart > handlerStart
            ? code[handlerStart..nextHandlerStart]
            : code[handlerStart..];

        Assert.Contains("OpenTranscribeAudioBatchDialog(vm, forceRestart: true)", handlerBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("RunTranscribeAudioAsync(vm)", handlerBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void TranscribeAudioStart_PromptsBeforeDeletingExistingTranscript()
    {
        string codePath = FindRepoFile("MainWindow.xaml.cs");
        string code = File.ReadAllText(codePath);

        Assert.Contains("Delete existing transcript?", code, StringComparison.Ordinal);
        Assert.Contains("Start will delete existing transcript data.", code, StringComparison.Ordinal);
        Assert.Contains("ConfirmTranscribeAudioRestartDeleteExistingTranscript()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TranscriptProcessingToolbar_DisablesReEnterActionsWhileAnyPanelVisible()
    {
        string codePath = FindRepoFile("MainWindow.xaml.cs");
        string code = File.ReadAllText(codePath);

        Assert.Contains("IsAnyTranscriptProcessingPanelVisible", code, StringComparison.Ordinal);
        Assert.Contains("CanRunReTranscribePrimaryAction", code, StringComparison.Ordinal);
        Assert.Contains("IsDetectSpeakerPrimaryActionEnabled", code, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectSpeakerRoute_DoesNotShowGenericDidNotCompleteDialog()
    {
        string codePath = FindRepoFile("MainWindow.xaml.cs");
        string code = File.ReadAllText(codePath);

        Assert.DoesNotContain("ShowTranscribeAudioErrorDialog(\"Detect Speaker did not complete.\")", code, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectSpeakerRoute_ShowsProcessingPanelForActiveDetectSpeakerWorkflow()
    {
        string codePath = FindRepoFile("MainWindow.xaml.cs");
        string code = File.ReadAllText(codePath);

        Assert.Contains("_activeTranscriptProcessingWorkflow == TranscriptProcessingWorkflowKind.DetectSpeakers", code, StringComparison.Ordinal);
        Assert.Contains("ShouldShowDetectSpeakerPanel", code, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectSpeakerRoute_UsesDedicatedDetectSpeakerPanel()
    {
        string xamlPath = FindRepoFile("MainWindow.xaml");
        string xaml = File.ReadAllText(xamlPath);

        Assert.Contains("ShouldShowDetectSpeakerPanel", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetectSpeakerStartStopButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetectSpeakerRestartButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"DetectSpeakerStartStop_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"DetectSpeakerRestart_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsDetectSpeakerRestartVisible", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Pyannote Community-1\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=\"TranscribeAudioRestart_Click\"", xaml[xaml.IndexOf("ShouldShowDetectSpeakerPanel", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public void DetectSpeakerRoute_DecommissionsResumeConfirmationModal()
    {
        string codePath = FindRepoFile("ViewModels\\MainViewModel.cs");
        string code = File.ReadAllText(codePath);

        Assert.DoesNotContain("Resume speaker detection?", code, StringComparison.Ordinal);
        Assert.Contains("TryPrepareSpeakerDiarizationRun", code, StringComparison.Ordinal);
        Assert.Contains("GetSpeakerDiarizationPanelSessionSnapshot", code, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectSpeakerRoute_DoesNotSyncEngineLabelFromSelectedTranscriptionModelWhileActive()
    {
        string codePath = FindRepoFile("MainWindow.xaml.cs");
        string code = File.ReadAllText(codePath);

        Assert.Contains(
            "_activeTranscriptProcessingWorkflow == TranscriptProcessingWorkflowKind.DetectSpeakers",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "&& (IsTranscribeAudioBatchPendingStart || IsTranscribeAudioBatchTranscribing))",
            code,
            StringComparison.Ordinal);
        Assert.Contains("return;", code, StringComparison.Ordinal);
    }

    [Fact]
    public Task SessionTransitionReset_ClearsStaleDetectSpeakerStagingState()
    {
        return RunInStaAsync(() =>
        {
            var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
            SetPrivateField(
                window,
                "_activeTranscriptProcessingWorkflow",
                Enum.Parse(
                    typeof(MainWindow).GetNestedType("TranscriptProcessingWorkflowKind", BindingFlags.NonPublic)!,
                    "DetectSpeakers"));
            SetPrivateField(window, "_isTranscribeAudioBatchPendingStart", true);
            SetPrivateField(window, "_isTranscribeAudioBatchTranscribing", false);
            SetPrivateField(window, "_isTranscriptProcessingCanceling", false);

            Assert.True(GetPrivateField<bool>(window, "_isTranscribeAudioBatchPendingStart"));
            Assert.Equal("DetectSpeakers", GetPrivateField<object>(window, "_activeTranscriptProcessingWorkflow")!.ToString());
            Assert.True(window.ShouldShowDetectSpeakerPanel);

            window.ResetTranscriptProcessingStagingStateForSessionTransition();

            Assert.False(GetPrivateField<bool>(window, "_isTranscribeAudioBatchPendingStart"));
            Assert.Equal("None", GetPrivateField<object>(window, "_activeTranscriptProcessingWorkflow")!.ToString());
            Assert.False(window.ShouldShowDetectSpeakerPanel);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SessionTransitionCleanup_ClosesStagedReTranscribeWorkflow()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            string audioPath = CreateSilentWaveFile(16000);
            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queuedContext);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var sessionStore = new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService);
                TranscriptSessionLoadResult imported = sessionStore.ImportAudioFile(audioPath);
                imported.Document.Transcript.Lines.Add(new TranscriptSessionLineDocument
                {
                    Text = "original transcript",
                    SpeakerLabel = "Speaker 1",
                    StartSeconds = 0,
                    EndSeconds = 1,
                });
                imported.Document.Transcript.FinalText = "Speaker 1: original transcript";
                sessionStore.Save(imported.Document);

                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    sessionStore,
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    await viewModel.LoadRecentSessionAsync(Assert.Single(sessionStore.ListRecentSessions()));
                    queuedContext.Drain();
                    Assert.True(viewModel.TryPrepareTranscribeAudioWorkflow(forceRestart: true));

                    var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
                    SetPrivateField(
                        window,
                        "_activeTranscriptProcessingWorkflow",
                        Enum.Parse(
                            typeof(MainWindow).GetNestedType("TranscriptProcessingWorkflowKind", BindingFlags.NonPublic)!,
                            "TranscribeAudio"));
                    SetPrivateField(window, "_isTranscribeAudioBatchPendingStart", true);
                    SetPrivateField(window, "_isTranscribeAudioBatchTranscribing", false);
                    SetPrivateField(window, "_isTranscriptProcessingCanceling", false);

                    window.ClosePendingTranscribeAudioWorkflowForSessionTransition(viewModel);

                    Assert.False(viewModel.IsPreparedTranscribeAudioForceRestartRequested);
                    Assert.Equal("original transcript", Assert.Single(viewModel.FinalizedTranscriptLines).Text);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                DeleteDirectory(rootPath);
                File.Delete(audioPath);
            }
        });
    }

    [Fact]
    public void MediaPlayerPanel_IsRefreshedWhenTranscriptPanelVisibilityChanges()
    {
        string codePath = FindRepoFile("MainWindow.xaml.cs");
        string code = File.ReadAllText(codePath);

        Assert.Contains("OnPropertyChanged(nameof(ShouldShowMediaPlayerPanel));", code, StringComparison.Ordinal);
        Assert.Contains("ShouldShowLiveTranscriptionPanel", code, StringComparison.Ordinal);
        Assert.Contains("ShouldShowAudioTranscriptionPanel", code, StringComparison.Ordinal);
        Assert.Contains("ShouldShowDetectSpeakerPanel", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TranscriptRowContextMenu_OnlyShowsSupportedActions()
    {
        string xamlPath = FindRepoFile("MainWindow.xaml");
        string xaml = File.ReadAllText(xamlPath);

        Assert.Contains("<MenuItem Header=\"Transcribe This Row\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<MenuItem Header=\"Split into Two Rows\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<MenuItem Header=\"Combine with Previous Row\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<MenuItem Header=\"Rename Speaker…\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<MenuItem Header=\"Rename Session\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<MenuItem Header=\"Merge Adjacent Rows for This Speaker\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<MenuItem Header=\"Copy Row Text\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding DisplayName}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Engine\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<MenuItem Header=\"Insert Row Above\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<MenuItem Header=\"Insert Row Below\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<MenuItem Header=\"Duplicate Row\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<MenuItem Header=\"Copy Text\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<MenuItem Header=\"Copy Timeline + Text\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<MenuItem Header=\"Delete row\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<MenuItem Header=\"Delete Row\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveTranscriptContextMenuItemVisibility_ShowsSpeakerActionOnlyForSpeakerCell()
    {
        Visibility speakerCellVisibility = MainWindow.ResolveTranscriptContextMenuItemVisibility(
            header: "Rename Speaker…",
            isSpeakerCellMenu: true,
            isTextCellMenu: false,
            canRenameSpeaker: true);
        Visibility textCellVisibility = MainWindow.ResolveTranscriptContextMenuItemVisibility(
            header: "Rename Speaker…",
            isSpeakerCellMenu: false,
            isTextCellMenu: true,
            canRenameSpeaker: true);

        Assert.Equal(Visibility.Visible, speakerCellVisibility);
        Assert.Equal(Visibility.Collapsed, textCellVisibility);

        Visibility mergeOnSpeakerCell = MainWindow.ResolveTranscriptContextMenuItemVisibility(
            header: "Merge Adjacent Rows for This Speaker",
            isSpeakerCellMenu: true,
            isTextCellMenu: false,
            canRenameSpeaker: true);
        Visibility mergeOnTextCell = MainWindow.ResolveTranscriptContextMenuItemVisibility(
            header: "Merge Adjacent Rows for This Speaker",
            isSpeakerCellMenu: false,
            isTextCellMenu: true,
            canRenameSpeaker: true);

        Assert.Equal(Visibility.Visible, mergeOnSpeakerCell);
        Assert.Equal(Visibility.Collapsed, mergeOnTextCell);
    }

    [Fact]
    public void CanMergeAdjacentRowsAroundSelectedRow_RequiresAdjacencyToSelectedRow()
    {
        var lineA = new FinalizedTranscriptLineViewModel(TimeSpan.Zero, TimeSpan.FromSeconds(1), false, "a", "Speaker 1");
        var lineB = new FinalizedTranscriptLineViewModel(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), false, "b", "Speaker 1");
        var lineC = new FinalizedTranscriptLineViewModel(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), false, "c", "Speaker 2");
        var lineD = new FinalizedTranscriptLineViewModel(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), false, "d", "Speaker 1");
        var lineE = new FinalizedTranscriptLineViewModel(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), false, "e", "Speaker 1");

        Assert.True(MainWindow.CanMergeAdjacentRowsAroundSelectedRow([lineA, lineB, lineC], lineA));
        Assert.False(MainWindow.CanMergeAdjacentRowsAroundSelectedRow([lineA, lineC, lineB], lineA));
        Assert.False(MainWindow.CanMergeAdjacentRowsAroundSelectedRow([lineA, lineB, lineC], lineC));
        Assert.True(MainWindow.CanMergeAdjacentRowsAroundSelectedRow([lineA, lineB, lineC, lineD, lineE], lineD));
    }

    [Fact]
    public void TryResolveSelectedSpeakerMergeRange_UsesOnlySelectedAdjacentBlock()
    {
        var lineA = new FinalizedTranscriptLineViewModel(TimeSpan.Zero, TimeSpan.FromSeconds(1), false, "a", "Speaker 1");
        var lineB = new FinalizedTranscriptLineViewModel(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), false, "b", "Speaker 1");
        var lineC = new FinalizedTranscriptLineViewModel(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), false, "c", "Speaker 2");
        var lineD = new FinalizedTranscriptLineViewModel(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), false, "d", "Speaker 1");
        var lineE = new FinalizedTranscriptLineViewModel(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), false, "e", "Speaker 1");

        bool resolved = MainWindow.TryResolveSelectedSpeakerMergeRange(
            [lineA, lineB, lineC, lineD, lineE],
            lineD,
            out int start,
            out int end);

        Assert.True(resolved);
        Assert.Equal(3, start);
        Assert.Equal(4, end);
    }

    [Fact]
    public void NormalizeMergedRowTimelineNeighborhood_AlignsMergedEndToNextStart()
    {
        var merged = new FinalizedTranscriptLineViewModel(TimeSpan.Zero, TimeSpan.FromSeconds(5), false, "merged");
        var next = new FinalizedTranscriptLineViewModel(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), false, "next");

        int fixedRows = MainWindow.NormalizeMergedRowTimelineNeighborhood([merged, next], 0);

        Assert.Equal(1, fixedRows);
        Assert.Equal(TimeSpan.FromSeconds(3), merged.EndOffset);
    }

    [Fact]
    public void ResolveTranscriptContextMenuItemVisibility_ShowsTextActionsOnAllColumns()
    {
        Visibility textCellVisibility = MainWindow.ResolveTranscriptContextMenuItemVisibility(
            header: "Transcribe This Row",
            isSpeakerCellMenu: false,
            isTextCellMenu: true,
            canRenameSpeaker: false);
        Visibility speakerCellVisibility = MainWindow.ResolveTranscriptContextMenuItemVisibility(
            header: "Transcribe This Row",
            isSpeakerCellMenu: true,
            isTextCellMenu: false,
            canRenameSpeaker: false);
        Visibility otherCellVisibility = MainWindow.ResolveTranscriptContextMenuItemVisibility(
            header: "Transcribe This Row",
            isSpeakerCellMenu: false,
            isTextCellMenu: false,
            canRenameSpeaker: false);

        Assert.Equal(Visibility.Visible, textCellVisibility);
        Assert.Equal(Visibility.Visible, speakerCellVisibility);
        Assert.Equal(Visibility.Visible, otherCellVisibility);
    }

    [Fact]
    public void ResolveTranscriptContextMenuItemVisibility_AlwaysShowsGeneralActions()
    {
        Visibility otherCellVisibility = MainWindow.ResolveTranscriptContextMenuItemVisibility(
            header: "Copy Row Text",
            isSpeakerCellMenu: false,
            isTextCellMenu: false,
            canRenameSpeaker: false);

        Assert.Equal(Visibility.Visible, otherCellVisibility);
    }

    [Fact]
    public void LiveTranscriptionWindow_DoesNotContainAutomaticGainCheckbox()
    {
        string xamlPath = FindRepoFile("LiveTranscriptionWindow.xaml");
        string xaml = File.ReadAllText(xamlPath);

        Assert.DoesNotContain("Content=\"Automatic gain\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SourceDetailText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Automatic app gain", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveLiveUiState_ReturnsPreparing_WhenStartIsPending()
    {
        MainWindow.LiveUiState state = MainWindow.ResolveLiveUiState(
            isLiveTranscribing: false,
            isPanelOperationPending: true,
            isPanelStopping: false,
            isCancelPendingMode: false,
            selectedDeviceAvailable: true,
            lastKnownState: MainWindow.LiveUiState.Idle);

        Assert.Equal(MainWindow.LiveUiState.Preparing, state);
    }

    [Fact]
    public void ResolveLiveUiState_ReturnsRunning_WhenLiveIsActive()
    {
        MainWindow.LiveUiState state = MainWindow.ResolveLiveUiState(
            isLiveTranscribing: true,
            isPanelOperationPending: false,
            isPanelStopping: false,
            isCancelPendingMode: false,
            selectedDeviceAvailable: true,
            lastKnownState: MainWindow.LiveUiState.Preparing);

        Assert.Equal(MainWindow.LiveUiState.Running, state);
    }

    [Fact]
    public void ResolveLiveUiState_ReturnsStoppingCancel_WhenCancelModeIsActive()
    {
        MainWindow.LiveUiState state = MainWindow.ResolveLiveUiState(
            isLiveTranscribing: true,
            isPanelOperationPending: true,
            isPanelStopping: true,
            isCancelPendingMode: true,
            selectedDeviceAvailable: true,
            lastKnownState: MainWindow.LiveUiState.Running);

        Assert.Equal(MainWindow.LiveUiState.StoppingCancel, state);
    }

    [Theory]
    [InlineData(Key.Left)]
    [InlineData(Key.Right)]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    [InlineData(Key.Home)]
    [InlineData(Key.End)]
    [InlineData(Key.Back)]
    [InlineData(Key.Delete)]
    public void ShouldLetTranscriptCellEditorHandleKey_AllowsCaretAndTextEditingKeys(Key key)
    {
        Assert.True(MainWindow.ShouldLetTranscriptCellEditorHandleKey(
            key,
            ModifierKeys.None,
            acceptsReturn: false));
    }

    [Fact]
    public void ShouldLetTranscriptCellEditorHandleKey_AllowsShiftEnterLineFeed_ForTranscriptTextEditor()
    {
        Assert.True(MainWindow.ShouldLetTranscriptCellEditorHandleKey(
            Key.Enter,
            ModifierKeys.Shift,
            acceptsReturn: true));
    }

    [Fact]
    public void ShouldLetTranscriptCellEditorHandleKey_DoesNotConsumePlainEnter_ForTranscriptTextEditor()
    {
        Assert.False(MainWindow.ShouldLetTranscriptCellEditorHandleKey(
            Key.Enter,
            ModifierKeys.None,
            acceptsReturn: true));
    }

    [Fact]
    public void ShouldLetTranscriptCellEditorHandleKey_DoesNotConsumeEnter_ForSingleLineSpeakerEditor()
    {
        Assert.False(MainWindow.ShouldLetTranscriptCellEditorHandleKey(
            Key.Enter,
            ModifierKeys.None,
            acceptsReturn: false));
    }

    [Fact]
    public void ApplyPlaybackEditTranscriptionText_ReplacesTextAndClearsManualReview()
    {
        var line = new FinalizedTranscriptLineViewModel(
            startOffset: TimeSpan.Zero,
            endOffset: TimeSpan.FromSeconds(8),
            isTimestampEstimated: false,
            text: "Old text")
        {
            IsManuallyReviewed = true,
        };

        MainWindow.ApplyPlaybackEditTranscriptionText(line, "New transcript");

        Assert.Equal("New transcript", line.Text);
        Assert.False(line.IsManuallyReviewed);
    }

    [Fact]
    public void ApplyPlaybackEditTranscriptionText_CanClearTextBeforeRetranscribe()
    {
        var line = new FinalizedTranscriptLineViewModel(
            startOffset: TimeSpan.Zero,
            endOffset: TimeSpan.FromSeconds(8),
            isTimestampEstimated: false,
            text: "Old text")
        {
            IsManuallyReviewed = true,
        };

        MainWindow.ApplyPlaybackEditTranscriptionText(line, string.Empty);

        Assert.Equal(string.Empty, line.Text);
        Assert.False(line.IsManuallyReviewed);
    }

    [Fact]
    public void BuildRowFileTranscriptionText_KeepsOnlyTimedLinesInsidePaddedRowWindow()
    {
        var result = new TranscriptionResult(
            Text: "Okay, sweet. It should come up on your screen.\nCool.",
            Model: TranscriptionModelCatalog.WhisperSmall,
            CreatedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(8),
            TokenLogprobs: [],
            LowConfidenceTokens: [],
            TimedLines: [
                new TranscriptionTimedLine(
                    "Okay, sweet. It should come up on your screen.",
                    TimeSpan.FromMilliseconds(500),
                    TimeSpan.FromSeconds(2.4),
                    false),
                new TranscriptionTimedLine(
                    "Cool.",
                    TimeSpan.FromSeconds(3.2),
                    TimeSpan.FromSeconds(3.7),
                    false),
            ]);

        string text = MainWindow.BuildRowFileTranscriptionText(
            result,
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(2.5));

        Assert.Equal("Okay, sweet. It should come up on your screen.", text);
    }

    [Fact]
    public void BuildRowFileTranscriptionText_FallsBackToResultText_WhenNoTimedLinesRemain()
    {
        var result = new TranscriptionResult(
            Text: "Fallback text",
            Model: TranscriptionModelCatalog.WhisperSmall,
            CreatedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(8),
            TokenLogprobs: [],
            LowConfidenceTokens: [],
            TimedLines: [
                new TranscriptionTimedLine(
                    "Outside row.",
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(6),
                    false),
            ]);

        string text = MainWindow.BuildRowFileTranscriptionText(
            result,
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(2.5));

        Assert.Equal("Fallback text", text);
    }

    [Fact]
    public void ReconcileRowFileTranscriptionText_PreservesExistingLeadingPrefix_WhenTranscriptionIsSuffix()
    {
        string text = MainWindow.ReconcileRowFileTranscriptionText(
            "Okay, sweet. It should come up on your screen.",
            "It should come up on your screen.");

        Assert.Equal("Okay, sweet. It should come up on your screen.", text);
    }

    [Fact]
    public void ReconcileRowFileTranscriptionText_UsesNewText_WhenExistingTextIsEmpty()
    {
        string text = MainWindow.ReconcileRowFileTranscriptionText(
            string.Empty,
            "Fresh row transcription.");

        Assert.Equal("Fresh row transcription.", text);
    }

    [Fact]
    public void ReconcileRowFileTranscriptionText_UsesEmptyText_WhenTranscriptionIsEmpty()
    {
        string text = MainWindow.ReconcileRowFileTranscriptionText(
            "Existing text.",
            string.Empty);

        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void ReconcileRowFileTranscriptionText_MergesOldSuffixWithNewPrefix()
    {
        string text = MainWindow.ReconcileRowFileTranscriptionText(
            "Okay, sweet.",
            "sweet. It should come up on your screen.");

        Assert.Equal("Okay, sweet. It should come up on your screen.", text);
    }

    [Fact]
    public void ReconcileRowFileTranscriptionText_MergesNewSuffixWithOldPrefix()
    {
        string text = MainWindow.ReconcileRowFileTranscriptionText(
            "It should come up on your screen.",
            "Okay, sweet. It should come up on your screen.");

        Assert.Equal("Okay, sweet. It should come up on your screen.", text);
    }

    [Fact]
    public void ReconcileRowFileTranscriptionText_DropsExistingTrailingRedundancy_WhenTranscriptionIsMiddle()
    {
        string text = MainWindow.ReconcileRowFileTranscriptionText(
            "Okay, sweet. It should come up on your screen. Cool.",
            "It should come up on your screen.");

        Assert.Equal("Okay, sweet. It should come up on your screen.", text);
    }

    [Fact]
    public void ReconcileRowFileTranscriptionText_DropsExistingLeadingRedundancy_WhenTranscriptionAddsPrefix()
    {
        string text = MainWindow.ReconcileRowFileTranscriptionText(
            "It should come up on your screen.",
            "Okay, sweet. It should come up on your screen.");

        Assert.Equal("Okay, sweet. It should come up on your screen.", text);
    }

    [Fact]
    public void ReconcileRowFileTranscriptionText_DetectsOverlapIgnoringCaseAndPunctuation()
    {
        string text = MainWindow.ReconcileRowFileTranscriptionText(
            "Okay, SWEET!",
            "sweet, it should come up on your screen.");

        Assert.Equal("Okay, SWEET! it should come up on your screen.", text);
    }

    [Fact]
    public void ReconcileRowFileTranscriptionText_UsesNewText_WhenItDoesNotMatchPreviousText()
    {
        string text = MainWindow.ReconcileRowFileTranscriptionText(
            "Old unrelated text.",
            "Fresh row transcription.");

        Assert.Equal("Fresh row transcription.", text);
    }

    [Fact]
    public void ReconcileRowFileTranscriptionText_ReplacesNearBoundaryCorrection_WithNewWording()
    {
        string transcribedText = "anywhere pending FTA."
            + Environment.NewLine
            + "And mine's around 20 young people and families that I'm supporting at the";

        string text = MainWindow.ReconcileRowFileTranscriptionText(
            "We have anywhere pending FTE",
            transcribedText);

        Assert.Equal(
            "We have anywhere pending FTA."
            + Environment.NewLine
            + "And mine's around 20 young people and families that I'm supporting at the",
            text);
    }

    [Fact]
    public void ReconcileRowFileTranscriptionText_DropsStaleExistingSuffix_WhenTranscriptionContinuesAfterOverlap()
    {
        string transcribedText = "We have anywhere pending FTA."
            + Environment.NewLine
            + "And mine's around 20 young people and families that I'm supporting at the moment.";

        string text = MainWindow.ReconcileRowFileTranscriptionText(
            "We have anywhere pending FTE",
            transcribedText);

        Assert.Equal(transcribedText, text);
    }

    [Fact]
    public void BuildRowClipboardText_ReturnsTabSeparatedValues()
    {
        var line = new FinalizedTranscriptLineViewModel(
            startOffset: TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(15),
            endOffset: TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(23),
            isTimestampEstimated: false,
            text: "We've got a whole function of recovery.",
            speakerLabel: "Maria");

        string value = MainWindow.BuildRowClipboardText(line);

        Assert.Equal(
            "02:15\t02:23\tMaria\tWe've got a whole function of recovery.",
            value);
    }

    [Fact]
    public void TryResolvePlaybackEditStopOffset_ClampsExactRowEndToAudioDuration()
    {
        var line = new FinalizedTranscriptLineViewModel(
            startOffset: TimeSpan.FromSeconds(8),
            endOffset: TimeSpan.FromSeconds(12),
            isTimestampEstimated: false,
            text: "Row");

        bool resolved = MainWindow.TryResolvePlaybackEditStopOffset(10, line, out TimeSpan stopOffset);

        Assert.True(resolved);
        Assert.Equal(TimeSpan.FromSeconds(10), stopOffset);
    }

    [Fact]
    public void TryResolvePlaybackEditStopOffset_UsesExactRowEndOffset()
    {
        var line = new FinalizedTranscriptLineViewModel(
            startOffset: TimeSpan.FromSeconds(135),
            endOffset: TimeSpan.FromSeconds(143),
            isTimestampEstimated: false,
            text: "Row");

        bool resolved = MainWindow.TryResolvePlaybackEditStopOffset(500, line, out TimeSpan stopOffset);

        Assert.True(resolved);
        Assert.Equal(TimeSpan.FromSeconds(143), stopOffset);
    }

    [Fact]
    public void TryResolvePlaybackEditStopOffset_RejectsMissingRowEndOffset()
    {
        var line = new FinalizedTranscriptLineViewModel(
            startOffset: TimeSpan.FromSeconds(20),
            endOffset: null,
            isTimestampEstimated: true,
            text: "Row");

        bool resolved = MainWindow.TryResolvePlaybackEditStopOffset(120, line, out TimeSpan stopOffset);

        Assert.False(resolved);
        Assert.Equal(TimeSpan.Zero, stopOffset);
    }

    [Fact]
    public void CanCombineToPreviousRow_RejectsFirstRow()
    {
        FinalizedTranscriptLineViewModel first = CreateTimedLine(0, 4, "First");
        FinalizedTranscriptLineViewModel second = CreateTimedLine(4, 8, "Second");

        Assert.False(MainWindow.CanCombineToPreviousRow([first, second], first));
        Assert.True(MainWindow.CanCombineToPreviousRow([first, second], second));
    }

    [Fact]
    public void TryResolveCombineToPreviousRowMerge_MiddleRowExtendsPreviousToNextStart()
    {
        FinalizedTranscriptLineViewModel first = CreateTimedLine(0, 4, "First");
        FinalizedTranscriptLineViewModel second = CreateTimedLine(4, 8, "Second");
        FinalizedTranscriptLineViewModel third = CreateTimedLine(8, 12, "Third");

        bool resolved = MainWindow.TryResolveCombineToPreviousRowMerge(
            [first, second, third],
            second,
            out FinalizedTranscriptLineViewModel mergeTargetLine,
            out TimeSpan mergeEndOffset);

        Assert.True(resolved);
        Assert.Same(first, mergeTargetLine);
        Assert.Equal(TimeSpan.FromSeconds(8), mergeEndOffset);
    }

    [Fact]
    public void TryResolveCombineToPreviousRowMerge_LastRowExtendsPreviousToDeletedEnd()
    {
        FinalizedTranscriptLineViewModel first = CreateTimedLine(0, 4, "First");
        FinalizedTranscriptLineViewModel second = CreateTimedLine(4, 8, "Second");

        bool resolved = MainWindow.TryResolveCombineToPreviousRowMerge(
            [first, second],
            second,
            out FinalizedTranscriptLineViewModel mergeTargetLine,
            out TimeSpan mergeEndOffset);

        Assert.True(resolved);
        Assert.Same(first, mergeTargetLine);
        Assert.Equal(TimeSpan.FromSeconds(8), mergeEndOffset);
    }

    [Theory]
    [InlineData("Previous row.", "Deleted row.", "Previous row. Deleted row.")]
    [InlineData("", "Deleted row.", "Deleted row.")]
    [InlineData("Previous row.", "", "Previous row.")]
    [InlineData("", "", "")]
    [InlineData("Previous row.\nPart 2", "Deleted\r\nrow.", "Previous row. Part 2 Deleted row.")]
    public void BuildMergedDeletedRowText_JoinsRowsAsSingleParagraph(
        string previousText,
        string deletedText,
        string expectedText)
    {
        string text = MainWindow.BuildMergedDeletedRowText(previousText, deletedText);

        Assert.Equal(expectedText, text);
    }

    [Fact]
    public void MergeDeletedRowTextIntoPreviousRow_PreservesPreviousSpeakerAndMetadata()
    {
        var previous = new FinalizedTranscriptLineViewModel(
            startOffset: TimeSpan.Zero,
            endOffset: TimeSpan.FromSeconds(4),
            isTimestampEstimated: false,
            text: "Previous row.",
            speakerLabel: "Maria",
            speakerLabelSource: SpeakerLabelSources.Manual,
            diarizationRevision: 3,
            lastDiarizedChunkIndex: 8);
        var deleted = new FinalizedTranscriptLineViewModel(
            startOffset: TimeSpan.FromSeconds(4),
            endOffset: TimeSpan.FromSeconds(8),
            isTimestampEstimated: false,
            text: "Deleted row.",
            speakerLabel: "John",
            speakerLabelSource: SpeakerLabelSources.DiarizationFinal,
            diarizationRevision: 9,
            lastDiarizedChunkIndex: 12);

        MainWindow.MergeDeletedRowTextIntoPreviousRow(previous, deleted);

        Assert.Equal("Previous row. Deleted row.", previous.Text);
        Assert.Equal("Maria", previous.SpeakerLabel);
        Assert.Equal(SpeakerLabelSources.Manual, previous.SpeakerLabelSource);
        Assert.Equal(3, previous.DiarizationRevision);
        Assert.Equal(8, previous.LastDiarizedChunkIndex);
        Assert.True(previous.IsManuallyReviewed);
    }

    [Fact]
    public void TryResolveSeparateRowRange_RejectsMissingOrInvalidTimeline()
    {
        var untimed = new FinalizedTranscriptLineViewModel(
            startOffset: null,
            endOffset: null,
            isTimestampEstimated: true,
            text: "Row");
        var invalid = new FinalizedTranscriptLineViewModel(
            startOffset: TimeSpan.FromSeconds(4),
            endOffset: TimeSpan.FromSeconds(4),
            isTimestampEstimated: false,
            text: "Row");

        Assert.False(MainWindow.TryResolveSeparateRowRange(untimed, out _, out _));
        Assert.False(MainWindow.TryResolveSeparateRowRange(invalid, out _, out _));
    }

    [Fact]
    public void TryResolveSeparateRowRange_ResolvesValidTimeline()
    {
        FinalizedTranscriptLineViewModel line = CreateTimedLine(29, 46, "Row");

        bool resolved = MainWindow.TryResolveSeparateRowRange(line, out TimeSpan startOffset, out TimeSpan endOffset);

        Assert.True(resolved);
        Assert.Equal(TimeSpan.FromSeconds(29), startOffset);
        Assert.Equal(TimeSpan.FromSeconds(46), endOffset);
    }

    [Fact]
    public void SplitRowTextForSeparate_SplitsAtFirstLineFeed()
    {
        (string firstText, string secondText) = MainWindow.SplitRowTextForSeparate("First line.\nSecond line.");

        Assert.Equal("First line.", firstText);
        Assert.Equal("Second line.", secondText);
    }

    [Fact]
    public void SplitRowTextForSeparate_SplitsAtFirstPeriodWhenNoLineFeedExists()
    {
        (string firstText, string secondText) = MainWindow.SplitRowTextForSeparate("First sentence. Second sentence.");

        Assert.Equal("First sentence.", firstText);
        Assert.Equal("Second sentence.", secondText);
    }

    [Fact]
    public void SplitRowTextForSeparate_SplitsAtFirstPunctuationWhenNoPeriodExists()
    {
        (string firstText, string secondText) = MainWindow.SplitRowTextForSeparate("Wait, then continue");

        Assert.Equal("Wait,", firstText);
        Assert.Equal("then continue", secondText);
    }

    [Fact]
    public void SplitRowTextForSeparate_SplitsNearMidpointOnWordBoundaryWhenNoPunctuationExists()
    {
        (string firstText, string secondText) = MainWindow.SplitRowTextForSeparate("alpha beta gamma delta");

        Assert.Equal("alpha beta", firstText);
        Assert.Equal("gamma delta", secondText);
    }

    [Fact]
    public void ResolveInitialSeparateSplitOffset_UsesMidpointInsideRange()
    {
        TimeSpan splitOffset = MainWindow.ResolveInitialSeparateSplitOffset(
            TimeSpan.FromSeconds(29),
            TimeSpan.FromSeconds(46));

        Assert.Equal(TimeSpan.FromSeconds(37), splitOffset);
    }

    [Fact]
    public void SplitRowTextAtIndex_TrimsBothSplitParts()
    {
        const string source = " First line.\r\nSecond\tline  ";
        (string firstText, string secondText) = MainWindow.SplitRowTextAtIndex(source, 12);

        Assert.Equal("First line.", firstText);
        Assert.Equal("Second\tline", secondText);
    }

    [Fact]
    public void TryValidateSeparateRowTextSplit_RejectsOutsideBounds()
    {
        Assert.False(MainWindow.TryValidateSeparateRowTextSplit("abc", 0, out _));
        Assert.False(MainWindow.TryValidateSeparateRowTextSplit("abc", 3, out _));
    }

    [Fact]
    public void TryValidateSeparateRowTextSplit_AcceptsValidInternalSplit()
    {
        bool valid = MainWindow.TryValidateSeparateRowTextSplit("alpha beta", 5, out string error);

        Assert.True(valid);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void TryValidateSeparateRowInput_RejectsEmptyTexts()
    {
        bool valid = MainWindow.TryValidateSeparateRowInput(
            TimeSpan.FromSeconds(29),
            TimeSpan.FromSeconds(46),
            TimeSpan.FromSeconds(37),
            "First",
            string.Empty,
            out string errorMessage);

        Assert.False(valid);
        Assert.Equal("Both row texts are required.", errorMessage);
    }

    [Theory]
    [InlineData(29, 29)]
    [InlineData(46, 46)]
    public void TryValidateSeparateRowInput_RejectsSplitOutsideOriginalRange(int splitSeconds, int rowEndSeconds)
    {
        bool valid = MainWindow.TryValidateSeparateRowInput(
            TimeSpan.FromSeconds(29),
            TimeSpan.FromSeconds(rowEndSeconds),
            TimeSpan.FromSeconds(splitSeconds),
            "First",
            "Second",
            out string errorMessage);

        Assert.False(valid);
        Assert.Equal("Timeline split point must stay inside the original row range.", errorMessage);
    }

    [Fact]
    public void TryValidateSeparateRowInput_AcceptsValidSplit()
    {
        bool valid = MainWindow.TryValidateSeparateRowInput(
            TimeSpan.FromSeconds(29),
            TimeSpan.FromSeconds(46),
            TimeSpan.FromSeconds(37),
            "First",
            "Second",
            out string errorMessage);

        Assert.True(valid);
        Assert.Equal(string.Empty, errorMessage);
    }

    [Fact]
    public void CreateSecondSeparatedRow_CopiesSpeakerAndMetadataWhenSpeakerExists()
    {
        var sourceLine = new FinalizedTranscriptLineViewModel(
            startOffset: TimeSpan.FromSeconds(29),
            endOffset: TimeSpan.FromSeconds(46),
            isTimestampEstimated: false,
            text: "Original",
            speakerLabel: "Maria",
            speakerLabelSource: SpeakerLabelSources.Manual,
            diarizationRevision: 3,
            lastDiarizedChunkIndex: 8);

        FinalizedTranscriptLineViewModel secondRow = MainWindow.CreateSecondSeparatedRow(
            sourceLine,
            TimeSpan.FromSeconds(37),
            TimeSpan.FromSeconds(46),
            "Second row");

        Assert.Equal(TimeSpan.FromSeconds(37), secondRow.StartOffset);
        Assert.Equal(TimeSpan.FromSeconds(46), secondRow.EndOffset);
        Assert.Equal("Second row", secondRow.Text);
        Assert.Equal("Maria", secondRow.SpeakerLabel);
        Assert.Equal(SpeakerLabelSources.Manual, secondRow.SpeakerLabelSource);
        Assert.Equal(3, secondRow.DiarizationRevision);
        Assert.Equal(8, secondRow.LastDiarizedChunkIndex);
        Assert.True(secondRow.IsManuallyReviewed);
    }

    [Fact]
    public void MergeDeletedRowTextIntoPreviousRow_DoesNotCopyDeletedSpeakerWhenPreviousSpeakerIsEmpty()
    {
        var previous = new FinalizedTranscriptLineViewModel(
            startOffset: TimeSpan.Zero,
            endOffset: TimeSpan.FromSeconds(4),
            isTimestampEstimated: false,
            text: string.Empty);
        var deleted = new FinalizedTranscriptLineViewModel(
            startOffset: TimeSpan.FromSeconds(4),
            endOffset: TimeSpan.FromSeconds(8),
            isTimestampEstimated: false,
            text: "Deleted row.",
            speakerLabel: "John",
            speakerLabelSource: SpeakerLabelSources.DiarizationFinal,
            diarizationRevision: 9,
            lastDiarizedChunkIndex: 12);

        MainWindow.MergeDeletedRowTextIntoPreviousRow(previous, deleted);

        Assert.Equal("Deleted row.", previous.Text);
        Assert.Equal(string.Empty, previous.SpeakerLabel);
        Assert.Equal(string.Empty, previous.SpeakerLabelSource);
        Assert.Null(previous.DiarizationRevision);
        Assert.Null(previous.LastDiarizedChunkIndex);
        Assert.True(previous.IsManuallyReviewed);
    }

    [Fact]
    public async Task CanRunExplicitRowTranscription_AllowsLoadedAudioTimedRow()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            string audioPath = CreateSilentWaveFile(16000);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService),
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    Assert.True(viewModel.TryImportAudioFileFromPath(audioPath));

                    var line = new FinalizedTranscriptLineViewModel(
                        startOffset: TimeSpan.Zero,
                        endOffset: TimeSpan.FromSeconds(4),
                        isTimestampEstimated: false,
                        text: "Existing text");

                    Assert.True(MainWindow.CanRunExplicitRowTranscription(viewModel, line, out string failureMessage));
                    Assert.Equal(string.Empty, failureMessage);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                DeleteDirectory(rootPath);
                File.Delete(audioPath);
            }
        });
    }

    [Fact]
    public async Task CanRunExplicitRowTranscription_RejectsMissingAudioOrTimeline()
    {
        await RunInStaAsync(async () =>
        {
            string rootPath = CreateTempDirectory();
            string audioPath = CreateSilentWaveFile(16000);

            try
            {
                var playbackService = new FakeAudioPlaybackService();
                var processLogService = new ProcessLogService();
                var transcriptionService = new StubAudioTranscriptionService([]);
                var viewModel = new MainViewModel(
                    TranscriptionModelCatalog.Models,
                    transcriptionService,
                    CreateChunkedSpeakerDiarizationService(transcriptionService, processLogService),
                    playbackService,
                    processLogService,
                    new TranscriptSessionStore(Path.Combine(rootPath, "sessions"), processLogService),
                    new AppPreferencesStore(Path.Combine(rootPath, "app-preferences.json")),
                    new AppThemeService(),
                    new AppPreferencesSnapshot(
                        CopyFinalizedWithTimeline: false,
                        AutoTranscribeWithAi: false,
                        ThemePreference: AppThemePreference.System,
                        AutoPlayTimelineSelection: true,
                        LiveAudioSourceKind: LiveAudioSourceKind.DefaultPlayback,
                        LiveAudioDeviceNumber: -1,
                        SelectedEngineId: TranscriptionModelCatalog.WhisperSmall));

                try
                {
                    var timedLine = new FinalizedTranscriptLineViewModel(
                        startOffset: TimeSpan.Zero,
                        endOffset: TimeSpan.FromSeconds(4),
                        isTimestampEstimated: false,
                        text: "Existing text");
                    var untimedLine = new FinalizedTranscriptLineViewModel(
                        startOffset: null,
                        endOffset: null,
                        isTimestampEstimated: true,
                        text: "Existing text");

                    Assert.False(MainWindow.CanRunExplicitRowTranscription(viewModel, timedLine, out string missingAudioMessage));
                    Assert.Equal("Load or restore the session audio before transcribing this row.", missingAudioMessage);

                    Assert.True(viewModel.TryImportAudioFileFromPath(audioPath));

                    Assert.False(MainWindow.CanRunExplicitRowTranscription(viewModel, untimedLine, out string missingTimelineMessage));
                    Assert.Equal("The selected row does not have a usable timeline.", missingTimelineMessage);
                }
                finally
                {
                    await viewModel.DisposeAsync();
                }
            }
            finally
            {
                DeleteDirectory(rootPath);
                File.Delete(audioPath);
            }
        });
    }

    private static Task RunInStaAsync(Func<Task> action)
    {
        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action().GetAwaiter().GetResult();
                completionSource.SetResult();
            }
            catch (Exception ex)
            {
                completionSource.SetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completionSource.Task;
    }

    private static string FindRepoFile(string fileName)
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate repo file '{fileName}' from '{AppContext.BaseDirectory}'.");
    }

    private static object? GetPrivateField(object target, string fieldName)
    {
        return target.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(target);
    }

    private static T GetPrivateField<T>(object target, string fieldName)
        where T : notnull
    {
        object? value = GetPrivateField(target, fieldName);
        return value is T typedValue
            ? typedValue
            : throw new InvalidOperationException($"Field '{fieldName}' was not of expected type '{typeof(T).FullName}'.");
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        FieldInfo? field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null)
        {
            throw new MissingFieldException(target.GetType().FullName, fieldName);
        }

        field.SetValue(target, value);
    }

    private static void InvokePrivateMethod(object target, string methodName, params object?[]? parameters)
    {
        MethodInfo? method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (method is null)
        {
            throw new MissingMethodException(target.GetType().FullName, methodName);
        }

        method.Invoke(target, parameters);
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            _queue.Enqueue((d, state));
        }

        public void Drain()
        {
            while (_queue.TryDequeue(out (SendOrPostCallback Callback, object? State) workItem))
            {
                workItem.Callback(workItem.State);
            }
        }
    }

    private static FinalizedTranscriptLineViewModel CreateTimedLine(
        int startSeconds,
        int endSeconds,
        string text)
    {
        return new FinalizedTranscriptLineViewModel(
            startOffset: TimeSpan.FromSeconds(startSeconds),
            endOffset: TimeSpan.FromSeconds(endSeconds),
            isTimestampEstimated: false,
            text: text);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"AudioScript-mainwindow-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateSilentWaveFile(int sampleRate)
    {
        string path = Path.Combine(Path.GetTempPath(), $"AudioScript-mainwindow-tests-{Guid.NewGuid():N}.wav");
        using var writer = new BinaryWriter(File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None), Encoding.ASCII);
        int bytesPerSample = 2;
        short channels = 1;
        short bitsPerSample = 16;
        int dataLength = sampleRate * bytesPerSample;
        int byteRate = sampleRate * channels * bytesPerSample;
        short blockAlign = (short)(channels * bytesPerSample);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static ChunkedSpeakerDiarizationService CreateChunkedSpeakerDiarizationService(
        IAudioTranscriptionService audioTranscriptionService,
        ProcessLogService processLogService)
    {
        var waveClipExtractor = new WaveClipExtractor();
        var audioChunkingService = new AudioChunkingService(
            new AudioStandardizer(),
            new SilenceIntervalDetector(),
            new SilenceAwareChunkPlanner(),
            waveClipExtractor);
        var offlineDiarizationService = new OfflineSpeakerDiarizationService(
            new TestSpeakerDiarizationEngine(),
            processLogService);

        return new ChunkedSpeakerDiarizationService(
            audioChunkingService,
            offlineDiarizationService,
            processLogService);
    }

    private sealed class StubAudioTranscriptionService : IAudioTranscriptionService
    {
        private readonly IReadOnlyList<TranscriptionTimedLine> _timedLines;

        public StubAudioTranscriptionService(IReadOnlyList<TranscriptionTimedLine> timedLines)
        {
            _timedLines = timedLines;
        }

        public Task<TranscriptionResult> TranscribeAudioFileAsync(
            string audioFilePath,
            string model,
            CancellationToken cancellationToken,
            IProgress<TranscriptionProgressSnapshot>? progress = null,
            string? diagnosticRoute = null)
        {
            TimeSpan duration = _timedLines.Count == 0
                ? TimeSpan.Zero
                : (_timedLines[^1].EndOffset ?? TimeSpan.Zero);

            return Task.FromResult(new TranscriptionResult(
                Text: string.Join(Environment.NewLine, _timedLines.Select(line => line.Text)),
                Model: model,
                CreatedAt: DateTimeOffset.UtcNow,
                Duration: duration,
                TokenLogprobs: [],
                LowConfidenceTokens: [],
                TimedLines: _timedLines));
        }
    }

    private sealed class TestSpeakerDiarizationEngine : ISpeakerDiarizationEngine
    {
        public Task<IReadOnlyList<SpeakerDiarizationTurn>> DiarizeAudioFileAsync(
            string audioFilePath,
            CancellationToken cancellationToken,
            IProgress<SpeakerDiarizationProgress>? progress = null)
        {
            IReadOnlyList<SpeakerDiarizationTurn> turns = [];
            return Task.FromResult(turns);
        }
    }

    private sealed class FakeAudioPlaybackService : IAudioPlaybackService
    {
        private string? _loadedFilePath;
        private bool _isPlaying;
        private TimeSpan _position;

        public event EventHandler? PlaybackStateChanged;

        public string? LoadedFilePath => _loadedFilePath;

        public bool IsLoaded => !string.IsNullOrWhiteSpace(_loadedFilePath);

        public bool IsPlaying => _isPlaying;

        public TimeSpan Duration => string.IsNullOrWhiteSpace(_loadedFilePath) ? TimeSpan.Zero : TimeSpan.FromSeconds(10);

        public TimeSpan Position => _position;

        public void LoadFile(string filePath)
        {
            _loadedFilePath = Path.GetFullPath(filePath);
            _position = TimeSpan.Zero;
            _isPlaying = false;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void LoadLiveRecordingManifest(string manifestPath)
        {
            LoadFile(manifestPath);
        }

        public void UnloadFile()
        {
            _loadedFilePath = null;
            _position = TimeSpan.Zero;
            _isPlaying = false;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Play()
        {
            if (string.IsNullOrWhiteSpace(_loadedFilePath))
            {
                throw new InvalidOperationException("No audio file is loaded.");
            }

            _isPlaying = true;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Pause()
        {
            _isPlaying = false;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Stop()
        {
            _isPlaying = false;
            _position = TimeSpan.Zero;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Seek(TimeSpan position)
        {
            _position = position < TimeSpan.Zero ? TimeSpan.Zero : position;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
        }
    }
}
