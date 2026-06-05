# AudioScript

AudioScript is a Windows desktop WPF app for local/offline audio transcription, speaker diarization, transcript editing, and session management.

## Overview

- Imports local audio files for playback and transcription
- Captures live audio from playback and/or microphone
- Runs transcription locally with a bundled `whisper-small` model and optional premium Whisper models
- Processes live transcription in parallel chunk workers with adaptive dispatch
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

## Live Transcription Pipeline

- Live audio is captured from playback and/or microphone, segmented into rolling WAV chunks, and transcribed locally.
- Live chunk transcription is dispatched through parallel workers to reduce queue buildup during active sessions.
- Chunk submission uses adaptive hold behavior so boundary context can be preserved without reintroducing long startup stalls.
- Stop/finalize paths use priority drain behavior so buffered chunks are flushed immediately when a live run ends.
- Live transcripts currently do not use interim text updates. The app commits finalized chunk results only.

### Key Implementation Areas

- `Services/LiveSegmentTranscriptionSession.cs`
  - live chunk buffering, adaptive dispatch, parallel worker orchestration, stop-drain behavior
- `Services/WhisperAudioTranscriptionService.cs`
  - local Whisper invocation and request execution
- `Audio/SegmentedLiveRecordingWaveStream.cs`
  - segmented live WAV capture support
- `ViewModels/MainViewModel.cs`
  - live transcript append, row consolidation, boundary cleanup, and quality heuristics
- `Services/ProcessLogService.cs`
  - process logging used for live timing and transcription diagnostics

### Current Quality-Tuning Notes

- Current tuning work is focused on live transcript boundary quality rather than throughput.
- The main post-processing logic is in `ViewModels/MainViewModel.cs`, where the app:
  - drops placeholder and malformed live rows
  - trims repeated adjacent boundary phrases
  - merges or reattaches short continuation fragments when they clearly belong together
  - preserves a conservative cleanup baseline to avoid over-normalizing valid text
- Remaining quality issues are concentrated at chunk boundaries, especially where model output introduces duplicated fragments or substitutions. The current strategy favors small, targeted cleanup rules over broad transcript rewriting.

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
- Footer version display: derived from the Store package version and normalized to `Major.Minor.Build.0`
- Store package target architecture: `x64` only
- Upload mode: `StoreUpload`

### Generate Store Upload Artifact

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' .\AudioScript.Package\AudioScript.Package.wapproj /t:Restore,Build /p:Configuration=Release /p:Platform=x64 /p:UapAppxPackageBuildMode=StoreUpload /p:AppxBundle=Always /p:AppxBundlePlatforms=x64 /m
```

### Expected Output Location

- `AudioScript.Package\AppPackages\*.msixupload`
- Current Store package version: `2.0.20.0`

## Premium Entitlement Model

- Basic mode remains usable forever.
- Basic can run Live Transcription for up to 10 minutes per active run. When that limit is reached, the app stops the live session and surfaces the Premium upsell flow.
- Basic can use Speaker Diarization / Detect Speaker for up to 5 minutes of diarized audio per run.
- Basic cannot install or use premium models: `whisper-medium`, `whisper-large-v3`, `whisper-large-v3-turbo`.
- Premium is unlocked only by Microsoft Store durable add-on ownership.
- Premium removes the 10-minute Live Transcription cap and unlocks the premium-only features above.
- Add-on catalog source of truth: `Services/Store/StorePremiumAddonCatalog.cs`
- Entitlement verification and fallback cache: `Services/AppEntitlementModels.cs`
- Premium CTA convergence uses the shared in-app purchase flow (`StoreContext.RequestPurchaseAsync(addOnStoreId)`), including the footer `AppStatusDisplay` Upgrade button and the Settings window premium upsell path.

## Bundled Engine Runtime

- Production packages are expected to include the required/basic engine runtime:
  - `whisper-small`
  - `whisper.cpp` CLI runtime
  - `pyannote-community-1` model
  - bundled Python x64 runtime and required diarization modules
- Premium Whisper models remain optional installs and are not bundled by default.
- Packaged production builds do not repair or re-download required bundled engines at runtime.
- If the bundled runtime is missing or corrupted in production, reinstall AudioScript from Microsoft Store.

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
