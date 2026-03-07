# TODO

## Transcript Editing Persistence (Not Implemented Yet)
- Define a persisted project/session format for editable finalized transcript lines:
  - timeline value
  - transcription text
  - source audio file path (and optional file hash for validation)
  - selected model and transcription metadata
- Add `Save` and `Save As` commands in the main window.
- Add `Open` command to restore a saved editable transcript session.
- Handle missing or moved audio files when reopening a session.
- Add autosave option (disabled by default) and crash-safe recovery.
- Add versioning/migration for future session schema changes.
- Add unit tests for save/load roundtrip and malformed session handling.
