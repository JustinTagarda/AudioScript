# AudioScript Architecture Summary

AudioScript is a single-instance WPF desktop application on .NET 10 for offline audio transcription. It supports local audio-file transcription, live capture transcription, in-app playback, transcript editing, session persistence, and local Whisper model management.

## Architecture Summary

- `App.xaml.cs` owns startup, single-instance activation, dependency composition, app theme setup, and global exception logging.
- `MainWindow.xaml` and `MainWindow.xaml.cs` own UI layout, interaction events, playback-edit workflows, live capture orchestration, and toast/dialog behavior.
- `ViewModels/MainViewModel.cs` owns transcript state, session autosave, command state, selected engine/mode state, audio preview state, and clipboard formatting.
- `Services/WhisperAudioTranscriptionService.cs` is the local transcription backend for file, playback-edit, and live transcription.
- `Services/ChunkedAudioTranscriptionService.cs` handles long-file chunking and merges local transcription results back into one timed transcript.
- `Audio/` contains playback, capture, audio standardization, silence detection, and chunk planning helpers.
- `Services/TranscriptSessionStore.cs`, `AppPreferencesStore.cs`, and `WindowPlacementService.cs` persist local app state.

## Runtime Flow

1. Startup creates the local Whisper model manager and transcription service.
2. The engine list is populated from installed local Whisper models.
3. File transcription standardizes the audio, chunks when needed, transcribes each chunk locally, and merges timed lines.
4. Live and playback-edit transcription capture PCM audio, standardize it, and send it through the same local Whisper service.
5. Sessions are stored under `%LocalAppData%\AudioScript\Sessions`.

## Verification

- Run `dotnet build .\AudioScript.csproj`.
- Run `dotnet test .\AudioScript.Tests\AudioScript.Tests.csproj`.
- For model-selection changes, run `TranscriptionModelCatalogTests` and `WhisperModelManagerTests`.
- For chunking changes, run `ChunkedAudioTranscriptionServiceTests` and `SilenceAwareChunkPlannerTests`.
