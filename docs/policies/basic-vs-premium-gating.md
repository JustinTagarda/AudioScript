# Basic vs Premium Gating Policy

## Purpose

This document is the product contract for Basic and Premium feature access in AudioScript.
Any code change that affects gating behavior must keep this policy accurate.

## Scope

This policy applies to:

- Entitlement state and Microsoft Store verification flow.
- Feature-level gating (Live Transcription, Speaker Diarization, model installs/usage).
- Basic session-cap upsell behavior.
- Startup wiring that determines whether gating is active in packaged production builds.

## Definitions

- Basic: user does not have Premium entitlement.
- Premium: user has Premium entitlement.
- Packaged production build: Microsoft Store installed app/package context.

## Source of Truth

- Entitlement model and gate helpers: `Services/AppEntitlementModels.cs`
  - `StoreEntitlementService`
  - `AppFeatureAccess`
- Runtime VM gating behavior: `ViewModels/MainViewModel.cs`
- UI enforcement and upsell entry points: `MainWindow.xaml.cs`, `SettingsWindow.xaml.cs`
- Store add-on identifiers: `Services/Store/StorePremiumAddonCatalog.cs`

## Access Matrix

### Basic (must be allowed)

- File transcription using `whisper-small`.
- Manual transcription mode (`manual-transcription`).
- Session management up to 10 sessions.

### Basic (must be blocked)

- Live Transcription.
- Speaker Diarization / Detect Speaker.
- Installing or using premium models:
  - `whisper-medium`
  - `whisper-large-v3`
  - `whisper-large-v3-turbo`

### Premium (must be allowed)

- All Basic features.
- Live Transcription.
- Speaker Diarization / Detect Speaker.
- Installing and using premium models.
- No Basic 10-session cap.

## Production Wiring Requirements

For store-installed production behavior, startup must create and pass a real entitlement service instance to `MainViewModel`.

Required behavior in packaged builds:

- `MainViewModel` receives non-null `IEntitlementService`.
- Premium status is derived from Store entitlement verification, not development fallback.
- Purchase and restore/re-check flows are functional.

## Known Risk (Current Branch Snapshot)

At the time of writing, startup currently passes `entitlementService: null` in `App.xaml.cs`, which causes the ViewModel to use development fallback entitlement (`HasPremium = true`).
This bypasses production Basic/Premium separation at startup unless explicitly wired.

Treat this as a compliance gap against this policy for packaged production behavior.

## Restoration Checklist

When restoring or validating gating:

1. Confirm packaged startup wires a non-null `IEntitlementService` into `MainViewModel`.
2. Confirm Basic cannot run Live Transcription.
3. Confirm Basic cannot run Speaker Diarization.
4. Confirm Basic cannot install/use premium models.
5. Confirm Basic session creation blocks at 10 and raises Premium upsell.
6. Confirm Premium can access all gated features.
7. Confirm downgrade flow reverts premium-only selected model to `whisper-small`.
8. Confirm tests covering this policy pass in CI.

## Change Control

Any intended product change to Basic/Premium boundaries must include:

1. Policy document update.
2. Regression test updates.
3. Clear PR note explaining the product decision and migration implications.

