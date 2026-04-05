# AudioScript Architecture Analysis

## 1. Executive Summary

AudioScript is a single-instance WPF desktop application on .NET 10 focused on local-audio transcription workflows, with optional OpenAI-powered AI assist and speaker diarization.

Current architecture is a pragmatic desktop MVVM style:

- App startup and composition are centralized in App.xaml.cs.
- UI orchestration is concentrated in MainWindow.xaml.cs.
- Stateful workflow and persistence orchestration are concentrated in ViewModels/MainViewModel.cs.
- Transcription, diarization, persistence, placement, and settings concerns are split into Services and Audio helpers.

Main strengths:

- clear end-user workflow for import -> transcribe -> edit -> copy
- robust local persistence and recovery model with SHA-256-based session identity
- strong focused unit test suite for high-risk service behavior
- explicit handling of long-running OpenAI requests and cancellation

Main weaknesses / risks:

- MainWindow.xaml.cs and MainViewModel.cs are large orchestration hotspots
- update/version policy components exist but are not currently wired into startup/runtime flow
- AI prompt text currently includes hardcoded prose in service configuration

High-value next steps:

1. keep extracting workflow units from MainWindow/MainViewModel into focused collaborators
2. formalize prompt ownership strategy for AI request text
3. decide and implement intended runtime wiring for update/version check behavior

## 2. Project Purpose And Scope

AudioScript provides desktop transcription workflows for local audio files:

- Segment mode:
  - Manual timeline placeholder generation
  - AI-assisted segment transcription using playback capture
- Speaker diarization mode:
  - Silence-aware chunk planning
  - Chunked OpenAI requests
  - Speaker-labeled merged output

The app is local-first and session-oriented, with reopen/recovery behavior built around audio fingerprint identity.

## 3. Source-Verified Stack

Application and runtime:

- .NET: net10.0-windows
- UI: WPF
- Startup object: AudioScript.App
- Single-instance behavior: Mutex + activation event signaling
- Windows Forms interop: used for screen/window placement handling

Dependencies:

- NAudio: playback, loopback capture, audio processing
- SharpVectors.Wpf: SVG assets (titlebar resources)
- OpenAI Audio Transcriptions endpoint integration

Testing:

- xUnit test project: AudioScript.Tests

Packaging and distribution:

- MSIX Store packaging project: AudioScript.Package
- Build script: Build-StorePackage.ps1
- Publishes self-contained win-x64 and win-arm64, then packages/bundles artifacts

## 4. Repository Map

Top-level structure (functional view):

- App.xaml.cs
  - startup composition
  - single-instance enforcement
  - main window creation and lifecycle shutdown
- MainWindow.xaml + MainWindow.xaml.cs
  - interaction-heavy transcript UI orchestration
  - batch transcription flow, row operations, copy actions, dialog flow
- ViewModels/MainViewModel.cs
  - core state, command gating, transcript collections, autosave, session operations
- Services/
  - AI request services/parsers
  - session store, preferences store, credential store
  - diagnostics and window placement
- Audio/
  - playback/capture and chunk-planning primitives
- Abstractions/
  - shared contracts and domain records
- AudioScript.Tests/
  - service and view-model unit tests

## 5. Architecture And Responsibility Boundaries

### 5.1 Startup And Composition

App.xaml.cs composes runtime dependencies in one place:

- OpenAiTranscriptionOptions and credential snapshot load
- App preferences + theme application
- HttpClient initialization with infinite timeout
- construction of parsing, audio, transcription, diarization, and session services
- MainViewModel and MainWindow creation
- window placement restore/attach

Assessment:

- Composition is straightforward and explicit.
- Service graph is understandable.
- No DI container is used; manual composition is acceptable for current size.

### 5.2 UI And Workflow Layering

MainWindow.xaml.cs:

- owns UI event handlers, dialog interactions, batch locks, grid-focused editing orchestration
- coordinates playback-edit transcription sessions for row-level operations

MainViewModel.cs:

- owns app state, command enablement, transcript mode state, autosave scheduling, and persistence orchestration
- exposes operations for placeholders, speaker diarization, session management, settings behavior

Assessment:

- Separation exists (window for UI orchestration, view model for state/business orchestration).
- Two major files remain high-coupling hotspots and primary change-risk zones.

### 5.3 Service Layer

Services are mostly focused:

- PlaybackTranscriptionService: OpenAI transcription request lifecycle and parsing integration
- ChunkedSpeakerDiarizationService + OpenAiSpeakerDiarizationService: chunk planning and diarization requests
- TranscriptSessionStore: session identity and persistence/recovery
- AppPreferencesStore/OpenAiCredentialStore/WindowPlacementService: local settings and shell integration concerns
- ProcessLogService: event-driven in-memory log emission

Assessment:

- Service responsibilities are generally cohesive.
- AI behavior remains reasonably centralized in service/options paths.

## 6. Data And Persistence Model

Local storage root:

- %LocalAppData%\AudioScript

Key persisted artifacts:

- Sessions\<sessionId>\session.json
- Sessions\<sessionId>\audio\...
- app-preferences.json
- window-placement.json

Credential storage:

- Windows Credential Manager target: AudioScript.OpenAI.ApiKey

Session identity:

- SHA-256 fingerprint of imported source audio determines session id

Reliability characteristics:

- atomic write patterns for key JSON persistence paths
- resilient default fallbacks on failed settings reads
- stored-audio integrity verification during session load

## 7. Runtime Flow Analysis

### 7.1 Import And Session Resolution

1. User opens/drops an audio file.
2. Session store validates and fingerprints the file.
3. Existing or new session document is materialized.
4. Audio copy metadata and integrity fields are saved.

### 7.2 Segment Mode

Manual branch:

- creates timeline placeholders using fixed segment duration
- enables manual text editing in-grid

AI-assisted branch:

- requires configured API key and AI-assist mode
- performs batch row orchestration with playback-driven capture/transcription
- supports cancellation and row-level progress signaling

### 7.3 Speaker Diarization Mode

1. Validates API key and current audio state.
2. Uses silence detection + chunk planner for request boundaries.
3. Executes chunked diarization requests.
4. Resolves/merges speaker mappings into final transcript lines.

### 7.4 Edit And Copy

- grid supports timeline/text edits plus row insert/duplicate/delete actions
- copy workflows support text-only and timeline+text variants
- edits are persisted through autosave scheduling in view model

## 8. Observability And Error Handling

Observability:

- ProcessLogService emits categorized runtime messages consumed by UI
- useful for diagnosing transcription/diarization request paths

Error handling patterns:

- service and persistence paths favor resilience with fallback defaults
- UI surfaces blocking requirements (for example, missing API key)
- cancellation behavior is explicit in long-running operations

## 9. Test Coverage Snapshot

High-value tested areas include:

- playback transcription service/session behavior
- speaker diarization request behavior
- silence-aware chunk planner logic
- transcript session store persistence/recovery behavior
- app preferences and credential store behavior
- window placement behavior
- main view model behavior

Assessment:

- core non-UI logic has meaningful regression coverage
- UI-heavy orchestration in MainWindow remains comparatively less isolated/testable

## 10. Technical Debt And Risks

### 10.1 Large Orchestration Hotspots

Where:

- MainWindow.xaml.cs
- ViewModels/MainViewModel.cs

Risk:

- higher regression probability for cross-cutting changes
- harder to reason about side effects across playback, editing, and persistence

Priority:

- High

### 10.2 Update/Version Policy Not Runtime-Wired

Where:

- Services/ApplicationVersionCheckService.cs
- UpdateRequiredDialogWindow.xaml.cs

Risk:

- policy behavior may diverge from intent if callers are not established

Priority:

- Medium

### 10.3 Prompt Ownership Governance Risk

Where:

- OpenAiTranscriptionOptions default prompt text

Risk:

- prompt drift if prose expands in multiple locations over time

Priority:

- Medium

## 11. Refactoring Opportunities

### 11.1 Extract Batch Transcription Coordinator

Target:

- move segment batch orchestration from MainWindow into a dedicated coordinator/service

Benefits:

- smaller UI code-behind
- easier unit testing of batch state transitions and cancellation

Risk:

- Medium

### 11.2 Extract Transcript Grid Edit Controller

Target:

- isolate row command and edit-loop behaviors from window event surface

Benefits:

- lowers coupling between visual tree concerns and workflow rules

Risk:

- Medium

### 11.3 Formalize Update Wiring Decision

Target:

- either wire ApplicationVersionCheckService into startup flow with explicit UX, or retire/defer component

Benefits:

- removes ambiguity in operational behavior

Risk:

- Low to Medium

### 11.4 Prompt Configuration Ownership

Target:

- define single ownership location/rule for AI prompt prose and derived prompt variants

Benefits:

- avoids duplicated AI instruction text and drift

Risk:

- Low

## 12. Suggested Implementation Roadmap

Phase 1 (low-risk clarity):

1. Document and decide update/version runtime strategy.
2. Add small architecture notes for current startup/service graph.
3. Add tests where current behavior is ambiguous around cancellation and autosave edges.

Phase 2 (targeted extraction):

1. Extract segment batch coordinator.
2. Extract transcript grid edit controller.
3. Keep MainWindow as wiring shell for UI events.

Phase 3 (governance hardening):

1. Enforce prompt ownership and request-construction conventions.
2. Add test coverage for any new prompt/config rules.

## 13. Verification Guidance For Future Changes

If changing transcription request/response behavior:

- run PlaybackTranscriptionServiceTests
- run OpenAiTranscriptionModelCatalogTests when model selection/ids change

If changing diarization chunking or mapping:

- run SilenceAwareChunkPlannerTests
- run OpenAiSpeakerDiarizationServiceTests

If changing session persistence/recovery:

- run TranscriptSessionStoreTests
- run MainViewModelTests

If changing playback capture/session behavior:

- run PlaybackAudioCaptureServiceTests
- run PlaybackTranscriptionSessionTests

If changing startup/window state behavior:

- run WindowPlacementServiceTests
- run MainViewModelTests

## 14. Open Questions

1. Should ApplicationVersionCheckService be wired into App startup and, if yes, what is the intended blocking UX?
2. Should prompt text remain code-defined in options or move to a dedicated settings/resource contract?
3. What level of UI automation coverage is desired for MainWindow interaction-heavy workflows?
