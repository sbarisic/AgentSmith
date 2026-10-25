# Explicit exit and Windows output recording acceptance

Verified locally on 15 October 2026. This release delivers explicit chat exit and capture/file management. Whole-recording transcription and analysis are not implemented.

## Delivered build and behavior

The self-contained Windows executable is published to `artifacts/publish/win-x64/smith.exe`, the existing local PATH target. Service protocol is 2; build compatibility uses the Tools assembly identity (`93ccce9264b74337b2c84a5293403fc8` for this delivery). Older services must be stopped with their matching executable before starting the new build. The default service was restarted and left available with no recording active. No unrelated chats were terminated, and no Git commit, push or startup registration was made.

Ctrl+C at an idle chat preserves the draft. During work it cancels the run without closing the chat. Question/review pickers ignore Ctrl+C; Esc enters command mode and empty Enter restores the same note/selection. `/exit` is explicit and holds queued work. A pending decision keeps the existing exit code 3; ordinary idle exit returns 0. Actual EOF remains an external interruption. The real-console test caught and fixed System.CommandLine's independent Ctrl+C termination handler and the composer's rejection of empty Enter in question command mode.

Recording uses NAudio.Wasapi 3.1.0 with a fixed playback endpoint. It needs a running service but no installed models. Commands cover devices, start, paginated list, status, stop, export and delete. A recording survives its chat/launcher; another client can stop it. The retained metadata reference is not an artifact grant. Export preserves ordered WAV chunks, hashes and the manifest in a new directory, plus a playlist.

## Automated and process checks

- Full Release run with real-model acceptance enabled: **555 unit/runtime tests + 324 integration tests = 879 passed**, no failures or skips. Reports: `tests/AgentSmith.Tests/TestResults/capture-final-release.trx` and `tests/AgentSmith.IntegrationTests/TestResults/capture-final-release.trx`.
- After the final export timeout adjustment, 34 capture/transport/shared-service tests passed. After the final picker empty-Enter and saved-plan continuation regressions were added and fixed, 50 composer/question/continuous-chat tests passed. These are scoped follow-up runs, not an additional full-suite count.
- Python helper regression: **8 passed** using the existing managed Python installation. No runtime/model downloads were needed.
- Fake sources verify serialized starts, persisted operation-ID reuse, repeated stop, rotation, leading/trailing silence, overflow, invalid timestamps, device failure/discontinuity, storage limits, recovery, active-recording restrictions and export/delete lease exclusion.
- Named-pipe checks verify capture without conversation databases or models, streamed export length/hash validation, invalid identifiers, no-overwrite export, and graceful/forced service shutdown. Existing shared-service tests continue to cover identity/version failures, malformed/bounded transport, queue limits and client isolation.
- Actual processes verify crash recovery, no capture restart on service restart, graceful Completed versus forced Interrupted outcomes, capture surviving chat exit, another client stopping it, and persisted conversation reference metadata.
- A hidden real Windows console verifies repeated idle Ctrl+C, a preserved draft, explicit exit, picker Ctrl+C, Esc command mode, rejection of ordinary text, and return to the same picker note. The published executable passed these checks.
- Real Whisper/YAMNet/speaker inference completed through the same service while recording was active. Status samples show both an active inference job and active capture; the recording finalized Completed. The scripted chat provider is loopback-only and makes no paid/cloud requests.

## Controlled audio and measurements

The selected endpoint was **Speakers (High Definition Audio Device)**, 48,000 Hz, two channels, 32-bit IEEE float. Playback used checked speech, a low-level 440 Hz tone and explicit silence. The source WAV was 140 seconds and 26,880,044 bytes. Capture was explicitly started and stopped; no private call or microphone was used.

Final service capture retained **140.3846 seconds, 53,908,000 bytes and four WAV chunks**, with Completed state and no device-gap error. Chunk frame offsets were contiguous, retained frames matched the manifest, every exported SHA-256 matched, and Windows successfully played a short PCM preview decoded from exported audio. Native capture duration includes acquisition/playback start/stop overhead. Initial/trailing missing callbacks are labeled inferred silence. This verifies capture and file playback, not transcription accuracy or bit-perfect operation on every device.

Across 26 status/memory samples during this capture:

| Measurement | Result |
| --- | ---: |
| Private bytes near capture start | 11.76 MiB |
| Peak private bytes (OS high-water mark) | 26.37 MiB |
| Maximum sampled working set | 63.45 MiB |
| Private bytes near the end | 20.83 MiB |
| Inference helper | Unloaded throughout |

These figures cover the Smith service, not the playback driver, verification Python process or all system audio components. They are a short-run bounded-memory observation, not a multi-hour endurance claim. The configured audio buffer limit is 16 MiB; process memory includes the runtime, metadata and other allocations.

An earlier run reported a real device discontinuity at roughly 70 seconds. The implementation was adjusted to retain a coverage marker and continue recoverable capture, with an Interrupted final outcome if such a gap occurs. Fatal buffer, timestamp and storage failures still stop capture. Subsequent 140-second runs completed without reported discontinuities. No test result was relabeled to hide a gap.

## Reproduction and evidence

```powershell
$env:SMITH_AUDIO_ACCEPTANCE_FIXTURES = "$PWD\artifacts\audio-service-acceptance\fixtures"
$env:SMITH_AUDIO_ACCEPTANCE_DATA = "<existing managed Smith data directory>"
dotnet test AgentSmith.slnx -c Release -- xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1
& "<installation>\env\Scripts\python.exe" -I tests/local_audio_test.py
dotnet publish src/AgentSmith.Cli -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64

# These capture tests explicitly record the selected default output. The live test plays audio.
python tests/audio_capture_acceptance.py --smith "$PWD\artifacts\publish\win-x64\smith.exe" --data-dir "<dedicated test directory>" --output "<new evidence directory>"
python tests/audio_capture_process_acceptance.py --smith "$PWD\artifacts\publish\win-x64\smith.exe" --data-dir "<fresh dedicated test directory>" --output "<evidence directory>"
python tests/chat_exit_console_acceptance.py --smith "$PWD\artifacts\publish\win-x64\smith.exe" --output "<evidence directory>"
python tests/chat_exit_console_acceptance.py --smith "$PWD\artifacts\publish\win-x64\smith.exe" --output "<evidence directory>" --question
```

The process driver deliberately kills only the service it started in its dedicated data directory. Do not point it at a service with unrelated work. The live driver requires NumPy and the existing checked `artifacts/audio-service-acceptance/fixtures/speech.wav` fixture. Export directories must be new.

Ignored local evidence is under `artifacts/capture-acceptance`: `process-capture.json`, intermediate recordings and exports, and `final/live-capture.json`, `final/console-exit.json`, `final/console-question-exit.json`, `final/inference-during-capture.json`. Final capture exports and a playable preview are beside the final JSON. The shared-audio smoke also refreshed `artifacts/audio-service-acceptance/published-smoke.ndjson` and its evidence JSON. Test recordings are retained for inspection; none remains active.

## Remaining limits

- Physical endpoint unplug/replug, real suspend/resume and actual disk exhaustion were not induced on this machine. Their failure paths have deterministic coverage; disruptive hardware acceptance remains open.
- Only this speaker endpoint's native 48 kHz stereo-float format received live acceptance. Other formats/devices and multi-hour endurance remain unverified.
- A stalled multi-gigabyte export was not exercised. Control admission extends the five-second idle timeout for export; transport remains frame-bounded and export holds a lease against deletion.
- Capture stays local and endpoint-wide. It does not include microphone mixing, per-application isolation, automatic recording, automatic deletion, live transcription or complete long-recording analysis.
- Missing callbacks can indicate silence; inferred silence is labeled, and device discontinuity flags are retained. The service cannot prove that an audio driver never loses unreported samples.
- Interrupted exports can leave an explicitly named staging directory. Native WAV chunks are exported without resampling, and partial coverage remains in the manifest.
