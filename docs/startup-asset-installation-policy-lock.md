# Startup Asset Installation Policy Lock

Instruction source: `D:\Projects\desktop-startup-asset-installation-policy.md`

## Policy Lock Checklist

- `SAIP-01` Startup auto-install trigger in production:
  - Acceptance: when running in production context, missing required manifest assets are auto-installed at startup before normal app interaction.
- `SAIP-02` Production definition:
  - Acceptance: packaged/store context is treated as production; installer-style execution is treated as production; dev execution remains unchanged unless explicitly forced by environment toggle.
- `SAIP-03` Provisioned assets source:
  - Acceptance: required assets come from `assets/bootstrap/asset-manifest.json` through the provisioning service manifest API.
- `SAIP-04` Startup progress surface title:
  - Acceptance: startup surface text includes `Initializing... please wait`.
- `SAIP-05` Non-interactive startup installation flow:
  - Acceptance: no user controls required for startup installation and close attempts are blocked until provisioning completes.
- `SAIP-06` Surface visibility during installation:
  - Acceptance: startup surface remains visible for the entire startup asset installation run and displays per-asset progress bars with percentages.
- `SAIP-07` Continue-on-failure behavior:
  - Acceptance: failure for one asset does not stop installation attempts for remaining assets.
- `SAIP-08` Failure summary modal:
  - Acceptance: after all attempts complete, if failures exist, a single informational modal lists each failed asset and reason/limitation.
- `SAIP-09` Continue into main app:
  - Acceptance: after failure summary is dismissed (or no failures), app continues to main window startup.

## PR Traceability Requirement

Any PR touching startup provisioning behavior must include:

- A `Policy Lock` section in the PR body.
- Explicit references to each satisfied checklist item (`SAIP-xx`).
- Test evidence for each referenced checklist item.

## No Drift Rule

- Behavior changes that deviate from this policy are not allowed as ad-hoc implementation changes.
- Any deviation request must be tracked as a separate approved exception record and linked from the PR.
