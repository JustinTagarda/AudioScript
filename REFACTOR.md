# Refactoring Instructions: Replace Existing Transcription Implementation

## Objective

Refactor the project VoxTranscriber to **completely replace** the current audio transcription implementation with a new OpenAI Audio Transcriptions integration optimized for the following real-world input:

- classroom recording captured on a **mobile phone**
- **background student voices** and ambient noise
- language is **mixed English and Cebuano**, but **mostly Cebuano**
- transcription output must be **exactly what was spoken**
- **no translation** under any circumstance
- priority is **highest practical transcription accuracy**, especially for **Cebuano**

## Critical Constraints

1. **Remove the existing transcription implementation entirely** before adding the new one.
2. **Clean up the codebase first**:
   - remove obsolete transcription services
   - remove old request/response models related to the previous implementation
   - remove dead configuration keys and constants related to the old transcription flow
   - remove unused helper classes, adapters, factories, wrappers, or command handlers that exist only for the old transcription logic
   - remove unused DI registrations
   - remove unreachable code paths and stale comments
3. **Keep the UI intact**.
   - do not redesign screens
   - do not alter layout/visual structure unless required for compilation
   - do not change UX behavior except where necessary to preserve compatibility with the new backend flow
4. Preserve all unrelated logic.
5. Produce a **production-ready refactor**, not a partial migration.
6. Do not keep fallback code for the old transcription provider.
7. Do not add translation features.
8. Do not add diarization unless the existing UI explicitly requires speaker labels.

---

## Required Technical Direction

Implement the new transcription flow using the OpenAI Audio Transcriptions API with these decisions:

### Model choice
Use:

- `gpt-4o-transcribe`
- `gpt-4o-mini-transcribe`

User explicitly select the model from the transcription engine selection (combobox)

### Endpoint purpose
Use the API for **transcription**, not translation.

The application must preserve the original spoken language exactly as spoken:

- Cebuano remains Cebuano
- English remains English
- mixed-language utterances remain mixed-language utterances

### Required request settings
Use the following transcription settings as the default implementation:

- `model = "gpt-4o-transcribe"` (or whatever model selected on the combobox model selection)
- `response_format = "json"`
- `temperature = 0`
- `chunking_strategy = "auto"`
- `include = ["logprobs"]`
- `stream = false`

### Prompt strategy
The request **must** include an explicit prompt that guides the model toward verbatim, non-translated, Cebuano-preserving output.

Use this exact prompt unless project conventions require a constant/resource file:

```text
This audio is a classroom recording captured on a mobile phone. The spoken language is mostly Cebuano with some English code-switching. Transcribe exactly what was spoken. Do not translate. Preserve the original language used by each speaker. Keep filler words and hesitations when audible. Use Cebuano spellings when the speech is Cebuano. If a word is unclear because of overlapping background speech or noise, keep the closest phonetic transcription instead of translating or rewriting it.
```

### Language parameter rule
Do **not** hardcode a language value. 

Default behavior:

- omit the explicit `language` parameter
- rely on the transcription prompt above

Reason:
The audio is mixed-language and mostly Cebuano. The implementation must avoid introducing a wrong forced language hint that reduces accuracy.

---

## Execution Plan

### Phase 1 — Audit and remove old implementation

Before writing any new code, inspect the codebase and identify all existing transcription-related components.

Remove or replace all old implementation pieces, including but not limited to:

- transcription services
- API clients for the previous provider or previous OpenAI flow
- request builders
- response parsers
- audio upload handlers tied to the old implementation
- settings/config sections used only by the old implementation
- view models or command handlers that call old interfaces
- stale tests covering removed behavior

After cleanup, ensure there is **one clear transcription pathway** in the codebase.

### Phase 2 — Design the new architecture cleanly

Implement a clean replacement that fits the current project architecture.

Recommended structure:

- `ITranscriptionService` or equivalent application-facing abstraction
- OpenAI-specific implementation class
- request/response DTOs only if needed
- central configuration object/options class for OpenAI transcription settings
- thin orchestration from ViewModel/application layer to service layer

The architecture must:

- if required, refactor the transciption result panel (interim and finalized)
- keep other UI unchanged
- isolate OpenAI-specific code from UI logic
- support future maintenance without rewriting the UI
- avoid leaking raw HTTP details into ViewModels

### Phase 3 — Implement OpenAI transcription integration

Implement a new service that:

1. accepts an audio file path or stream from the existing flow 
2. validates that the file exists and is readable
3. submits the file to the OpenAI Audio Transcriptions API
4. uses multipart form-data correctly
5. sends the required model/settings/prompt values
6. parses the JSON response robustly
7. returns a clean domain/application result to the rest of the app

### Phase 4 — Confidence-aware post-processing

Because the audio contains background noise and overlapping speech, capture and preserve `logprobs` from the response where available.

Implement support for low-confidence detection in the backend, even if the current UI does not expose it yet.

Required behavior:

- parse `logprobs` if returned
- keep them available in the domain result or an internal metadata structure
- do not discard them during mapping
- avoid changing the visible UI unless already supported

Do **not** invent or fabricate corrections from low-confidence tokens.

### Phase 5 — Wire into the existing app without UI redesign

Update the current wiring so the existing UI triggers the new transcription service.

Required:

- retain existing screens
- preserve button flow and user interaction as much as possible
- keep existing UI bindings where feasible
- only adapt ViewModels/commands enough to match the new backend contract

### Phase 6 — Testing and verification

Add or update tests for the new implementation.

test audio file located at root: test-audio.m4a

At minimum, cover:

- successful transcription request construction
- required request values are present
- prompt is included
- translation is not used
- JSON response parsing works
- error handling for invalid file paths
- error handling for API failures
- error handling for malformed/empty responses

If the project has integration-test patterns already, follow them. If not, add focused unit tests around the service and parsing logic.

---

## Implementation Requirements

### 1. Configuration

Introduce or update configuration in a way consistent with the existing project.

Required configurable values:

- OpenAI API key
- model name
- optional base URL only if the project already supports it
- timeout value
- optional transcription prompt override only if useful and safe

Defaults must resolve to:

- model: `gpt-4o-transcribe`
- temperature: `0`
- response format: `json`
- chunking strategy: `auto`
- include logprobs: enabled

Do not create excessive configuration complexity.

### 2. HTTP client usage

Use a proper reusable `HttpClient` pattern consistent with modern .NET application design.

Requirements:

- do not instantiate `HttpClient` per request unless the existing architecture explicitly manages it safely
- use dependency injection where the project already uses DI
- set authorization header with the OpenAI API key
- send multipart form-data correctly for file upload
- set safe request timeout for potentially long audio uploads/transcriptions

### 3. File handling

The implementation must safely handle audio files.

Requirements:

- validate file path before request
- open file stream safely
- dispose streams correctly
- avoid loading unnecessarily large files fully into memory if streaming upload is possible through the chosen implementation
- surface clear errors for inaccessible files

### 4. Response parsing

Parse the transcription response defensively.

Requirements:

- handle missing or empty text safely
- map returned transcript text cleanly
- preserve optional metadata such as duration/logprobs if available
- avoid brittle parsing assumptions
- do not treat any translated or normalized text as acceptable unless it is the actual returned transcript

### 5. Error handling
use the existing Process Logs

Implement robust production-grade error handling. 

Requirements:

- distinguish user-facing errors from internal technical details
- preserve diagnostic information for logs
- do not expose secrets
- handle HTTP non-success status codes explicitly
- capture API error payloads where useful
- fail cleanly on network timeouts, unauthorized requests, invalid request payloads, and unsupported file conditions

### 6. Logging
use the existing Process Logs

Add or update logging around the new transcription flow.

Log useful operational information without logging secrets or entire sensitive payloads.

Good logging targets:

- transcription request started
- file name / extension / size where appropriate
- request succeeded / failed
- API status code on failure
- parsing failures
- elapsed time

Do not log:

- API key
- raw authorization headers
- full transcript unless the existing app already intentionally stores/logs it and that behavior is clearly desired

### 7. Cancellation support

If the existing application already supports cancellation tokens, preserve and propagate them through the new transcription workflow.

If cancellation is already part of the UI command flow, do not break it.

### 8. Maintainability

The refactor must leave the codebase cleaner than before.

Requirements:

- remove duplication
- keep class responsibilities narrow
- avoid giant service classes
- avoid magic strings scattered across the codebase
- avoid embedding unrelated UI logic in the API service

---

## Required Behavior Contract

The final implementation must behave as follows:

1. User selects or supplies an audio file using the existing UI.
2. The app sends the audio to the OpenAI transcription endpoint using `gpt-4o-transcribe`.
3. The app requests verbatim transcription with the provided no-translation Cebuano-focused prompt.
4. The app returns transcript text reflecting the spoken language exactly as heard.
5. The app does not translate Cebuano to English.
6. The app keeps the UI intact.
7. The app no longer contains the old transcription implementation.

---

## Output Expectations for the Refactor

When making changes, provide a complete refactor with all affected files updated consistently.

Do not leave the project in an intermediate state.

### Mandatory deliverables in the code changes

- removal of old transcription implementation
- new OpenAI-based transcription service
- updated dependency wiring
- updated config/options handling
- updated request/response/domain models as needed
- updated tests
- successful compilation in the target solution structure

### Mandatory report back

After completing the refactor, provide:

1. a concise summary of what was removed
2. a concise summary of what was added
3. a file-by-file change list
4. any required app settings/environment updates
5. any assumptions made based on actual inspected code
6. confirmation that the UI layout was left intact

---

## Non-Goals

Do not implement the following unless they already exist and are required to preserve compatibility:

- UI redesign (except the transcription result panel with interim and finalized, refactor this panel if neccessary)
- speaker diarization
- subtitle export formats
- translation
- transcript rewriting or polishing
- automatic punctuation rewriting beyond what the model returns naturally
- speculative Cebuano normalization rules outside the model prompt

---

## Quality Bar

The result must be suitable for a real production desktop app.

That means:

- clean replacement, not layered hacks
- no dead legacy transcription code left behind
- strong error handling
- maintainable service boundaries
- exact preservation of the existing UI
- implementation aligned with the stated transcription requirements

---

## Final Instruction

Inspect the current codebase first, then perform the refactor end-to-end.

Do not guess at project structure. Use the actual existing files and architecture. Remove the old transcription implementation cleanly, then integrate the new OpenAI transcription implementation with the settings specified above while keeping the UI unchanged.


