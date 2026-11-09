# Call Recorder v1 — implementation verification

Date: 2026-10-29. This record distinguishes implementation checks from live-call acceptance.

## Completed checks

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
