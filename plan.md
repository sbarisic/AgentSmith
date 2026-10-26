# AgentSmith implementation status and future work

## AgentSmithLite: separate legacy build (26 October 2026)

- [x] Independent .NET 6 / win-x86 console project, ChatGPT sign-in/chat, bounded file tools, owned command execution and independent app launch.
- [x] Plain console, isolated DPAPI credentials and no full-app project dependencies. Modern-host build script creates a self-contained portable folder and ZIP.
- [x] 29 acceptance checks passed under .NET 6.0.36 in a real x86 process on Windows 11, including scripted HTTP chat/tool execution, denial, cancellation, child-process cleanup, DPAPI, signed identity-token validation, manual sign-in and streamed-output regressions. The full application's 45 relevant authentication/process regression tests also passed before the manual sign-in addition; 24 authentication regressions passed afterward.
- [x] User-reported Windows 8.1 acceptance: manual sign-in worked, and on 26 October 2026 the user confirmed the latest build works on the Windows 8.1 machine. Automated verification remains on Windows 11; this is not a comprehensive independently observed Windows 8.1 compatibility test.
- [x] `auth login offline` prints a sign-in link without launching a browser and accepts a pasted callback from another machine, retaining PKCE/state/identity validation and cancellation. Covered by local protocol fixtures; the user subsequently reported that it works on the other machine.
- [x] Version 1.0.1 retains completed streamed output items when the final response omits them. The original `Invalid response completion` error was reproduced with fixtures and fixed; no tools execute before successful response completion. The user subsequently confirmed the latest build works on the Windows 8.1 machine.

See [Lite build and usage](AgentSmithLite/README.md). This is a separate minimal application; the full application's target remains .NET 10.

Updated 15 October 2026. The shared local audio service, explicit chat exit, Windows output recording and whole-recording analysis are implemented. Verified behavior and remaining acceptance limits are recorded below.

## Shared local audio service: delivered

One explicitly managed background Smith process serves each Windows user and canonical audio storage directory. Chats and workers share one queue and one loaded helper. Conversation storage, permissions, artifact reuse and receipts remain client-owned. The service never opens conversation databases or creates a global evidence cache.

Use `smith audio setup`, then `smith service start`. `smith service run` supports foreground diagnostics; `service status`, `audio status` and `/audio status` inspect readiness without loading models. `service stop` drains accepted work; `service stop --force` interrupts it. Commands support the existing data-directory and JSON options. No elevation or automatic startup registration is added.

Current limits are one inference slot, 32 inference clients, 32 waiting/upload reservations, four waiting/upload reservations per client, 64 KiB protocol frames, 25 MiB sources and 120-second clips. The five-minute request deadline includes preparation, transport and queue time. Incomplete uploads expire after at most 30 seconds. Models unload after five idle minutes while the service stays available.

Installed audio tools remain available when the service is stopped. Matching durable evidence and receipts need no connection. Short-audio requests requiring new inference report startup guidance. Requested whole-recording analysis starts the compatible service when needed. Neither path starts private helpers or automatically replays uncertain work. Source bytes are validated and streamed into leased temporary service directories; the service reconstructs its own paths. Immutable generation IDs/fingerprints remain pinned to each request.

### Completed implementation and verification

- [x] Lifecycle commands, detached process ownership, canonical identity including Windows package redirection, singleton lease, restricted local named pipe and matching identity/build/protocol handshake.
- [x] Bounded admission, FIFO dispatch, progress, complete request deadlines, cancellation/disconnect handling and graceful/forced shutdown.
- [x] Service-owned helper, lazy loading, real five-minute idle release and helper-tree cleanup after service failure.
- [x] Client integration, shared parent/worker connection, pre-service component reuse, preserved artifacts/permissions/receipts and historical helper/artifact compatibility.
- [x] Installation readiness, immutable generation validation, fingerprint mismatch rejection and real helper switching between pinned generations of the same engines.
- [x] Protocol, queue, cancellation, upload, cleanup and recovery tests; independent-process start/exit/isolation and 32-client/32-reservation limit checks.
- [x] Real inference with three independent clients, a spawned worker and parent, cold/warm/queue/memory measurements and offline cache reuse after reload with the service stopped.
- [x] Full Release verification: 542 unit/runtime/real-model tests and 324 integration tests passed without failures or skips; eight Python helper tests passed.
- [x] Self-contained Windows publication to the existing PATH folder, fresh published-service/CLI smoke, updated help/README/design and service left available.

Measurements, commands and evidence are in [LOCAL_AUDIO_SERVICE_ACCEPTANCE.md](LOCAL_AUDIO_SERVICE_ACCEPTANCE.md). Sampled peak private memory for three clients decreased from 3.465 GiB with independent helpers to 1.512 GiB with the shared service; throughput and latency remain workload-dependent.

### Additional acceptance not yet performed

- [ ] Interactive access attempts from a separate Windows account. The current-user-only kernel DACL was inspected, and remote UNC pipe access was rejected with access denied.
- [ ] OS-enforced firewall isolation of audio inference. Existing offline settings, helper socket blocking and file-only decoding remain verified.
- [ ] Accuracy/compatibility acceptance for a future upgrade to different model releases. Current tests cover pinned generations of the same engines, legacy helper protocol handling and incompatible fingerprints.

These additional checks do not add recording, microphone capture, remote service access, GPUs or automatic startup to this release.

## Explicit chat exit: delivered

- [x] Idle Ctrl+C clears the entire unsent draft (text, pasted blocks and attachment references); repeated presses keep the chat open; active cancellation returns to the same chat and holds queued work.
- [x] Interactive chat/resume own Ctrl+C instead of System.CommandLine's invocation termination handler. Standalone command cancellation remains separate.
- [x] Ctrl+C stays in question/review pickers. Esc enters command mode, ordinary text is not submitted, and empty Enter restores the same selection/note and prior composer draft.
- [x] `/exit` uses common cancellation/queue hold and reports ongoing recording. Actual redirected EOF retains cleanup/recovery behavior.
- [x] Keyboard/integration regressions and real Windows-console checks cover repeated Ctrl+C, idle draft clearing, preserved active/picker drafts, question command-mode return and explicit exit. Help and completion guidance updated.

## Windows output recording: delivered (capture first)

Recording and file management use NAudio.Wasapi 3.1.0 in the shared service, independent of the inference queue and installed models. Whole-recording analysis is a separate, explicitly started client workflow described below.

- [x] Matching `/audio` and `smith audio` commands: devices, capture start, list, status, stop, export and delete. Terminal controls bypass conversations/accounts. Chat records a metadata reference without granting model access.
- [x] One explicit capture per service, fixed output endpoint, retained start-operation identity, no automatic replay or restart, and no model-facing capture controls.
- [x] Current-user recording directory, exclusive leases, 16 MiB bounded audio buffer, numbered native WAVs, durable manifest and recovery of complete retained frames.
- [x] Rotation at 60 seconds or 16 MiB, timestamp/sample-position accounting, inferred-silence labels and explicit discontinuity/interruption outcomes.
- [x] Configurable 10 GiB aggregate retention budget, 80% warning, 1 GiB disk reserve, no automatic deletion, streamed export to a new directory and exclusion between export/delete.
- [x] Chat exit leaves capture active; another client can stop it. Graceful shutdown finalizes; forced shutdown and service crash retain Interrupted audio. Service/audio status and composer show capture state.
- [x] Fake-backend and transport checks, actual process shutdown/crash recovery, real console behavior and real inference while capture is active.
- [x] Controlled output capture exceeded 120 seconds and 25 MiB: 140.38 seconds, 53,908,000 bytes, four WAVs, no reported gaps. Hashes/frame offsets and exported playback verified. Sampled service private-memory peak: 26.4 MiB without models.
- [x] Full Release suite with real models: 555 unit/runtime and 324 integration tests, zero failures/skips; eight Python helper tests. Final targeted regressions and published-console checks also passed.
- [x] Self-contained local Windows publication, updated help/README/design and service available with no recording active. No Git commit, push or automatic startup registration.

Commands, evidence, final regression counts and measurement limits: [WINDOWS_RECORDING_ACCEPTANCE.md](WINDOWS_RECORDING_ACCEPTANCE.md).

### Additional recording acceptance not yet performed

- [ ] Physical endpoint unplug/replug, a real system suspend/resume and actual disk exhaustion. Failure paths have deterministic coverage; these disruptive machine tests remain unverified.
- [ ] Live capture on endpoints other than this machine's 48 kHz stereo float speaker output, including unusual multichannel formats.
- [ ] Multi-hour capture endurance and a deliberately stalled multi-gigabyte export. Bounded buffering, rotation, export hashes and lease exclusion are tested at smaller scales.

Microphone mixing and per-application isolation remain separate future extensions. Output capture alone does not inherently include the user's microphone.

## Whole-recording transcription and analysis: delivered with acceptance limits

`smith audio analyze capture <id>` and `smith audio analyze file <path>` start a client-owned job. Replace `smith audio` with `/audio` in chat. Analysis starts only on user request. In chat, “summarize that recording” uses the parent-only analysis tool, automatically attaches evidence and reuses existing results. Capture start and requested analysis may start the service; status and cached reads do not. The client snapshots and decodes the complete source, then submits 110-second cores with up to five seconds of context on each side. The shared service retains its one-slot queue, 120-second/25 MiB request limits and five-minute request deadline.

Job metadata, source fingerprints, pinned decoder/model generations, partitioned evidence and summary checkpoints live separately under `audio/analyses`. Each attempt defaults to 30 minutes, 100 summary requests and a 10 GiB aggregate storage budget, preserving a 1 GiB disk reserve. Cancellation or client exit pauses/interruption-retains work. Resume is explicit; failed and uncertain requests require separate retry flags. A temporary preparation reservation can pause another analysis; completed evidence is retained.

Speaker reconciliation uses local TitaNet embeddings, chronological matching and a versioned threshold/margin. Labels are anonymous and recording-scoped. Summaries use the configured provider with tools disabled, evidence batching, resumable hierarchical reduction and segment/timestamp citations. `--no-summary` requires no provider authentication. Reads and exports do not need the service or models. Explicit immutable attachment grants conversation access; workers still require grants.

- [x] Matching analyze and analysis list/status/resume/read/export/delete commands, chat attachment, progress, configurable budgets and explicit retry controls.
- [x] Streaming source preparation, WAV/MP3 support, source-handle validation, manifest/hash/range checks, native capture format validation and explicit Interrupted-source handling.
- [x] Complete chunk plans, per-component receipts, exclusive execution/storage leases, mutation detection, durable commits and no automatic replay of uncertain submissions.
- [x] Timestamped transcript assembly, overlap evidence/uncertainty, unique processed coverage, optional sound windows and bounded paginated reads/search/export.
- [x] Versioned speaker reconciliation measured on a three-voice fixture: no measured false merges or splits and 0.73% unknown coverage. One voice was held out; broader accuracy remains unverified below.
- [x] Real GPT-6-Astra cited summaries, deterministic multi-batch summary budget/resume checks, citation validation and provider-failure evidence retention. GPT-6-Astra saved as the user-selected default.
- [x] Artifact attachment, stranger/worker grant checks, embedding exclusion, retained attachment reads after job deletion and service-free evidence export.
- [x] Real 380-second WAV/MP3 and variable-bitrate MP3 analysis, original 140-second/four-WAV capture import, Interrupted capture with sound analysis, and 7,203-second sparse-source coverage.
- [x] Actual client-crash recovery, unchanged completed receipts, explicit uncertain retry and a real Windows-console Ctrl+C pause.
- [x] Full Release suites: 572 unit/runtime/real-model and 327 integration tests passed, zero failures/skips; five decoder and eight Python helper tests passed. Final format/pagination checks passed separately.
- [x] Self-contained local publication and fresh-process evidence reads/export, VBR transcription/summary, cached resume and chat attachment. No commit or push.

Commands, IDs, timings, memory/storage measurements, discovered races and limitations are in [FULL_RECORDING_ANALYSIS_ACCEPTANCE.md](FULL_RECORDING_ANALYSIS_ACCEPTANCE.md).

### Additional analysis acceptance not yet completed

- [ ] Broad, manually annotated held-out speaker and recognition evaluation, including similar voices, short speech, simultaneous speakers and languages without whitespace boundaries. Current fixture accuracy must not be generalized.
- [ ] Multi-hour continuous speech and live-provider summaries exceeding one context window. The two-hour fixture is sparse; large hierarchical summaries currently have deterministic-provider acceptance.
- [ ] Exhaustive cancellation/service-crash injection at every preparation, inference, assembly and summary commit boundary; real disk exhaustion and power-loss testing. Existing process/fake-backend checks cover selected transitions.
- [ ] OS-enforced decoder network isolation and adversarial filesystem replacement races. Current containment checks, fixed decoding and socket blocking are not an OS sandbox.

Live transcription, microphone mixing, per-application capture and cross-recording voice identification remain proposed work outside this delivery.


## Simple recording conversations and draft clearing: delivered

Acceptance: [commands, results, measurements and limitations](SIMPLE_RECORDING_ACCEPTANCE.md).

- [x] Capture start/stop retain the chat selection; busy selection persists independently and is restored safely after work or restart.
- [x] `/audio recordings`, `/audio summarize [capture-id]`, `/audio transcript [cursor]` and `/audio coverage [cursor]`; numbered fallback and existing commands retained.
- [x] Parent-only requested analysis, explicit selection/Interrupted consent, automatic attachment, immutable evidence grants and completed-result reuse before service/model checks.
- [x] Compatible service startup for capture start and requested chat analysis; no recording/model installation from tools, automatic analysis on stop, or uncertain-request replay.
- [x] Compact normal tool output, direct saved-report display, retained JSON/details, timestamp rounding and hidden segment identifiers in normal direct views.
- [x] Idle Ctrl+C clears the full unsent draft; active cancellation and picker state remain protected. Real Windows-console checks passed against the published executable.
- [x] Full Release suites: 572 unit/runtime/real-model and 341 integration tests passed; final 15-test focused run adds a synthetic capture-to-summary transport test. Thirteen Python regressions passed.
- [x] GPT-6-Astra natural-language acceptance, explicit interrupted-job resume, full 140.3845625-second coverage, and published service-off summary reuse with zero parent model requests.
- [x] Existing local Windows publication updated; both same-user service directories verified compatible with no active capture. No commit, push or startup registration.

Broader recognition/speaker evaluation, live transcription, microphone mixing and per-application capture remain proposed work as listed above.
