# AudioScript

AudioScript is a Windows desktop app (WPF, .NET 10) for offline transcription, speaker diarization, and transcript editing from local files and live capture.

## What It Does

- Imports supported audio files and previews playback in-app
- Supports offline transcript generation with installed Whisper models
- Supports offline speaker diarization using bundled pyannote Community-1 assets
- Splits long audio into silence-aware chunks before local transcription
- Enables transcript editing directly in the grid:
  - Timeline edits
  - Text edits
  - Insert/duplicate/delete row actions
  - Per-row playback edit transcription workflows
- Provides transcript export workflows (`.docx`, subtitle/text formats)
- Persists sessions so work can be reopened and recovered

## Technology Stack

- .NET: `net10.0-windows10.0.17763.0`
- UI: WPF
- Audio: NAudio (`NAudio`)
- SVG rendering: SharpVectors (`SharpVectors.Wpf`)
- Transcription: local Whisper via `Whisper.net.AllRuntimes`
- Speaker diarization: bundled pyannote Community-1 runtime/assets
- Document export: Open XML SDK (`DocumentFormat.OpenXml`)
- Tests: xUnit (`AudioScript.Tests`)

## Runtime Behavior

- Single-instance app behavior is enforced at startup.
- Dependency wiring is performed in `App.OnStartup`.
- `MainWindow` and `MainViewModel` orchestrate UI state and workflows.
- Long transcription and speaker-diarization operations use cancellation tokens.
- Store-packaged builds use in-app Store update checks/download/install with busy-state gating.

## Data Storage

AudioScript stores local data under Windows app data. Store/MSIX builds use the package data container:

- `%LocalAppData%\Packages\<PackageFamilyName>\LocalState\Models`
- `%LocalAppData%\Packages\<PackageFamilyName>\LocalState\Sessions`
- `%LocalAppData%\Packages\<PackageFamilyName>\LocalState\Settings`
- `%LocalAppData%\Packages\<PackageFamilyName>\LocalState\Logs`

Unpackaged development builds use:

- `%LocalAppData%\AudioScript\Models`
- `%LocalAppData%\AudioScript\Sessions`
- `%LocalAppData%\AudioScript\Settings`
- `%LocalAppData%\AudioScript\Logs`

Session identity is based on SHA-256 audio fingerprinting.
Downloaded optional Whisper models are stored in app data rather than the app package and can be removed individually from the setup window.

## Repository Layout

- `App.xaml.cs`: app startup, single-instance activation, dependency composition
- `MainWindow.xaml` + `MainWindow.xaml.cs`: main UI and interaction orchestration
- `ViewModels/MainViewModel.cs`: core state, commands, autosave, transcription/diarization workflows
- `Services/`: offline transcription, speaker diarization, persistence, preferences, diagnostics, updates, export
- `Audio/`: playback/capture and audio processing/chunk planning helpers
- `Abstractions/`: shared contracts and models
- `AudioScript.Tests/`: unit tests
- `AudioScript.Package/`: Store/MSIX packaging project assets and outputs

## Requirements

- Windows 10/11
- .NET SDK `10.0.201` (see `global.json`)
- Optional for Store packaging: Windows SDK tools including `makeappx.exe`

## Run Locally

```powershell
dotnet run --project .\AudioScript.csproj
```

## Build (Development)

```powershell
msbuild .\AudioScript.csproj /t:Build /p:Configuration=Debug /p:RunAnalyzers=false /m
```

## Run Tests

```powershell
dotnet test .\AudioScript.Tests\AudioScript.Tests.csproj
```

Targeted examples:

```powershell
dotnet test .\AudioScript.Tests\AudioScript.Tests.csproj --filter "FullyQualifiedName~AudioScript.Tests.MainViewModelTests"
dotnet test .\AudioScript.Tests\AudioScript.Tests.csproj --filter "FullyQualifiedName~AudioScript.Tests.TranscriptSessionStoreTests"
dotnet test .\AudioScript.Tests\AudioScript.Tests.csproj --filter "FullyQualifiedName~AudioScript.Tests.PlaybackTranscriptionSessionTests"
dotnet test .\AudioScript.Tests\AudioScript.Tests.csproj --filter "FullyQualifiedName~AudioScript.Tests.TranscriptionModelCatalogTests"
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

- Transcription is offline-only and does not require an API key.
- Speaker diarization runs locally through bundled pyannote Community-1 assets/runtime.
- Microsoft Store packaged builds use the Store update APIs for passive update detection, background download, and idle-gated install; unpackaged builds skip Store update checks safely.
