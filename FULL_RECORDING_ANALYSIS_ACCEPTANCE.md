# Full-recording analysis acceptance

Date: 15 October 2026. Local Windows x64 delivery; no commit, push, automatic recording or startup registration.

## Delivered behavior

User commands connect retained captures and explicit WAV/MP3 files to client-owned preparation, chunked shared-service inference, anonymous speaker reconciliation, retained evidence and configured-provider summaries. The initiating client owns execution. Completed components survive cancellation and crashes; uncertain submissions require explicit retry. Readers, exports and previously attached conversation artifacts remain available without inference.

Service protocol is 3, with exact build matching. Helper protocol remains 2 with the additive `recordingAnalysis: 1` capability. Setup installed generation `4f8e7656d92c4136825399423e0b15c8`, fingerprint `FA90DEFDDF9CEA13FAA979C8DB70E23B59823E2A19C4EE367DE5176673525701`. Jobs pin this generation and a private decoder copy/hash. The user selected GPT-6-Astra and explicitly authorized saving it as the default model; actual summaries used the signed-in ChatGPT account.

## Automated verification

The Release build has zero warnings and errors. The full Release suites passed **572 unit/runtime/real-model tests and 327 integration tests**, with **zero failures or skips**. After final capture-format and Unicode-page hardening, 37 focused service/analysis checks passed, followed by all 16 recording-analysis tests with the completed/interrupted manifest assertions. Five decoder and eight Python helper regressions passed. TRX records are `analysis-release-delivery.trx` in each test project's `TestResults`, plus `analysis-final-format.trx` and `analysis-delivery-final-targeted.trx`.

- Recording-analysis unit tests cover complete two-hour chunk plans, unchanged-source receipts, partial-component retry, uncertain dispatch, exclusive leases, storage reservations/recovery, cancellation, overlap repetition, speaker ambiguity, missing citations, hierarchical summary request-budget resume, Unicode paging, coverage unions, export, deletion, attachment and explicit worker grants.
- The command integration test exercises offline status/read/attachment/deletion without provider initialization or a service. Short-audio regression coverage verifies embeddings cannot enter conversation artifacts.
- `tests/recording_decode_test.py`: **5 passed**, covering a 241-second WAV larger than 25 MiB, variable-bitrate MP3 packet-size verification and skip-sample duration, capture gaps, budget failure without promoting partial output, malformed capture length and disguised playlists.
- `tests/local_audio_test.py`: **8 passed**, including existing model-helper validation and diarization regressions.
- An early suite run overlapped heavy setup/inference and had six unit and one integration failure involving timeouts/SQLite contention. Targeted reruns passed (17 unit and 53 integration checks). Final suites run serially with explicit real-model fixtures; the initial failures are not hidden by the later results.
- Serial full runs also exposed two pre-existing concurrency races. A worker test compared mutable delivery status instead of stable message identity; its assertion now permits legitimate delivery progress while checking one durable message. Temporary-audio disposal could race a cleanup owner after releasing its lease; disposal now tolerates that owner's file lock, leaving cleanup to finish. The existing stress test now exercises 300 concurrent creations instead of 30. These findings were fixed before publication.

Reproduction commands (PowerShell; set `$data` to the canonical Smith data directory and `$python` to that generation's `env/Scripts/python.exe`):

```powershell
smith audio setup --json
smith model gpt-6-astra --provider ChatGpt --json
dotnet build AgentSmith.slnx -c Release
$env:SMITH_AUDIO_ACCEPTANCE_DATA = $data
$env:SMITH_AUDIO_ACCEPTANCE_FIXTURES = "$PWD/artifacts/audio-service-acceptance/fixtures"
dotnet test tests/AgentSmith.Tests -c Release --settings artifacts/analysis-serial.runsettings
dotnet test tests/AgentSmith.IntegrationTests -c Release --settings artifacts/analysis-serial.runsettings
& $python -I tests/recording_decode_test.py
& $python -I tests/local_audio_test.py
& $python -I tests/recording_analysis_acceptance.py --smith artifacts/analysis-validation/smith.exe --data-dir $data --output artifacts/recording-analysis-acceptance/new-run --multi-hour
& $python -I tests/recording_analysis_acceptance.py --smith artifacts/analysis-validation/smith.exe --data-dir $data --output artifacts/recording-analysis-acceptance/new-heldout --heldout
```

The serial runsettings disable xUnit collection parallelism and set one test CPU. Use a new acceptance output directory because export never overwrites. The running service must match the test assembly build; the final suite starts the service from `src/AgentSmith.Cli/bin/Release/net10.0/smith.dll` before testing. Public fixtures are prepared by the existing audio acceptance scripts. No private call or new output recording was used.

## Real media and measurements

| Source | Unique processed duration | Observed wall time | Peak client private memory | Result |
| --- | ---: | ---: | ---: | --- |
| Stereo 48 kHz WAV, 72.96 MB | 380 s, four chunks | 85.30 s | 29.22 MiB | Completed, final speech retained |
| MP3 of the same timeline | 380 s, four chunks | 79.95 s | 29.03 MiB | Completed, final speech retained |
| Published-build variable-bitrate MP3, with summary | 380 s, four chunks | 106.31 s | 33.59 MiB | Completed, cited GPT-6-Astra report |
| Sparse two-hour WAV | 7,203 s, 66 chunks | 91.25 s in initial isolated run | 36.30 MiB | Completed; reference speech distributed through the file |
| Original controlled output capture, four native float WAVs | 140.3845625 s | 123.54 s, including contention and summary | Not separately sampled | Completed with cited GPT-6-Astra report |
| Three-voice fixture | 300 s | 87.25 s | 29.32 MiB | Completed; speaker metrics below |

Memory is sampled every 250 ms through Windows process counters; peaks between samples may be missed. Client memory does not include its decoder or the shared helper. The first two-hour run's helper peak committed memory was about 1.18 GiB; later mixed workloads including YAMNet reached about 1.58 GiB. These are workload observations, not a fixed memory ceiling. This is a sparse fixture with four reference speech placements, not two hours of continuous meeting speech.

Cached completed WAV/MP3 resumes took about **0.50 seconds** each without inference. The 380-second WAV job retained **97,385,546 bytes**, with **80.13 seconds** of service inference, **0.63 seconds** upload and negligible uncontended queue time. The capture job retained **62,942,992 bytes**, with **32.95 seconds** queue delay and **78.64 seconds** inference. A final two-hour job retained **691,638,971 bytes**. Jobs now retain separate preparation, assembly and summary timings; service receipts retain connection/upload/queue/inference timings.

The original capture was copied from the earlier acceptance data directory into the default recording store only when its ID was absent, then imported through authenticated service export. Original files were preserved. Capture ID: `7e6754abba7e41ad94bbee094aed7862`; analysis: `b937ab6644ac4687a02852473e20ca53`. Rounding from 48 kHz to 16 kHz changes the endpoint by less than one normalized sample. Its inferred leading/trailing silence remains labeled.

Recovered interrupted capture `4f2cf13046da4eaf8c6df3965f5c3df4` was also copied from prior controlled process-test material. Analysis `53b5d9fa9074410cbfcdaddbfb361754` used `--allow-interrupted --sounds --no-summary`: all three components succeeded, but the outcome correctly remained **CompletedWithGaps**, with an unknown interrupted tail. Processing success did not erase source uncertainty.

A later two-hour run shared the environment with another preparation and paused after four completed chunks because preparation reserved the available storage capacity. Explicit resume completed all **7,203 seconds** with **107.71 seconds** cumulative execution. This exposes a conservative concurrency limitation: temporary reservations can pause another analysis even when eventual disk usage would fit. No completed receipts were discarded or automatically replayed.

Evidence: `artifacts/recording-analysis-acceptance/acceptance.json`, `release/acceptance.json`, `job-metrics.json`, `artifacts/analysis-full-capture.ndjson`, `analysis-interrupted-capture.ndjson` and `analysis-two-hours-resume.ndjson`.

## Speaker calibration and acceptance

Development material used the checked LibriSpeech voices `5703-47212-0000` and `3436-172162-0000`. Observed different-voice cosine similarities were about 0.039–0.111; short/long spans of the same voice were about 0.772–0.780, with repeated spans about 0.991–0.999. The frozen reconciliation fingerprint is `titanet-cosine-v1;threshold=.75;margin=.10`.

Acceptance added `198-209-0000` after freezing thresholds. Its SHA-256 is `0473ba9613befa1026db5e254546f105aeffba8366b5468fc5e8d474d844ca1d`, checked against the [official librosa registry](https://raw.githubusercontent.com/librosa/librosa/main/librosa/util/example_data/registry.txt). Attribution/license text is retained beside the downloaded fixture. Six placements included returning speakers across chunk boundaries and distant intervals.

The energy-based reference contained 6,749 ten-millisecond active frames. **49 frames (0.73%) were unknown**, with **99.27% attribution accuracy**, **zero measured false-merge frames** and **zero false-split labels**. This is approximate energy alignment, not manually annotated diarization. One voice was held out; two were development voices. It does not establish general accuracy for similar voices, short utterances, accents, multilingual recordings or simultaneous speech. Embeddings remain private to each analysis. Ambiguous transcript segments retain an explicit ambiguous label.

Evidence: `artifacts/recording-analysis-acceptance/final/heldout-acceptance.json`. Implementation uses the existing [pinned sherpa-onnx extractor API](https://raw.githubusercontent.com/k2-fsa/sherpa-onnx/v1.13.8/python-api-examples/speaker-identification-with-vad.py), not a new identification model. Streaming preparation uses [PyAV audio frames and resampling](https://pyav.basswood.io/docs/stable/api/audio.html).

## Recovery, summaries and permissions

`tests/recording_analysis_process_acceptance.py` killed only its own client after chunk one committed and chunk two was submitted. Status reported Interrupted. Ordinary resume processed later work but left the uncertain request, reporting CompletedWithGaps. Explicit `--retry-uncertain` completed all 380 seconds; the first evidence file's SHA-256 was unchanged. Analysis `623ef22b18aa488fa10e05803575d265`; evidence `artifacts/recording-analysis-acceptance/process/acceptance.json`.

`tests/recording_analysis_console_acceptance.py` created a hidden real Windows console, sent Ctrl+C to its owned process and observed exit code 2 with Paused and "Cancelled; completed evidence retained." Analysis `a8a86396dfbf4e0d82f2a42459a51c49`; evidence `artifacts/recording-analysis-acceptance/console/acceptance.json`.

GPT-6-Astra produced real cited reports for an 11-second checked speech fixture and the full controlled capture. Every returned reference resolved to retained source segments. Synthetic long evidence exercised multiple summary batches, hierarchical reduction, request-budget pause and cache-preserving resume. A provider failure left evidence readable and required explicit retry of the uncertain summary stage. Citation validation establishes provenance; it cannot prove that a claim is supported semantically.

Attachment tests cover a stranger, a child without a grant, an explicitly granted child, deletion of the original job and tampered bundle membership. Saved attachment evidence remains independent of the source job. New analysis embeddings are stripped from short-audio conversation artifacts and are not copied into recording-analysis bundles.

## Remaining acceptance limits

- Broad manually annotated speaker/recognition acceptance, similar voices, real overlap and languages without whitespace boundaries remain unverified. Current overlap/repetition and unknown-speaker behavior have deterministic coverage; real accuracy measurements are narrow.
- Multi-hour continuous speech, multi-hour live-provider summary quality and semantic claim entailment have not been measured. The long-summary recovery test uses a deterministic provider.
- Crash/cancellation at every individual filesystem instruction, decoder crash at every sample boundary, real disk exhaustion and power-loss durability are not exhaustively tested. Atomic writes, leases, explicit uncertainty and bounded preparation have targeted coverage.
- No new OS firewall isolation claim is made. Local helpers block network APIs and force local demuxing; they are not an OS sandbox.
- JSONL views use bounded streaming scans; late-page access can scan earlier evidence. Attachments recheck bundle hashes on access. Very large bundles can therefore add read latency.
- Live transcription, microphone mixing, per-application capture and cross-recording voice identification remain outside this delivery.

## Local publication and final state

Published with `dotnet publish src/AgentSmith.Cli -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64`. `Get-Command smith` resolves to this folder. The final service reports protocol **3**, build **5ae4de4971d44f75a63874a8e3d06772**. Apphost SHA-256: `885FE4324D61E03510E41FA5A542C821D7D816A56316827E829963A9357D58C4`. Only the owned idle service was stopped/restarted; unrelated chats were not terminated.

`tests/recording_analysis_publish_smoke.ps1` verified service-free transcript reads and export, explicitly started the published service, analyzed a true VBR MP3, resumed the completed result and attached it in a new GPT-6-Astra chat. VBR frame sizes ranged from 96 to 192 bytes; the earlier 380-second MP3 measurements were constant bitrate and are labeled separately. Published analysis `f0b6eec593a64fbe956f60dbba4513ef` covered all 380 seconds. Preparation took **5.03 s**, inference **83.60 s**, queueing **0.0025 s**, assembly **0.085 s**, and summary **15.46 s**. Retained analysis storage was **26,019,891 bytes**.

Sampled published-client private memory peaked at **33.59 MiB** and service private memory at **29.46 MiB**. The helper process tree's Job Object peak committed memory reached **1.74 GiB** across VBR and the subsequent native-capture check. The reported helper PID is a small venv launcher; sampling that PID alone undercounts its child. The corrected `published/metrics.json` distinguishes the launcher from the Job Object counter. These peaks were not simultaneous and must not be added into an aggregate private-memory measurement. Decoder memory was not separately sampled.

The final native-format check used the published executable and the original four-WAV capture. Analysis `fa1ca4f184c94d91a7765fe6a7375c13` completed all **140.3845625 seconds**, including the stricter bit-depth/float-format validation. Preparation took 2.64 s; assembly took 0.061 s. Evidence is `artifacts/analysis-published-capture.ndjson`.

The fresh chat attachment is recorded in `artifacts/recording-analysis-acceptance/published/chat-attach.ndjson`; its conversation is `c351bbb149a442948c446f8ef183e220`. Final service status is retained in `published/service-final.json`: available, **zero active jobs, zero waiting jobs, no active recording**. Models unload normally after five idle minutes. No Git commit or push was made. README, help, architecture and `plan.md` describe the delivered workflow and leave unverified criteria incomplete.
