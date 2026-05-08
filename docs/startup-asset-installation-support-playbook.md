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

## Release Gates (Operational)

- Gate 1: policy lock checklist `SAIP-01` through `SAIP-09` complete.
- Gate 2: automated startup provisioning tests pass.
- Gate 3: packaged-install manual smoke test on clean profile completed.
