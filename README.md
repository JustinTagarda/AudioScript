# AudioScript

AudioScript is a Windows desktop app (WPF, .NET 10) for transcribing audio from local files and live playback capture, with optional OpenAI-powered transcription and speaker diarization.

## Quick project analysis

- App type: single-instance WPF desktop app (`net10.0-windows`)
- Primary workflow: load audio -> generate transcript (manual segments or OpenAI) -> edit/copy output -> persist session
- AI integration: OpenAI Audio Transcriptions endpoint (`/v1/audio/transcriptions`)
- Persistence:
  - OpenAI API key in Windows Credential Manager (`AudioScript.OpenAI.ApiKey`)
  - transcript sessions under `%LOCALAPPDATA%\\AudioScript\\Sessions`
- Audio stack: `NAudio` for playback/capture and processing
- Packaging: includes `AudioScript.Package` (`.wapproj`) + `Build-StorePackage.ps1` for x64/arm64 self-contained MSIX bundle
- Test coverage: `AudioScript.Tests` (xUnit), currently `49` passing tests

## Repository layout

- `App.xaml.cs`: app startup, dependency wiring, single-instance handling
- `MainWindow.xaml(.cs)`: desktop UI
- `ViewModels/`: UI state and commands (`MainViewModel` is the main orchestration layer)
- `Services/`: app services (OpenAI calls, session store, preferences, logging, theme, version checks)
- `Audio/`: audio playback/capture/chunking utilities
- `Abstractions/`: shared domain contracts and models
- `AudioScript.Tests/`: unit tests
- `AudioScript.Package/`: Store/MSIX packaging project and assets

## Requirements

- Windows 10/11
- .NET SDK `10.0.201` (see `global.json`)
- Optional: OpenAI API key (for AI transcription/diarization features)
- Optional (Store packaging): Windows SDK tools including `makeappx.exe`

## Run locally

```powershell
dotnet run --project .\AudioScript.csproj
```

## Run tests

```powershell
dotnet test .\AudioScript.Tests\AudioScript.Tests.csproj
```

## Build

```powershell
dotnet build .\AudioScript.csproj -c Release
```

## Create Microsoft Store package

```powershell
.\Build-StorePackage.ps1
```

Default output is written under:

- `AudioScript.Package\AppPackages\store-selfcontained\...`

The script publishes self-contained `win-x64` and `win-arm64` builds, packs `.msix` files, bundles them, and produces a `.msixupload` artifact.

## Notes

- The app supports both manual transcription mode and OpenAI-assisted mode.
- Session data is keyed by audio fingerprint (SHA-256) to help resume/reopen work reliably.
- API key management is handled in-app and persisted via Windows Credential Manager.
