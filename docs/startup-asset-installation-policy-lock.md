# Startup Asset Validation Policy Lock

Instruction source: `D:\Projects\desktop-startup-asset-installation-policy.md`

## Policy Lock Checklist

- `SAIP-01` Startup bundled-runtime validation in production:
  - Acceptance: when running in production context, required bundled manifest assets are validated at startup before normal app interaction.
- `SAIP-02` Production definition:
  - Acceptance: packaged/store context is treated as production; installer-style execution is treated as production; dev execution remains unchanged unless explicitly forced by environment toggle.
- `SAIP-03` Provisioned assets source:
  - Acceptance: required assets come from `assets/bootstrap/asset-manifest.json` through the provisioning service manifest API.
- `SAIP-04` Startup progress surface title:
  - Acceptance: startup surface text includes `Initializing... please wait`.
- `SAIP-05` Non-interactive startup validation flow:
  - Acceptance: no user controls required for startup validation and close attempts are blocked until validation completes.
- `SAIP-06` Surface visibility during validation:
  - Acceptance: startup surface remains visible for the entire startup validation run and displays per-asset status.
- `SAIP-07` Fail-fast recovery model:
  - Acceptance: packaged production does not repair, mutate, or re-download required bundled assets at runtime.
- `SAIP-08` Failure summary modal:
  - Acceptance: after all attempts complete, if failures exist, a single informational modal lists each failed asset and reason/limitation.
- `SAIP-09` Recovery guidance:
  - Acceptance: packaged production failure messaging instructs the user to reinstall AudioScript from Microsoft Store.

## PR Traceability Requirement

Any PR touching startup bundled-runtime validation behavior must include:

- A `Policy Lock` section in the PR body.
- Explicit references to each satisfied checklist item (`SAIP-xx`).
- Test evidence for each referenced checklist item.

## No Drift Rule

- Behavior changes that deviate from this policy are not allowed as ad-hoc implementation changes.
- Any deviation request must be tracked as a separate approved exception record and linked from the PR.
