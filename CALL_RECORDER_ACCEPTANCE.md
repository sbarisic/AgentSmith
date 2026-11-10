# Call Recorder v1 — implementation verification

Date: 2026-10-29. This record distinguishes implementation checks from live-call acceptance.

## Completed checks

### 2026-11-09 recorder agent settings and orange interface

- Added independent recorder agent preferences, account/model discovery, browser sign-in using the existing protected vault, and explicit summary restart with completed-summary provenance retained.
- The focused recorder/authentication/summary suite passed **23 tests**, including successful synthetic sign-in with both active-account policies, explicit account selection, cancellation, offline catalogs, preference reload, and failed replacement preservation. OAuth exchange and catalogs use test handlers; no real account sign-in or inference was performed.
- The full unit run reported **650 passed, 2 failed, 3 skipped**. Both failures were existing verification fixtures selecting the machine's default .NET 11 preview SDK. Rerunning the verification and recorder-agent fixtures with a temporary .NET 10 SDK scope passed **18 tests**. The later focused run includes three additional account tests. This is not a claim that the entire suite was rerun in the corrected environment.
- The self-contained package passed desktop/service/export smoke checks with disposable synthetic recordings: navigation, saved versus draft agent settings, stale catalog cancellation, offline saving, model ID selection, playback replay, device handling, and owned-service shutdown. Evidence: `artifacts/recorder-design-smoke/package-smoke.json`.
- Headless screenshots were inspected for the compact orange light/dark design, configuration, transcript rows, and the minimum window size. The fixture renders 100%, 125%, 150%, and 200% DPI previews; this does not establish interactive behavior on every monitor. Source palettes and typography are centralized in `src/AgentSmith.Desktop/RecorderTheme.xaml`.
- Builds used installed .NET SDK **10.0.400** through a temporary build-directory pin. The repository's **10.0.401** pin was left unchanged.
- Live browser sign-in, real-provider summary quality, physical keyboard navigation, and hardware/display combinations remain separate live acceptance checks. No existing recording was retranscribed or sent to a provider during this work.

### 2026-11-09 tab and playback follow-up

- Top navigation reserves focus-border space, and workspace tabs use a plain horizontal header panel with rounded borders. Headless layout checks confirm the content's vertical position stays fixed across Home, Recordings, and Configuration.
- The summary model and settings button share the card's top row. The fixture checks right alignment and removal of the empty action row for completed summaries.
- Playback controls now use an orange slider with elapsed/total time and ten-second skip buttons. Silent-output checks cover drag preview, cancelled drag restoration, seek completion while playing, and skip clamping at both ends. Published package smoke checks, including existing replay and export checks, passed.
- Compact light and dark previews were inspected. These checks use synthetic audio and do not claim a new physical-device listening test.

### Original recorder verification

- Release unit suite: **592 passed, 3 skipped**. Results: `tests/AgentSmith.Tests/TestResults/recorder-unit-final.trx`.
- Release integration suite: **351 passed**. Results: `tests/AgentSmith.IntegrationTests/TestResults/recorder-integration-final.trx`.
- After the final capture/normalization adjustments, affected call, transport, and recording-analysis tests passed again: **39 passed**. An earlier focused capture regression run also passed all 26 tests.
- New fixtures cover independent native formats, a common capture clock, microphone mute before disk storage, paired startup rollback, missing endpoints, device loss, aggregate retention limits, recovery from per-track checkpoints, and exclusion of a second capture.
- Synthetic two-hour native chunks with delayed starts and positive/negative clock drift keep known audio markers within 100 ms at the beginning, middle, and end. These checks sample retained synthetic audio through the timeline reader; they are not a two-hour real call or a full two-hour transcription benchmark.
- Direct native-track preparation verifies separate normalized source offsets and subsequent inference. A 12 kHz test component is rejected when downsampling 48 kHz microphone audio to 16 kHz. Missing microphone frames retain the surviving call track and explicit missing coverage.
- Transcript fixtures preserve simultaneous speech, separate source labels, reuse completed inference, retain another track after uncertain requests, and require explicit summary generation. Speaker aliases/language change summary cache identity.
- Named-pipe tests exercise dual-device discovery, legacy device enumeration, start, mute, compact status, stop, raw export with separate track playlists, and deletion using synthetic sources.
- Self-contained Windows x64 publish completed. The publish script verifies identical `AgentSmith.Tools.dll` hashes for GUI and CLI/service.
- Published-package smoke passed under a **non-elevated account** with **no window shown**: view construction/layout, synthetic transcript loading/search, speaker rename persistence, evidence export, native audio reads, matching service startup (protocol 4), retained paired-recording listing/export, and service shutdown. Evidence: `artifacts/recorder-headless-check/package-smoke.json`.
- `git diff --check` passed.

One initial concurrent whole-solution run failed the existing `ExplicitMtpAndPlanWorkflowRejectBuildExecution` test. That test passed in isolation, and the subsequent complete unit run passed. The audio downsampling fixture initially exposed insufficient high-frequency attenuation; sinc resampling fixed that failure before the passing targeted and final checks.

## Reproduction

```powershell
dotnet test tests/AgentSmith.Tests/AgentSmith.Tests.csproj -c Release
dotnet test tests/AgentSmith.IntegrationTests/AgentSmith.IntegrationTests.csproj -c Release
./publish-recorder.ps1
python tests/prepare_recorder_ui_fixture.py artifacts/recorder-headless-check
./tests/recorder_publish_smoke.ps1 -Package artifacts/recorder-win-x64 -Data artifacts/recorder-headless-check
```

The headless smoke mode requires an explicit disposable data directory with the fixture marker. It never activates a window, records a device, or requests model inference.

## Pending live acceptance

### 2026-11-09 capture timing regression check

- Affected audio/capture/analysis suite: **154 passed, 3 skipped**. Includes the observed 16 kHz device-position / 48 kHz sample mismatch, recorded QPC jitter, preservation of flagged packets, normal paired stop with warnings, and analysis retaining CompletedWithGaps.
- Published-package smoke passed under a non-elevated account, including one-request GUI close and shutdown of its owned service. Evidence: `artifacts/recorder-timing-fix-check/package-smoke.json`.
- With explicit user permission, recorded Galaxy Buds+ microphone and output for ten seconds in an isolated local data directory. Actual duration: **10.180432 seconds**. Capture completed normally. Microphone: **857 packets, 0 position regressions, 0 overlapping frames discarded, 39 Windows discontinuity flags (including the initial packet)**. The saved manifest retains 38 post-start glitch coverage intervals and missing-audio coverage, including device startup delay.
- The output delivered **zero packets** because no audio was playing; its inferred-silence coverage was retained. This check does **not** validate call-output sound, speech intelligibility, recognition, Teams/VM behavior, or glitch-free Bluetooth operation. No playback or inference was performed. The isolated diagnostic service shut down afterward; the existing shared service was untouched.
- Local diagnostic evidence: `artifacts/galaxy-buds-live-check/audio/recordings/f6c4bdfe66934dc2a9d754e692c2d2e4/manifest.json`. Recorded audio remains local and is excluded from version control.

### Remaining scenarios

- Actual headset calls in Teams and inside a VM routed through Windows host audio.
- A real two-hour call, listening checks at beginning/middle/end, device unplugging, and Teams mute versus independent recorder mute.
- Human-reviewed English, Croatian, and Slovenian speech recognition and speaker attribution. Automated language checks establish accepted language settings only.
- Live-provider summary quality and cancellation during a real model request.
- Interactive playback listening, close/pause behavior during real recording/processing, and keyboard use across target hardware/display setups.

These scenarios require explicit live testing; no live-call compatibility or recognition-accuracy claim is made by the automated results.

## Compact recorder and precise transcript playback

- The default window is 1060 x 740 logical pixels, with no outer Recording-tab scrollbar. Saved recordings offer New; its title and device overrides are a separate draft. Starting and transcribing no longer save unfinished Configuration edits.
- Headless desktop checks cover draft retention, device disconnect/reconnect, unchanged global device preferences, fractional seeks, 0.1-second keyboard increments, overlapping highlights, end boundaries, silent gaps and track filtering. Light/dark previews include the expanded transcript row and range markers.
- Translation fixtures verify selected-passage-only requests, summary language, no tools, cached success, language/model/source separation, cancellation and provider failure without modifying transcript evidence. Twelve focused recorder/translation tests passed.
- These checks use synthetic audio and a fixture provider. Live translation quality, hardware playback alignment and interactive device recording remain manual checks; millisecond display precision does not imply millisecond speech alignment accuracy.
