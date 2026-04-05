# AudioScript

AudioScript is a Windows desktop app (WPF, .NET 10) for transcribing audio from local files and live playback capture, with optional OpenAI-powered transcription and speaker diarization.

## What It Does

- Imports supported audio files and previews playback in-app
- Supports segment-based transcript generation:
  - Manual mode: creates placeholder timeline rows for manual entry
  - AI-assisted mode: transcribes segments through OpenAI
- Supports speaker diarization mode:
  - Splits long audio into chunked requests
  - Uses silence-aware planning to improve chunk boundaries
  - Merges speaker-labeled segments into a final transcript
- Enables transcript editing directly in the grid:
  - Timeline edits
  - Text edits
  - Insert/duplicate/delete row actions
  - Per-row playback edit transcription workflows
- Provides copy workflows for transcript output
- Persists sessions so work can be reopened and recovered

## Technology Stack

- .NET: `net10.0-windows`
- UI: WPF
- Audio: NAudio (`NAudio`)
- SVG rendering: SharpVectors (`SharpVectors.Wpf`)
- AI integration: OpenAI Audio Transcriptions API (`/v1/audio/transcriptions`)
- Tests: xUnit (`AudioScript.Tests`)

## Runtime Behavior

- Single-instance app behavior is enforced at startup.
- Dependency wiring is performed in `App.OnStartup`.
- `MainWindow` and `MainViewModel` orchestrate UI state and workflows.
- Long transcription/diarization HTTP operations use cancellation tokens with infinite `HttpClient` timeout configured at app startup.

## Data Storage

AudioScript stores local data under:

- `%LocalAppData%\AudioScript\Sessions\<sessionId>\session.json`
- `%LocalAppData%\AudioScript\Sessions\<sessionId>\audio\...`
- `%LocalAppData%\AudioScript\app-preferences.json`
- `%LocalAppData%\AudioScript\window-placement.json`

OpenAI API key storage:

- Windows Credential Manager target: `AudioScript.OpenAI.ApiKey`

Session identity is based on SHA-256 audio fingerprinting.

## Repository Layout

- `App.xaml.cs`: app startup, single-instance activation, dependency composition
- `MainWindow.xaml` + `MainWindow.xaml.cs`: main UI and interaction orchestration
- `ViewModels/MainViewModel.cs`: core state, commands, autosave, transcript workflows
- `Services/`: transcription, diarization, persistence, preferences, diagnostics, window placement
- `Audio/`: playback/capture and audio processing/chunk planning helpers
- `Abstractions/`: shared contracts and models
- `AudioScript.Tests/`: unit tests
- `AudioScript.Package/`: Store/MSIX packaging project assets and outputs

## Requirements

- Windows 10/11
- .NET SDK `10.0.201` (see `global.json`)
- OpenAI API key (required for AI-assisted segment transcription and speaker diarization)
- Optional for Store packaging: Windows SDK tools including `makeappx.exe`

## Run Locally

```powershell
dotnet run --project .\AudioScript.csproj
```

## Build

```powershell
dotnet build .\AudioScript.csproj -c Release
```

## Run Tests

```powershell
dotnet test .\AudioScript.Tests\AudioScript.Tests.csproj
```

Targeted examples:

```powershell
dotnet test .\AudioScript.Tests\AudioScript.Tests.csproj --filter "FullyQualifiedName~AudioScript.Tests.MainViewModelTests"
dotnet test .\AudioScript.Tests\AudioScript.Tests.csproj --filter "FullyQualifiedName~AudioScript.Tests.TranscriptSessionStoreTests"
dotnet test .\AudioScript.Tests\AudioScript.Tests.csproj --filter "FullyQualifiedName~AudioScript.Tests.PlaybackTranscriptionServiceTests"
dotnet test .\AudioScript.Tests\AudioScript.Tests.csproj --filter "FullyQualifiedName~AudioScript.Tests.OpenAiSpeakerDiarizationServiceTests"
```

## Create Microsoft Store Package

```powershell
.\Build-StorePackage.ps1
```

Default output root:

- `AudioScript.Package\AppPackages\store-selfcontained\...`

The packaging script:

- Publishes self-contained `win-x64` and `win-arm64` builds
- Creates architecture-specific `.msix` packages
- Bundles into a `.msixbundle`
- Produces a `.msixupload` artifact

## Notes

- Manual transcription mode remains available without OpenAI.
- AI-assisted segment transcription and speaker diarization require a configured API key.
- Version-check/update components exist in the codebase (`ApplicationVersionCheckService`, `UpdateRequiredDialogWindow`) but are not currently wired into app startup/runtime flow.
