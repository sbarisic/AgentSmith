# Future plan: AgentSmith improvements

Status: proposed; not implemented. Created 15 October 2026.

## Goal

Run one background Smith service that performs local audio inference for multiple Smith CLI instances and their workers. Clients share loaded Whisper, YAMNet and speaker-separation models instead of each loading its own copies. Preserve offline inference, tool permissions, conversation isolation, artifacts and receipt recovery.

## Current behavior

`AudioTool` and `LocalAudioService` serialize audio work within one Smith process. Parent and worker requests share that process's Python helper. Separate Smith processes have independent queues, helpers and model allocations. Model files are shared on disk when processes use the same data directory. Each helper unloads after five idle minutes.

Existing `audio_read` views and durable component reuse do not require new inference. Keep these operations in the client. The service shares computation and loaded models; it does not become a global conversation or evidence cache.

## Proposed defaults and scope

- Windows x64, CPU inference, current pinned audio engines and explicit `smith audio setup` installation.
- One service per Windows user and canonical Smith data directory. All clients using that directory share it. A different data directory deliberately creates a separate service/model pool; status must make this visible.
- One active audio job across connected clients, with a bounded FIFO queue. Initial limit: 32 waiting jobs total and four per connected client, with explicit queue-full errors. Worker requests use their parent process's connection and quota.
- Load models lazily. Unload the Python helper after five idle minutes, while keeping the lightweight service available for future requests. Never unload an active job.
- Preserve the 25 MiB source limit, 120-second clip limit and five-minute end-to-end request deadline for individual inference requests, including snapshot preparation, connection and queue time. The completed-recording workflow below orchestrates bounded chunks for longer calls rather than truncating them to these limits.
- Keep the service independent of the chat provider. No API key, account or network audio inference is required.
- First release is an explicitly started background process, without elevation, Windows Service Control Manager installation or automatic login startup. Remote access, GPUs, multiple inference slots and cross-user sharing are outside this scope.

## User interface and lifecycle

Proposed commands:

| Command | Behavior |
| --- | --- |
| `smith service start` | Start a hidden background instance, wait for a readiness handshake, and return its status. Starting an existing compatible instance is idempotent. |
| `smith service run` | Run the same host in the foreground for diagnostics. |
| `smith service status` | Show service identity, data directory, protocol/build, installation generation, engine readiness, helper state, active work and queue counts. |
| `smith service stop` | Stop admitting work, drain already accepted jobs within their deadlines, unload the helper and exit. Return a clear draining state if shutdown is still in progress. |
| `smith service stop --force` | Explicitly interrupt active and queued work and stop immediately. Notify affected clients. |

Support the existing `--data-dir` and structured JSON conventions. Extend `smith audio status` and `/audio status` with connection, queue and shared-helper information.

In this service release, audio tools connect to the service by default. If it is unavailable, show `smith service start` guidance; never silently launch a private model helper as fallback. Model-callable tools cannot install models or start/stop the service. Saved audio evidence remains readable while the service is stopped.

The service survives individual CLI exits. Its Python child remains inside the existing kill-on-close Windows job owned by the service. Explicit shutdown or service failure terminates that child. Launch the background Smith host with its own lifetime, independent of a requesting chat's child-process job; verify this with a real parent-exit test.

## Local transport and access boundaries

- Use a local Windows named pipe, restricted to the current Windows user, with remote pipe clients rejected. Derive the endpoint from user identity and canonical data-directory identity. Do not expose a TCP listener.
- Use an OS-backed singleton lock to arbitrate concurrent starts. Validate endpoint ownership and handshake identity; a PID file alone does not establish ownership or liveness.
- Define a separate, versioned client/service protocol with request IDs and bounded frames. Messages cover handshake, submission, upload, queue/progress, component outcomes, completion, cancellation and errors. Keep this independent of the existing Python helper protocol and audio artifact schema.
- Bound connections, in-flight uploads, metadata, total audio bytes and result size. Apply admission control before accepting audio uploads. Reject unsupported versions and malformed or oversized input explicitly.
- Preserve workspace path, exclusion, link, file-handle and permission validation in the client. Snapshot and hash the validated input before submission; send its bounded bytes, not an arbitrary filesystem path for the service to open.
- Stream the snapshot into a service-owned private job directory, verify its length/hash and validate operation, clip range, language and component settings again. Only invoke the fixed managed helper. Neither paths nor requests authorize arbitrary commands or scripts.
- Bind each request and cancellation handle to its connection. A client cannot retrieve another client's results or cancel another client's jobs. Keep conversation data and source contents out of service status and routine logs.
- Named-pipe user isolation is not a sandbox against malicious programs running as that same Windows user. Existing conversation ownership and artifact grants remain enforced by each Smith client.

## Scheduling, cancellation and recovery

1. Admit requests using the global/per-client limits, then process accepted jobs in FIFO order. Emit waiting progress only for jobs that actually wait, with queue position and elapsed queue time.
2. Carry the caller's remaining deadline into the service. Queueing and transport must not reset the five-minute budget. Drop expired jobs before inference and enforce cancellation on active work.
3. Cancelling or disconnecting a queued client removes only its jobs. Cancelling or disconnecting the active client terminates the helper if needed; the next queued request starts a fresh helper. Other clients remain queued and receive updated progress.
4. Helper crashes or invalid output fail the active request explicitly. The service may start a fresh helper for a different queued request, but never automatically replay interrupted inference.
5. A service crash makes outstanding client requests interrupted. A fresh service does not replay an old queue. Successful client receipts remain authoritative; receipt-less interruptions require a fresh user request.
6. Reject duplicate request IDs on a connection. Do not reconnect and resubmit automatically after an uncertain submission or result-delivery failure.
7. Delete uploaded/decoded audio after success, failure, cancellation or disconnect. Clean abandoned service job directories on startup using exclusive leases and bounded cleanup, without touching active work or unrelated files.

## Installation, updates and evidence

- Continue using explicit staged `smith audio setup`, checksums, self-tests and the atomic installation-generation pointer. Service startup never downloads dependencies or weights.
- Each admitted request identifies the required model/settings fingerprint. Pin that generation for the request; do not switch it while running. Reject unavailable/incompatible generations with actionable setup/reconnect guidance.
- On a generation change, finish active work before unloading the old helper and switching. Handle queued generation mismatches explicitly rather than silently changing their model provenance. Retain generations needed by active work.
- Establish a client/service compatibility handshake. Refuse incompatible builds/protocols without private-helper fallback. Document stopping/draining the service before replacing a locked Windows executable and starting it again after publication.
- Clients perform authorized durable component lookup before submission and send only missing components. Preserve original inference timestamps and provenance links when combining reused and new evidence.
- The service returns structured results; clients validate them, align transcript words to speaker turns, redact as appropriate, save artifacts and commit successful-call receipts using existing behavior. The service never opens conversation databases or writes conversation artifacts.
- Preserve existing audio tool schemas, read-only permissions, worker access, historical artifacts and focused views. Record service/helper/protocol versions, engine fingerprints, queue/upload/decode/load/inference timings and total client time separately.

## Implementation order

- [ ] **Transport and lifecycle:** add service commands, singleton identity, secure named-pipe transport, bounded protocol and detached host lifecycle. Exercise these with a fake inference backend first.
- [ ] **Shared scheduler:** implement bounded admission, FIFO dispatch, progress, deadlines, disconnect handling and graceful/forced shutdown. Move helper ownership into the service while reusing existing containment and idle expiry.
- [ ] **Tool integration:** replace direct helper calls with the service client, retain local artifact reuse/receipts, transfer validated snapshots and route parent/worker requests through the shared queue.
- [ ] **Installation compatibility and presentation:** handle engine readiness, generations and version negotiation; update `/audio status`, help, model instructions and README. Document startup and release-update behavior.
- [ ] **Acceptance and delivery:** verify the criteria below, record measurements in a new acceptance document, run the full Release suite, publish to the existing PATH folder, and smoke-test a fresh service with multiple fresh Smith clients.

## Verification and acceptance criteria

### Deterministic and process tests

- Concurrent starts yield exactly one service per user/data directory; different directories remain isolated and clearly identified.
- Closing the launching CLI leaves the service alive; service exit/crash kills its helper tree. Idle expiry releases models without stopping the service or interrupting active work.
- Multiple clients and parent/worker requests share one serialized inference slot. Queue limits, queue positions and per-client limits behave predictably.
- Queued cancellation leaves active work intact. Active cancellation, disconnect, timeout and helper failure release resources and allow other clients to progress without replaying interrupted work.
- Deadlines include connection, upload and queue time. Service stop drains bounded work; forced stop reports explicit interruption to every affected client.
- Reject unauthorized endpoint access, remote connections, malformed/oversized frames, invalid uploads, bad hashes, invalid ranges and incompatible protocol/generation requests.
- A client cannot cancel or read another client's work. No raw audio survives normal cleanup; crash cleanup respects leases.
- Existing artifact ownership/grants, component reuse, partial results, saved receipts, restart recovery, historical records and offline behavior remain intact. A durable cache hit does not contact or require the service.

### Real-machine acceptance

- Launch at least three independent Smith clients against one service. Submit audio concurrently and confirm one helper/model pool, correct queueing and successful isolated results.
- Measure total memory across service/helper/clients, cold and warm latency, queue delay and throughput. Compare with the previous independent-helper behavior; report observed figures rather than assuming linear savings.
- Use the existing checked transcript and sound/speaker fixtures. Preserve the transcription accuracy threshold and report diarization/overlap limitations separately.
- Verify five-minute idle model release, client exit, service restart, active cancellation and installation-generation changes.
- Run with audio credentials absent and application-level network blocking. Where available, also test OS-enforced network isolation and distinguish those results.
- Record test results, measured memory/latency, compatibility decisions and remaining limitations separately from historical acceptance records.

## Completion definition

Several Smith instances can submit local audio work to one explicitly managed background service, with one loaded model pool, bounded scheduling, independent client cancellation and unchanged conversation evidence boundaries. Stopping a chat does not stop unrelated audio work; stopping the service releases its models and reports affected requests honestly.

## Explicit chat exit

Status: proposed; not implemented.

- [ ] Make Ctrl+C at an empty, idle input prompt do nothing: keep the chat open, preserve session state and leave the prompt ready for input. Repeated Ctrl+C must not escalate to exiting.
- [ ] Require an explicit `/exit` command for normal user-requested chat closure. Ctrl+C must never serve as a chat-exit shortcut, including after cancelling work or returning from a question/review prompt.
- [ ] Preserve cancellation of active work without closing the chat. Preserve existing nonempty-draft handling where compatible with the explicit-exit requirement; never submit or discard a draft merely to exit.
- [ ] Apply the behavior consistently to new and resumed interactive chats. Update help and keyboard guidance to explain `/exit` and distinguish cancellation from exiting.
- [ ] Verify empty-prompt Ctrl+C, repeated presses, return to the prompt after cancellation, nonempty drafts, pending questions/reviews, and explicit `/exit` with saved-state recovery. Terminal closure, process termination and input-stream loss remain external interruptions and must retain existing cleanup/recovery behavior without spinning on EOF.

Acceptance: pressing Ctrl+C at an empty interactive prompt leaves Smith ready for the next message; `/exit` closes the chat cleanly. Cancelling active work returns control to the same chat.

## User-controlled sound output capture with NAudio

Status: proposed; not implemented.

### Goal and capture scope

Let the user explicitly start and stop recording Windows sound output, such as a Teams call, then ask Smith to transcribe and analyze the entire completed recording locally. Do not perform inference on that recording while capture is active or infer permission to record from a calendar event, running application or model suggestion.

- [ ] Add a pinned, compatible stable NAudio dependency through central package management. Use its supported WASAPI loopback recording API for the selected Windows playback endpoint. Verify the API against the pinned release: upstream now documents `WasapiRecorder` as the replacement for the legacy `WasapiLoopbackCapture` API. References: [NAudio recording](https://github.com/naudio/NAudio/blob/main/Docs/WasapiRecorder.md) and [loopback capture](https://github.com/naudio/NAudio/blob/main/Docs/WasapiLoopbackCapture.md).
- [ ] Enumerate playback devices and let the user select the endpoint used by Teams or another application. Display its name before capture and throughout recording. Endpoint capture can include other applications and notifications playing through that endpoint; do not describe it as Teams-only capture.
- [ ] Make clear that sound output capture records what plays through the selected output. It does not inherently include the user's microphone or local speech. Microphone mixing and per-application isolation are separate future extensions; do not silently enable microphone capture or promise that an output-only recording contains both sides of a call.
- [ ] Keep recording under explicit user control, with visible recording state, elapsed time, captured size and the selected endpoint. Starting and stopping capture are user commands, not model-callable tools. Worker agents cannot start or stop recording.

### Proposed controls and ownership

| Command | Behavior |
| --- | --- |
| `/audio devices` | List available output endpoints and identify the current default. |
| `/audio capture start [device-id]` | Start a recording owned by this conversation using the explicitly selected endpoint, or the displayed current default. Return a capture ID. |
| `/audio capture status [capture-id]` | Show Recording, Finalizing, Completed, Interrupted or Failed state, duration, size and any gaps/errors. |
| `/audio capture stop [capture-id]` | Stop acquiring audio, drain pending buffers, finalize all files and return the completed recording ID. Safe to repeat; it must not create duplicate recordings. |
| `/audio capture analyze <capture-id>` | Analyze a finalized recording after the user requests it; default to transcription with anonymous speaker evidence. Offer combined sound classification as an explicit option. |

Provide equivalent `smith audio capture ...` commands for inspecting and stopping captures when the original chat is unavailable. Resolve ambiguity explicitly; never stop a different recording merely because it is the newest. Model analysis uses a dedicated read-only `audio_analyze_capture` tool for an authorized completed recording, plus existing focused evidence readers for follow-ups. A user may also explicitly request analysis when stopping; it starts only after finalization succeeds.

- [ ] Host capture in the shared Smith service, independently of the inference helper and queue. Initially allow one active capture per service and reject a second start clearly. Other already-completed audio jobs may run, but must not block capture callbacks or cause silent dropped audio.
- [ ] Record ownership and capture lifecycle in service-owned metadata without opening conversation databases. Clients register references under existing conversation ownership/grant rules; possession of a capture ID alone does not grant a model access. User CLI management remains available to the same Windows user.
- [ ] A chat exit or disconnect does not silently stop a user-started service recording. Show the active recording and the command to stop it when exiting; allow explicit stopping from another CLI. Restarting the service never automatically restarts capture.
- [ ] Graceful service stop finalizes an active recording and reports that capture ended. Device removal, service crash, forced stop, disk exhaustion and system suspend produce explicit interruption/gap states; preserve recoverable completed audio and never label an incomplete recording as complete. Do not silently switch devices when the default changes.

### Storage and finalization

- [ ] Stream capture to bounded, numbered files in a private recording directory instead of retaining the meeting in RAM or relying on one ever-growing WAV. Use a bounded producer/consumer buffer; report dropped samples or discontinuities. Rotate files below format/size limits while preserving exact frame offsets and format metadata.
- [ ] Track monotonic recording time, sample positions, silence and gaps. A lack of loopback callbacks during silence must not compress the meeting timeline. Finalize headers and flush all pending writes before transitioning to Completed; wait for the recording-stop lifecycle to finish.
- [ ] Create a durable manifest containing capture ID, owner, endpoint identity, start/stop times, total duration, ordered chunks, sample formats, hashes and coverage/gap information. Validate leases, containment and links for recording directories and crash cleanup.
- [ ] Retain intentionally captured audio locally so completed recordings can be analyzed or retried later. Keep it separate from disposable inference snapshots and prevent the existing temporary-audio cleanup from deleting recordings. Show location/size and provide explicit user export/deletion controls. Do not put recordings in Git or automatically upload them to the chat provider.
- [ ] Check available disk capacity before and during capture. Document a configurable recording storage budget; approaching it must be visible, and reaching it must finalize what was captured with an explicit limit/interruption outcome rather than silently discarding or overwriting audio. Ordinary duration is controlled by the user's start/stop actions, not the inference clip limit.

### Analyze the entire completed recording

- [ ] Refuse analysis while Recording or Finalizing. Allow an explicit request to analyze recoverable Interrupted recordings, with missing coverage preserved in the result; never claim this covers the entire original call.
- [ ] Add a recording-level orchestrator that validates the manifest and schedules every captured interval through the shared inference service in chunks within the existing 25 MiB/120-second limits. Include bounded overlap at chunk boundaries, with overlap included in the per-request limit. Never treat duration zero as permission to analyze only the first 120 seconds of a meeting.
- [ ] Preserve the five-minute deadline for each individual inference request. Give the overall recording analysis its own visible progress, cancellation and configurable execution budget; exhausting it pauses with completed coverage retained. Do not apply the five-minute single-call timeout to an entire multi-hour meeting or silently remove execution limits.
- [ ] Persist per-chunk receipts, hashes, engine/settings fingerprints and analysis outcomes. On explicit resume, reuse successful chunks; uncertain interrupted chunks require an explicit retry. Do not replay completed work or duplicate final artifacts.
- [ ] Merge transcripts in recording-relative timestamp order, resolving overlap using word timing and overlap evidence rather than blindly deleting repeated phrases. Preserve intentional repetition, silence and segment provenance. Expose a full paginated transcript, processed/total duration and any failed or missing intervals.
- [ ] Keep anonymous speaker labels scoped to their chunk unless a separately validated within-recording reconciliation step establishes continuity. Never merge unrelated `Speaker 1` labels from different chunks as if they were one person. Report unknown/overlapping attribution and speaker-count uncertainty honestly.
- [ ] Return a recording summary and linked component artifacts. Full transcription is retained even when it exceeds a single tool/context page; Smith can retrieve it through focused paginated views. Optional sound analysis runs YAMNet over the captured audio and retains source-relative timelines. Audio stays local; returned text and labels may enter the configured chat provider context.

### Verification and delivery

- [ ] Deterministic tests cover explicit start/stop, duplicate commands, ownership, active-recording analysis rejection, buffer draining, chunk rotation, silent periods, gaps, device loss, disk limits, service/client shutdown and crash recovery.
- [ ] Long-recording tests exceed both 120 seconds and 25 MiB and verify complete interval coverage, correct offsets, no missing/duplicated boundary words, bounded memory, explicit partial failures and resume without repeated successful inference.
- [ ] Test repeated phrases at chunk boundaries, changing speakers, overlap and chunk-local speaker labels. Compare transcription to checked references and report recognition limitations; complete processing does not guarantee perfect recognition.
- [ ] Perform a user-started/user-stopped live Windows capture from a selected output endpoint, including speech, silence and other sound. Verify recording indicators, captured duration, finalized playback and subsequent local transcription. Test Teams or a comparable call with known reference material and document whether the user's own voice is present in the selected output.
- [ ] Update help, README, model instructions and service status; run relevant NAudio, lifecycle, CLI and full Release tests. Record real-machine results separately before publishing the capture feature.

Acceptance: the user starts capture, stops it when finished, and Smith can subsequently transcribe all retained audio across the completed recording, with timestamps, bounded resource use and explicit coverage limitations. Recording never starts autonomously, and analysis never begins before audio finalization.
