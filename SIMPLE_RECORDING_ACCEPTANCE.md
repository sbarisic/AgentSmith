# Simple recording conversations and draft clearing

Verified on 2026-10-15. No new live audio recording was made. Existing uncommitted work was preserved; no commit or push was made.

## Delivered behavior

In chat, `/audio capture start`, `/audio capture stop`, then “Summarize that recording” selects the capture, runs or reuses analysis, attaches immutable evidence and answers in the same turn. Capture never starts analysis by itself. `/audio recordings` selects recordings made elsewhere; `/audio summarize [capture-id]` displays the report directly, and `/audio transcript [cursor]` / `/audio coverage [cursor]` read retained evidence.

Only the parent Work chat has the new recording metadata and analysis tools. Unselected sources require a selection question, interrupted sources require consent, and workers still require explicit artifact grants. Saved reports bypass service/model readiness checks. Source-level leases serialize concurrent requests; call-to-job mappings and per-revision attachments prevent duplicate inference and attachment. Busy capture selection is persisted separately from the running session and applied after the turn or on reload.

Idle Ctrl+C clears text, pasted blocks and draft attachment references without deleting stored files. It also exits unsent paste mode. Active cancellation retains the follow-up draft and holds queued work. Picker Ctrl+C preserves the answer; command-mode clearing affects only its temporary draft. `/exit`, EOF and standalone command handling retain their separate behavior.

## Automated checks

- Release build: zero warnings and errors.
- Full serial unit/runtime/real-model suite: **572 passed, zero failed or skipped** (`simple-recording-verified-unit.trx`). This includes real installed inference through the service and parent/worker regression checks.
- Full serial integration suite after the busy-selection fix: **341 passed, zero failed or skipped** (`simple-recording-delivery-integration.trx`).
- Final focused integration run: **15 passed** (`simple-recording-final-focused.trx`), including an additional synthetic-device named-pipe test of capture start → stop → natural-language summary. This test does not open a real playback endpoint.
- Python: **5 decoder tests and 8 helper tests passed** using the managed runtime.
- Actual Windows console, including the published executable: repeated idle Ctrl+C, complete draft clearing, preserved question notes, Esc command mode/return and explicit exit passed. Evidence is under `artifacts/simple-recording-acceptance/published-console`.
- New tests cover selection and reload, immutable/idempotent attachments, concurrent summaries, service-free reuse, summary-only processing, deleted or changed sources, interrupted consent, explicit resume, recovery without replay, metadata-only listing, worker rejection, compact rendering, EOF and saved attachment reads after analysis deletion.
- `git diff --check` passed; existing line-ending notices remain.

An initial concurrent suite run exposed a timing assumption in the existing storage-budget capture test: stop can return `Finalizing` after its five-second control-response budget. The test now waits for a terminal state before checking the unchanged interruption and retained-audio assertions. A later real-model run used stale test output assemblies and correctly failed exact-build handshakes. Rebuilding the complete solution and running serially produced the results above.

## Live workflow and measurements

The controlled 140.3845625-second, four-WAV fixture from the prior recording acceptance was used. A copy with capture ID `b6e3e69f54b545fb84149fc6b7b5701e` allowed fresh processing without changing the original. GPT-6-Astra supplied both the hierarchical written summary and the parent chat response.

| Check | Wall time | Parent model requests | Outcome |
| --- | ---: | ---: | --- |
| Existing transcription, missing summary | 24.45 s | 2 | One `audio_analyze`, automatic attachment and one answer; one additional summary request, no repeated inference |
| Published client, explicit resume after interrupted submission | 114.60 s | 3 | `audio_analyze` plus `audio_read`; full 140.3845625 s processed, one summary request and one final answer |
| Published `/audio summarize`, service stopped | 4.33 s | 0 | Saved report displayed and attached; service remained stopped |

The first fresh published run started the service automatically and reached inference. The acceptance harness then terminated its client after an overly short 60-second silence timeout. The job retained `Submitted` components and the service unloaded its helper. The harness was corrected to tolerate quiet inference within its overall deadline. A new explicit user prompt authorized resume and failed/uncertain retries; it completed job `3ada9a7a027149c1af8f58452383c44b`. No uncertain request was silently replayed.

That job recorded 3.98 seconds of preparation, 0.064 seconds of assembly and 7.55 seconds of summary work. These are observations on this machine, not latency guarantees. The first attempt's uncommitted inference time is not included in committed per-stage timings. Parent request counts exclude the separately recorded summary request. Raw evidence, requests, responses and measurements are retained under `artifacts/simple-recording-acceptance`.

Reproduction examples:

```powershell
dotnet build AgentSmith.slnx -c Release
$env:SMITH_AUDIO_ACCEPTANCE_DATA = $data
$env:SMITH_AUDIO_ACCEPTANCE_FIXTURES = "$PWD/artifacts/audio-service-acceptance/fixtures"
dotnet test tests/AgentSmith.Tests -c Release --settings artifacts/analysis-serial.runsettings
dotnet test tests/AgentSmith.IntegrationTests -c Release --settings artifacts/analysis-serial.runsettings
& $python -I tests/recording_decode_test.py
& $python -I tests/local_audio_test.py
python tests/simple_recording_acceptance.py --smith $smith --data-dir $data --capture $capture --output $newOutput
python tests/chat_exit_console_acceptance.py --smith $smith --output $newConsoleOutput --question
```

The test service must match the rebuilt Tools assembly. `$python` is the installed generation's `env/Scripts/python.exe`. Set `$data` to the canonical data directory and use a new output directory. The live workflow driver accepts `--direct` for the zero-parent-request saved-summary path and `--prompt` for an explicit resume/retry test.

## Local delivery and limits

The self-contained build is published at `artifacts/publish/win-x64/smith.exe`, the existing PATH location. Published `smith.dll` SHA-256 is `48E5C3E7B73CC7E374B1263B43D4D4B0A3EEA9DBC8FE78F86564DECFDEF1ECEA`. Tools build identity is `f022bb423977457b87a8be499c40432f`; service protocol remains 3.

Both same-user service directories were checked idle before compatibility restarts: the ordinary `C:\Users\cartm\AppData\Local\AgentSmith` directory and Codex's package-local directory. The ordinary service was restarted through the desktop shell to retain its non-virtualized directory identity. Both were verified compatible and available with no capture active. No unrelated chat was terminated and no startup registration was added.

Existing speech-recognition, anonymous-speaker and citation limitations still apply. This delivery does not add broad linguistic accuracy evaluation or a new live endpoint-capture endurance test. Selection authorization is enforced by the client; whether a natural-language request should invoke analysis is also described in model instructions. Model request counts can vary with follow-up evidence reads. Natural-language analysis remains parent-only in Work; saved evidence keeps existing worker grants. Low-level legacy analysis commands retain their explicit service-start workflow. Long recordings retain existing budgets, explicit retries and storage limits.

Live transcription, microphone mixing, per-application capture and cross-recording voice identification remain outside this delivery.
