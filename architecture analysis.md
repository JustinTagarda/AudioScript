# AudioScript Architecture Summary

AudioScript is a single-instance WPF desktop application on .NET 10 for offline transcription and speaker diarization. It supports local audio-file transcription, live capture transcription, in-app playback, transcript editing, session persistence, local Whisper model management, and transcript export.

## Architecture Summary

- `App.xaml.cs` owns startup, single-instance activation, dependency composition, app theme setup, and global exception logging.
- `MainWindow.xaml` and `MainWindow.xaml.cs` own UI layout, interaction events, playback-edit workflows, live capture orchestration, export flows, and toast/dialog behavior.
- `ViewModels/MainViewModel.cs` owns transcript state, session autosave, command state, selected engine/mode state, diarization checkpoint state, audio preview state, and clipboard formatting.
- `Services/WhisperAudioTranscriptionService.cs` is the local transcription backend for file, playback-edit, and live transcription.
- `Services/ChunkedAudioTranscriptionService.cs` handles long-file chunking and merges local transcription results back into one timed transcript.
- `Services/ChunkedSpeakerDiarizationService.cs` and `Services/OfflineSpeakerDiarizationService.cs` run chunked speaker diarization and speaker-label application.
- `Services/PyannoteCommunity*.cs` provides the bundled pyannote Community-1 runtime/model integration.
- `Services/TranscriptDocumentExporter.cs` provides `.docx` export.
- `Services/AppUpdateService.cs` and `Services/StoreUpdateClient.cs` coordinate in-app Store update flows for packaged builds.
- `Audio/` contains playback, capture, audio standardization, silence detection, and chunk planning helpers.
- `Services/TranscriptSessionStore.cs`, `AppPreferencesStore.cs`, and `WindowPlacementService.cs` persist local app state.

## Runtime Flow

1. Startup creates the local Whisper model manager and transcription service.
2. The engine list is populated from installed local Whisper models.
3. File transcription standardizes the audio, chunks when needed, transcribes each chunk locally, and merges timed lines.
4. Detect Speakers runs chunked diarization with checkpoint/resume support and applies speaker labels back onto transcript rows.
5. Live and playback-edit transcription capture PCM audio, standardize it, and send it through the same local Whisper service.
6. Sessions are stored under `%LocalAppData%\AudioScript\Sessions`.

## Verification

- Run `msbuild .\AudioScript.csproj /t:Build /p:Configuration=Debug /p:RunAnalyzers=false /m`.
- Run `dotnet test .\AudioScript.Tests\AudioScript.Tests.csproj`.
- For model-selection changes, run `TranscriptionModelCatalogTests` and `WhisperModelManagerTests`.
- For chunking changes, run `ChunkedAudioTranscriptionServiceTests` and `SilenceAwareChunkPlannerTests`.
- For speaker-labeling changes, run `SpeakerDiarizationLabelingTests`.
