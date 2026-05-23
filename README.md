# AudioScript

AudioScript is a Windows desktop app built with WPF on .NET 10 for offline transcription, speaker diarization, transcript editing, and local session management.

## Overview

- Imports local audio files for playback and transcription
- Captures live audio from the default playback device, a microphone, or both
- Runs transcription locally with installed Whisper models
- Runs speaker diarization locally with bundled `pyannote-community-1` assets
- Supports editing transcript rows in the grid, including timeline and text edits
- Supports row operations such as insert, duplicate, delete, split, and speaker renaming
- Autosaves sessions and restores them from local app data
- Exports transcripts to Word documents
- Checks Microsoft Store updates after first render, supports user-initiated update checks, and defers silent installs until app exit
- Uses a single-instance startup model so only one app window runs at a time

## Features

### Transcription

- Offline transcription with local Whisper models
- Available model options:
  - `whisper-small`
  - `whisper-medium`
  - `whisper-large-v3`
  - `whisper-large-v3-turbo`
  - `manual-transcription` for manual-only editing
- Supported file inputs:
  - `.wav`
  - `.mp3`
  - `.flac`
  - `.aac`
  - `.m4a`
  - `.ogg`
  - `.wma`
  - `.mp4`
- Playback-based transcription workflows for editing against local audio
- Live transcription workflows for microphone and playback capture

### Speaker Diarization

- Offline diarization using bundled `pyannote-community-1` runtime/assets
- Supports resuming incomplete diarization jobs from saved checkpoints
- Can relabel speakers in existing sessions

### Session Management

- Imports audio into persistent sessions
- Reopens recent sessions from local storage
- Saves transcript state, editing state, and audio metadata
- Restores missing session audio when the original file is available again
- Deletes sessions and their stored files from the session store

### Export

- Exports transcript documents to `.docx`
- Export layouts:
  - tab-delimited table layout
  - interview-style layout
- Opens the exported document after a successful save when a local app is available

### Microsoft Store Updates

- Uses Microsoft Store / MSIX update APIs for packaged builds
- Starts a hidden update check only after the main window has rendered
- Can throttle repeated hidden startup checks with `MinimumCheckInterval` while still revalidating deferred installs on close
- Falls back to Store / OS-provided update UI when silent download is unavailable or fails
- Defers successful silent downloads until the user closes the app
- Shows a non-cancellable app-owned install progress window on exit when deferred install exists
- Includes a user-initiated `Check for updates` action in the main window footer
- Routes the bottom-right version label through the same user-initiated update flow
- Does not automatically restart the app after an update

## Technology Stack

- UI: WPF
- Runtime: `net10.0-windows10.0.17763.0`
- Audio capture/playback: NAudio
- SVG rendering: SharpVectors.Wpf
- Transcription: Whisper.net runtime packages
- Export: Open XML SDK
- Tests: xUnit

## Premium And Access Rules

- Basic mode is limited to 10 sessions
- Premium unlocks:
  - live transcription
  - speaker diarization
  - premium model installation/use
- Premium product name in the app: `AudioScript Premium`
- Microsoft Store add-on ID used by the app: `9PD5288V5Q49`
- Premium is configured from a single durable add-on catalog entry with lifetime `Forever`, and the promo-code target is the same Premium product.
- Premium is represented as a durable Microsoft Store add-on and is expected to remain a lifetime entitlement for the parent app.
- Promo code redemption targets the same durable Premium add-on.

## Repository Layout

- `App.xaml.cs`: app startup, single-instance activation, dependency composition, update/entitlement wiring
- `MainWindow.xaml` and `MainWindow.xaml.cs`: main shell, dialogs, export workflow, and UI orchestration
- `ViewModels/MainViewModel.cs`: session state, commands, autosave, transcription, diarization, preferences, and logging
- `Services/`: app data, persistence, preferences, updates, entitlement, export, provisioning, and model management
- `DeferredUpdateInstallWindow.xaml` and `DeferredUpdateInstallWindow.xaml.cs`: modal exit-time update progress window
- `Audio/`: capture, playback, standardization, chunk planning, and audio utilities
- `Abstractions/`: shared contracts and models
- `AudioScript.Tests/`: unit tests
- `AudioScript.Package/`: MSIX / Microsoft Store packaging assets and manifest

## Data And Storage

AudioScript stores user data in app-local folders.

### Packaged builds

Root:

- `%LocalAppData%\Packages\<PackageFamilyName>\LocalState`

Subfolders:

- `Models`
- `Provisioning`
- `Assets`
- `Sessions`
- `Logs`
- `Temp`
- `Settings`

Additional update state:

- `%LocalAppData%\Packages\<PackageFamilyName>\LocalState\Settings\update-state.json`

Settings file:

- `%LocalAppData%\Packages\<PackageFamilyName>\LocalState\Settings\app-preferences.json`

### Unpackaged builds

Root:

- `%LocalAppData%\AudioScript`

Subfolders:

- `Models`
- `Provisioning`
- `Assets`
- `Sessions`
- `Logs`
- `Temp`
- `Settings`

Additional update state:

- `%LocalAppData%\AudioScript\Settings\update-state.json`

Settings file:

- `%LocalAppData%\AudioScript\Settings\app-preferences.json`

### Session Notes

- Session identity is derived from a SHA-256 audio fingerprint
- Transient live-recording sessions are stored separately from imported audio sessions
- Missing session audio is reported in the UI and can be restored from a replacement file when the fingerprint matches

## Requirements

- Windows 10 or Windows 11
- .NET SDK `10.0.201` or compatible SDK from `global.json`
- For Store packaging:
  - Visual Studio 2026 MSBuild
  - Windows SDK tools including `makeappx.exe`

## Run Locally

```powershell
dotnet run --project .\AudioScript.csproj
```

## Build Development

Use the fast build path for routine development:

```powershell
msbuild .\AudioScript.csproj /t:Build /p:Configuration=Debug /p:RunAnalyzers=false /m
```

Verified with:

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe' .\AudioScript.csproj /t:Build /p:Configuration=Debug /p:RunAnalyzers=false /m
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

## Microsoft Store Package

```powershell
.\Build-StorePackage.ps1
```

Default output root:

- `AudioScript.Package\AppPackages\store-x64-self-contained`

The packaging script:

- Publishes a self-contained `win-x64` build
- Removes unsupported runtime payload and heavy provisioned assets from the package layout
- Creates a single x64 `.msix` package
- Bundles the x64 package into a single-architecture `.msixbundle`
- Produces the x64 bundle `.msixupload` artifact required by Partner Center

## Notes

- Transcription runs locally and does not require an API key
- Speaker diarization runs locally through bundled `pyannote-community-1` assets
- Packaged builds use Store update APIs for entitlement checks and update handling
- Restore purchase is available from Settings via `Re-check Premium`; it rechecks entitlement and reports the current Premium state in a small toast
- Clicking the bottom-right version label runs the same user-initiated update flow as the main footer button
- Unpackaged builds skip Store update checks safely
- Store update behavior assumes Microsoft Store automatic app updates are off and handles that case by falling back to Store / OS UI when needed
- Startup update checks are silent and may be throttled without affecting deferred install revalidation on close
