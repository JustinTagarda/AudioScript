# Bundled whisper.cpp assets

Place whisper.cpp runtime files here so end users do not need to download them manually.

Expected structure:

- Release/
  - whisper-cli.exe
  - ggml-base.dll
  - ggml.dll
- models/
  - one or more model files (.bin or .gguf), e.g. ggml-base.en.bin

At build/publish time, everything under this folder is copied to output as:

- whisper/whisper-cli.exe
- whisper/Release/*
- whisper/models/*

Runtime defaults in `WhisperCppOptions` will auto-detect these bundled files.
Environment variables (`WHISPER_CPP_CLI_PATH`, `WHISPER_CPP_MODEL_PATH`) still override bundled defaults.

Automation:

- Run `scripts/prepare-whisper-assets.ps1` to download `whisper-cli.exe` and `ggml-base.en.bin` automatically.
- Build/publish copies this folder to app output as `whisper/`.
- End users can click `Start Live` without manual Whisper setup.
