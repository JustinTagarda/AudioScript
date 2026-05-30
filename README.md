# AudioScript

AudioScript is a Windows desktop WPF app for local/offline audio transcription, speaker diarization, transcript editing, and session management.

## Overview

- Imports local audio files for playback and transcription
- Captures live audio from playback and/or microphone
- Runs transcription locally with installed Whisper models
- Runs speaker diarization locally with bundled `pyannote-community-1` assets
- Supports transcript editing, row operations, autosave, and restore
- Exports transcripts to `.docx`
- Uses a single-instance startup model

## Technology Stack

- UI: WPF
- Runtime: `net10.0-windows10.0.17763.0`
- Audio: NAudio
- SVG: SharpVectors.Wpf
- Export: Open XML SDK
- Tests: xUnit

## Repository Layout

- `App.xaml.cs`: startup and dependency composition
- `MainWindow.xaml` and `MainWindow.xaml.cs`: shell UI and orchestration
- `ViewModels/MainViewModel.cs`: state, commands, transcription, diarization, preferences
- `Services/`: persistence, app data, export, provisioning, model management
- `Audio/`: audio capture/playback/chunking utilities
- `Abstractions/`: shared contracts/models
- `AudioScript.Tests/`: unit tests

## Build Development (FAST)

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' .\AudioScript.csproj /t:Build /p:Configuration=Debug /p:RunAnalyzers=false /m
```

## Run Debug EXE

```powershell
.\bin\Debug\net10.0-windows10.0.17763.0\AudioScript.exe
```

## Run Tests

```powershell
dotnet test .\AudioScript.Tests\AudioScript.Tests.csproj
```

## Microsoft Store Packaging Baseline (x64-only)

- Packaging project: `AudioScript.Package\AudioScript.Package.wapproj`
- Packaging manifest: `AudioScript.Package\Package.appxmanifest`
- Fixed manifest requirements:
  - `Identity Name=JustinTagardaSoftware.AudioScript`
  - `Identity Publisher=CN=68EC506E-4B5E-416B-93E8-BA707CA3BE0F`
  - `TargetDeviceFamily Name=Windows.Desktop`
- Store package target architecture: `x64` only
- Upload mode: `StoreUpload`

### Generate Store Upload Artifact

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' .\AudioScript.Package\AudioScript.Package.wapproj /t:Restore,Build /p:Configuration=Release /p:Platform=x64 /p:UapAppxPackageBuildMode=StoreUpload /p:AppxBundle=Always /p:AppxBundlePlatforms=x64 /m
```

### Expected Output Location

- `AudioScript.Package\AppPackages\*.msixupload`

## Premium Entitlement Model

- Basic mode remains usable forever.
- Premium is unlocked only by Microsoft Store durable add-on ownership.
- Add-on catalog source of truth: `Services/Store/StorePremiumAddonCatalog.cs`
- Entitlement verification and fallback cache: `Services/AppEntitlementModels.cs`
- Premium CTA convergence uses shared in-app purchase flow (`StoreContext.RequestPurchaseAsync(addOnStoreId)`), including the footer `AppStatusDisplay` Upgrade button.

## Microsoft Store App Update Flow

- Store update logic runs only when package identity is present and Store APIs are available.
- On startup (after first render), the app performs a non-blocking Store update check via `StoreContext.GetAppAndOptionalStorePackageUpdatesAsync()`.
- Startup checks are throttled to at least 30 minutes apart and capped to ten checks in a rolling 24-hour window.
- When no update is found at startup, the app schedules a one-hour in-session retry.
- The footer shows a compact `Update` button only when updates are positively confirmed.
- Clicking `Update` starts `StoreContext.RequestDownloadAndInstallStorePackageUpdatesAsync(...)`.
- Update progress is shown in a dedicated modal window with phase, percentage, and package detail text.
- On startup, the app attempts Store queue recovery (associated queue items) and keeps the modal state synchronized when an active update is detected.
- Update failures surface concise retry guidance; normal app usage remains available.
