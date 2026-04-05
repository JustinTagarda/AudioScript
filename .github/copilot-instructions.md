# AudioScript

## Overview

AudioScript is a WPF desktop app on .NET 10 for transcribing audio from local files and live playback capture, with optional OpenAI-powered transcription and speaker diarization.

Primary responsibilities:

- import supported audio files and manage playback preview
- generate transcript timelines in segment mode (manual or AI-assisted)
- generate speaker-diarized transcripts through chunked OpenAI requests
- support in-grid transcript editing, timeline adjustments, copy workflows, and per-row re-transcription
- persist transcript sessions and audio metadata locally for reopen/recovery
- manage OpenAI API key, AI preferences, and application theme settings

Core stack and dependencies:

- WPF on `net10.0-windows`
- Windows Forms interop (window placement/screen awareness)
- NAudio for playback, capture, and audio processing
- SharpVectors for SVG titlebar assets
- OpenAI Audio Transcriptions API integration
- xUnit in `AudioScript.Tests`

Most state lives under `%LocalAppData%\AudioScript`. OpenAI API key storage uses Windows Credential Manager target `AudioScript.OpenAI.ApiKey`.

OpenAI is required for AI-assisted segment transcription and speaker diarization.

## Architecture

### Startup And Window Lifecycle

1. `App.Main` creates and runs WPF application startup.
2. `App.OnStartup` enforces single-instance behavior via mutex/event handle and activation signaling.
3. Dependencies are composed in `App.OnStartup` (HTTP client, audio services, stores, parsers, view model).
4. `MainWindow` is created with `MainViewModel`, window placement is restored via `WindowPlacementService.Apply`, and live placement persistence is attached.
5. On shutdown, app disposes view model/services, stops activation listener, and releases mutex safely.

### Main UI Surfaces

- Main transcription surface in `MainWindow`:
  - audio import and drag/drop
  - playback transport and timeline sync
  - transcript mode selection (`Segments` or `Speaker diarization`)
  - segment batch transcription orchestration
  - transcript grid editing, copy actions, and row operations
  - process log stream and status feedback
- OpenAI settings modal in `OpenAiSettingsWindow`
- shared confirmation/error dialogs in active workflows:
  - `ConfirmationDialogWindow`
  - `ErrorDialogWindow`
- available but currently not invoked by runtime call sites:
  - `UpdateRequiredDialogWindow`

### Core Runtime Flow

1. User imports an audio file (open dialog or drag/drop).
2. `TranscriptSessionStore` fingerprints the file (SHA-256), creates/loads a session, and stores audio/session metadata.
3. User selects transcript mode:
   - `Segments`: placeholder timeline generation; optional AI per-segment transcription via playback capture/transcription pipeline.
   - `Speaker diarization`: chunk planning + silence-aware chunking + OpenAI diarization requests + merged speaker-labeled output.
4. Transcript lines are editable in UI and persisted through session autosave.
5. Copy actions read from current transcript collections and user preferences.
6. Reopening a recent session restores transcript data and validates stored audio integrity.

### Storage And Persistence

Key local state:

- `%LocalAppData%\AudioScript\Sessions\<sessionId>\session.json`
- `%LocalAppData%\AudioScript\Sessions\<sessionId>\audio\...`
- `%LocalAppData%\AudioScript\app-preferences.json`
- `%LocalAppData%\AudioScript\window-placement.json`

Credential storage:

- Windows Credential Manager target: `AudioScript.OpenAI.ApiKey`

Persistence and failure behavior:

- session and preferences use JSON with resilient fallback defaults on read failure
- persistence writes use atomic temp/replace patterns where implemented
- missing/corrupt stored audio is detected on session load and surfaced as recovery guidance
- UI favors continuity and user feedback over hard failures when persistence errors occur

### Diagnostics

- process diagnostics are emitted through `ProcessLogService` and surfaced in UI log panels
- logs are primarily in-memory/event-driven during runtime; inspect `ProcessLogService` consumers first when debugging flow issues
- for network/transcription failures, prioritize playback/openai log messages emitted by service classes

### Constraints

- Do not break single-instance activation behavior in `App`.
- Do not break session identity semantics based on audio SHA-256 fingerprints.
- Do not change local storage paths lightly (`%LocalAppData%\AudioScript\...`).
- Do not assume OpenAI-dependent features work without a configured API key.
- Keep manual transcription flow functional even when AI services are unavailable.
- `MainWindow` state is highly interactive; validate edit-loop, segment-batch, playback sync, and grid command behavior together.

## Instruction File Protection

Strict rule for all agents:

- Do not edit, rewrite, regenerate, move, or delete `AGENTS.md` or any similar instruction file whose purpose is to define agent behavior, workflow rules, safety constraints, or operating policy.
- Treat these files as human-owned and read-only by default.
- You may read, cite, and follow these files, but you must not modify them as part of unrelated work.
- The only exception is when the developer or user explicitly asks for a specific instruction-file change in the current conversation.
- If no explicit human request exists, stop and refuse to edit the protected instruction file.
- If a task appears to require changing an instruction file, notify the developer and ask for an explicit instruction-file update request instead of editing it.
- If it is unclear whether a file is an instruction file, treat it as protected until the developer clarifies otherwise.
- Treat this as a blocking rule, not a preference.

## Runtime Data Governance

Strict rule for all agents:

- Do not hardcode user-specific transcript content, personally identifiable information, speaker identity assumptions, API secrets, or production runtime audio/transcript payloads in app code or defaults.
- Runtime transcript/session data must come from session JSON state and user interactions, not developer-authored inline records.
- Code may define schema fields, validation rules, placeholder labels, and empty defaults, but must not embed real-user business content.
- If hardcoded runtime user/business data is discovered, stop and notify with file references before proceeding.
- Synthetic test fixtures are allowed in tests and must remain clearly non-production.
- Treat this as a blocking rule, not a preference.

## AI And Prompt Governance

Strict rule for all agents:

- Keep AI request construction centralized in service-layer configuration (`OpenAiTranscriptionOptions`, request services, and parsers).
- Avoid scattering duplicate prompt/request prose across unrelated UI and service files.
- Any new prompt or diarization instruction text should have a single owning source and be reused through configuration paths.
- Do not introduce duplicate fallback prompt branches that can drift from the owning source.
- If AI behavior changes, update or add targeted tests in `AudioScript.Tests` for parsers/services/model catalog behavior.

## UI And UX Governance

Strict rule for all agents:

### WPF UI Style Guide

This guide is for the current AudioScript WPF desktop app. It follows the existing resource-driven styling approach already used in the project.

#### Design Goals

- Modern desktop feel
- Clear hierarchy
- Fast scanning
- Minimal visual noise
- Consistent interaction patterns

#### Visual Language

- Use a restrained color palette.
- Reserve accent emphasis for primary actions, selected states, and key highlights.
- Keep semantic status colors limited, purposeful, and easy to distinguish from accent emphasis.
- Prefer neutral surfaces and readable contrast.
- Avoid strong gradients, glossy effects, and heavy shadows unless the app already uses them consistently.
- Prefer subtle styling over decorative styling.
- Reuse existing `ResourceDictionary`, `StaticResource`, `DynamicResource`, theme brushes, shared styles, and shared templates before introducing new visual treatments.

#### Typography

- Keep font usage consistent across windows and dialogs.
- Preserve a clear size hierarchy:
  - window or page title
  - section title
  - body text
  - secondary or help text
- Avoid unnecessary font-weight changes.
- Prefer readable contrast and stable text layout over decorative emphasis.

#### Spacing And Layout

- Keep spacing consistent within cards, groups, forms, and toolbars.
- Keep similar controls spaced the same way across the app.
- Prefer breathing room over cramped layouts.
- Keep labels, inputs, buttons, and lists aligned.
- Prefer `Grid` when alignment matters more than simple stacking.
- Avoid hardcoded sizes unless they are needed for usability, readability, or established window behavior.
- Prefer layouts that resize cleanly in resizable windows and panels.
- Fixed-size dialogs must remain stable and avoid clipping when content changes slightly.

#### Forms And Controls

- Align labels and inputs consistently.
- Group related inputs into clear sections.
- Keep required and invalid feedback near the related input when the current UI supports it.
- Do not rely on color alone for validation.
- Reuse existing `Style`, `ControlTemplate`, `DataTemplate`, converters, and shared resources before creating new ones.
- Preserve caret, focus, and selection behavior in text-entry controls.

#### Buttons And Actions

- Keep one clear primary action per area when possible.
- Secondary actions should not visually compete with the primary action.
- Destructive actions must be visually distinct and use appropriate caution.
- Disable actions only when necessary and make the reason clear from nearby context when possible.
- Show a busy or progress state when an action takes noticeable time.
- Reuse the existing icon approach when icons are needed. Prefer current glyph, `Path`, `Viewbox`, or `DrawingImage` patterns over introducing SVG or a new icon system.

#### Lists And Tables

- Keep row layout simple and scannable.
- Keep row density readable.
- Avoid excessive inline controls or too many competing actions in each row.
- Keep important columns visible and ordered by usefulness.
- Prefer stable alignment and predictable spacing across rows.

#### Feedback States

Loading:

- Show progress clearly.
- Keep the UI responsive whenever possible.

Empty:

- Explain what the user can do next.

Error:

- Show a useful message near the affected area when possible.
- Avoid disruptive dialogs for simple recoverable issues when inline feedback is enough.

Success:

- Keep confirmation noticeable but not disruptive.

#### Window Behavior

- Preserve sensible minimum sizes.
- Resizable windows and panels should resize gracefully.
- Fixed-size dialogs should preserve layout stability and avoid clipped content.
- Avoid layout breakage when text expands.
- Keep focus behavior predictable on open and close.
- Avoid sudden shifts in window size or position during normal interaction.

#### Accessibility And Usability

- Maintain sensible tab order.
- Preserve keyboard navigation and Enter/Escape expectations where present.
- Ensure visible focus indication.
- Avoid very small click targets.
- Keep common actions easy to find.
- Reduce unnecessary clicks where it is safe to do so.
- Do not introduce surprising behavior.

#### Refactor Priorities

1. Fix broken alignment and spacing.
2. Clarify primary and secondary actions.
3. Improve visual consistency.
4. Reduce clutter.
5. Only then consider stylistic polish.

### WPF Screen Checklist

Use this checklist when adding or changing a WPF screen, dialog, panel, or reusable control in AudioScript.

This checklist complements the WPF UI Style Guide section above. It is focused on implementation review rather than design principles.

#### Before Changing The Screen

- Confirm the requested change is scoped to the relevant screen, dialog, panel, or control.
- Identify nearby screens or controls that already solve the same UI problem.
- Reuse existing `Style`, `ControlTemplate`, `DataTemplate`, converters, and shared resources before creating new ones.
- Confirm whether the target surface is a resizable window, fixed-size dialog, embedded panel, or reusable control.
- Check whether the screen already has established loading, empty, error, and success patterns.

#### Layout And Alignment

- Keep labels, inputs, buttons, and lists aligned with nearby components.
- Use `Grid` when alignment matters more than simple stacking.
- Keep spacing consistent within sections and between related groups.
- Avoid unnecessary nesting that makes alignment harder to maintain.
- Avoid hardcoded width or height values unless they are needed for usability or established window behavior.
- Preserve sensible `MinWidth` and `MinHeight` values for windows and important controls.
- Confirm the layout still reads clearly when text becomes slightly longer.

#### Visual Consistency

- Match nearby spacing, sizing, corner radius, borders, and typography.
- Reuse existing theme brushes and resource-driven colors instead of introducing new hardcoded values.
- Keep accent emphasis limited to primary, selected, or high-importance states.
- Avoid decorative styling that does not already fit the app.
- Reuse the existing icon approach when needed. Do not introduce SVG or a new icon system unless explicitly requested.

#### Forms And Controls

- Group related inputs into clear sections.
- Keep required or invalid feedback close to the affected control when supported by the screen.
- Do not rely on color alone for validation or status.
- Preserve focus, caret, and selection behavior in text-entry controls.
- Keep click targets large enough for comfortable desktop use.
- Ensure destructive actions are clearly distinguishable from non-destructive actions.

#### Actions And Feedback

- Make the primary action easy to identify.
- Ensure secondary actions do not visually compete with the primary action.
- Disable actions only when necessary.
- When an operation takes noticeable time, show a visible busy or progress state.
- Prefer inline or nearby feedback for simple recoverable issues.
- Use dialogs only when interruption is justified.

#### Lists, Tables, And Item Collections

- Keep row or item layout simple and scannable.
- Keep row density readable.
- Avoid too many inline actions in one row or item card.
- Keep the most useful information easy to scan first.
- Preserve stable alignment across rows and repeated items.

#### Keyboard And Accessibility

- Verify sensible tab order.
- Preserve Enter and Escape behavior where the screen already uses it.
- Ensure focus visibility remains clear during keyboard use.
- Check that keyboard-only navigation still works for the main interaction path.
- Preserve existing shortcuts or key behaviors where present.

#### Window And Dialog Behavior

- Resizable windows and panels should resize cleanly without overlapping or clipped content.
- Fixed-size dialogs should remain visually stable and avoid clipping.
- Keep window open/close focus behavior predictable.
- Avoid sudden changes in window size or layout during normal interaction.
- Check that modal dialogs are centered and sized appropriately for their content.

#### State Checks

Verify the screen in the states that apply:

- normal
- loading
- empty
- error
- success
- disabled
- selected or active

#### Final Review

- Build the solution after UI changes only when explicitly requested by the user or when verification is required by the task.
- If the app is running and a build is requested, close it before building. Restart after build only when explicitly requested.
- Check for compile-time XAML issues.
- Check for broken bindings, missing resources, and template/resource lookup issues.
- Compare the updated screen against nearby screens for consistency.
- If the requested change conflicts with the established design system or shared component behavior, call out the conflict before broadening the refactor.

### Additional UI/UX Rules For AudioScript

- Preserve existing AudioScript visual language and interaction patterns.
- Keep transcript editing responsive and predictable under playback.
- Preserve the existing import -> transcribe -> edit -> copy flow unless explicitly redesigning.
- Keep keyboard-centric transcript editing behavior intact.
- Avoid introducing surprising playback side effects while editing timelines/text.
- Follow existing MVVM split (`MainViewModel` orchestration + `MainWindow` UI/event coordination).
- Keep business/transcription logic in services and view model layers.
- Keep UI thread responsive; avoid blocking work on the dispatcher thread.
- Preserve current binding/event wiring unless there is a clear improvement.
- Only change files needed for the requested behavior.
- Avoid unnecessary cross-cutting refactors in `MainWindow` and `MainViewModel` unless required for correctness.
- If shared services/models are changed, explain downstream impact.

## Task Routing

### Start Here By Task

- App startup, dependency wiring, and single-instance activation:
  - `App.xaml.cs`
  - `App.xaml`
- Main UI orchestration, transcript grid actions, dialogs, playback-edit loop:
  - `MainWindow.xaml.cs`
  - `MainWindow.xaml`
- Core UI state, command enablement, session autosave, transcript collections:
  - `ViewModels/MainViewModel.cs`
- Session persistence and restore behaviors:
  - `Services/TranscriptSessionStore.cs`
  - `Services/AppPreferencesStore.cs`
  - `Services/WindowPlacementService.cs`
- AI segment transcription requests/parsing:
  - `Services/PlaybackTranscriptionService.cs`
  - `Services/OpenAiTranscriptionResponseParser.cs`
  - `Services/OpenAiTranscriptionOptions.cs`
  - `Services/OpenAiTranscriptionModelCatalog.cs`
- Speaker diarization pipeline:
  - `Services/ChunkedSpeakerDiarizationService.cs`
  - `Services/OpenAiSpeakerDiarizationService.cs`
  - `Services/OpenAiSpeakerDiarizationResponseParser.cs`
  - `Audio/SilenceAwareChunkPlanner.cs`
  - `Audio/SilenceIntervalDetector.cs`
  - `Audio/WaveClipExtractor.cs`
- Audio playback/capture implementation details:
  - `Audio/NaudioAudioPlaybackService.cs`
  - `Audio/PlaybackAudioCaptureService.cs`
  - `Audio/WasapiLoopbackCaptureService.cs`
- OpenAI credential/settings flow:
  - `OpenAiSettingsWindow.xaml.cs`
  - `Services/OpenAiCredentialStore.cs`
  - `Services/OpenAiApiKeyValidationService.cs`
- Version/update policy components (currently present but not wired into app startup/runtime flow):
  - `Services/ApplicationVersionCheckService.cs`
  - `UpdateRequiredDialogWindow.xaml.cs`

### Highest-Value Files

Primary files to inspect first for most product behavior changes:

- `MainWindow.xaml`
- `MainWindow.xaml.cs`
- `ViewModels/MainViewModel.cs`
- `Services/PlaybackTranscriptionService.cs`
- `Services/PlaybackTranscriptionSession.cs`
- `Services/ChunkedSpeakerDiarizationService.cs`
- `Services/TranscriptSessionStore.cs`
- `OpenAiSettingsWindow.xaml.cs`
- `App.xaml.cs`
- `AudioScript.Tests/MainViewModelTests.cs`

### Current Workflow Dialogs

- `ConfirmationDialogWindow.xaml.cs`
- `ErrorDialogWindow.xaml.cs`
- `OpenAiSettingsWindow.xaml.cs`

Repo note:

- `Services/ApplicationVersionCheckService.cs` and `UpdateRequiredDialogWindow.xaml.cs` are covered/available but currently have no runtime call site. Verify intended wiring before changing expiration policy behavior.

## Verification

- Do not automatically build, run, or launch the app after edits unless the user explicitly requests it.

### Test Inventory

High-value test classes in `AudioScript.Tests`:

- `ApplicationVersionCheckServiceTests`
- `AppPreferencesStoreTests`
- `FinalizedTranscriptLineViewModelTests`
- `MainViewModelTests`
- `OpenAiCredentialStoreTests`
- `OpenAiSpeakerDiarizationServiceTests`
- `OpenAiTranscriptionModelCatalogTests`
- `PlaybackAudioCaptureServiceTests`
- `PlaybackTranscriptionServiceTests`
- `PlaybackTranscriptionSessionTests`
- `SilenceAwareChunkPlannerTests`
- `TranscriptSessionStoreTests`
- `WindowPlacementServiceTests`

### Commands

PowerShell-friendly examples:

```powershell
dotnet test AudioScript.Tests\AudioScript.Tests.csproj
dotnet test AudioScript.Tests\AudioScript.Tests.csproj --filter "FullyQualifiedName~AudioScript.Tests.MainViewModelTests"
dotnet test AudioScript.Tests\AudioScript.Tests.csproj --filter "FullyQualifiedName~AudioScript.Tests.TranscriptSessionStoreTests"
dotnet test AudioScript.Tests\AudioScript.Tests.csproj --filter "FullyQualifiedName~AudioScript.Tests.PlaybackTranscriptionServiceTests"
dotnet test AudioScript.Tests\AudioScript.Tests.csproj --filter "FullyQualifiedName~AudioScript.Tests.OpenAiSpeakerDiarizationServiceTests"
dotnet test AudioScript.Tests\AudioScript.Tests.csproj --filter "FullyQualifiedName~AudioScript.Tests.SilenceAwareChunkPlannerTests"
dotnet test AudioScript.Tests\AudioScript.Tests.csproj --filter "FullyQualifiedName~AudioScript.Tests.WindowPlacementServiceTests"
```

### What To Run For Common Changes

- If you touch `Services/PlaybackTranscriptionService.cs` or OpenAI request/response parsing:
  - run `PlaybackTranscriptionServiceTests`
  - run `OpenAiTranscriptionModelCatalogTests` if model handling changed
- If you change session import/persistence/recovery:
  - run `TranscriptSessionStoreTests`
  - run `MainViewModelTests` for UI-state integration expectations
- If you change playback capture/session streaming behavior:
  - run `PlaybackAudioCaptureServiceTests`
  - run `PlaybackTranscriptionSessionTests`
- If you change speaker diarization chunking or request mapping:
  - run `SilenceAwareChunkPlannerTests`
  - run `OpenAiSpeakerDiarizationServiceTests`
- If you change startup/window state behavior:
  - run `WindowPlacementServiceTests`
  - run `MainViewModelTests` where startup state/reporting can be impacted
- If you change credential/settings persistence:
  - run `OpenAiCredentialStoreTests`
  - run `AppPreferencesStoreTests`