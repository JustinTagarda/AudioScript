# Startup Asset Installation Support Playbook

## Structured Logging Contract

Startup provisioning logs must include:

- `startup_provisioning begin` with UTC timestamp and asset count.
- Per-asset structured outcome:
  - `status` in `ready`, `installed`, `failed`, `unsupported`, `skipped`.
  - `asset id`, `display name`, and normalized user-facing reason.
- `startup_provisioning end` with UTC timestamp, duration, and failure count.

## First-Run Failure Triage

1. Open latest `audioscript-YYYYMMDD.log`.
2. Locate `startup_provisioning begin` and `startup_provisioning end`.
3. Extract all per-asset entries with `status='failed'` or `status='unsupported'`.
4. Classify issue:
   - Network/download issue.
   - Permission/write failure.
   - Unsupported architecture.
   - Source not configured/unavailable.
5. Confirm user-facing modal reason matches normalized failure reason.

## Common Failure Guidance

- Network unavailable:
  - Verify internet connectivity and retry app startup.
- Permissions:
  - Verify app can write to local app data paths.
- Unsupported architecture:
  - Confirm platform support for each required asset.
- Source unavailable:
  - Validate manifest URI/source availability and packaging.
- Whisper transcription loader failure:
  - Check the bundled whisper tool directory for `whisper-cli.exe`, `whisper.dll`, `ggml.dll`, `ggml-base.dll`, `ggml-cpu.dll`, `msvcp140.dll`, `vcruntime140.dll`, `vcruntime140_1.dll`, and `vcomp140.dll`.
  - If `whisper-cli.exe` exits immediately with `0xC0000135`, treat it as a missing native dependency problem rather than an engine/model failure.
- Detect Speaker dependency failure:
  - Check the on-demand speaker-diarization installation under package `LocalState\Assets` first.
  - Confirm the Pyannote Community-1 model and Python x64 runtime download endpoints are available before release.
  - A `404 (Not Found)` for the pyannote runtime download path indicates a release-publishing regression, not an inference failure.
  - If the installed speaker-diarization runtime is missing, corrupted, or has native loader failures, instruct the user to run Detect Speaker again to trigger repair.
  - Reserve “reinstall AudioScript from Microsoft Store” guidance for startup validation failures of bundled required assets, not for on-demand speaker-diarization runtime repair scenarios.

## Release Gates (Operational)

- Gate 1: policy lock checklist `SAIP-01` through `SAIP-11` complete.
- Gate 2: automated startup provisioning tests pass.
- Gate 3: packaged-install manual smoke test on clean profile completed.
