# AudioScript

AudioScript is a Windows desktop app (WPF, .NET 10) for offline transcription from local files and live playback capture.

## What It Does

- Imports supported audio files and previews playback in-app
- Supports offline transcript generation with installed Whisper models
- Splits long audio into silence-aware chunks before local transcription
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
- Transcription: local Whisper via `Whisper.net.AllRuntimes`
- Tests: xUnit (`AudioScript.Tests`)

## Runtime Behavior

- Single-instance app behavior is enforced at startup.
- Dependency wiring is performed in `App.OnStartup`.
- `MainWindow` and `MainViewModel` orchestrate UI state and workflows.
- Long transcription operations use cancellation tokens and local Whisper processing.

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
- `ViewModels/MainViewModel.cs`: core state, commands, autosave, transcript workflows
- `Services/`: offline transcription, persistence, preferences, diagnostics, window placement
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
- Version-check/update components exist in the codebase (`ApplicationVersionCheckService`, `UpdateRequiredDialogWindow`) but are not currently wired into app startup/runtime flow.
