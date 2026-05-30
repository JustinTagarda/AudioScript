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