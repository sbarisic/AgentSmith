# AgentSmith

AgentSmith is a Windows-first C# coding assistant with a terminal interface, ChatGPT and local-model providers, delegated workers, checkpointed file editing, process execution, and durable conversations. Work, Plan, Document, Review, and Memorize workflows share permission checks and saved task state.

Future work is tracked in [plan.md](plan.md), starting with a shared background audio service for multiple Smith instances. This service is planned, not implemented.

Licensed under the [MIT License](LICENSE). This repository distributes source, tests, and documentation only. It contains no binaries, model weights, local session data, or CI/CD workflows. Build locally with the .NET SDK pinned in `global.json`; local model servers and credentials are configured separately.

## Repository memorization

`/memorize` starts bounded source inspection and reconciliation of repository-local development knowledge. `/memorize --full` requests a bounded full rescan; `/memorize --path src` restricts inspection to a workspace-relative path. `/mode memorize` and startup `--workflow memorize` select the workflow. This describes the local implementation, not a published release.

Memorize is source-read-only: it allows dedicated memory/index/report state updates, not source edits, project evaluation, process/build/test execution or HTTP writes. Workers may inspect assigned sources; the parent reconciles and commits after collecting them. Resolve active work and recovery before switching workflows. Successful completion returns to Work with the existing permission level. Follow-up questions do not start another Memorize report; use `/memorize` to start another scan. Use `/workflow work` or `/mode work` to leave unfinished work explicitly. Leaving an unfinished report does not mark it complete.

Use `/memorize status` for the latest Memorize report in this conversation, `/reports` to list reports, and `/report <id>` for saved evidence and limitations. Status does not scan, refresh or request model inference. Report export is blocked while in Memorize.

Completion follow-up fix (2026-10-15): returning to Work is saved with the completed report and receipt, including recovery support for older receipts. This prevents a follow-up summary question from creating another Memorize report. All 814 Release tests passed, including scripted follow-up, reload, queue, permission and interrupted-finalization regressions; the Windows publish was refreshed and startup smoke-tested. These checks do not claim a new live-model memorization run.

### Storage, retrieval and privacy

- Managed memory bodies, source hashes/line references, revision history, scan manifests and commit receipts live under the selected workspace's `.agentsmith/memory`. The global discovery catalog stores repository IDs, locations and registration timestamps, not memory bodies. Treat this local state as private; do not commit it unintentionally.
- Later model requests prioritize bounded, task-relevant current-repository memories and retain the existing shared global library. Memories are historical evidence, never instructions, authorization or proof that code currently passes verification.
- Model tools `repository_memory_discover`, `repository_memory_search`, `repository_memory_read` and `repository_memory_status` expose discovery and reads. Search/read use an empty `repositoryId` for this workspace; another registered ID explicitly selects another repository. CLI reads use `/memory current list [page]|search <text>|show <id-or-topic>`, `/memory repositories` (catalog metadata only), or `/memory repository <registered-id> list [page]|search <text>|show <id-or-topic>`. Repository IDs must be registered, validated IDs, not arbitrary paths. Unscoped commands retain the legacy global library; `/memory global list|search|show` makes that scope explicit. The same grammar works with `smith memory`; `current` selects the chat workspace or, outside chat, the current working directory. Repository list pages contain 20 entries. Repository CLI operations are read-only; relinking remains a storage API operation.
- Existing memory disablement controls apply to repository memory tools, all new scoped CLI reads/discovery (including `/memory global` aliases), and automatic retrieval. Legacy unscoped global-library commands retain their existing behavior. Disabling retrieval does not erase stored history or backups.
- **Local storage does not mean local inference.** Selected source evidence and retrieved memories may be sent to the configured model provider. Sensitive-file exclusions and secret filtering reduce risk but cannot guarantee that secrets are absent. Review the selected workspace and provider before memorizing it.

### Bounds and recovery

Default scans cap inspected files at 200, total bytes at 2,000,000, each file at 128,000 bytes, model-visible evidence at 64,000 characters, each initial file excerpt at 120 lines and 12,000 characters (or the remaining evidence budget), and visited entries at 10,000. Hashes cover the full inspected file bytes, not just the excerpt; reported evidence truncation means the file was not comprehensively inspected. Scanning skips links and recognized generated/build/vendor/binary/sensitive content and applies `.gitignore`/`.ignore` conservatively. Unsupported ignore syntax can stop a scan. `--full` does not remove these bounds; read reported omissions rather than assuming comprehensive coverage.

Refresh currently rescans bounded eligible sources and reconciles existing topics. `--full` requests full reconsideration within the same limits; it is not a separate unbounded traversal or a changed-file-only scan optimization.

Commits use scan-bound provenance, source rechecks and expected revisions. Identical topics avoid revision churn; user-authored entries are protected, and partial scans do not establish deletion. Catalog contention is reported as incomplete discovery registration; retry after the competing operation settles. Workspace identity is local/path-based, not a remote-URL identity; copied memory state is not automatically adopted by another clone. Move relinking currently exists at the storage API level, not as a CLI command. Local state and catalog updates are separate writes, so interrupted operations require explicit retry rather than an assumption of atomic success.

Repository reads recheck recorded source hashes; changed or inaccessible sources, or a previously recorded stale flag, mark an entry stale. Matching source hashes and a recorded verification timestamp do not mean builds/tests passed or that unreferenced code was inspected.

Relinking requires the original local memory state at the destination and refuses a different destination while the original workspace directory still exists. It is not a clone-merging operation.

Repository state limits are 1,000 entries, 16 scan manifests, 16,000 historical revisions and 4,096 commit receipts. Discovery examines at most 1,000 catalog registrations; these storage/discovery limits are separate from source inspection bounds.

Path/link checks and source hash rechecks do not guarantee containment against adversarial concurrent filesystem path swaps, or lock sources through the final memory save. Use a trusted, stable workspace; these checks are not a filesystem sandbox.

There is no background watcher, vector database, cloud sync, automatic source repair or execution. The October 14, 2026 acceptance record reports a bounded ChatGPT CLI fixture covering creation, fresh-session retrieval, unchanged reconciliation without revision churn, and changed-source refresh followed by fresh retrieval. This is not all-provider or comprehensive concurrency acceptance; see the scoped evidence and limitations in `MEMORIZE_ACCEPTANCE.md`.

## Interactive composer

Type `/` for local command and finite-argument suggestions. Up/Down selects suggestions and Tab/Enter completes without executing; press Enter again to submit. Outside the menu, Up/Down recalls accepted prompts from this conversation and restores your unsent draft. Ctrl+Backspace deletes a word without splitting pasted blocks or joined Unicode characters. See [composer keyboard controls](COMPOSER_KEYS.md) for limits, literal paste behavior and terminal caveats.

## Local audio transcription and sound recognition

Run `smith audio setup` once on Windows x64 to download a private Python 3.11 runtime, hash-locked packages, Whisper multilingual small, Google YAMNet and sherpa-onnx speaker models. All downloads are public and require no account or token. Use `smith audio status` or `/audio status` to inspect readiness. Pass the same `--data-dir <directory>` you use for Smith when selecting a custom data directory. Setup leaves system Python and PATH unchanged, and retains the previous working installation if an update fails. The Microsoft Visual C++ 2015–2022 x64 Redistributable is required; setup reports missing DLLs with installation guidance.

After setup, inference is local, CPU-only and offline, with no API key. Tools are advertised only when their engines are ready:

- `audio_transcribe(path, startSeconds, durationSeconds, language)` returns timestamped speech using Whisper small INT8 with voice activity filtering and anonymous speaker separation. Use `auto` for language detection or a supported Whisper language code, such as `en` or `hr`.
- `audio_diarize(path, startSeconds, durationSeconds)` estimates anonymous speaker turns without transcription. Labels such as Speaker 1 apply only to that analysis; they do not identify people or match voices across recordings.
- `audio_read(artifactId, view, cursor, maxCharacters)` reads saved `summary`, `transcript`, `speakers`, `timeline`, `scores` or `metadata` views without inference, with 4,000-character pages.
- `audio_classify(path, startSeconds, durationSeconds)` returns YAMNet's sound categories, aggregate scores and top-five labels for each analysis window.
- `audio_listen(path, question, startSeconds, durationSeconds)` runs transcription, classification and speaker separation. The question helps Smith interpret the evidence; it is never sent to Whisper as a prompt. A successful component is retained if another fails.

Example: `Transcribe tests/test01_20s.wav and identify the sounds in the background.` Transcription identifies words; classification identifies likely sound categories; Smith interprets their results. YAMNet's 521 categories do not provide arbitrary audio question answering, calibrated certainty or precise event boundaries. Whisper can miss or misrecognize speech. No speech is reported explicitly. Speaker separation uses FP32 pyannote segmentation 3.0 and NeMo TitaNet small through sherpa-onnx 1.13.8, with automatic clustering (threshold 0.5; minimum speech 0.3 s, silence 0.5 s). Word attribution requires majority time coverage by one speaker and no overlap from another; ambiguous words remain labeled as such. Short and overlapping speech can be misassigned. This release excludes translation, real-person identification, microphone capture, CLAP and long-recording workflows.

WAV and MP3 share the same range rules: whole-second nonnegative start, duration 0..120 seconds, with zero selecting up to 120 seconds. Source files are limited to 25 MiB. PyAV decodes a private snapshot, downmixes to mono and resamples to 16 kHz; timestamps are relative to the original recording, including MP3 encoder-delay correction. Workspace exclusions and link/traversal checks apply. Smith does not play audio or fetch audio URLs. One job runs at a time per Smith process, including workers, with visible waiting/decoding/transcribing/classifying stages and a five-minute deadline including waiting. Models load lazily in one contained helper shared by parent and workers, and unload after five idle minutes. Cancellation of active inference and process exit terminate it; cancelling a queued request leaves active work intact. Old installed helpers remain usable until explicit setup upgrades them.

Compact evidence appears in chat; `/artifact <id>` shows an audio summary and `/artifact <id> audio <view> [cursor]` opens a focused view. Explicit `/artifact <id> read <cursor>` still reads original raw evidence, including the class map. Mean label display uses a 0.05 threshold; timelines merge consecutive windows at 0.20 and show approximate intervals, not exact events or confidence. Summary timelines show up to ten intervals; omitted labels do not establish absence. Unfiltered scores remain available. Artifacts retain source and clip hashes, ranges, engine/model versions, timings and component outcomes. Fresh requests validate and hash current input, then reuse matching successful components from owned or explicitly granted artifacts; changed input, ranges, settings or installed fingerprints invalidate reuse. Interpretation questions do not change inference keys. Reuse survives reload, preserves original evidence times, and never uses an unrestricted cross-conversation cache. Raw audio is deleted after processing; abandoned snapshots from a killed process are cleaned on the next local audio call. Successful receipts recover without repeating inference. Interrupted calls without a receipt require a fresh request. Historical cloud-audio artifacts remain readable.

Audio stays local, but returned transcript text and labels can enter your independently configured chat provider's context. Spoken instructions and model output are untrusted evidence, never authorization. Setup is an explicit terminal command, not a tool the model can invoke automatically. The internal fixed helper does not grant permission to execute project commands.

Model/runtime pins are in `src/AgentSmith.Tools/LocalAudio/downloads.json`; package versions and hashes are in `requirements.lock`. Downloads, environments and models live under `<Smith data directory>/audio`, outside Git. See [local audio acceptance](LOCAL_AUDIO_ACCEPTANCE.md), [faster-whisper](https://github.com/SYSTRAN/faster-whisper) and [YAMNet](https://www.tensorflow.org/hub/tutorials/yamnet).

## Worker execution budgets

Pending source update: the busy status bar includes active worker numbers, for example `agents 2 [#8, #9]`. It refreshes once per second from saved worker state. Prepared/waiting and cancelling workers remain listed until settled; completed/failed workers drop out. More than eight worker numbers are summarized with `+N`; `/agents` shows the full list. This status-bar change awaits a rebuild.

Delegation guidance: prefer workers when at least two agents can make useful progress concurrently—multiple workers, or a worker alongside identified independent parent work. The parent handles single sequential tasks itself when it would otherwise only wait for one worker, even when delegation is explicitly requested. Requests do not override permissions, manual-delegation opt-in, enabled profiles, concurrency limits, file reservations or process barriers. Splitting dependent work or filling worker slots is not a goal.

The composer context indicator `ctx last ~32k/128k tok (25%)` shows the last prepared input versus its working budget, not cumulative usage or the model's full capacity. `~` marks estimates; unknown-capacity sessions use `chars` instead of tokens. Narrow terminals show a compact percentage. New tool output and unsent draft text are not included until context is prepared again; `/context` provides details.

The worker wait cap is **six minutes (360,000 ms)**. `agent_wait` still returns early on selected-worker completion, steering or cancellation. Process waits remain capped at 60 seconds.

New workers default to **100 model requests / 30 minutes**, bounded by the parent's remaining deadline. Use `/worker-budget` to inspect the setting and `/worker-budget 200 45` while idle to change it for future workers in that conversation. New chats accept `--worker-max-requests 200 --worker-max-minutes 45`. This persists across resume, includes compaction requests, and is separate from `/worker-limits` concurrency. Existing workers keep their spawn-time budget; stopped workers are not automatically restarted. These changes are included in the local 2026-10-11 build.

## Markdown tables

Formatted responses display Markdown tables with square borders, aligned columns and wrapped cell contents. Narrow terminals use labeled rows when columns would be too cramped. Inline emphasis remains styled and ordinary text stays white. `/markdown off` keeps the original table markup.

Routine tool summaries and C# symbol listings use light gray; tool headings use cyan. Git changes, plan progress and diagnostic headings use status colors while keeping their text labels. Normal answers stay white; `--no-color` retains plain text. These changes were published locally on 2026-10-11 after 37 renderer tests passed. Start a new Smith process to use them.

## C# navigation and diagnostics

Smith can find C# declarations with `csharp_symbols` in any workflow, including Inspect and workers. This parses source without evaluating projects. Results include signatures, locations, hashes, scan coverage and omission reasons. Syntax searches do not resolve overloads or establish complete references.

An Execute parent in Work or Document can use `csharp_load` with a workspace `.csproj`, `.sln` or `.slnx`. Configuration defaults to Debug; multi-target projects require a framework. Loading starts an owned helper process. Smith waits for that process and checks `csharp_status`, then uses `csharp_query` for definitions, references, callers or implementations. MSBuild evaluation runs project code as your Windows account and may write generated files. It is not a sandbox. Loading does not request restore, workload downloads or a normal build; missing dependencies produce partial or unavailable coverage.

Queries use an immutable Roslyn workspace without analyzers or source generators. Linked-file project variants are labeled; metadata-only definitions identify an assembly rather than inventing a source path. Only workspace source is included. Generated source files already produced by design-time loading inside the workspace may be included; files outside the workspace are excluded. Workers can query snapshots explicitly shared through `agent_spawn.navigationSnapshotIds`, but cannot load or refresh projects.

Source/configuration/reference changes invalidate snapshots. Settle editing workers before refreshing. Each root conversation allows two loaded snapshots; active worker grants prevent replacement. Snapshots last for the current CLI lifetime only. Resume preserves provenance and retained query artifacts, but never evaluates projects automatically. Queries return at most 50 results and 8 KiB per page; use the returned cursor or artifact to inspect more evidence.

Build/test summaries now prioritize compiler errors and failed tests. Repeated compiler diagnostics are grouped while retaining project/framework associations. Passing test names remain available in report details:

```text
/diagnostics <report-id>
/diagnostics <report-id> errors
/diagnostics <report-id> failed 2
```

The model's `diagnostics_read` tool also supports path/text filters and cursor paging, including explicitly shared worker reports. Existing saved reports are normalized when read; raw artifacts are preserved. Report completeness, process exit status and verification success are separate. Missing locations or target metadata remain unavailable rather than inferred. Use current build/test evidence to verify current code.

Automated and local acceptance outcomes, including unavailable live-provider checks, are recorded in the retired CLI implementation record (in Git history).

## Model selection and chat defaults

```text
/models                  List models for this conversation's provider/account
/model                   Show the current model and saved new-chat default
/model <id>              Select the next prompt's model and save it as the default
smith model <id>         Save a default outside a conversation
smith model              Show the saved default
smith chat               Start a new chat using that default
```

Change models between runs; unfinished work, pending decisions and workers must settle first. Existing conversations keep their provider and account. Use `smith model <id> --provider local --endpoint http://127.0.0.1:8080/v1` to configure a local default for new conversations. Worker profiles and permission/workflow settings are unchanged. Model IDs are validated against the selected provider before saving; smaller models must have enough context for the configured budget.

Defaults are scoped to `--data-dir` and include provider, endpoint, account reference and output budget, never credentials. A successfully configured `smith chat --model <id>` also saves its selection; explicit CLI options override defaults. Without a saved default, new chats use the most recent parent session's selection. A fresh installation with no prior model prompts you to configure one. `smith resume` keeps that session's saved model. `smith run` can use defaults but does not replace them. Usage remains attributed to each model across switches.

Delivered locally on 2026-10-10 with **452 passing Release tests** and a refreshed self-contained Windows x64 build. Published checks with an isolated mock catalog confirmed default persistence, `smith chat` without a model argument, `/models`, and a `/model` change across new chats. No live provider inference was performed.

## Repetition and oversized context recovery

Submitted prompts and queue acknowledgements display image attachment labels with dimensions and size when available. Replay uses the same labels. The live transcript previously displayed only prompt text even though image parts were passed to the runner. These labels are attachment metadata, not graphical previews or confirmation that inference succeeded. This change is included in the 452-test local delivery above; physical clipboard/terminal acceptance was not repeated.

Smith now automatically collects settled worker results when the parent attempts to finish, including failed, blocked and cancelled workers. It saves report and checkpoint references, then gives the parent another request to inspect that evidence. Collection does not turn failed work into success or bypass recovery. For the reported collection loop, use `smith resume` and explicitly `continue`; no manual collection or compaction is needed for this blocker.

The automatic-collection follow-up passed **438 Release tests** and was published to the local Windows x64 folder. Published smoke checks passed. Coverage includes a parent that never calls `agent_result`, failed saves and restart between the saved parent receipt and worker collection marker. Live ChatGPT/Bonsai acceptance of this change remains unverified.

If completion is blocked by uncollected worker results or an unfinished Document/Review report, Smith now shows the specific requirement and required tool action. The same feedback is included in the next model request and retained across pauses. Failed and cancelled workers still require result collection; they do not need to be relaunched. `/status` shows the pause reason and completion requirement. This does not bypass report, permission or recovery checks.

The completion-feedback follow-up passed **434 Release tests** and was published to the local Windows x64 folder. Published smoke checks passed; live provider compliance remains unverified.

Generated answers now stop with a visible progress pause when substantial phrases repeat excessively. Unfinished response tool calls never run. Smith also pauses after three tool-free attempts to finish while required work remains unsettled. Completed responses flush Markdown separately, preventing adjacent answers from joining into one paragraph.

Compaction summarizes oversized historical groups through bounded, provenance-linked evidence slices. The original history and complete tool-call/result groups stay intact. A snapshot becomes active only after all slices succeed and the protected current context fits. The eight-request limit still applies; cancellation or a failed slice leaves the previous context intact.

For a session already paused with the old oversized-group error: reopen with `smith resume`, run `/compact`, and type `continue` after compaction succeeds. Saved edits are not rolled back. A repetition pause instead calls for inspecting `/status` and `/agents`, optionally giving `/steer <direction>`, and explicitly continuing.

Delivered locally on 2026-10-10 with **430 passing Release tests** and a refreshed self-contained Windows x64 build, including the activity and error-display changes below. Published help and isolated JSON status checks passed. Live provider reproduction and physical terminal visual acceptance were not repeated. Full delivery evidence is recorded in the retired CLI implementation record in Git history. Earlier verification records describe the revisions tested at that time.

Steering/worker display: worker activity uses `› Spawn Agent #n`, `› Wait for Agent #n`, and `› Message Agent #n`. Saved steering shows its recipient, sender (You or Parent agent), and a quoted, redacted preview. Subsequent delivery notices reference the same message without repeating its body in compact mode. `/steering` lists previews up to 1,000 characters and full message IDs for removal; verbose activity also shows the expanded bounded preview. These presentation changes do not alter delivery or execution.

## Actionable errors

New tool failures identify the action and target, then show **Reason**, **Effect**, and **Next**, with a stable error code and `/details <call-id>` link. Missing paths, stale hashes, patch conflicts, reserved files, unknown workers, and inactive workers have distinct explanations. Worker assignment errors show the attempted path and assigned exact files: naming a folder does not authorize its descendants.

Worker failures report the observed request/context/time limit where recorded, retained file-operation count, and completion-report availability. `/agent #number` displays these details; `/changes` exposes recorded edits. A failed worker can leave useful edits behind. Uncertain effects retain recovery requirements, and older events do not invent missing causes or edit counts. SQLite storage errors have a distinct category and do not promise that the latest state was saved.

These paths have automated regression coverage. No live session data was changed for validation.

## Compact activity and fetched-content display

The interactive composer shows the selected parent model, reasoning setting and current workflow/permission mode above its status and input rows, for example `gpt-6.1-sol · reasoning: default · Work / Execute`. Smith currently sends no reasoning-effort override, so `default` means the provider's setting; it is not a measured low/medium/high usage tier. The row refreshes for model/workflow changes and resume. Long local model paths show their filename; narrow terminals shorten the model label before the mode. This is included in the local build; physical terminal verification remains pending.

Web-read excerpts and HTTP response bodies now use nested square content blocks with white text on a subtle dark background. Web blocks identify the source, preserve code indentation, and keep wrapped lines and omission notices inside the block. Retained-artifact links remain below it. Plain output keeps the same guides without color.

Compact activity uses one worker-start block with a bounded task description and a vertical assignment list. Worker waits/results identify conversation-local aliases, wait limits use minutes, and completed workers expose their recorded reason and report/transcript commands. Short status badges carry background colors while surrounding text stays white. Routine reads, listings and searches combine their result and call duration on one line; `/display verbose` retains separate call timings. The busy composer identifies the current activity and distinguishes total run time from worker/process duration. The worker-report renderer now handles artifact ID references directly instead of emitting empty rows.

The next readability pass follows three steps: clean and frame process streams, shorten plan/map summaries, then expose direct retained-log navigation. Source implementation strips terminal control sequences across stdout/stderr chunks, groups output in bounded content blocks, and factors repeated directories out of path-only listings. Raw logs remain unchanged. Process completion and exit code stay outside the preview budget. Plan updates show completed/total counts and abbreviated changed steps; `/tasks` preserves full descriptions and verification criteria. Map notices distinguish depth/scan budgets, excluded entries, scan issues and preview omissions.

While idle, open a specific process log with `/artifact process:#15`, search it with `/artifact process:#15 search error`, or page it with `/artifact process:#15 read <cursor>`. Process aliases use the saved conversation's numbering and survive restart. Ordinary artifact IDs remain supported; the next-page command uses the returned character cursor. No new artifact numbering or schema migration is needed. Regression coverage includes fragmented control sequences, path-only shortening and log access.

## Live steering and worker messages

Delivered locally on 2026-10-10: **401 tests passed** across the core and CLI suites. The Windows x64 publish folder is refreshed. Bonsai parent/worker steering, wait wakeup and crash/restart delivery were exercised; ChatGPT live steering remains unverified.

Redirect the current run without adding another queued task:

```text
/steer Stop editing the renderer; investigate the input bug first.
/steering
/steering remove <message-id>
/agent #1 message Focus on edge cases in the assigned file.
```

Ordinary text submitted while busy still queues a separate follow-up. Steering is text-only: up to 32 KiB UTF-8 per message and twenty pending messages per target. Pasted slash commands stay literal. Rejected submissions preserve the composer draft. `/status` and the running composer show pending counts.

**Steering saved** means the inbox commit succeeded. **Added to next request** means it was added to the current run's history. **Sent to model** means inference began returning events for that request; it does not prove compliance. `/steering` shows pending/incorporated messages and recent sent messages for the parent and its workers. Only pending messages can be removed.

An in-flight response or started tool finishes normally. At the next safe boundary, remaining unstarted calls receive correlated skipped results before the steering is added. Process/worker waits wake promptly with their current status; steering itself does not stop the process or worker. The parent can also use `agent_message(agentId, text)` for an active owned worker. Parent directions are labeled separately from direct user instructions, and neither can expand worker assignments or permissions.

Steering never answers a question, approves a plan, restarts a worker, or resumes paused work. Saved messages remain tied to their original run after cancellation, failure or restart. Use the existing explicit continuation/decision controls. Messages held behind a committed finalization or abandoned run remain visible and cannot leak into a new task. Original task and incorporated steering stay in context through compaction.

Delivery and observed verification are recorded in the implementation record (in Git history).

## Direct web images and longer worker waits

Delivered locally on 2026-10-10 after the running agent exited: **376 Release tests passed** (278 core/tool, 98 integration). The self-contained Windows x64 build includes direct web images and the three-minute worker wait. Published help and assembly hash checks passed. Live model inspection of a remote image remains unverified.

`image_read` now accepts either a workspace path or a direct public image URL in its existing `path` argument, for example `{"path":"https://example.com/screenshot.png"}`. Ask Smith to inspect the image URL normally. A vision-capable parent or worker receives the decoded image through the existing attachment pipeline. This reads the image itself; it does not take a screenshot of a webpage.

Public HTTP/HTTPS on default ports only; no login, cookies, credentials, localhost or LAN access. Up to five redirects are checked, with a 30-second download timeout. PNG/JPEG/BMP only, at most 10 MiB downloaded and normalized, and 32 megapixels decoded. HTML, SVG, GIF and WebP are rejected. The saved snapshot includes source URLs, retrieval time and source hash, and survives restart without another download. Images remain untrusted evidence. URL query strings are omitted from compact activity lines; recognized credential URLs are rejected, without claiming perfect secret detection.

## Image, workspace, and HTTP inspection

Delivered locally on 2026-10-10: **360 Release tests passed**. The self-contained `artifacts/publish/win-x64` build was refreshed, and published help, resume and `/tools` checks passed. This build includes the previously staged web, worker-limit and terminal updates.

Ask normally: “Inspect artifacts/screenshot.png”, “Map this solution and its project references”, or “Check the localhost API and inspect its JSON response”. `/tools` shows availability; `/artifact <id>` reads retained results.

| Tool | Behavior |
|---|---|
| `image_read(path)` | Sends actual workspace image evidence to a vision-capable model, including workers. PNG/JPEG/BMP, 10 MiB input/normalized output, 32 megapixels. Exact paths under `artifacts`, `bin`, and `obj` are allowed; other exclusions and link/traversal checks remain. |
| `workspace_map(path, depth, maxEntries)` | Bounded tree and static `.sln`/`.slnx`/`.csproj` declarations, with a retained searchable map. Zero selects defaults: depth 3, 200 preview entries. No MSBuild execution or semantic symbol analysis. |
| `http_request(method, url, headers, body, timeoutSeconds)` | Unauthenticated localhost/public HTTP and HTTPS. GET/HEAD inspect; POST/PUT/PATCH/DELETE require an Execute parent in Work/Document. Workers cannot send writes. |

HTTP accepts `Accept`, `Content-Type`, `If-Match`, and `If-None-Match` headers. Bodies are UTF-8 text/JSON up to 64 KiB; timeout zero means 30 seconds, maximum 120. Response excerpts are capped at 8 KiB and retained text at 1 MiB. LAN/reserved addresses, credentials, automatic redirects, and binary downloads are unsupported. Recognized secrets are rejected or withheld; detection is not perfect. GET/HEAD are treated as inspection, but the server controls their actual behavior. HTTP writes are blocked while editing workers hold reservations. Interrupted writes without a confirmed response require recovery, are never automatically replayed, and cannot be undone with file checkpoints.

Image bytes remain in immutable conversation-owned attachments; history and JSON events contain references, not base64. Bytes are resolved only when preparing model requests. Maps report coverage limits and conditional/unresolved declarations; use narrower paths and retained artifacts for further evidence.

Observed 2026-10-10: Bonsai2 correctly identified a red square on the left, a blue circle on the right, and `net10.0` using these tools. A read-only Bonsai worker repeated the inspection and the parent collected its report; its estimated pixel coordinates were inaccurate. A separate Bonsai run used GET `/health` and POST `/tokenize`, both returning 200 with expected JSON. ChatGPT live acceptance remains incomplete because account/profile access returns a generic local-state/account error in this environment. See the implementation record for automated checks and delivery status.

A Windows C# coding agent with its own runtime. It supports ChatGPT account sign-in, LM Studio Responses, and local Chat Completions servers such as Bonsai 2 on llama.cpp. AgentSmith can inspect a project, edit files with checkpoints, run builds and tests, and resume saved conversations.

It also supports a separate Plan workflow, persistent task progress, searchable retained artifacts, and structured .NET build/test reports.

Shared long-term memory is enabled by default. The parent agent can save useful findings during normal work and retrieve them in future conversations, including other projects. Entries retain their origin and source; memory does not grant permission or replace current verification.

## Public web research

ChatGPT sessions default to native hosted `web_search` through the existing ChatGPT sign-in connection. No separate OpenAI API key is needed; availability depends on the model and account policy. Parent agents and workers can search in Work, Plan, Document, and Review, including Inspect permission. Ask normally: "Search the official .NET documentation for this API, open the relevant page, and cite the source." Smith shows search progress and converts provider citations into readable source links. Original search records and citation annotations remain in saved history. Native-mode text is displayed when each message completes so its citations can be included.

Use `/web-search` to inspect the setting, or select one of these while idle:

```text
/web-search native   # ChatGPT hosted search; local models use basic search
/web-search basic    # DuckDuckGo search through Smith
/web-search off      # Disable search; web_read and HTTP tools stay available
```

The selection persists per conversation and new workers inherit it. `/status` shows the effective setting. Compaction does not enable search. Smith does not silently switch providers when native search fails; select `basic` or `off` explicitly if the account/model cannot use it. Hosted queries execute at the provider during inference, so Smith's local URL/query checks do not intercept them. Instructions prohibit sending private content in queries; this is not a local privacy filter.

The following limits apply to **basic search and Smith's `web_read`**, independently of native search:

- Search uses [DuckDuckGo's non-JavaScript HTML search](https://duckduckgo.com/duckduckgo-help-pages/features/non-javascript), returning up to five titles, source URLs, and bounded snippets. Queries are sent to DuckDuckGo; prompts instruct the model to use public terms rather than private project/memory content. Recognized credential patterns are rejected before sending, without claiming perfect secret detection.
- Page reads accept public HTTP/HTTPS HTML and text on default ports. A separate client sends no ChatGPT tokens, browser cookies, or user authentication. Scripts and subresources do not run. Private/reserved addresses, credential-bearing URLs, and private redirect destinations are rejected; DNS addresses are checked and the connection uses the validated address.
- Reads are bounded to 30 seconds, five redirects, and 2 MiB decompressed input. Up to 100,000 extracted characters are retained as a conversation artifact; the model initially receives up to 6,000 characters and five links. Artifacts retain the source URL, retrieval time, truncation state, and up to thirty links. Use `/artifacts` and `/artifact <id>` to inspect them; the model can page/search them with artifact tools. Source text is historical evidence, never authorization.
- The lightweight reader extracts the main/article text where available and removes scripts, forms, navigation, and hidden markup. It is not a full browser or a guarantee of complete page coverage. PDF, images, OCR, authenticated pages, JavaScript rendering, and browser automation remain unsupported. CAPTCHA, unsupported responses, timeouts, and unrecognized search layouts return explicit errors. There is no hidden provider fallback.

Previous basic-search verification: a separate PowerShell HTTP probe returned ten DuckDuckGo HTML result links with HTTP 200 and no challenge on 2026-10-10. The earlier C# implementation passed its baseline suite after upgrading AngleSharp to 1.5.0. Native search is a source-only update: request, stream, citation, persistence and policy regression cases were added but have not been compiled or run while the user's agent is active. Live native-search and worker acceptance remain unverified. The current running build is unchanged. See [OpenAI's sign-in limitations](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations) and [web-search contract](https://developers.openai.com/api/docs/guides/tools-web-search).

## Clipboard, context, and larger tasks

The locally published 2026-10-10 milestone adds Alt+V text/image input, model-aware context budgets, longer execution batches, and proactive local delegation. Release verification passed 231 tests. Live ChatGPT/Bonsai image sharing, structured tests, checkpoint undo, queued-image reopening, compaction and a 34-request task passed. Forced delegation increased cloud usage on the tiny comparison fixture; no fixed savings is claimed. Delivery evidence and remaining live checks are recorded in the implementation record (in Git history).

```powershell
smith chat --model gpt-6.1-sol --workers bonsai-edit,bonsai-review --context-budget balanced --max-steps 150 --max-minutes 60
smith run --model gpt-6.1-sol --prompt "Explain this screenshot" --image C:\Images\example.png
```

The saved-work continuation screen is a confirmation, not the chat composer: type `continue` first. Alt+V there displays guidance without reading the clipboard, and `/attach` is rejected until the confirmation is resolved.

Alt+V reads the Windows clipboard only when pressed in the chat composer. Images take precedence over text. Short text inserts at the cursor; multiline text or more than 2,000 characters becomes a paste block. Tab focuses a block, Enter shows its contents, and Delete removes it. Enter from the composer submits explicitly. Pasted slash commands stay message content. Image labels identify attachments; focus shows dimensions and size. Question/review note fields accept text only and never select or submit an answer on paste. `/attach <path>` provides a file alternative. Physical keyboard behavior still needs the manual check noted in the delivery record.

Each prompt allows 32 KiB of aggregate UTF-8 text and four images, each at most 10 MiB after PNG normalization and 32 megapixels. File input also checks the original 10 MiB size. Immutable hashed images live in the data directory; history, events, and queued prompts contain IDs, not base64. Provider payloads materialize image bytes only when sent. Queued images survive restart but stay out of active context until dispatch. A worker sees only submitted images explicitly assigned to it. Unsupported image models fail clearly.

`/context budget balanced|max|<tokens>` changes the idle conversation's working input budget. Balanced uses up to 128,000 tokens, capped by capacity minus output reserve and safety headroom. Compaction starts at 90% and targets 60%. ChatGPT defaults to a 16,384-token output reserve; local inference retains 8,192. GPT-6.1 Sol's published 1,050,000-token capacity is catalog evidence, not proof of the account route's effective limit. Bonsai2's current loaded capacity is 32,768, not its training context. Unknown capacities retain the 120,000-character fallback unless an explicit token budget is configured. `/context` identifies the source, estimate/count method, reserves, categories, and summary provenance.

The local adapter counts the chat template and tools with `/apply-template` and `/tokenize`; images use a separately labeled estimate. The ChatGPT adapter probes the documented counting endpoint and caches unsupported responses, then uses conservative estimates calibrated against reported input. Large process results, listings, Git output and diffs use bounded model-facing excerpts and retained artifacts. Full saved history remains unchanged. Cumulative `/usage` input includes history sent repeatedly; it is not current context occupancy.

`/limits` shows the batch policy; `/limits 200 90` sets 200 requests and 90 minutes while idle. Defaults are **150 model requests and 60 minutes** per explicitly started/continued batch, including compaction requests. Request/time pauses direct you to `/limits`; context pauses direct you to `/context` and `/compact`. Three identical non-waiting tool rounds pause for lack of progress. Questions, reviews, cancellation, recovery and queue holds still require explicit action.

Enabled-worker conversations default to `/delegation proactive`; use `--delegation manual` or `/delegation manual` to delegate only when requested. Suitable tasks include investigation, report diagnosis, focused edits, test writing, patch review, documentation and shared screenshot analysis. Parents retain integration, test execution, and compaction. Workers cannot execute processes or approve decisions. Worker admission follows the saved concurrency limits (new chats default to six total and one local), with 16 launches per parent run and a default 100 requests/30 minutes per worker, within the parent deadline. A one-slot endpoint serializes inference and displays waiting status. `agent_wait` supports up to six minutes (360,000 ms), returning early when a selected worker finishes or the run is cancelled. `process_wait` remains capped at 60 seconds. Waiting itself makes no extra model calls. `/usage` distinguishes purpose, outcome, waiting, reported cached input and compaction overhead; local tokens are not exact cloud savings.

Schema v7 creates a consistent v6 backup and persists attachment records plus policy/capability/grant metadata. Migration preserves saved decisions and held queues. No local server reconfiguration, automatic model loading, background inference, commit or public release is part of this milestone.

## Shared memory

```text
/memory                         List entries and enabled state
/memory list 2                  Next page (10 entries per page)
/memory search <text>
/memory show <id>
/memory add <text>
/memory edit <id> <text>
/memory forget <id>
/memory on
/memory off
```

The same actions work outside chat, for example `smith memory search "Release build"` and `smith memory add "Use Release configuration for performance measurements."`. They use the selected `--data-dir`; each directory has one library shared across all its workspaces, conversations and providers. Turning memory off disables automatic retrieval and model memory tools at subsequent request boundaries. Explicit user commands remain available. Read-only commands work while busy; changes in a running chat wait until the agent is idle.

The parent can use `memory_search`, `memory_read`, `memory_save` and `memory_update` without extra approvals in Work or Plan, including Inspect mode. Workers can read/search and report learnings to the parent; they cannot save. Entries use validated `user:<history index>` or `tool:<completed call ID>` evidence references. A collected worker report is an agent conclusion, not proof of verified behavior. Memory updates use revision checks. User additions and corrections are protected from automatic updates.

Each entry has a title up to 120 characters, a body up to 2,000 characters and at most eight tags. Literal FTS5 search ranks relevance, then matching origin workspace and recency. Requests include at most five entries within 6,000 estimated characters, with provenance shown in `/context` and counted in the normal context budget. Selection stays fixed during a compaction attempt. No embedding service, extra extraction request, background learning or historical backfill is used; saving depends on the agent's normal tool choices.

`forget` removes active retrieval and search results, not original conversations, revision records or backups. Historical material already present in a conversation remains history. Known credential patterns and terminal controls are rejected before memory storage; detection cannot recognize every possible secret. Recognized credentials in proposed memory-call arguments are also removed before those calls enter saved protocol history. Memory read failures warn and allow work to continue without retrieved memory.

The v6 migration makes a SQLite-consistent v5 backup and starts with an empty library. The memory delivery record (in Git history) records verification and live acceptance.

Verified and locally published on 2026-10-09: **219 automated tests passed** plus live ChatGPT saving, cross-project recall, worker read-only access, corrections, forgetting and disabled-memory checks. Published command checks passed. The live worker used ChatGPT; local-provider memory inference was not separately exercised. The normal data directory starts with memory enabled and no imported fixture entries.

The CLI also includes compact tool activity with argument summaries, automatic context compaction, Escape cancellation, and a durable FIFO prompt queue. A parent agent can delegate small tasks to explicitly enabled worker profiles using different models. See the historical delivery and acceptance record (in Git history).

## Build and run

Updated local build (2026-10-09): compaction aims for a shorter summary and retries an oversized response once per chunk within the existing eight-request limit. If it still fails, use `/compact`; after success, type `continue`. Repeated `continue` does not restart a context-blocked run. Saved history and checkpoints are preserved. Curated Git inspection also supports a workspace inside a larger repository, restricting file results to that workspace; status paths remain repository-relative. All 194 automated tests passed across the two suites after correcting two test-only issues. The self-contained Windows publish folder was refreshed, including `/usage`, and the published help and saved usage reports were checked. Live model compaction on the reported session has not been rechecked.

The repository pins .NET SDK 10.0.401. From the repository directory:

```powershell
dotnet build AgentSmith.slnx
dotnet test AgentSmith.slnx
dotnet run --project src/AgentSmith.Cli -- auth login
dotnet run --project src/AgentSmith.Cli -- models
dotnet run --project src/AgentSmith.Cli -- chat --workspace . --model <model-id>
```

Use the model ID printed by `models`. Sign-in opens your system browser and waits for explicit consent. For ChatGPT, an eligible account and ChatGPT-plan permission are required. LM Studio conversations require neither ChatGPT sign-in nor account credentials. A valid identity alone does not enable inference. No API key or existing Codex credentials are used.

Build the self-contained Windows executable:

```powershell
dotnet publish src/AgentSmith.Cli -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64
.\artifacts\publish\win-x64\smith.exe --help
```

Keep the entire publish directory together; this is a self-contained folder deployment, not a single-file executable.

## Bonsai 2 on a local Chat Completions server

Use `--provider local` for the server at `http://127.0.0.1:8080/v1`. This adapter discovers models through `/v1/models` and streams `/v1/chat/completions`; it does not require an API key. Both an origin and a base URL ending in `/v1` are accepted. The endpoint is loopback-only, with no automatic retries or provider fallback.

```powershell
$model = (Invoke-RestMethod http://127.0.0.1:8080/v1/models).data[0].id
smith models --provider local
smith chat --provider local --model $model
smith workers add bonsai-edit --provider local --model $model --mode edit
smith workers add bonsai-review --provider local --model $model --mode inspect
smith chat --model gpt-6.1-sol --workers bonsai-edit,bonsai-review
```

The configured `bonsai-edit` and `bonsai-review` profiles now use `E:\Projects\AgentSmith\bonsai\models\bonsai2-gguf\27B\Ternary-Bonsai-2-27B-PQ2_0.gguf`. Start a new parent conversation to use the updated profiles: existing conversations retain their original profile snapshots and provider. ChatGPT remains the default parent provider.

The running server reported 32,768 loaded context tokens and one inference slot on 2026-10-09. AgentSmith can schedule two workers, but this server may service their requests serially. Its loaded context is distinct from the model's 262,144-token training capacity; the working budget uses the loaded value. The default output budget remains 8,192 tokens; a server context rejection pauses/fails the request without changing history or switching providers. The CLI now accepts image attachments when the selected model advertises vision support.

The adapter preserves streamed tool-call IDs and arguments, sends results back as tool messages, keeps separate reasoning out of answer text, and requires a successful finish plus the stream terminator before dispatch. Resume, workers, and compaction use the same persisted provider selection. See the Bonsai 2 delivery record (in Git history).

The endpoint update passed 176 automated tests in Release. The Windows build was refreshed and its discovery/profile commands verified. A real local-only Bonsai 2 run read a file with `file_read` and quoted its contents in 9.3 seconds. A ChatGPT parent also delegated a checkpointed fix to Bonsai 2, collected its report, and independently verified the edited file; unrelated fixture files retained their hashes. A separate read-only worker completed successfully.

Worker profiles belong to the selected state directory. If chat reports an unknown profile, run `smith workers list --data-dir <path>` in the same terminal. The listing and error include the effective directory; register profiles with `workers add --data-dir <path>` and use that same directory for chat. An empty listing means the profiles must be created there, even if another invocation can list them. AgentSmith does not silently import profiles from another store.

## GPT workers and configurable parallelism (source update; build pending)

GPT workers use the existing ChatGPT account route. Register profiles in the same data directory as chat:

```powershell
smith workers add gpt-edit --provider chatgpt --model gpt-6.1-sol --mode edit --max-output-tokens 16384
smith workers add gpt-review --provider chatgpt --model gpt-6.1-sol --mode inspect --max-output-tokens 16384
# After the next build:
smith chat --model gpt-6-luna --workers bonsai-edit,bonsai-review,gpt-edit,gpt-review --max-workers 6 --max-local-workers 1
```

Use a model listed by `smith models`. Multiple tasks can use the same profile concurrently; profile names describe capabilities, not dedicated slots. The parent prefers enabled local profiles for routine work and enabled GPT profiles for difficult analysis, implementation, or review. The guidance above makes useful parallel work the first criterion, before choosing a profile. It starts independent tasks before waiting and remains responsible for integration and verification. Proactive delegation does not spawn unnecessary tasks just to fill capacity.

New chats default to six active workers total, with at most one local worker inside that total. Both settings accept 1..16; the parent is excluded. The total limit applies to all providers combined, so six GPT workers may run when no local workers occupy slots. `/worker-limits` shows limits; `/worker-limits 8 1` changes them while idle. `/status` also shows the saved policy. Sessions without a saved worker policy default to two total/one local. Explicitly saved limits are preserved; use `/worker-limits <total> 1` while idle to lower an existing local limit. Sixteen launches per run and existing worker deadlines still apply. Endpoint capacity continues to govern local inference; raising worker limits does not increase local server slots.

GPT requests can overlap, but share account allowance and server limits. More parallel workers do not imply lower token usage. Workers belong to the CLI instance: they run alongside the parent, stop on exit/cancellation, and are not detached background daemons. File reservations, process barriers, Plan/Review restrictions, and result collection still apply.

Source changes and regression cases were initially held on 2026-10-10 while the agent was working. They were subsequently compiled, tested and locally published with the image/map/HTTP delivery above. Profile registration through the installed executable failed with the generic account/state error, including `auth status`; the two GPT profiles were not created by that attempt. Run the registration commands in the working user terminal. Live parallel GPT acceptance remains pending.

## LM Studio and worker profiles

Start LM Studio's local server and load a tool-trained model. The default endpoint is `http://127.0.0.1:1234`; `--endpoint` accepts another loopback HTTP origin. AgentSmith neither downloads nor loads models automatically, sends no ChatGPT credentials to the server, and never silently falls back to a cloud model.

```powershell
smith models --provider lmstudio
smith chat --provider lmstudio --model prism-ml/bonsai-27b
smith workers add bonsai-edit --provider lmstudio --model prism-ml/bonsai-27b --mode edit
smith workers add bonsai-review --provider lmstudio --model prism-ml/bonsai-27b --mode inspect
smith workers list
smith chat --model gpt-6.1-sol --workers bonsai-edit,bonsai-review
smith workers remove bonsai-review
```

Worker profiles support ChatGPT too: use `--provider chatgpt --model <available-id>` and optionally `--account <registration-id>`. Only profiles named in `--workers` are enabled. Each conversation keeps its profile snapshots; changing or deleting the configured profile does not alter existing tasks. New conversations still default to ChatGPT. A saved conversation retains its provider on resume. `--max-output-tokens` defaults to 8192 for local inference; server reasoning defaults remain unchanged. Loaded capacity, usable input budget, and observed or estimated request tokens are reported separately.

The parent uses the saved worker concurrency limits (see the pending parallelism update above), at most sixteen launches per run. Each worker has twenty model rounds, a ten-minute deadline, and the parent's remaining deadline. Workers have independent sessions and can read the workspace and edit only exact assigned files. They cannot run processes, change plans, ask the user directly, or spawn more workers. The parent owns final verification and durable questions. Plan workflow allows read-only delegation.

Exact file reservations reject overlapping assignments, parent edits and undo against reserved files. Moves require both paths. Existing conversation processes must stop with confirmed termination before editing workers start; parent process/build/test launches are blocked while editing reservations remain. These are AgentSmith coordination rules, not an OS sandbox or a lock against other applications. External edits remain protected by hash checks. Full path names are required; Windows short-name aliases are rejected for reservations.

Use `/agents`, `/agent <id|#number>`, and `/agent <id|#number> cancel`. `/changes` includes child checkpoints; `/undo <checkpoint-id>` applies the usual conflict checks. Collected worker reports and transcripts are available through `/artifacts` and `/artifact`. To share a registered artifact with a worker, the parent includes `[artifact:ID]` in its task; up to sixteen explicit read-only grants allow paging and searching retained evidence without copying a short excerpt. Unregistered/cross-conversation IDs are rejected.

Parent completion and queued follow-ups wait for worker completion and report collection. Escape or `/cancel` cancels the task tree and holds the prompt queue. Completed edits remain in checkpoints. After a crash, children become interrupted and never restart automatically. Uncertain edits retain reservations for `/recover` or explicit `/recover abandon`. Resume the root conversation; children are inspectable but cannot be resumed independently.

Schema v5 adds profiles, provider identity, child sessions, lifecycle/spawn receipts, reports and reservations. `sessions-v4-*.backup.db` is a SQLite-consistent pre-migration backup. Existing sessions remain ChatGPT sessions with no enabled workers.

Verification on 2026-10-09: **158 automated tests passed**, and the self-contained Windows x64 publish folder was refreshed. Live acceptance covered:

- A ChatGPT parent delegated two file fixes to Bonsai. Both workers completed in four model rounds and produced checkpoints; the parent obtained a successful build and 5/5 passing tests.
- The unrelated edit survived. After restart, artifact reads and child undo succeeded; only the unrelated diff remained.
- A local-only Bonsai conversation read and answered from a file. Plan delegation, explicit clarification routing, cancellation, and queue holds/FIFO release also passed.
- Local compaction reduced supplied fixture-history context from 48,715 to 15,626 estimated characters while preserving source history.

Earlier attempts on compact one-line code produced rejected patches and worker limits. Tool-use metadata does not guarantee model reliability. See the acceptance record (in Git history) for evidence and test limitations.

## Commands

### Cloud and local token usage

Use `/usage` inside a chat, including while work is running. For a closed conversation, use `smith usage <conversation-id>` (and the same `--data-dir`, if applicable). Add `--json` for structured totals and per-parent/worker rows.

The report shows ChatGPT input/output tokens, local input/output tokens, and the subset handled by local workers. It reads saved completion events across all runs and child sessions, so it survives restarts and works for existing recorded conversations. Recorded successful compaction usage is included and identified separately. It does not call a model or change pending questions, reviews, or queued prompts.

**Local worker tokens measure work handled locally, not exact cloud tokens or money saved.** Models use different tokenizers and prompts, and delegation adds cloud coordination overhead. Cached input and nested usage attribution are already part of the reported top-level counts and are not added again. Failed/cancelled runs still contribute completed requests with usage. Missing usage, unfinished/failed requests, and failed compaction attempts are shown explicitly; their unreported consumption is not guessed.

### CLI reference

```text
smith auth login                      Register another ChatGPT account/workspace
smith auth login --account <id>       Reauthorize a saved registration
smith auth status
smith auth accounts
smith auth use <id>
smith auth logout                     Clear active account tokens; attempt revocation
smith models
smith chat --workspace <path> --model <id> --mode execute
smith chat --plan --workspace <path> --model <id>
smith run --workspace <path> --model <id> --mode inspect --prompt "Find the entry point"
smith resume                         Open the most recently updated session
smith resume <conversation-id>       Open a specific session
smith sessions
smith inspect <run-id>
smith usage <conversation-id>
```

Global options: `--data-dir <path>`, `--json`, `--no-color`, `--provider chatgpt|lmstudio|local`, `--endpoint <loopback-http-url>`, and `--max-output-tokens <count>`. Provider options configure model discovery, new conversations, and worker profiles; resume uses the saved connection. Local Chat Completions defaults to port 8080 with `/v1`; LM Studio defaults to the port 1234 origin. ChatGPT remains the default provider. See the worker examples above for `--workers` and profile management.

With no ID, `smith resume` selects the most recently updated parent conversation in the selected data directory, across workspaces. Worker sessions are excluded. An empty store reports how to start a chat; a locked or unavailable latest session reports its error rather than opening an older session. Pending questions, reviews, held queues and unfinished-work confirmation still require explicit input.

After replaying recent messages, resume shows a saved-state panel with the last run's status, recorded execution time, current workflow, and next action. Document/Review tasks include their saved report summary, coverage, limitations, and the command to read the full report. Completed sessions explicitly say that no continuation is required; pending work and held follow-ups remain separate. This panel reads persisted state and does not execute tools or infer completion from earlier assistant messages.

Interactive Windows sessions play the Windows notification sound and prefix the console title with `[!]` when waiting for a question, a plan-review decision or permission choice, a resume confirmation, or a new message. Each distinct request alerts once; picker redraws, typing, and invalid answers do not repeat it. The prefix clears when work starts, and the original title is restored on normal exit. The busy follow-up composer does not alert while the agent is working. Sound follows Windows sound settings; title overrides in the terminal can hide title updates. JSON and redirected input/output produce no sounds or title changes. `--no-color` does not disable notifications.

Normal terminal output uses white body text with green accents, square message borders without spacer rows between messages, a compact workspace header, model/account lists, readable conversation history, brief tool activity, and a completion summary. Context manifests and detailed usage stay out of the conversation; use `/context`, `inspect`, or `--json` for diagnostics. `NO_COLOR` also disables color. Durations of a minute or more use minutes and seconds (for example, `8m 03s`); short calls retain fractional seconds. Redirected output uses the same readable layout without ANSI styling; `--json` retains the original machine event format.

Message blocks have a vertical guide. Section guides use workflow colors: Work is green, Plan cyan, Document purple, and Review amber. Saved reports retain their workflow color. New message workflow tags survive restart; older messages without a recorded workflow use gray guides. These display tags are separate from provider history. Each active response segment shows elapsed execution time, including responses that pause for a question. Timing includes model and tool work and excludes time spent waiting for an answer; continuing after an answer starts a new segment. Historical messages are separated in the same way, but do not claim timings that were not saved.

`/help` shows common commands and examples. Use `/help workflows`, `/help session`, `/help tools`, `/help queue`, or `/help settings` for a topic, and `/help all` for the complete command list. Unknown slash commands and help topics are rejected locally.

Use `/mode` to see both the workflow and permission level. `/mode work|plan|document|review|memorize` selects a workflow without starting inference; type your task afterward. `/mode planning` and `/mode documentation` are aliases. `/mode inspect|edit|execute` changes permissions without bypassing workflow restrictions or plan approval. `/work` returns to Work, subject to the same unfinished-work checks as leaving a workflow.

To start immediately, use `/plan Make a plan to implement player movement`, `/document Update the installation instructions`, or `/review`. `/plan new <task>` remains supported; `/plan` alone shows the saved plan. Reserved plan commands are `new`, `show`, `read`, `revise`, `implement`, and `leave`; use `/plan new <task>` if your task begins with one of those words. `/mode plan <task>` and `/mode document <task>` also start tasks immediately; `/mode review --path <path>` starts a scoped review. `/memorize [--full|--path <relative-path>]` starts repository memorization; `/memorize status` reads saved status without inference. Existing `/workflow`, `/plan leave`, and permission commands remain available.

- Conversation and display: `/status`, `/usage`, `/context`, `/compact`, `/paste`, `/markdown`, `/display compact|verbose`, `/details <call-id|process-alias>`, and `/exit`.
- Tools and recovery: `/tools`, `/mode`, `/changes`, `/undo <checkpoint-id>`, `/processes`, `/stop <process-id|#number>`, and `/recover [abandon]`.
- Planning and evidence: `/plan`, `/tasks`, `/artifacts`, and `/artifact <id> [search <text>]`.
- Workers and follow-ups: `/agents`, `/agent <id|#number> [cancel]`, `/queue [remove <id>|clear|continue]`, and `/cancel`.

Finish multiline paste with `/end`. Ctrl+C, Escape, and `/cancel` cancel active work, stop processes started by that run, and settle its workers. Submit follow-ups in the running composer to queue separate runs. Cancellation holds the queue until `/queue continue`. Pending questions and reviews still require explicit decisions; see the continuous-conversation section for `/answer` and `/review` routing.

## Plan and task tracking

Workflow and permissions are separate. **Work** is the default workflow; new conversations still select Execute permissions. **Plan** permits inspection, questions, artifact reading, and internal plan updates. It disables workspace edits, undo, arbitrary processes, and build/test launches, even if `/mode execute` is selected. Entering Plan stops the conversation's running processes.

```text
/plan new <task>            Investigate a new task in Plan workflow
/plan show                  Display the current saved plan
/plans [page]               Find plans saved for this workspace across conversations
/plan read <id>             Read another saved plan without activating it
/plan revise <instruction>  Revise the active plan
/plan implement             Open the implementation review picker
/plan leave                 Return to Work without approving a draft
/tasks                      Display step statuses and verification notes
```

Plans are saved in AgentSmith's SQLite state, not automatically written into the workspace. They retain revisions, stable step IDs, verification criteria, notes, references, and approval history. At most one step is In progress. Finishing a response does not automatically complete its tasks.

Directory lookup: new plans record the normalized workspace directory where they were created and their creation time. `/plans` lists ten plans per page from the same exact directory and Smith data directory; `/plan read <id>` shows the latest revision with its source conversation. Windows path case and trailing separators are normalized; parent, child, and sibling directories are separate. These reads work while busy. Older plans use their saved conversation's workspace, explicitly marked as inferred; their creation time remains unknown. Lookup does not rewrite old history.

Parent agents and workers can use `plan_list(page)` and `plan_fetch(planId)` to find earlier plans before substantial work. Fetching provides read-only evidence: it does not activate a plan, transfer approvals, grant artifact access, or allow edits to another conversation's plan. Current user instructions and permissions remain authoritative, and files must be checked again. Resume the source conversation to update its plan. Directory lookup is implemented in source but has not been built or live-verified yet; the running executable remains unchanged.

`plan_submit` requires Plan workflow and no unresolved recorded questions. In Work, `plan_update` tracks progress without a review gate. Invalid submissions and stale/invalid updates are rejected before execution, so they do not create an uncertain-mutation recovery state. For an older run stopped on a rejected `plan_submit`, exit and resume with the updated build: recovery records a missing submission receipt as not committed, preserving the draft without replaying or approving it. Continue only when explicitly requested; genuine file/process uncertainty still requires recovery.

The agent uses `plan_read`, `plan_update`, and `plan_submit`. A submitted plan pauses tool dispatch and presents **Implement / Revise / Keep planning**. Implement then requires an explicit **Edit or Execute** selection. Revise accepts a note. Nothing is selected or approved on a timer. EOF, Escape, or restart leaves the decision pending. Redirected/JSON input accepts `implement edit`, `implement execute`, `revise <note>`, or `keep planning`; typing only `implement` opens the permission selection without approving.

Approval is tied to the displayed revision and permission mode. Implementation starts a new run and refreshes file evidence. Queued calls from the planning response are closed without executing. A saved decision interrupted before its new run starts requires explicit `continue` after restart.

Routine progress can change steps within the approved scope. In `plan_update`, null goal/deliverables/constraints preserve those fields verbatim. Changing them produces a draft and disables implementation; a resolved draft goes back to review. Increasing the implementation permission ceiling also requires review. Ordinary Work conversations can track tasks without a plan-approval gate.

Use `/recover abandon` before `/plan leave` when a planning run remains unfinished. Leaving does not approve or implement the draft. Planning uses the same automatic compaction and bounded context policy as Work; pending decisions cannot be summarized away or approved by compaction.

## Artifacts and .NET verification

`artifact_list`, `artifact_read`, and `artifact_search` access only artifacts registered to the current conversation. `/artifacts` lists them; `/artifact <id>` displays the first section; `/artifact <id> search <text>` searches literally and case-insensitively. Ask the agent to continue reading with the returned cursor. Process logs are decoded with stdout/stderr labels, including searches across chunks of the same stream. Read/search responses are bounded to 32 KiB, with up to 100 search matches. Truncated, missing, corrupt, and unavailable content is reported explicitly. In-progress logs expose complete records only.

Structured verification tools:

- `dotnet_build(project, configuration, framework, timeoutSeconds)` launches a build with a bundled MSBuild diagnostic logger.
- `dotnet_test(project, configuration, framework, filter, timeoutSeconds)` launches VSTest and retains TRX reports.
- `verification_result(verificationId)` returns compiler locations, test outcomes, failure details, exit code, duration, report completeness, and artifact references.

Build/test launches require Execute in Work workflow. They return process and verification IDs; poll the process to exit, then read its verification result. Empty configuration selects Debug, empty framework selects all frameworks, empty filter selects all tests, and timeout 0 uses the existing ten-minute default. They use the same Job Objects, cancellation, process limits, and ownership rules as generic process tools.

This version supports the .NET 10 SDK and VSTest, including xUnit. Explicitly detected Microsoft.Testing.Platform configurations are rejected for structured tests; use process tools for unsupported runners or SDKs. AgentSmith does not change project configuration to enable a runner. Build diagnostics come from MSBuild events, not guesses from console text. Test reports have invocation-specific prefixes and retain separate project/framework results. Missing or interrupted reports and zero discovered tests never count as passing verification. Reports are parsed with bounded input and XML external entities disabled.

Artifacts include retained process logs, diagnostic JSONL, TRX, and normalized verification JSON. Complete normalized reports remain local artifacts; only bounded excerpts enter model context. Full details: [MSBuild logger contract](https://learn.microsoft.com/en-us/visualstudio/msbuild/build-loggers?view=visualstudio) and [VSTest reporting](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test-vstest).

Markdown display is enabled by default for assistant responses and resumed assistant history. Headings, quotes, lists, bold/italic text, inline code, fenced code blocks, and links have terminal formatting. Text wraps inside the message guide. This is a lightweight Markdown subset: tables and HTML remain text, and code blocks have a distinct style without language-specific syntax highlighting. Responses render one complete line at a time; the final line is flushed when the response stops. Raw responses and `--json` events remain unchanged.

Use `/markdown off` to display Markdown source or `/markdown on` to restore formatting for subsequent output. `/markdown` reports the current mode. Slash commands are handled locally, including while answering a pending question: unknown commands and invalid arguments show an error and never invoke the model or enter conversation history. Use `/paste` at the chat prompt for a literal message beginning with `/`; finish with `/end` to submit. EOF discards an unfinished paste.

An agent question is persisted and waits for an explicit nonempty answer. Empty input never selects a choice. EOF leaves it unanswered. A one-request `run` stops with exit code 3 if input is required; use `resume` to answer. Resume prints saved history. For other unfinished runs, type `continue` to proceed, `cancel` to abandon, or leave the prompt to retain the state.

In an interactive terminal, questions show a keyboard picker: use arrow keys to move and Space or Enter to select. Tab moves between the options, note field, and submit button. Add an optional note, or type a custom answer without choosing an option. Enter in the note field or on the submit button sends the answer. Escape or Ctrl+C leaves the question pending for resume; unsent notes are discarded. Redirected input, `--json`, and very small terminals retain line-oriented answers. Nothing is submitted on a timer.

New and resumed chats show “Remaining usage unavailable” and a link to [ChatGPT usage settings](https://chatgpt.com/settings/usage). The documented sign-in API does not currently provide a remaining plan percentage; per-request token counts are not an allowance estimate.

| Exit | Meaning |
| --- | --- |
| 0 | Command/run completed; evidence remains separate from task success |
| 1 | Failed run or operational error |
| 2 | Invalid configuration, unavailable account, or rejected operation |
| 3 | Explicit user input/continuation required |
| 4 | Run paused at a limit |
| 5 | Tool outcome requires recovery |
| 130 | User cancellation |

Parser errors are reported by System.CommandLine and may use its own nonzero exit code. `--json` command events use one JSON object per line on stdout; help and parser diagnostics are ordinary command-line output. Sequence numbers apply to the current CLI process, while SQLite has its own durable event order.

## Current tools and limits

New conversations default to **Execute**. Existing schema-v1 conversations migrate to **Inspect**. Set `--mode inspect|edit|execute` on `chat` or `run`; use `/mode` to show the current mode or `/mode edit` to change it. `/tools` lists registry definitions and which are enabled. Dispatch is serial, and only fully received, persisted model responses can cause tool execution.

| Mode | Tools |
| --- | --- |
| Inspect | Workspace reads, questions, and curated Git inspection |
| Edit | Inspect plus file creation, exact patches, moves, and deletion |
| Execute | Edit plus process start, poll, stdin, and stop |

Execute runs programs as the current Windows account. Its working directory is **not an OS sandbox**. Enabled tools run without repeated approval prompts. Downgrading from Execute stops the conversation's processes.

- `workspace_list`: up to 200 files, excluding `.git`, `bin`, `obj`, `node_modules`, `.vs`, and `artifacts`.
- `file_read`: UTF-8 text, at most 256 KiB/file and 200 requested lines; includes the full-file SHA-256 hash even for partial reads.
- `text_search`: literal case-insensitive search, up to 100 matches; skipped large/binary/unreadable files are counted.
- `user_ask`: explicit answers only, with optional choices and free-text input.
- `file_create(path, content)`: create without overwriting; can create missing parent directories.
- `file_apply_patch(path, expectedHash, patch)`: exact single-file unified patch. Stale hashes and mismatched context are rejected; no fuzzy matching.
- `file_move(source, destination, expectedHash)`: move without overwriting.
- `file_delete(path, expectedHash)`: delete one file, never a directory tree.
- `git_status`, `git_diff(staged)`, `git_log(limit)`, `git_show(revision)`: bounded repository inspection. External diff, text conversion, fsmonitor, optional index writes, and pagers are disabled. Open the repository root as the workspace.
- `process_start(executable, arguments, workingDirectory, environment, timeoutSeconds)`, `process_poll(processId, cursor, waitMilliseconds)`, `process_input(processId, text, closeStdin)`, and `process_stop(processId)`.

File mutations preserve UTF-8 BOM and consistent line endings. Mixed line endings, binary data, and other encodings are rejected. Each operation is journaled independently; there is no multi-file transaction. `/changes` displays checkpoint diffs separately from Git's repository-wide changes. `/undo <id>` journals a reverse operation and refuses to overwrite newer changes. Only empty parent directories created by that operation are removed. **Checkpoints cover file tools, not arbitrary process side effects.** Undo never uses Git reset or clean.

Process arguments are an array, not an implicit shell command. For PowerShell, use `pwsh` with `['-NoProfile', '-Command', '<script>']` or `['-NoProfile', '-File', '<path>']`. Environment overrides are an array of `{name, value}` objects; an empty array inherits the CLI environment. AgentSmith does not inject its OAuth credentials. Use timeout 0 for the ten-minute default; the maximum is one hour. Up to four processes may run per conversation.

Start returns within one second with a process ID, observed status, and output cursor. Poll waits at most ten seconds and returns at most 32 KiB. Continue polling while `hasMoreOutput` is true, including after exit. stdout and stderr retain their identities, but their cross-stream ordering is not guaranteed. Logs retain at most 20 MiB per process; further output is drained and marked truncated. Check the exit code and termination confirmation before reporting success.

Processes may continue between turns in the same CLI. Windows Job Objects contain each process tree: launch is suspended until assignment succeeds, and closing or crashing the CLI kills its jobs. There is no process reattachment or daemon. Full terminal emulation is deferred; programs must support redirected streams.

Paths are workspace-relative. Links and junctions are rejected, including in the workspace's ancestor path. Traversal is bounded to 10,000 entries and 20 nested levels. Root guidance is bounded to 12,000 characters; descendant AGENTS.md guidance accompanies file results. This is project context, not permission to run commands. File access is not a sandbox against another process deliberately racing filesystem changes.

The runtime defaults to 150 requests and 60 minutes per explicitly started or continued batch. Compaction shares these budgets. Model-aware context budgets are described above; unknown-capacity sessions retain conservative character accounting. Required context is never silently discarded. Responses streams are bounded; incomplete streams never dispatch their proposed tools.

## Storage and recovery

Default state: `%LOCALAPPDATA%\AgentSmith`. Use a separate `--data-dir` for experiments and tests. ChatGPT receives the conversation and file excerpts needed for inference; local storage does not mean local inference.

- `sessions.db`: schema v5 stores conversations, permissions/workflow, plans/revisions/reviews, idempotency receipts, artifacts and verification records, runs, protocol items, tool calls/results, questions, ordered events, mutation journals, original/result bytes and hashes, file metadata, and process records.
- `sessions-v1-*.backup.db`: SQLite-consistent backup made before transactional v1-to-v2 migration.
- `sessions-v2-*.backup.db`: SQLite-consistent backup before v3. Existing sessions retain their permission mode and migrate to Work workflow.
- `sessions-v3-*.backup.db`: SQLite-consistent backup before v4 adds context snapshots/attempts, queued prompts and holds, and process display aliases. Queue rows are independent of active-run session saves.
- `artifacts/<conversation-id>/<process-id>.jsonl`: retained process output with stream identity and cursors.
- `credentials.dpapi`: account registrations and tokens protected with current-user Windows DPAPI.
- `locks/`: exclusive conversation ownership. Credential operations also use an exclusive file lock to serialize refreshes.

Tokens are excluded from ordinary logs and account listings. Session history can contain private user messages and workspace excerpts; no automatic retention deletion is implemented. Do not commit or share the state directory.

Completed results are reused after restart. File operations interrupted around the effect/result boundary are reconciled against recorded pre/post states. Conflicting states remain unresolved. Uncertain process launches and stdin writes are never repeated. Old CLI process records become interrupted, with retained logs and no claim of successful completion. `/recover` shows unresolved state; `/recover abandon` explicitly acknowledges uncertainty and closes unfinished work. Abandonment does not undo side effects. History inspection never executes tools. Partial streamed text is displayed but is not retained as a completed assistant message after interruption.

Conversations remain bound to their saved account registration and model. `auth use` affects new conversations. `auth logout` retains registration metadata for future reauthorization, clears local tokens, and reports whether remote revocation succeeded.

## Verification

Readable/Continuous Conversations validation on 2026-10-09: **131 tests passed** in Release. Live ChatGPT acceptance covered automatic compaction, FIFO follow-ups, explicit question/review barriers, editing with passing build/tests, artifact search, Escape cancellation, crash cleanup without replay, restart, and checkpoint undo. The self-contained Windows build in `artifacts/publish/win-x64` was refreshed and smoke-tested. Start a new `smith` process to use it. Detailed measurements and conversation IDs are in the delivery record (in Git history).

Plan/Track/Verify validation on 2026-10-09: **111 tests passed** in Release. Live ChatGPT planning, clarification, revision, explicit implementation approval, structured build/test results (4/4 passed), artifact search, restart persistence, and checkpoint undo passed. The unrelated note and test-file hashes were preserved. See implementation evidence (in Git history) for conversation IDs, report details, and limits.

Automated tests use synthetic tokens, scripted model responses, temporary workspaces, and separate CLI processes. They cover stream fragmentation/failure, callback and identity validation, DPAPI storage, tool correlation, pending questions across EOF/restart, cancellation, limits, junction rejection, conversation locks, and secret-free account output.

Verification on 2026-10-09 includes live ChatGPT editing with `gpt-6.1-sol`: AgentSmith repaired a disposable C# project, ran its tests (4 passed, exit 0), inspected Git changes, and preserved an unrelated edit. A restarted CLI undid the checkpoint and restored the original file hash. Automated checks also exercise native stdin/stdout/stderr, an HTTP development server, timeout and process-tree cleanup after forcibly killing the owner, migration, mutation failure injection, and conflicting undo. Exact suite results and evidence are in the retired CLI implementation record (in Git history). This establishes the tested account/model path, not every possible account configuration.

## Continuous conversations

Normal display includes commands/paths and useful arguments, stable process aliases (`#1`), task descriptions, and separate call/process timing. It suppresses unchanged empty polls and groups stdout/stderr fragments into lines. Compact previews show at most 20 content lines or 4 KiB, with omission notices and retained-log references. Errors, truncation, and exit/termination status remain visible. Use `/artifacts` to read complete retained output within the existing retention limits. Environment values and stdin bodies are omitted from activity summaries; recognized secret arguments are redacted.

```text
/display compact|verbose     Choose activity detail
/details <call-id|#process>   Show sanitized call identity, arguments and timing
/compact                     Compact while idle or paused for context
/queue                       Inspect queued prompts and hold reason
/queue remove <id>           Remove one pending prompt
/queue clear                 Remove all pending prompts
/queue continue              Release a held queue after resolving blockers
/cancel                      Cancel active work, keeping queued prompts
```

The scrolling terminal composer supports cursor movement, Home/End, Backspace/Delete, and Enter to submit. Incoming output is written above the composer. Long input scrolls horizontally. Escape during active inference, processes, or compaction requests cancellation and shows cleanup progress; completed edits are not rolled back. Escape in question/review pickers leaves the decision pending. Unsubmitted follow-up text survives run cancellation; drafts are not durable across CLI exit. `/paste` and `/end` also work while busy for multiline queued input.

Queued prompts run serially, without entering the current model request. Limits are 20 pending prompts and 32 KiB UTF-8 per prompt. Questions and plan reviews block the queue until explicitly resolved and the workflow completes. Cancelled/failed/paused runs, recovery, EOF, and restart hold the queue for explicit continuation. Queue claims, user messages, and run creation are committed together. There is no background daemon; existing process ownership rules still apply.

Redirected and JSON chat use line input. Once chat has accepted input concurrently with a run, decisions require `/answer <question-id> <text>` or `/review <review-id> <decision>` so a pending line cannot accidentally answer or approve a question. Resuming a decision with no queue retains legacy line answers. `smith run` remains single-request. EOF retains submitted queue items, cancels active work, and exits. Busy commands that require mutations or workflow changes are rejected locally.

Compaction keeps the full source history, tool results, checkpoints, artifacts, questions, and approvals. Only inference context is reduced. Summaries use the conversation's selected provider/model with tools disabled, retain source hashes and lineage, and consume model usage. Each attempt permits at most eight requests, with batches bounded by 32,000 input tokens or the smaller usable model budget; unknown-capacity sessions retain the 48,000-character summary-input fallback. Summaries remain capped at 8,000 characters. Failed/interrupted summaries never replace active context. `/context` reports the current snapshot; `/compact` does not resume a paused run or grant approval. Type `continue` to resume paused work, and `/queue continue` separately to release held follow-ups.

Controls and busy input require an interactive terminal for key handling. `--no-color` keeps cursor control where available; redirected output and `TERM=dumb` use append-only text. Physical Escape is not interpreted in redirected text; use `/cancel` there. The terminal renderer remains a lightweight scrolling UI, not a full-screen terminal emulator.

See historical delivery evidence (in Git history) and [runtime architecture](DESIGN.md). This milestone updates the local Windows build without a commit. No remote push or public release is included.

## Later roadmap

API-key access, remote/authenticated LM Studio servers, automatic model loading, full terminal emulation, browser automation, MCP/plugins, desktop UI, nested agents, worker process execution, and public distribution remain deferred. Public web search/read tools are implemented as described above. LM Studio and bounded model-specific workers are implemented.

Protocol references: [ChatGPT sign-in](https://developers.openai.com/siwc/token-sharing-open-source/sign-in), [inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference), and [preview requirements](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations). Reviewed on 2026-10-09.


## Document and Review workflows

Start a task with `/document <task>` or `/review`. Review defaults to staged, unstaged, and untracked changes; select a different target with `/review --base <ref>`, `/review --commit <ref>`, or `/review --path <path>`. A base comparison uses the merge base against HEAD; a commit comparison uses its first parent (or the root commit). Path audits work without Git. New conversations and single-request runs accept `--workflow document|review`; `--workflow plan` is equivalent to `--plan`, and contradictory options are rejected.

Document can edit `.md`, `.markdown`, `.rst`, `.adoc`, `.asciidoc`, and `.txt` files with Edit/Execute permission. It inspects source as evidence but cannot directly edit source comments, MDX, notebooks, or configuration. Inspect produces a saved draft. Execute permits documentation builds and example checks; processes run project code under your account and are not restricted to documentation files. Checkpoints and conflict-safe undo still apply.

Review permits reads, curated Git inspection, internal reports, and memory operations. It blocks file edits, undo, arbitrary processes, and build/test launches regardless of `/mode`. Workers inherit that restriction. Findings distinguish defects and risks from broader quality suggestions. Review captures comparison artifacts and workspace hashes, then rechecks them before completion. Changed, excluded, oversized, binary, or uncaptured evidence is reported as a coverage limitation. Read complete diff pages through artifact tools instead of treating previews as complete evidence.

Both workflows retain progress, revisions, findings, evidence, and limitations in `sessions.db`. Use `/reports`, `/report <id>`, and `/workflow` to inspect them. `/report export <id> <new-path.md>` explicitly creates a checkpointed Markdown file; it never overwrites an existing file or grants model editing permissions. Export is available while idle even in Review/Inspect. Completed reports also appear in `/artifacts`.

The parent must save and finish a report after collecting worker results. Successful completion returns to Work before queued follow-ups start, preserving permissions and plan approvals. Questions, cancellation, limits, recovery, and restart retain unfinished workflow state. `/workflow work` explicitly leaves a settled unfinished report as abandoned; resolve an active run first. Plan decisions must be resolved or explicitly left before entering these workflows.

Schema v8 backs up v7 before adding report/revision storage. This release also includes green terminal styling, compact square message guides, and minute/second duration displays. Live verification outcomes are recorded in the retired CLI implementation record in Git history.

Document/Review delivery verified on 2026-10-10: **250 automated tests passed**. Live ChatGPT/Bonsai review, documentation edits, saved-progress recovery, structured compilation, report export, unrelated-edit preservation, and worker checkpoint undo passed. One local documentation attempt exhausted its output budget; a smaller retry succeeded. The Windows publish folder is refreshed. The detailed acceptance record above distinguishes the injected restart check from a physical console crash test.
