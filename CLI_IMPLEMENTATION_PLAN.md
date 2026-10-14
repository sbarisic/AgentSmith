# AgentSmith CLI implementation plan

## Native ChatGPT web search — source changes, 2026-10-11

Added hosted `web_search` to ordinary ChatGPT OAuth Responses requests, with source inclusion and no duplicate local search function. New and resumed conversations default to native; `/web-search native|basic|off` persists an explicit per-conversation choice while idle. Local models use basic search and new workers inherit the selection. `/status` and `/help` expose the controls. Off is enforced during both tool preparation and execution. Compaction remains tool-free.

Search progress reaches the terminal through structured runtime events. Native messages wait for their completed annotations before display; citations become bounded source links, while original search items and annotations are retained in session history. Resume applies the same display projection. Hosted calls are not queued as local function calls. Provider failures show explicit fallback guidance without retrying or logging error bodies. The ChatGPT request builder also omits the unsupported `max_output_tokens` field, retaining the setting as a context reserve and leaving other providers unchanged.

Added regression cases for native/basic request shapes, hosted stream items and citation deduplication, empty completion envelopes, citation bounds/unsafe URLs, history persistence, workflow/provider selection, worker inheritance, disabled local dispatch, CLI persistence and failure-body suppression. Existing basic-search fixtures now select basic explicitly. Static `git diff --check` passed. No build, test execution, publish, live inference or running-agent changes were performed under the current build hold. Model/account availability, live citations, worker native search and compiled regressions remain unverified; the currently published executable does not yet contain this feature.

## Audio listening and active-worker status — local delivery, 2026-10-11

Implemented `audio_listen` for parent and worker use in all Inspect-capable workflows. It reads bounded workspace PCM WAV/MP3 files, supports WAV time selection and normalization, and sends audio to a separately configured OpenAI audio model. `OPENAI_API_KEY` is required; ChatGPT OAuth is never forwarded. `SMITH_AUDIO_MODEL` optionally overrides `gpt-audio-1.5`. Responses retain source/clip hashes, range, model and usage in an `audio-analysis` artifact with a saved receipt. The terminal shows a bounded analysis block and artifact link. Missing configuration, invalid formats, unsupported MP3 ranges and provider failures produce actionable messages.

Verification: 21 audio tool/clip tests passed, then the full Release regression suite passed all 526 tests (371 core/tool tests and 155 integration tests). Coverage includes PCM normalization, clip limits, MP3 restrictions, workspace boundaries, request payload/auth separation, absent credentials, error-body suppression, cancellation, response bounds, workflow availability, persisted receipt reuse and terminal escaping. The suite also verifies the previously pending active-worker status display. The self-contained Windows x64 publish completed successfully; this supersedes the build hold in the next entry.

Live audio-model acceptance was not run: no `OPENAI_API_KEY` was configured in this environment. MP3 duration and local decoding remain unavailable; it is sent whole with a signature check and provider-side validation. No physical terminal/audio-quality acceptance, commit or public publication was performed.

## Active workers in the composer — source changes, 2026-10-11

Added `agents 2 [#8, #9]` to the busy status row, refreshed with the existing one-second composer tick from the current parent's persisted worker records. Prepared/waiting and cancelling workers remain active; terminal states are removed. Shows zero explicitly, orders numbers, and bounds the list to eight numbers plus an overflow count. Added test cases for state transitions and large lists. Static diff checks passed; tests and build remain deferred while the user's agent runs.

## Worker and context updates — local delivery, 2026-10-11

Built and refreshed the self-contained Windows x64 folder after user authorization. This delivers worker budgets (100 requests / 30 minutes by default, `/worker-budget` and startup flags), six-minute worker waits, the composer context indicator, and guidance favoring useful parallel delegation. The source-only build holds in the following three entries are superseded by this delivery.

The complete Release suite ran 502 tests: 501 passed and one failed on a wording expectation for enabled ChatGPT profiles. After aligning the instruction wording, all 39 worker tests passed on rerun. The initial full run included 152 passing integration tests, including context display, budget persistence and busy-command behavior. Worker tests cover completion beyond 20 requests, retained lower-limit enforcement, six-minute wait validation and steering wake behavior. Published `chat --help` shows the new flags; CLI and Runtime assembly hashes match the build output. Static diff checks passed.

No live model task or physical terminal acceptance was run. The HTTP 520 error wording discussed separately is unchanged in this build. No commit or public publication.

## Prefer useful parallel delegation — source changes, 2026-10-11

Updated parent instructions and `agent_spawn` tool guidance to prefer at least two agents making useful progress concurrently: multiple workers or parent plus worker. Single sequential tasks remain with the parent when it would otherwise only wait. Explicit user delegation requests remain supported. Instructions reject artificial splits, duplicate work, filling slots and counting blocked/dependent tasks as parallel work. File reservations, process barriers and runtime admission rules are unchanged. Static diff checks passed; no build, model inference or publish was run while the agent is active. Live model adherence remains unverified.

## Composer context indicator — source changes, 2026-10-11

The model/mode row now includes the last prepared context input, its working budget and percentage used. Estimates are labeled `~`; conservative character-based sessions show character units. Missing/malformed manifests do not invent counts. Narrow layouts use a compact percentage. Rendering reads the saved manifest only and makes no inference or workspace requests. Added regression cases for exact/estimated counts, character fallback, invalid data, resizing and updated snapshots. Static diff checks passed; compilation, test execution and publish are deferred while the agent runs.

## Larger worker budgets — source changes, 2026-10-11

Raised the `agent_wait` maximum from three to six minutes (360,000 ms), including validation, tool description, parent guidance and activity labels. Updated completion/steering tests to request the new maximum and added rejection assertions immediately above the cap and below zero. These test changes remain unrun under the running-agent build hold; the published build still has the three-minute cap. Ordinary progress does not wake waits; completion, steering and cancellation still do.

New worker defaults are 100 requests / 30 minutes, with per-conversation `/worker-budget [requests minutes]` and startup `--worker-max-requests` / `--worker-max-minutes` options. Request accounting includes compaction. Each child gets an independent execution-policy snapshot, and the parent cancellation/deadline still bounds it. Concurrency changes preserve execution budgets; budget changes preserve concurrency. Older parent policies lacking the additive fields receive the new defaults; existing worker records are not rewritten or relaunched.

Regression test source covers completion beyond 20 requests, assignment persistence/isolation, legacy defaults, explicit lower-limit failure with preserved edits, validation and CLI persistence. Busy-command coverage includes rejecting changes while work runs. Compilation and execution remain deferred because the user's agent is running; the published executable retains the previous budget. No commit or publication.

## Markdown tables and activity colors — local delivery, 2026-10-11

Follow-up presentation change: Read/List/Search completion summaries, routine process details and symbol listings use light gray; tool/worker headings and web links use cyan. Git rows, plan progress and diagnostic headings distinguish status with color and retain explicit text labels. Normal assistant prose remains white.

Added streaming table recognition, square borders, stable column widths, wrapped cells, delimiter alignment and a labeled fallback for narrow terminals. Ordinary text remains white; inline emphasis uses the existing theme. Escaped/code pipes, empty cells, partial final lines, malformed rows and literal code/source mode are handled without dropping contents. Rendering buffers only a candidate header and current row, with the existing line-size guard and a 16-column bound.

Added nine regression test cases covering fragmentation, wrapping, narrow layouts, alignment, escaping, literal fallback, empty/header-only tables, Unicode widths and bounded streaming. After the user permitted rebuilding, all 37 selected Markdown/rendering tests passed in Release. The first run exposed an incorrect right-alignment padding expectation in one new test; that assertion was corrected. Static diff checks passed. Refreshed the self-contained Windows x64 publish folder; published help exited 0 and the CLI assembly hash matches the build output. Physical terminal verification and a new full-suite run were not performed; the earlier 481-test result below remains the last full-suite result. No commit or public publication.

## C# navigation and actionable diagnostics — local delivery, 2026-10-11

Implemented `AgentSmith.CodeAnalysis` with Roslyn 5.3.0 and MSBuild Locator 1.11.2. New tools are `csharp_symbols`, `csharp_load`, `csharp_status`, `csharp_query`, and `diagnostics_read`. `/diagnostics <report-id> [errors|warnings|failed|all] [page]` is available both idle and busy. Existing verification summaries now show compiler errors and failed tests first, with stable diagnostic IDs, duplicate counts, project/framework associations and links to details. Raw logs and historical report artifacts remain intact.

Syntax navigation needs no project evaluation. Semantic loading is an owned Execute process restricted to Work/Document parents and the existing editing-worker barrier. It supports csproj, sln and slnx, explicit multi-target selection, no requested restore/build, and bounded partial-coverage reporting. Queries operate on immutable source/options/reference data without analyzers or generators. Results label ambiguous candidates and linked-file variants. Hash checks, managed mutation/undo invalidation, explicit worker grants, two-snapshot capacity, cancellation and restart unavailability are implemented. Session/provenance fields are additive; schema v10 remains unchanged.

Verification:

- Final full Release regression run: **481 passed**, zero failures/skips (348 core/tool and 133 integration), using `xUnit.MaxParallelThreads=1`, `xUnit.ParallelizeTestCollections=false` and `RunConfiguration.MaxCpuCount=1`. The 15-test navigation/diagnostic set and five SDK/helper integration cases also passed independently. Six diagnostic tests passed again after the last defensive bound on historical report metadata.
- Real disposable SDK fixtures covered project/solution loading without restoring missing assets, multi-target rejection/selection, outside-source exclusion, semantic references, source/configuration staleness, read-only worker grants, restart unavailability, capacity protection, helper termination and preservation of unrelated content. Existing structured compiler-error and failing/skipped/passing TRX tests passed in the full suite.
- The first parallel Debug suite hit two process-fixture timeouts under contention. Those tests passed when the complete suite used one xUnit collection thread. Two new terminal tests initially used culture-sensitive escape-character comparison; their assertions now use ordinal comparison and pass. Production timeout limits were not increased.
- Published self-contained Windows x64 to `artifacts/publish/win-x64`. The executable help and isolated project loader smoke checks exited 0; the helper returned four workspace documents, explicitly reported missing assets/generator coverage, did not create `project.assets.json`, and preserved the unrelated text edit. Evidence: `artifacts/navigation-acceptance-20261010-235731/summary.json`, `help.txt`, and `publish-hashes.json`.

Live ChatGPT/Bonsai model acceptance was **not run**: the configured Bonsai endpoint at `http://127.0.0.1:8080/v1/models` was unavailable. No model server was started or reconfigured. The automated checks use real SDK helpers and controlled worker/session fixtures; they do not establish how live models will choose these tools. Physical terminal/composer interaction also remains unverified.

This delivery includes the pending directory plan catalog, readable Git status labels and composer model/workflow row. The earlier source-only build holds below are historical and superseded by this delivery. Production sessions were not rewritten. No commit or public publication.

## Composer model and mode — source changes, 2026-10-10

Added a dedicated composer metadata row with the selected parent model, `reasoning: default`, and workflow/permission mode. Both provider adapters currently omit effort overrides, so the display does not invent low/medium/high values or equate effort with token usage. Context is refreshed on conversation setup, idle command loops and running-status updates. The input owner tracks the number of drawn rows when clearing/redrawing around output, preserving the existing draft and cursor behavior. Local model filenames and width-bounded labels keep long model IDs from hiding the mode.

Reviewed source and checked whitespace. No build, tests, publish or physical terminal verification was performed while the user's agent is active.

## Git status labels — source changes, 2026-10-10

The terminal now translates Git porcelain status into readable labels: `??` becomes `Untracked`, with an explanation that new files have not yet been added to Git. Modified/added/deleted/renamed/copied/type-changed entries show staged and unstaged state separately; conflicts are identified explicitly. Paths, rename endpoints, raw model results and saved history stay intact. The display retains preview bounds and truncation notices; failed status calls cannot be shown as a clean workspace. This is a presentation change only: no files are staged or committed. Source reviewed and whitespace checked; no build, tests or publish while the user's agent runs.

## Directory plan discovery — source changes, 2026-10-10

Added creation workspace/time to new plans and inferred workspace provenance for legacy plans. `IPlanCatalog` reads the existing transactional plan projection without loading or mutating another conversation. The additive JSON metadata keeps schema v10; no migration or production-state rewrite is required. Listing is limited to ten summaries per page, and fetching returns the latest revision with source conversation and origin metadata. Exact normalized workspace matching excludes parents, children and siblings; data directories remain separate.

Added parent/worker tools `plan_list(page)` and `plan_fetch(planId)`, plus `/plans [page]` and `/plan read <id>`. These commands work while busy; mutations remain blocked. Reads do not change active plans, approvals, permission ceilings, workflow, artifact access or worker assignments. Updated help, activity labels, terminal provenance and model guidance.

Added regression-test source for persistence, latest revision, legacy inference without rewriting, directory normalization, pagination, data-directory isolation, worker restrictions, detached reads, CLI routing and rendering. Checked the actual catalog SQL against synthetic records in an in-memory Python SQLite database: current and legacy provenance, latest revision, bounded listing goals and parameterized lookup passed. `git diff --check` passed. C# tests, builds, publish and live acceptance were **not run**, as requested while the user's agent is active. No running process or production database was changed.

## Model commands and image display — local delivery, 2026-10-10

After the user confirmed Smith was closed, built the pending changes and ran the full Release suite. The first run exposed a cancellation ordering race: a cancelled run could be observed before its queue hold was saved. Changed cancellation to persist the hold before signalling the runner, using `finally` to preserve cancellation if persistence fails. The full rerun passed **452 tests** (326 core/tool, 126 integration), zero failures or skips.

Published the self-contained Windows x64 build to `artifacts/publish/win-x64`. Published checks using an isolated loopback mock catalog confirmed `smith model` persistence, `smith chat` without `--model`, `/models`, `/model` selection and the selected model in a subsequent new chat. Core/Storage/Tools/Runtime/CLI assembly hashes match the build. Evidence: `artifacts/model-image-delivery-20261010/published-smoke.json`. No live provider inference, production-state changes or physical clipboard/terminal checks were performed. No commit or public push.

## Model commands and persistent defaults — implementation

Added `/model [id]`, `/models` and `smith model [id]`. Idle model selection validates a detached candidate and saves the session model plus the new-chat default transactionally. `smith chat` no longer requires `--model`: explicit options override the saved default; existing installations without one fall back to their latest parent session. New successful chats remember the resolved selection. `smith run` reads defaults without replacing them. Workers, workflow and permissions remain unchanged; active/unfinished work and pending review prevent model switches. Existing conversations cannot switch provider/account through this command.

Schema v10 adds the defaults row after a v9 backup. Added model-specific usage attribution, token calibration reset and model-aware compaction attempt identity. Regression coverage includes migration with held queues, atomic rollback, persistence/reopening, explicit override precedence, parent-only fallback, model validation failure, unfinished-run rejection and usage across model switches. Updated help and documentation. Included in the verified local delivery above; no production settings were changed during validation.

## Submitted image visibility — implementation

The reported live message omitted attachment labels because `user.submitted` carried only the text, while replay read image references from rich history. Added an allowlisted content projection and shared image-label rendering for live submissions, queued dispatch and replay, plus image counts/metadata in queue acknowledgements. Labels retain part order and show dimensions/size when registered metadata is available. Image-only prompts remain visible; legacy text events still render. No provider transport or saved prompt changes are required.

Regression coverage for Enter submission, image-only prompts, ordered multiple images, queue/replay visibility, metadata-only events and legacy text events passed in the full suite above. Included in the local publish. Physical clipboard/terminal acceptance remains unverified; live conversations were not changed during testing.

## Automatic worker collection follow-up — 2026-10-10

Live user evidence showed that explicit completion feedback alone was insufficient: the parent repeated its answer while three cancelled/blocked workers remained uncollected. Moved this bookkeeping into the runtime completion check. Settled results are imported and saved, their original outcomes and checkpoint/artifact references enter the next request, and the parent gets another inference to inspect the evidence. No worker is relaunched or marked successful by collection. Uncertain effects and held reservations still block collection.

Parent run receipts are saved before worker collection markers. Recovery after a marker-write failure reuses the saved receipt; parent-save failures leave the worker uncollected. Manual `agent_result` uses the same idempotent path. A collection notice identifies the worker outcome and retained report. Source history and user workspace contents are unchanged by these checks.

Verification: **438 Release tests passed** (323 core/tool, 115 integration), no failures or skips. Tests include a parent that never calls `agent_result`, cancelled work without a report, failed receipt persistence, restart between receipt and marker commits, recovery-required effects and terminal notices. Live provider acceptance remains unverified; the user's paused task was not resumed for testing. Refreshed the self-contained Windows x64 publish folder after checking that Smith was closed. Published help, isolated JSON authentication status and Core/Tools/Runtime/CLI assembly hash checks passed; evidence is at `artifacts/automatic-collection-20261010/published-smoke.json`. No commit or public push.

## Completion feedback follow-up — 2026-10-10

The next screenshot showed the non-progress guard working, but the parent still repeated its final answer before pausing. Completion barriers previously returned only a boolean; the next request relied on general workflow/worker instructions rather than feedback about the refused completion.

Added a persisted completion requirement with a code, concrete reason and next action. Uncollected workers are identified by number, state and tool ID, including cancelled/failed workers. Document/Review directs the model to read/update/finalize its saved report. Worker prose receives an agent_finish instruction instead of an immediate failure. Feedback is refreshed before inference, counted in context and cleared after the requirement is settled. Terminal notices and /status show the reason and next action. Existing permission, report-validation, recovery and explicit-continuation gates remain in place.

Automated cases exercise parent result collection followed by completion, worker report submission after a premature prose answer, workflow finalization, feedback persistence across a paused restart and terminal/status rendering. Live ChatGPT/Bonsai compliance with this correction has not been verified; scripted tests establish runtime delivery and state transitions only.

Delivery: **434 Release tests passed** (320 core/tool, 114 integration), with no failures or skips. Refreshed `artifacts/publish/win-x64` after confirming no Smith process was running. Published help, isolated JSON authentication status and Core/Tools/Runtime/CLI hash checks passed; evidence is at `artifacts/completion-feedback-20261010/published-smoke.json`. No saved user work was resumed or changed; no commit or public push. For the reported non-progress pause, use `smith resume` and explicitly `continue`; the runtime refreshes the outstanding completion requirement before inference.

## Repetition and oversized compaction recovery — delivered locally, 2026-10-10

- Fixed the deterministic oversized-group compaction rejection. Summary requests now consume bounded quoted evidence slices with source hashes and character ranges, including slices of large historical groups. Original history and ordinary inference protocol groups remain intact. Snapshot activation requires every slice, a validated summary, protected-context budget checks and an unchanged source boundary. Measured token overruns shrink the slice before inference. Failed/interrupted attempts keep the previous snapshot; the eight-request ceiling remains.
- Added a bounded generated-text repetition guard and a three-response completion-barrier guard. Both pause with NoProgress and explicit continuation guidance. Incomplete generated calls never execute. Model-response boundaries now flush Markdown so consecutive answers do not concatenate. These guards do not establish why the provider generated the reported repetition.
- Added regression coverage for a 90,000-character tool-result group, complete recent protocol groups, saved slice provenance, interrupted slices, exact-count overruns, streamed/authoritative repetitive text, explicit resume, completion barriers and adjacent rendered responses.
- Final Release suite: **430 passed** (317 core/tool, 113 integration), zero failed/skipped. Command: `dotnet test AgentSmith.slnx -c Release --no-restore -- RunConfiguration.MaxCpuCount=1 xUnit.MaxParallelThreads=1`.
- Published the self-contained Windows x64 build to `artifacts/publish/win-x64` after the user confirmed exit. Published help and isolated JSON authentication-status checks passed; Core, Runtime and CLI assembly hashes match the build. Smoke evidence is local at `artifacts/repetition-compaction-20261010/published-smoke.json`.
- This delivery includes the staged content blocks, process-output cleanup, worker aliases, steering previews and actionable errors documented below. It supersedes their earlier build/test deferrals. No live ChatGPT/Bonsai repetition or compaction acceptance was run for this fix, and physical terminal visual acceptance remains unverified. The user's saved sessions and sandbox were not modified or resumed. No commit or public publication in this delivery.

To recover the reported paused task, start `smith resume`, run `/compact`, and type `continue` after it succeeds. Compaction can still pause if protected current context cannot fit or the bounded attempt is exhausted; it never activates a partial summary.

## Steering previews and worker action labels — source checkpoint before the delivery above

Added readable `› Spawn`, `› Wait for`, `› Message`, `› Collect`, and `› Cancel` worker activity with conversation-local aliases. Compact worker starts avoid the duplicated tool/start task text. Steering notices show a bounded quoted message, recipient and sender, with separate Saved / Added to next request / Sent to model stages. Later stages do not repeat the full preview in compact output. `/steering` exposes expanded bounded previews and full IDs; older events retain their original notice when no preview was saved. `/details` refreshes worker action summaries from the saved arguments.

Regression test source covers action labels and aliases, sender/recipient visibility, duplicate-preview suppression, expanded listings, bounds/redaction, unchanged original inbox text, and legacy events. Tests, compilation, visual acceptance and publishing remain deferred while the user's agent runs. No live session data or running process was changed.

## Live steering and worker messaging — 2026-10-10

Implemented `/steer`, `/steering`, pending-message removal, `/agent <id|#number> message`, and parent-only `agent_message`. Schema v9 backs up v8 before migration. Inbox persistence is independent of mutable sessions, with atomic incorporation/skipped-call results, ordered delivery, idempotent model-issued receipts, and completion/dispatch races serialized through SQLite transactions. Existing continuation, queue holds, explicit decisions, worker assignments and permissions remain in force.

Wait notifications wake process polls/waits and worker waits without cancelling their underlying tasks. Original prompts and incorporated steering remain protected across compaction. CLI help, status, busy counts and structured delivery notices are integrated. A transport delivery notice is not a compliance claim.

Acceptance uses isolated state and a disposable workspace under `artifacts/steering-acceptance-20261010`, with the already-running Bonsai2 endpoint. Production profiles, credentials and server configuration were not changed.

- Parent (`parent.jsonl`): a 60-second process wait returned in 63 ms with `interruptedBy: steering` and state Running. The same run then stopped the sleeping process, created/read `corrected.txt` with CORRECTED, left `old-plan.txt` absent and preserved `unrelated.txt`. The run completed in about 102 seconds over seven model requests.
- Worker (`worker.jsonl`): a direct user message changed the assigned output from ORIGINAL to STEERED. The worker completed, and the parent collected its report and read the file. The unrelated file was preserved. The parent subsequently reached the fixture's four-minute limit after additional reads; this is not recorded as successful parent completion.
- Automated coverage includes in-flight inference, before/after mutation boundaries, prepared journals, correlated skipped calls, compaction success/failure, request-limit continuation, transaction failure injection, migration, ordered bounds/removal, completion races, independent child persistence, parent-message receipts/provenance, and busy queue separation.
- Release regression verification passed **401 tests** (302 core/tool, 99 integration). The full suite passed with `dotnet test AgentSmith.slnx -c Release --no-restore -- RunConfiguration.MaxCpuCount=1 xUnit.MaxParallelThreads=1`; after the final steering wording change, all 302 core tests and all 99 integration tests passed in separate project results. The added preparation-boundary case confirms a skipped process never launches and is recorded as NotStarted, not interrupted.
- Earlier runs encountered timing-sensitive SQLite cleanup failures and a 30-second output-drain timeout under load. The bounded-concurrency full suite passed those cases. A later cancellation-test cleanup failure prompted a test-lifecycle fix: always cancel and await its chat pump in finally, so assertion failures cannot leave database users running during directory removal. The final integration run passed. No product timeout or test assertion was weakened.
- First restart acceptance (`restart-*.jsonl`): pending steering survived a forced disposable CLI exit, reopening without continuation performed no inference, and continuation incorporated/sent it exactly once. Bonsai nevertheless followed the original filename, interpreting the generic approved-scope wording as overriding the correction. This failed compliance check led to explicit guidance that newer user steering supersedes ordinary earlier requests, while only an actual approved plan establishes fixed scope. A second isolated run checks the revised wording.
- Revised restart acceptance (`restart-v2-*.jsonl`, conversation `7e5e0cf3082743fe918a1bd0f53071d5`) passed: forced exit preserved Pending steering; reopening without continuation sent no model request; explicit continuation incorporated/sent it exactly once, created/read `resumed-v2.txt` as RESUMED, left `restart-v2-original.txt` absent and preserved `unrelated.txt`. The run completed.
- Self-contained Windows x64 publish succeeded to `artifacts/publish/win-x64`. Published `--help`, isolated completed-session resume, `/help queue`, `/steering`, `/status` and `/exit` checks passed without model inference. Published Core, Storage, Runtime and Tools assembly hashes matched the tested sources; `published-smoke.jsonl` records the command check.
- ChatGPT live steering remains unverified; no claim of hosted-provider acceptance is made from scripted tests or Bonsai results. No commit or public publication.

## Worker wait limit — delivered locally, 2026-10-10

Raised `agent_wait` from 60,000 to 180,000 milliseconds (three minutes), including argument validation, tool description and parent guidance. Completion and cancellation still end the wait early; ordinary progress updates do not. `process_wait` remains capped at 60 seconds. After the user exited the agent and authorized building, all 376 Release tests passed (278 core/tool, 98 integration). Self-contained Windows x64 publish succeeded; published help and matching Runtime/Tools assembly hashes were checked.

## Direct web images — delivered locally, 2026-10-10

- Implemented URL input through the existing `image_read(path)` contract, preserving local file support and worker availability. Uses public-web URL/DNS checks and a credential-free downloader, with validated redirects, cancellation, 30-second timeout, 10 MiB decompressed limit, and PNG/JPEG/BMP decoding up to 32 megapixels.
- Saves immutable image attachments with original/final URLs, retrieval time and source hash. Interrupted result persistence reuses the committed snapshot; it does not fetch a changed remote image. Existing provider image projection carries the actual pixels to vision-capable models.
- Added compact web-image rendering and query-free activity previews. Credential-bearing proposed image URLs are rejected before saved protocol items are recorded.
- Added regression cases for public redirects, private/credential destinations, loops, malformed/unsupported images, dimensions, known/unknown download lengths, cancellation, HTTP errors, restart/idempotency, model image projection, credential exclusion and terminal presentation.
- Source changes were initially held without compilation while the agent was running. Once the user authorized building, all 376 Release tests passed and the existing Windows publish folder was refreshed. The added web-image tests passed, including image projection, redirects, limits, recovery, cancellation and rendering. Published help exited successfully and Runtime/Tools assembly hashes matched the tested build. Live model inspection of a remote image remains unverified. No commit or public publication.

## Image inspection, workspace maps, and HTTP requests — 2026-10-10

Implemented `image_read`, `workspace_map`, and `http_request`: registry/worker availability, workflow enforcement, artifacts, image transport, context projection, cancellation/recovery, and terminal rendering. Additive JSON fields hold attachment provenance, call image references, and prepared network effects. Existing session identities, queues, approvals and database schema are preserved.

The existing source was built and published first, as requested. Baseline fixes: upgraded vulnerable AngleSharp 1.4.0 to patched 1.5.0 and corrected an xUnit analyzer violation. Baseline Release verification passed 331 tests (235 core/tool, 96 integration).

Acceptance evidence is under `artifacts/inspection-acceptance-20261010-154822`, using isolated AgentSmith state with memory disabled and a disposable workspace. Production profiles and the local server configuration were not changed.

- Bonsai2 parent: `local-parent.jsonl`, conversation `8fa04eca25f44fe0941ee5e47202e9b9`. Two model requests. Map and actual image input correctly produced red square/left, blue circle/right, white background and `net10.0`. No edits or processes.
- Bonsai2 worker: `local-worker.jsonl`, parent `8ace961e3ea04beea51809c43d56fbab`, child task `b0c4ab244d5341808d1bd146051edb44`. Read-only delegation used the tools, completed, and was collected. Shape/color/side findings were correct; estimated pixel coordinates were inaccurate and are not accepted as measurements.
- Bonsai2 HTTP: `local-http.jsonl`, conversation `741ab00c919241d3a7944bc1522121e0`. GET `/health` returned 200 and `{"status":"ok"}`; POST `/tokenize` returned 200 and `{"tokens":[14556]}`. No server configuration changed.
- Automatic approval review rejected a temporary Python server launch with only “blocked by policy” given. Managed in-process loopback tests cover actual POST wire content, error responses and redirects. Live model HTTP acceptance used the already-running local server's health/tokenization endpoints instead.
- ChatGPT parent/vision acceptance is incomplete: `auth status` and `workers list` returned the generic account/state-access error. Credentials were not changed. Payload tests cover ChatGPT Responses, LM Studio Responses and local Chat Completions image projection.
- One full-suite attempt hit a SQLite-lock failure in the existing Git-review/process cleanup test; its isolated rerun passed, and the subsequent full run passed all 360 tests (263 core/tool, 97 integration). The final additional three-provider image-payload assertions also passed separately.
- Final self-contained Windows x64 publish succeeded to `artifacts/publish/win-x64`. Published `--help` and an isolated saved-session resume followed by `/tools` and `/exit` exited 0; all three new tools were enabled. Published/source Tools assembly SHA-256 matched: `B07CE2CF5F186B50657C31439726B1EB3E30BE7A5B3EFC158DE16DE752E8858D`. Static diff checks passed. No commit or public publication.

Earlier source-only checkpoints below are retained as history. Their build/test holds are superseded by this delivery; their unperformed live acceptance remains unverified.

Updated: 2026-10-10

## Public web search and reading: initial source checkpoint

- Added `web_search(query)` with five bounded DuckDuckGo HTML results and `web_read(url)` with text extraction, source URLs, retrieval timestamps, and retained page artifacts. No new API key is needed.
- Registered both tools for parent and worker Inspect access in every workflow. Added public-research/citation instructions and explicit treatment of external content as untrusted evidence. Activity and results use the existing terminal renderer; JSON retains structured results.
- Isolated HTTP transport from OAuth and local-provider clients; blocked private/reserved destinations, credential URLs, redirect escapes, and unbounded downloads. DNS validation connects directly to a vetted address. No scripts, cookies, browser automation, authentication, proxy use, or binary/PDF reading.
- Added regression cases for private addresses, numeric/mapped IP aliases, credential patterns, redirect limits, result decoding, inert HTML extraction, artifact persistence, bounded previews, binary/oversized rejection, CAPTCHA/layout failures, cancellation, and parent/worker workflow permissions.
- Observed 2026-10-10: an independent PowerShell request to DuckDuckGo HTML returned HTTP 200 and ten result links, without a challenge. This is not verification of the new C# adapter. No C# build/test execution, agent inference, or local publish was performed while the user's agent was running.
- Pending after build authorization: restore the pinned HTML parser package; run web, artifact, workflow, worker, CLI, and full regression suites; exercise actual C# search/read requests; have ChatGPT and Bonsai independently retrieve and cite public documentation; verify retained artifact reads after restart; then refresh the local Windows publish when unlocked.

## GPT workers and configurable parallelism: initial source checkpoint

- Reused existing ChatGPT worker routing and account-reference profiles. Added separate total/local admission limits to saved sessions, defaulting to six/one for new CLI chats and two/one for sessions without a saved policy; explicit saved limits are preserved.
- Added `--max-workers`, `--max-local-workers`, `/worker-limits [total local]`, `/status` policy output, and help entries. Each limit accepts 1..16. Busy reads are allowed; changes are idle-only and cannot undercut active counts.
- Updated delegation guidance to use enabled GPT profiles for complex work, local profiles for routine work, and concurrent independent assignments before waiting. A single profile can be reused across workers. Worker summaries identify the provider.
- Retained reservations, process barriers, child permission ceilings, cancellation and recovery. Preserved sixteen launches per run and existing worker deadlines. Expanded `agent_wait` to sixteen IDs.
- Added regression cases for six mixed-provider workers, local versus total ceilings, racing admission, duplicate spawn receipts at full capacity, Plan restrictions, reservation conflicts, cancellation, persistence, invalid limits, and busy CLI changes.
- No build, automated test execution, live inference, or publish was performed because the user requested that the active agent remain undisturbed. Static diff checks are the only verification for this change so far.
- Attempted `gpt-edit` and `gpt-review` registration using the installed executable and explicit `%LOCALAPPDATA%\AgentSmith`. Both attempts and `auth status` returned the generic account/state error. Existing Bonsai profiles remained present; GPT profile creation is incomplete. Registration commands are in README.md for the working user terminal.

After build authorization: run worker, provider, CLI, and existing regression suites; register account-available GPT models; verify more than two real GPT workers overlap, collect their results, cancel a task tree, and confirm local endpoint gating and file reservations remain intact. Refresh the Windows publish only after verification and when unlocked. Do not treat the added tests as passing evidence until run.

## Clipboard input, larger tasks, and local delegation

Updated 2026-10-10. This is the current milestone; dated records below retain the earlier releases' limits and acceptance results. No commit, public publication, server reconfiguration, or background inference is included.

Resume clipboard correction (2026-10-10): the resumed-session confirmation previously opened before clipboard import/error callbacks were initialized, causing an image paste to disappear with only a redraw. `PrepareResume` now installs both callbacks first. The continuation screen explicitly directs users to continue before pasting, does not read the clipboard there, rejects `/attach`, and accepts an empty Enter as advertised. Prompt submission state is cleared when leaving the confirmation. Release regression coverage passed 25 clipboard/CLI/continuous-chat tests. After the user closed the running instance, the self-contained PATH build in `artifacts/publish/win-x64` was refreshed; published resume help passed. The staged copy remains in `artifacts/publish/resume-clipboard-fix`. Physical keyboard verification remains manual.

Implemented:

1. Persisted execution policy: 150 parent requests/60 minutes per explicit batch, with compaction included; `--max-steps`, `--max-minutes`, `/limits`, structured pause reasons, remaining-limit status, and repeated non-progress detection that excludes waits.
2. Model capabilities and balanced/max/custom input budgets. Loaded local context and slots are discovered; ChatGPT uses account metadata or a versioned sourced catalog. Output/headroom reserves precede the working budget. Local template/token counting includes tools; account counting is probed with a cached unsupported fallback. Estimates are labeled and calibrated against reported input. Image estimates never use base64 length. Source history remains intact while large tool results use bounded excerpts and artifact references for inference. Compaction uses provider-sized batches and policy/projection-aware attempt identities.
3. Windows Alt+V clipboard service and typed prompt parts. Multiline/large pasted text stays literal in collapsed blocks. Images are normalized, bounded, hashed and independently registered. Draft block focus, preview and removal do not submit. `/attach` and `--image` work without clipboard access. Atomic queued-run creation retains ordered image references, ownership and restart holds. Responses and Chat Completions materialize image bytes only in transport; unsupported models reject them. Parent-to-worker image copies retain source provenance.
4. Proactive/manual delegation over enabled profiles, purpose metadata, sixteen launches, twenty worker requests/ten minutes, endpoint capacity gates, and sixty-second worker/process waits. Specific artifact grants replace short copied evidence. Workers retain their existing restrictions; the parent owns final integration and verification. Usage reports include requests, known cache splits, purpose/outcome, waiting and compaction overhead.
5. SQLite v7 follows a consistent v6 backup. Attachment rows are independent of active runner snapshots; policies, capabilities, worker purpose and grants persist in their owning snapshots. Migration does not run work or release held queues. Existing memory, decisions, call IDs and checkpoints are retained.

Automated verification: **231 tests passed in Release** (181 core/runtime/provider/storage/tool tests, 50 CLI/integration tests). Added coverage includes 36-request runs, structured request-limit pauses, unchanged-round detection, endpoint-slot cancellation, process_wait completion/cancellation, model reserves, image accounting, result projection, v6 migration/held queues, clipboard failure, literal slash/multiline paste, draft preservation, note-picker semantics, ownership, image payload ordering, hash integrity, queue restart and duplicate-claim prevention. The existing OAuth, streaming, memory, compaction, recovery, process-tree cleanup, worker boundaries and renderer suites passed. A question-answer race found during testing was fixed: an addressed answer received just after the pending decision was persisted is deferred to the decision reader, rather than rejected as busy input.

Live acceptance used the signed-in ChatGPT account, `gpt-6.1-sol`, and the exact loaded `Ternary-Bonsai-2-27B-PQ2_0.gguf` model at loopback port 8080. Loaded capacity was 32,768 with vision and one slot. No server configuration was changed.

- Image input: ChatGPT and an explicitly assigned Bonsai worker independently read `ORBIT 7429` and identified the red square. Images entered through `/attach` in the harness, not a physical Alt+V keypress. The worker completed and its report was collected.
- Context: seeded historical fixture records produced **122,628 characters / 62,070 estimated tokens**. The balanced 128,000-token budget required **zero automatic compactions**; the old 96,000-character trigger would have fired. This is a threshold comparison, not a timed old-version benchmark. Explicit live compaction reduced the estimated context **62,070 → 4,472 tokens**, activated its snapshot, and subsequent inference retained the widget-color fact. The account counting endpoint was unavailable in this run, so these are estimates, not provider token counts.
- Queue: a submitted image was persisted, held, reopened through a fresh store, explicitly released and dispatched once. ChatGPT read the correct text/shape. A second claim returned no run.
- Coding: manual and forced-delegation versions of the same small addition defect both passed real structured tests, preserved `UNRELATED.txt`, and restored the original implementation through checkpoint undo. Manual execution passed 6 tests; the delegated version passed 4. The parent ran tests after local investigation, implementation/test writing and patch-review workers completed.
- Waiting: the delegated run used seven `agent_wait` calls requesting 60 seconds and one `process_wait`; no short process polls. Waits returned on completion. This records the new behavior; an old-version polling run was not repeated.

| Small coding fixture | Parent requests | ChatGPT input / output | Local input / output | Outcome |
| --- | ---: | ---: | ---: | --- |
| Manual | 14 | 43,541 / 1,224 | 0 / 0 | 6 tests passed; undo verified |
| Forced investigation + edit/tests + review | 23 | 141,618 / 1,252 | 40,506 / 9,045 | 4 tests passed; undo verified |

For this deliberately tiny fixture, forcing three delegations increased cloud consumption. The default guidance therefore excludes trivial work and asks workers for bounded deliverables before the parent repeats investigation. Local token totals are measured offload, not exact avoided GPT tokens or money saved. Larger-task savings remain workload dependent.

Live evidence (ignored local files): `artifacts/continuous-live-20261009-221302/{summary.json,events.jsonl,request-metrics.json}` and `artifacts/context-live-20261009-221831/{summary.json,events.jsonl}`. UTC directory dates differ from the local 2026-10-10 date. Acceptance harnesses are under `artifacts/continuous-live-harness` and `artifacts/context-live-harness`.

Long-run acceptance: conversation `0f87a42e7b264438afbb31b42c4834d8` followed 33 uniquely named evidence files and returned `CHAIN_VERIFIED_5831` in **34 real ChatGPT model requests**, without compaction or the old request-limit pause. The source history and request metrics remain in the evidence directory. After the final adjustments, targeted reruns passed: 39 context/runtime/worker tests, 21 context/compaction tests, 12 clipboard/continuous-input tests, and 7 model-budget tests. These overlap the 231-test suite; they are not additional unique tests. The self-contained Windows x64 publish folder was refreshed successfully; published help shows 150 requests/60 minutes and the new options, and published local discovery returned the expected Bonsai2 model and 32,768 loaded tokens. A published `smith run --image` completed against Bonsai2 and correctly read the fixture text and red square. Its context diagnostic used the server text tokenizer with an explicit image estimate, an 8,192-token output reserve, and a 22,937-token usable working budget. Evidence: `artifacts/published-image-smoke.jsonl`.

Remaining manual coverage: physical Alt+V in Windows Terminal (including image-priority and picker-note clipboard behavior), physical Escape with new attachment drafts, and a forced process crash exactly around an attachment dispatch. Automated key/clipboard doubles, queue atomicity, cancellation/restart and existing crash cleanup passed; they do not substitute for those physical/live checks. Full 1,050,000-token account-route capacity and savings on substantial production tasks remain unverified.

## Automatic shared memory

Implemented the shared SQLite memory library and schema v6 with a consistent v5 backup. The parent can save and revise bounded memories during normal Work/Plan turns without additional approval; workers can read/search only. Entries retain origin workspace, validated user/tool evidence, attribution, revision and dates. Writes commit atomically with revision history, FTS5 updates, receipts and library audit events. User-authored corrections are protected. Idempotent recovery does not repeat a committed write or resurrect a forgotten entry.

Added model memory tools, slash commands and `smith memory` equivalents, on/off settings, idle-only user changes, busy reads, compact notices, structured events and context provenance. Literal shared-library retrieval is capped at five entries/6,000 estimated characters; search previews at ten entries. Memory is counted in context and frozen during compaction. Known credential patterns and terminal controls are rejected; recognized secret-bearing memory-call arguments are scrubbed before protocol persistence. Read failures omit memory and warn. No embeddings, background extraction, historical import, commit or public publication is included.

Verification on 2026-10-09: **219 tests passed** (173 core/provider/storage/tool tests and 46 CLI integration tests). One existing question/queue integration test reached its 20-second timeout under concurrent suite load; its isolated rerun passed in 557 ms without a code change. Coverage includes migration, literal search and ranking, bounds, concurrent revisions, protected corrections, credential/control rejection, transactional failure injection, worker restrictions, exact evidence references, frozen compaction selection, queue exclusion, disabled memory and CLI persistence.

Live ChatGPT acceptance passed in isolated data under `artifacts/memory-live-20261009-214128`:

- Parent `b58d1091e3b04457954f810050298c2c` read fixture documentation and saved memory `def7394eaa2645d9a04f3f9bfe52026a` with a validated file-read source. Separate workspace conversation `630895d0afe845bfa8b9cfc09e2f18d1` retrieved it with provenance.
- Parent `dd757951bba94ef8a94c0fbe6850a658` delegated memory search/read to ChatGPT worker `aa54e5689d7d4f688db30a58c8df40e8`, collected its report, and independently verified that worker writes were rejected. This run used ChatGPT for both parent and worker; local-provider memory inference was not separately exercised.
- Explicit correction persisted revision 2 and user-authored protection; a new request included the corrected marker without the old marker. Forgetting removed retrieval in a subsequent conversation. Disabling excluded injected memory and memory tools; reopening the store preserved the setting. Six parent runs completed without changing source projects.

The initial live attempt (`artifacts/memory-live-20261009-214038`) exposed that provider call identifiers were not reliably visible to the model: it refused to invent evidence and did not save. Context now supplies bounded, exact recent evidence references, with validation unchanged. Acceptance was rerun successfully. Regression tests also caught and fixed stale auto-generated titles after explicit user corrections.

Published the self-contained Windows x64 build to `artifacts/publish/win-x64`. Published help and isolated add/edit/search/forget/settings checks passed. Opening memory in the default data directory completed migration and reported an enabled, empty library; fixture memories were not copied into it. Original conversations, credentials and source workspaces were preserved. No commit or public publication was performed.

## Latest-session resume shortcut

`smith resume` now accepts an optional conversation ID. Omission selects the most recently updated parent session from the selected data directory, across workspaces. Explicit IDs retain their previous behavior. Child sessions are excluded; empty stores give a start-chat hint, and lock errors do not cause fallback to another conversation. Selection preserves existing question/review/unfinished-run confirmation and queue holds. Integration regressions cover recency after updating an older session, child exclusion, explicit selection, empty stores and latest-session lock contention.

Verification: all 44 CLI integration tests passed after correcting the empty-store test to expect the established user-error exit code 2 (43 passed in the full run; the corrected case passed on rerun). Refreshed the self-contained Windows x64 publish folder. Published resume help shows the optional ID, and an ID-free resume selected the saved local fixture and exited without starting a run. No model request, user-session continuation, commit or public publication was performed.

## Delivered fixes: compaction overflow and subfolder Git inspection

The user reported an 8,000-character summary overflow, repeated zero-second pauses after `continue`, and Git inspection rejecting a workspace under a larger repository. Source changes now request shorter summaries and allow one oversized-summary retry per chunk, sharing the existing maximum of eight requests. They preserve source history, require a complete valid summary, retain the hard output/input limits, and leave `/compact` as the explicit retry after an unsuccessful attempt. The CLI checks the saved boundary before starting another paused run and gives actionable guidance. Git status/diff/log/show are restricted to the workspace with literal pathspecs even when the repository root is above it; status paths remain repository-relative. Rename detection is disabled to avoid exposing source paths outside the workspace.

Regression cases cover streamed and completed oversized responses, repeated overflow, the eight-request cap, restart retry gating, held queues, missing retry usage, and subfolder staged/unstaged/untracked/history results without sibling file content or index changes. After the user closed Smith and authorized building, validation passed **194 tests**: 41 integration tests and 153 core/provider/storage/tool tests. The first core build flagged an xUnit assertion style issue; the core suite then passed 152 tests with one Git test using an unsupported symbolic revision. Both test-only issues were corrected; the Git case passed when rerun using the commit hash returned by `git_log`. No production changes were needed during verification. Git tests exercised actual processes in disposable repositories; compaction/resume regressions used scripted models. Live model compaction on the user's reported session remains unverified.

Published the self-contained Windows x64 build to `artifacts/publish/win-x64`, including the usage report. Published `--help`, JSON usage for a saved local fixture, and readable usage for the saved ChatGPT/Bonsai worker fixture succeeded. The local fixture reported 4,301 input / 215 output tokens; the worker fixture retained the 8,811 local-worker-token total below. An initial usage check used the wrong fixture data directory and correctly returned `Conversation not found`; using the recorded store succeeded. No user conversation was resumed or changed, and no commit or public publication was performed.

## Cloud/local usage statistics

Added `/usage` (also available while busy) and `smith usage <conversation-id>` with structured JSON support. Totals separate ChatGPT from local models and identify the local-worker subset. Rows identify parent/worker, profile, provider, model, reported requests and committed compaction tokens. Reports use saved per-request events across runs, not only the last request or duplicate worker summaries, and can inspect closed conversations without inference. Missing/malformed usage, incomplete requests and failed compaction attempts remain visible. Input includes cached input as reported; nested attribution and cached/reasoning breakdowns are not added twice.

The display labels local worker tokens as measured work handled locally. Exact cloud or monetary savings are not claimed: different prompts/tokenizers and parent delegation overhead prevent a direct counterfactual calculation. No schema migration or changes to inference permissions are required.

Verification: 189 Release tests passed (149 core/provider/storage tests and 40 CLI integration tests). A read-only report over the recorded Bonsai 2 acceptance conversation `ab9978e5bacb45f8bebf6b93d60d6e88` counted 31,396 cloud input / 638 cloud output tokens over 11 requests and 7,528 local input / 1,283 local output tokens over three worker requests. The local worker handled 8,811 tokens; this is not a claim of 8,811 cloud tokens saved. The report found no missing usage in that fixture and made no inference request. The fixture counts include parent polling/coordination prompts and any reported cached input.

## Plan submission rejection and recovery fix

A runtime regression could turn a normal `plan_submit` precondition rejection (Work workflow, missing plan, or unresolved questions) into `RecoveryRequired`: validation ran after the invocation was marked started, and the generic effectful-tool handler found no receipt. Plan update revision/content checks had the same ordering problem.

Preparation now validates plan submission/update conditions before execution intent and before the worker coordinator can stop children. Execution retains update validation before process shutdown. Instructions distinguish Work task tracking from Plan submission. Recovery can close an older started submission without replay or approval when no atomic receipt exists; committed submissions retain their existing review. File/process recovery rules are unchanged. The user's exact saved run was not available in the agent-visible profile store, so regression fixtures cover the reported state instead of claiming that run was inspected or repaired.

Verification: 184 Release tests passed (146 core/provider/tool tests and 38 CLI integration tests), including Work-mode/missing-plan/unresolved-question rejections, stale updates, before/after-commit failure injection, and legacy submission recovery. The self-contained Windows build was replaced after the user closed it. A synthetic legacy run resumed through the published executable: exactly one call result was recorded, its draft/revision remained intact, and no review, approval, or inference was invented. Evidence: `artifacts/bonsai2-smoke/plan-rejection-core-tests.txt`, `plan-rejection-tests.txt`, `plan-rejection-publish.txt`, and `plan-recovery-resume.jsonl`. No commit or public publication was made.

## Bonsai 2 Chat Completions endpoint

Added `--provider local` for an already-running loopback server, defaulting to `http://127.0.0.1:8080/v1`. Discovery uses `/v1/models`; inference uses streamed `/v1/chat/completions`. This is a separate protocol from the retained LM Studio Responses adapter. The exact model discovered was `E:\Projects\AgentSmith\bonsai\models\bonsai2-gguf\27B\Ternary-Bonsai-2-27B-PQ2_0.gguf`, with 32,768 loaded context tokens and one server slot. No server configuration or model files were changed.

Implemented local history translation, correlated streamed tool calls/results, separate reasoning continuation, normalized usage, bounded SSE parsing, cancellation, explicit provider routing, and local terminal headers. Invalid/incomplete calls, unsuccessful finish reasons, missing stream terminators and reasoning-only responses fail before tool dispatch. The same factory handles parent inference, workers, resume and tool-free compaction. Existing schema v5 identities and profile snapshots remain unchanged; configured `bonsai-edit` and `bonsai-review` profiles were replaced for new conversations. No API key, account credential forwarding, retry, image input or remote endpoint support was added.

Transport behavior follows the [llama.cpp server contract](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md). The model catalog does not independently certify tool quality; actual tool interaction is part of acceptance. The old character budget remains an estimate, not a promise that a 32,768-token server context can fit every request below it.

Live local-only conversation `db8cf318651042adae9c4f2bb4842d2b` read `evidence.txt` through `file_read`, received the correlated result, and quoted its exact text. It completed in 9.3 seconds. Evidence is retained under ignored `artifacts/bonsai2-smoke/`; no new source commit or public publication is requested for this endpoint change.

Live ChatGPT parent `ab9978e5bacb45f8bebf6b93d60d6e88` delegated the arithmetic fix to Bonsai 2 through the published CLI. The worker finished in three model rounds (98.1 seconds), applied checkpoint `556c0c2afc834cc48ddc2c74a806d24c`, and the parent collected its report and independently read the corrected file. The parent completed in 115.8 seconds. Both `unrelated.txt` and `evidence.txt` retained their hashes. This focused transport/worker acceptance did not run a build or tests inside the fixture; repository regression tests are reported separately below.

An earlier read-only worker completed in 65.6 seconds under parent `cff61be14a4e4777b443f40241985801`. Its concurrent editing worker encountered mixed line endings introduced by the test fixture and repeatedly hit the existing patch rejection until its five-minute deadline. No patch was applied to that fixture; the paused test parent was explicitly abandoned. Repeating with a consistently LF-encoded fixture produced the successful edit above. Evidence: `parent-events.jsonl`, `settled-fixture-run.jsonl`, and `clean-parent-events.jsonl` under the same ignored evidence directory.

Release verification passed **176 tests** (139 core/provider/tool tests and 37 CLI integration tests). New coverage includes fragmented/interleaved calls, tool-result correlation, incomplete streams, malformed calls, reasoning-only completion, output-limit finish reasons, cancellation, event bounds, credential rejection, URL normalization, persisted provider routing, unchanged old profile snapshots, and local header rendering. The self-contained Windows x64 build was refreshed; published local model discovery and the updated worker profiles were verified. A Debug rebuild collided with the active acceptance CLI's loaded DLL; the subsequent Release build/test/publish completed successfully.

### Earlier milestone delivery records

Follow-up profile diagnostics: the user's PowerShell returned an empty worker list while agent-side checks returned two profiles. The cause of that discrepancy is not established. `workers list`, successful registration, and unknown-profile errors now expose the effective data directory; missing-profile errors also list available names. Profile lookup occurs before network/account setup and reads one consistent list. A focused three-test CLI suite passed, including distinct configured/empty stores, case-insensitive selection and duplicate removal. Registering profiles and starting chat must use the same explicit store in the user's terminal.

The continuous-conversation and LM Studio/worker milestones below were implemented, verified, published to the local Windows build folder, and committed at the user's request in `16c5e2d`. No remote push or public publication was included. These sections retain their original test counts and initial no-commit statements as historical records; their verification total was 158 passing tests.

## Delivered milestone: LM Studio and Model-Specific Subagents

Implemented on 2026-10-09. Initial configuration is a ChatGPT parent with `bonsai-edit` and `bonsai-review` profiles using the loaded `prism-ml/bonsai-27b` model at `http://127.0.0.1:1234`. The server reports 262144 loaded context tokens. Provider selection is persisted; local inference and compaction use the same factory and do not require account credentials.

### Delivered behavior

- Separate LM Studio Responses adapter, flat function schemas, streamed text/calls, loaded-model discovery, 8192-token output budget, server-default reasoning, and no automatic fallback/retry. Reasoning-only or output-limited results fail before dispatch.
- Schema v5 with consistent v4 backup, durable profiles, atomic parent/run/call spawn identity, independent child sessions, reports, lifecycle events and reservations. Existing conversations remain ChatGPT with delegation disabled.
- `smith workers add|list|remove`, `--provider`, `--endpoint`, `--max-output-tokens`, `--workers`, `/agents`, and `/agent <id|#number> [cancel]`.
- Parent tools `agent_spawn`, `agent_wait`, `agent_result`, `agent_cancel`; worker-only `agent_finish` ends dispatch, including subsequent calls from that model response.
- Two concurrent workers, eight launches per parent run, ten rounds/five minutes per worker within the parent deadline. Workers read and edit assigned files but cannot launch arbitrary processes, create subagents or control user decisions.
- Exact file ownership with case/ancestor checks, both move endpoints, existing workspace/link exclusions, serialized mutation critical sections and unchanged stale-hash/checkpoint rules. Editing workers require stopped processes; their reservations block parent edits, undo and process launches.
- Parent completion and FIFO follow-ups wait for collected results. Cancellation settles the task tree and holds queued prompts. Restart interrupts children without relaunching, retaining uncertain reservations for explicit recovery.
- Parent-visible child reports/transcripts and checkpoint undo; explicitly shared registered artifact excerpts use child-local copies. Terminal activity includes worker identity, model/profile, state and elapsed time; child raw text does not interleave into the composer.

### Observed acceptance

- Automated verification: `dotnet test AgentSmith.slnx` passed 158 tests (123 core/tool tests and 35 CLI integration tests). This includes worker persistence-failure injection before effects, after effects, and before result persistence; provider/credential boundaries; reservations; report/queue barriers; and all existing regression suites.
- Real ChatGPT (`gpt-6.1-sol`) parent conversation `047827604fcb411da17f1d749f9e6098` delegated separate Arithmetic and Parity fixes to two real Bonsai workers. Each finished in four model rounds with one applied checkpoint. The parent independently inspected the diff, obtained build exit 0, and read a complete VSTest report with 5 passed, 0 failed, 0 skipped.
- An unrelated `notes.txt` edit remained byte-for-byte unchanged. After restarting, worker artifacts were readable and both child checkpoints were undone. Git then showed only the unrelated note diff; source defects were deliberately restored as undo evidence.
- A local-only Bonsai parent read `notes.txt` and quoted its actual content in 21.5 seconds.
- A live editing-capable Bonsai worker held its assigned file reservation while injected parent `file_create` and `process_start` attempts were rejected before effects; the worker then cancelled cleanly. Evidence: `artifacts/lm-worker-live-guards.json`.
- Forced termination of an earlier test CLI left five durable child identities. Resume marked its two unfinished workers interrupted, retained the completed/failed records, and created no replacement workers. No uncertain effects were replayed.
- Earlier live attempts against compact one-line code produced incorrect unified patches, rejected by the exact-context guard, and some workers reached their round limit. Separate worker instructions and clear patch-format guidance improved the subsequent run; successful acceptance is not a guarantee of model reliability.
- Live Plan conversation `18c166caa1f140a4b07176b6f0f9ccbc` collected a blocked Bonsai report, exposed its clarification through the parent, kept a queued follow-up pending, and accepted an explicitly addressed answer before continuing.
- Live cancellation conversation `83121561f8cc47c6bac3ccfda9ae4388` cancelled its real worker, held two submitted prompts, and ran them as distinct FIFO runs after `/queue continue`. Cancellation used the same `/cancel` path as Escape; the new worker test did not require a physical keyboard event.
- Real LM Studio compaction in `c2ff9664420a4ed280b5595a656f2e59` reduced request context from 48715 to 15626 estimated characters in 99.6 seconds. This used supplied fixture history; the source history hash was unchanged. Local-only file interaction was independently tested above.
- Additional live workflow evidence is recorded in `artifacts/lm-worker-workflow-summary.json`.

Evidence is local and ignored: `artifacts/lm-workers-formatted-events.jsonl`, `lm-workers-observed.json`, `lm-local-parent-events.jsonl`, `lm-workers-crash-resume.jsonl`, `lm-worker-undo-events.jsonl`, and `lm-worker-workflow-summary.json`. The disposable repository is `artifacts/lm-workers-formatted-20261009`. Release publish succeeded with `dotnet publish src/AgentSmith.Cli -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64`. Published help, profile listing, and live local model discovery passed; the saved user PATH includes this folder (an already-running shell may need a new terminal). Source, tests, and documentation are included in the requested local commit; generated builds, private local state, acceptance fixtures, and the unrelated `bonsai/` directory are excluded.

Deferred: authenticated/remote local servers, model downloads/loading, image input, nested workers, worker shell/build/test tools, separate worktrees/change integration, and provider switching inside a saved conversation. File reservations coordinate the current conversation tree, not unrelated applications or separate AgentSmith conversations.

## Delivered milestone: Readable, Continuous Conversations

**Status: implemented, verified, and published locally.** This milestone follows Plan, Track, and Verify. The sections below retain the agreed implementation contract, with actual behavior and verification notes taking precedence.

### Delivered behavior and evidence

- Typed activity summaries, stable process aliases, readable task descriptions, compact/verbose output, bounded previews, stream-line assembly, and sanitized call details are implemented.
- Schema v4 stores summaries/attempts and an independent durable prompt queue. Queue claims create their user message and run transactionally. Context projection preserves original history and complete protocol groups.
- Automatic compaction uses ordinary tools-disabled ChatGPT requests, the 96,000/72,000 character trigger/target, 48,000-character summary inputs, 8,000-character outputs, and at most eight calls per attempt.
- The scrolling composer and status row share one console owner with question/review pickers. Escape and /cancel stop current work; queued follow-ups stay held until explicit continuation. Read-only busy commands operate on committed snapshots, not the runner's mutable session.
- Redirected chat requires addressed question/review responses after concurrent input begins, even if no prompt has yet entered the queue. This closes the completion/input race. Idle legacy resume without queued input keeps existing line answers.
- Live ChatGPT conversation `00da3506ae174e76baf4f1b83ea427c1` completed nine runs in FIFO order with two compactions: **68,086 → 22,835** and **64,809 → 20,219** estimated characters. The acceptance harness used an 80,000-character guard to exercise compaction sooner; the shipped default remains 120,000.
- A real clarification blocked queued work until an explicitly addressed Addition answer. The agent then repaired the disposable calculator through a hash-checked patch, completed structured build/test verification, searched retained artifacts, and finished. Checkpoint undo restored the original file hash; the unrelated note was unchanged.
- Native terminal Escape acceptance used conversation `9540cb86674d4da8b5ddc72c8890f287`: a real `pwsh` process stopped with termination confirmed, the submitted follow-up stayed pending, and `/queue continue` released it to return `ESC_QUEUE_OK`.
- Evidence is local and ignored: `artifacts/continuity-live-events.jsonl`, `continuity-live-summary.json`, `continuity-live-session.txt`, plus the review/crash/restart evidence described in the final verification record. Fixture files intentionally contain the original defect after successful undo.

The terminal keeps normal scrollback and a bounded horizontal composer. `/details` shows sanitized activity identity/arguments and timing; retained process content is available through artifacts. In-place status shows run elapsed time; process transitions retain append-only summaries. Display previews do not reduce the model's original tool results; context compaction handles that separately.

### Scope and user-visible behavior

1. Show useful arguments and outcomes beside tool activity; reduce repeated polling and oversized output.
2. Automatically compact request context so long conversations can continue without discarding their saved history.
3. Let Escape cancel active work while preserving question/review semantics and recovery guarantees.
4. Accept additional prompts while the agent works and start them in order immediately after the preceding prompt completes.

Keep serial dispatch, ChatGPT account sign-in, the provider abstraction, Work/Plan workflows, Inspect/Edit/Execute permissions, checkpoints, durable questions, and Windows process ownership. Do not add mid-request steering, parallel agents, process reattachment, or a background daemon. Delivery ends with verification and refresh of the existing local Windows publish folder; no commit or public release is included.

### Final verification and local delivery — 2026-10-09

Release verification: **131 passed, 0 failed** (97 core/runtime/tool tests and 34 CLI/terminal/integration tests). The CLI integration suite was rerun after the final process-details display adjustment and all 34 passed. Build and self-contained Windows publish succeeded. Updated executable: `artifacts/publish/win-x64/smith.exe`.

Additional live gates passed:

- Review conversation `e031af561b654614b94fd5fdaa840bc3`: queued input waited for explicit implementation review, the approved workflow ran first, then the follow-up completed. Exit code 0.
- Crash conversation `76a7f64af7d748e0ad91272853ee9d78`: killed a test CLI after dispatch and process launch. Its owned child exited, the side-effect marker contained exactly one line, and restarted inspection/abandonment issued no tool calls. The remaining prompt stayed queued.
- Published restart recovered the compaction snapshot and artifact registry without a new run or tool replay. Published no-color output contained no ANSI escapes; `/details #1` showed the launch command and stored call timing.
- Structured live verification reported **4 passed, 0 failed**, build/test exit codes 0, and confirmed process termination. Undo and unrelated-file hash checks passed.

Acceptance found and fixed two integration bugs: Windows' synchronous implementation of console `ReadLineAsync` could delay a decision transition, and a workflow-generated implementation prompt could mistakenly enter the ordinary FIFO. Both have dedicated regression tests. Redirected reads now use one persistent reader task; explicit plan continuation takes priority over queued user prompts.

Evidence: `artifacts/continuity-release-tests.txt`, `continuity-integration-final.txt`, `continuity-publish.txt`, `continuity-plan-review-summary.json`, `continuity-crash-summary.json`, `continuity-crash-restart.jsonl`, `continuity-escape-summary.json`, `continuity-published-restart.jsonl`, and `continuity-published-text.txt`. All evidence and disposable fixtures remain ignored. No commit, remote repository change, or public publication was made.

### 1. Readable tool activity and bounded output

Extend tool-start events with a typed display summary derived from validated arguments. Do not copy arbitrary argument JSON into normal output. Share the formatter between live activity, replay, and detailed inspection; presentation remains outside the runtime's execution policy.

| Tool family | Compact presentation |
| --- | --- |
| Read/list/search | Relative path, line range or query, scope, and requested limit |
| File mutation | Source/destination, observed added/removed lines, checkpoint reference |
| Process start | Executable, bounded argument preview, working directory, short process label |
| Process poll/input/stop | Same process label, changed status, new output or submitted byte count |
| Git inspection | Operation, staged/unstaged selection, revision or limit |
| Build/test | Project/solution, configuration, framework/filter when supplied, verification reference |
| Plan/task update | Revision and changed step descriptions/statuses, not bare S1/S2 labels |
| Artifact access | Artifact label, range/cursor or search text, result count and completeness |

Use normal-contrast commands/paths, dim secondary metadata, and status text/icons that remain intelligible without color. Escape terminal control sequences and bound summaries to terminal width. Long PowerShell scripts and patches get a preview and detail reference. Environment values, stdin contents, credentials, and recognized secret arguments are omitted or redacted in summaries and detail output; avoid adding new raw argument copies to diagnostic events. Do not claim arbitrary user content can be perfectly classified as secret.

Give processes stable conversation-local aliases such as `#1`, mapped to durable IDs and retained across restart. Accept aliases in `/processes`, `/stop`, and details while retaining full IDs for machine consumers. Explicitly distinguish tool-call duration, total process duration, and overall active response duration; a poll's wait is not total process runtime. Show exit status and whether termination was confirmed.

Suppress unchanged polls with no new output in compact mode. In line-oriented output, show a throttled heartbeat at most every 30 seconds while the process remains active, plus every meaningful status change. Keep all calls/results in storage and structured JSON. In-place status updates are a later step within this milestone, after the input/render coordinator exists; fall back to append-only output for redirected or limited terminals.

Coalesce process chunks into logical lines per stream, retaining partial lines across polls. Label a contiguous stream block once, preserve stdout/stderr identity, and flush a remaining partial line on exit or display it as partial when appropriate. Do not invent ordering between the two streams. Empty chunks must not create blank labeled rows. An unterminated line is bounded and cannot block display indefinitely.

Default compact preview budget: 20 lines or 4 KiB per tool result, whichever is reached first. For larger results show head/tail excerpts with an explicit omitted count and an artifact reference/cursor; preserve error state, exit code, and truncation outside that budget. Detailed artifact access provides the retained content within existing bounds. This display budget does not change process retention or tool-result protocol limits.

Local commands: `/display compact|verbose` and `/details <call-id>` (also accept an unambiguous process alias). Verbose exposes bounded call/result details and empty polls without changing execution. `/tasks` continues to show the complete plan; normal activity shows only changed task descriptions. JSON retains structured data with additive, documented event fields. Old saved events without argument summaries still render safely.

Example layout (illustrative command):

```text
│ › Run dotnet test AgentSmith.slnx --configuration Release
│   E:\Projects\AgentSmith · process #1
│   stdout
│     Passed: 111 · Failed: 0 · Skipped: 0
│ ✓ Finished · process 8.4s · exit 0
```

### 2. Automatic context compaction

Separate the append-only source history from the context projection assembled for inference. Compaction replaces older material only in that projection; it must never delete messages, protocol items, checkpoints, artifacts, decisions, or recovery records. Renderer compression alone does not reduce model context.

Extract the existing inline context assembly from `AgentRunner` into a testable context builder and add a compaction service. Keep the initial implementation provider-neutral using ordinary model requests with tools disabled; do not assume a provider-hosted compaction endpoint exists on the account route. Use the selected account/model without silent fallback. Verify request compatibility during implementation.

Budget policy:

- Count instructions, available tool schemas, approved scope/current tasks, the current request, unresolved state, summaries, and retained exchanges. Pending queue text is not model context until dispatched.
- Initially use the existing conservative 120,000-character guard, explicitly labeled as an estimate. Trigger automatically at 80% and aim for at most 60% after compaction, leaving headroom for responses and tool results. These thresholds are the shipped defaults, not token limits or provider guarantees. Use validated model metadata/token accounting when available without equating character counts with tokens.
- Run only at a protocol-safe boundary before the next model request, after completed response items and tool results have been persisted. Never split required call/result groups or opaque continuation groups. Defer while an unresolved call/review is awaiting resolution; if no valid boundary exists, pause rather than drop required state.
- Keep the active user request, recent complete exchanges, unresolved questions/reviews, current plan and approval ceiling, pending/recovery operations, and active process/artifact references. Load authoritative permission and plan state from storage on every request. A file hash in a summary is historical evidence; editing still requires a fresh read.
- Summarize older completed exchanges into goal, decisions, constraints, observed work and verification, unresolved issues, and source references. Distinguish user instructions, project/tool data, and assistant conclusions; do not promote quoted content into instructions. Summaries cannot approve a plan, answer a question, change permissions, or mark verification successful.
- Bound the summarizer's own input/output. For oversized histories use deterministic protocol-complete chunks and a bounded merge; never send the already oversized history as one request. Retain source ranges, hashes, prior-summary lineage, prompt version, model ID, and measured before/after sizes. Source history remains available for rebuilding and auditing repeated summaries.
- Limit one compaction attempt per unchanged history boundary, including a bounded number of chunk requests. Respect the active cancellation/time budget and report compaction usage separately. Do not automatically reset step/time limits or loop forever if mandatory context cannot fit.

Persist a completed summary and its source boundary transactionally before activating it. Interrupted, malformed, empty, or over-budget summaries never replace the prior context. A failed attempt leaves history intact and pauses with a reason and retry path; credentials/access/network failures remain explicit. Compaction can run during Work or Plan but cannot use workspace/process tools.

Emit `context.compaction.started`, `context.compaction.completed`, and `context.compaction.failed` with reason, source range, estimated sizes, duration, and available usage. Display one compact notice. Extend `/context` to show active summary provenance and retained context. `/compact` requests the same operation while idle or paused at a context limit; it does not resume unfinished work or resolve pending decisions. Automatic compaction before the next request needs no additional approval.

### 3. One input owner and Escape cancellation

Replace competing console reads with a single input coordinator. It owns key decoding, the editable composer, question/review picker focus, paste mode, EOF, and redirected input. Run model/tool work asynchronously; serialize state changes through the conversation owner and route rendering through one output coordinator so background output cannot overwrite typed text.

During active inference, tool work, or compaction, Escape requests cancellation once, shows `Cancelling…`, and leaves the CLI alive. Ctrl+C follows the same run-cancellation path. Stop dispatching new calls, cancel the model request, stop processes owned by that run, drain bounded final output, and persist observed outcomes before returning to input. Completed mutations remain in checkpoints; uncertain effects stay RecoveryRequired. Cancellation is not rollback and must not claim termination before it is confirmed.

Escape in a question or plan-review picker leaves that decision pending, with no default answer or approval; preserve the existing rule that unsent picker notes are discarded. With no active run, Escape may dismiss a transient view but does not exit or submit/clear the composer. Escape during an active run preserves both the unsent follow-up draft and already submitted queue items. Key autorepeat must not cancel a subsequent run accidentally. Queued work is held after cancellation until explicit continuation.

Redirected input has no physical Escape semantics: retain Ctrl+C signal handling and use `/cancel` as a local control in line-oriented chat input. Literal escape characters in pasted/redirected text must not act as key presses. Unknown slash commands remain local errors. Single-request `smith run` retains its existing exit semantics and does not become an interactive queue consumer.

### 4. Durable FIFO prompt queue

Keep a composer visible while a run is active. Submitting a nonempty prompt with Enter persists a queue item and displays its ID, position, and a bounded preview. Typing without submission remains a local draft. Queue items are user-authored follow-up requests, not tool calls, live steering, answers, or plan approvals.

Queue limits: 20 pending prompts and 32 KiB UTF-8 per prompt; reject excess visibly without discarding the composer. Persist ID, conversation, ordered sequence, submitted text/time, status, and eventual run ID. States are Pending, Dispatched, Completed, and Removed; queue-level hold reasons describe cancellation, failure, limits, decisions, recovery, and restart. A run's failure/cancellation remains on its linked record and does not requeue it automatically.

Dispatch rules:

| Situation | Queue behavior |
| --- | --- |
| Preceding run is Completed | Immediately dispatch the oldest item as one new run after final result persistence; this is turn completion, not proof of task success |
| Awaiting question or plan review | Keep the queue pending; require input explicitly addressed to that decision, then finish the existing workflow before dispatch |
| Cancelled, Failed, Paused, or RecoveryRequired | Hold the queue and explain why; explicit continuation is required after resolving blocking state |
| CLI exit, EOF, or crash | Retain submitted items; after restart show the queue and require explicit continuation, never silently start work |
| Work/Plan or permission change | Re-evaluate at dispatch; never inherit broader permissions from a queue item's submission time or bypass a pending review |

Append the queued user message to model history and create/link its run in the same transaction that claims the queue item. This prevents a crash from turning one submitted item into duplicate runs; uncertain external effects still follow normal recovery, not an exactly-once guarantee. If arrival races with completion, the serialized owner either dispatches it immediately while idle or takes it after the current run, preserving FIFO order.

Answers and review decisions use their existing durable IDs and focused input flow. A pending queue item must never be consumed as an answer, even if it contains `implement execute`. Choosing Implement can create its existing workflow-linked implementation run; queued follow-ups wait until that workflow finishes. For redirected chat, use explicit `/answer <question-id> <text>` and `/review <review-id> <decision>` after concurrent chat input begins or a queue exists, rather than treating unread stdin lines as implicit answers. Preserve ordinary idle line input and document this distinction.

Queue commands: `/queue` lists pending items and hold state; `/queue remove <id>` and `/queue clear` remove only pending items; `/queue continue` resumes dispatch when barriers are resolved. `/cancel` cancels the active run without deleting queued items. Do not add editing/reordering in the first slice. Slash commands execute locally or return a clear busy error; they are never silently queued as model prompts. Mutating commands such as `/undo`, `/mode`, and `/plan new` require an idle transition; read-only status/queue controls and cancellation remain usable while busy. Explicit `/paste` mode continues to permit literal slash-prefixed prompts.

The scheduler remains inside the current CLI instance and holds the existing exclusive conversation lease. Process sessions can outlive a completed prompt within that instance under existing rules; the queue does not promise that all background processes have exited. It does not introduce a daemon. JSON adds `prompt.queued`, `prompt.dispatched`, `prompt.removed`, and `queue.held` events with stable IDs; display coalescing never drops durable events.

### 5. Storage and implementation order

Schema v4 with a SQLite-consistent v3 backup and a transactional migration. Preserve conversation IDs, workflows, permissions, history, reviews, call IDs, journals, and artifact references. Add context snapshots/lineage, compaction attempt receipts, queue items/run links, and stable activity aliases. Existing sessions start with no summary or queued prompts. Recovery can reuse a committed summary and a claimed queue item but never repeat a dispatched run automatically.

1. Add typed activity summaries, task descriptions, process aliases, bounded previews, chunk-aware stream formatting, and compact/verbose rendering.
2. Add v4 persistence and extract context assembly; implement compaction with failure/restart tests before enabling its automatic trigger.
3. Introduce the input/output coordinator and active-run Escape cancellation; retain picker behavior and redirected fallbacks.
4. Add durable enqueue/dispatch transactions, hold barriers, local controls, and concurrent composer rendering. Only then add optional in-place process status.
5. Run regression and live acceptance, update all three documents with actual outcomes, and publish the verified Windows x64 build to the existing local folder.

### 6. Verification and acceptance gates

Automated coverage must include:

- Tool argument summaries, malformed/legacy events, secret/control-character filtering, narrow terminals, Unicode, no-color/redirected output, JSON compatibility, and process aliases across restart.
- Empty-poll suppression without losing final/error states; fragmented lines, interleaved streams, huge/unterminated lines, preview omissions, retained artifacts, and accurate duration labels.
- Compaction threshold/headroom accounting, bounded chunk requests, protocol/opaque-group preservation, plan/question/approval state, repeated summaries with provenance, and a mandatory context set that cannot fit.
- Compaction interruption before/after commit, stale history boundaries, invalid summaries, provider failure, no new permissions, no repeated effects, unchanged raw history, and pending queue text excluded until dispatch.
- Escape during streaming, process start/poll, mutation, and compaction; cleanup confirmation and RecoveryRequired behavior; repeated Escape; preservation of composer and pending queue; picker Escape without answer/approval.
- FIFO submission during output, completion/enqueue races, empty and oversized input, queue limits, explicit question/review targeting, removed items, local unknown commands, mode/workflow barriers, and no parallel runs.
- Failure injection around enqueue/claim/run creation and run completion, v3 migration, restart holds, conversation-lock contention, EOF and redirected/JSON behavior, and no silent duplicate execution.
- All existing authentication, streaming, file/process recovery, Plan/review, artifact, .NET verification, Markdown, and question-picker tests.

Live acceptance must use ChatGPT and a disposable workspace, not only scripted models:

1. Run a command producing fragmented stdout/stderr and enough output to exceed previews. Inspect useful arguments, compact progress, final exit/duration, and retained output through artifacts.
2. Cross a deliberately reduced test context threshold over several completed tool exchanges. Observe automatic compaction and continuation in the same conversation, retained decisions/references, and independently checked task results. Restart and confirm source history and summary lineage remain available.
3. Queue two different follow-ups while the first request runs. Verify visible FIFO acknowledgements, distinct runs started immediately on completion, and no early incorporation in the active request.
4. Repeat with a clarification and a plan review. Confirm queued text never answers or approves them and waits indefinitely until explicit input.
5. Press Escape during a long process, confirm process-tree termination or an explicit unconfirmed outcome, retain queued prompts, and continue them explicitly. Force a test CLI crash around dispatch and verify no duplicate effects on restart.
6. Preserve an unrelated file edit, validate checkpoint undo, then publish locally and smoke-test normal, no-color, redirected, and JSON modes. Record measured outcomes and any incomplete acceptance honestly.

## Delivered milestone: Plan, Track, and Verify

The CLI now adds a persisted Plan workflow alongside Inspect/Edit/Execute permissions, durable task revisions and review decisions, scoped artifact access, and structured .NET verification. The existing coding-agent milestone is retained below as earlier evidence.

### Implementation

- Schema v3 backs up v2 with SQLite's backup API and migrates existing sessions to Work without changing permissions or tool-call identities. Plans, receipts, artifacts, and verification records have transactional projections alongside the session aggregate.
- Plan workflow filters tools and validates permissions again at dispatch. Entering Plan stops owned processes. Undo and arbitrary process/build launches remain blocked regardless of `/mode`.
- `plan_read`, `plan_update`, and `plan_submit` persist revisions, stable task IDs, verification notes, references, and review history. Routine progress uses null scope fields to preserve approved wording exactly. Actual scope changes disable implementation and return to review.
- The Implement/Revise/Keep planning picker persists across EOF/restart. Implement requires explicit Edit/Execute selection. Old queued calls are closed without execution, and implementation starts a linked new run. A crash between decision and new run leaves an explicit continuation request.
- `artifact_list`, `artifact_read`, and `artifact_search` read only registered conversation artifacts. Reads/searches are bounded; process log chunks are decoded by stream. Search handles a match split across chunks. Links, traversal, other conversations, and credential paths are rejected.
- `dotnet_build` uses a bundled MSBuild logger for diagnostic events. `dotnet_test` retains VSTest TRX reports with unique invocation prefixes. `verification_result` normalizes outcomes and stores a complete local report with bounded model excerpts. SDK selection is limited to .NET 10; explicitly selected MTP test runners are unsupported.
- Structured launches disable persistent build servers, shared compilation, and node reuse so build work remains in the CLI-owned process tree. Existing cancellation, job ownership, process limits, and recovery apply.
- Header/status, `/plan`, `/tasks`, `/artifacts`, `/artifact`, terminal progress, and structured JSON events are integrated. Planning remains subject to the existing context limit; no compaction was added.

### Verification

Final Release suite: **111 passed, 0 failed** (86 unit/runtime/native/report tests and 25 CLI/terminal/integration tests). Build succeeded with zero warnings/errors. The self-contained Windows x64 folder was refreshed, including the diagnostic logger. Published CLI help exposes `--plan`; a published restart smoke check read task progress and searched the retained passing-test output without invoking the model or replaying tools.

The expanded suite covers workflow/mode enforcement, pending-call review barriers, revision conflicts, receipt recovery before/after persistence, scope changes, migration, review EOF/restart and mode selection, stopping processes when entering Plan, artifact boundaries/chunks/cursors/junctions, and real .NET diagnostics and TRX parsing. The .NET test fixture exercises two projects with the same name across two target frameworks: all four reports and all 12 passed/failed/skipped case results are retained. Malformed XML, DTDs, inconsistent counts, unavailable reports, interrupted processes, and zero tests have explicit non-successful report states.

Live acceptance on 2026-10-09 passed with `gpt-6.1-sol`, conversation `147d105580d341f9a811e1b30c41f291`, plan `e7f2ede11054409a93df60213c06fc27`:

1. Plan workflow asked an explicit addition-versus-subtraction clarification, inspected the fixture, saved a plan, and paused at review without workspace mutations or build launches.
2. Restart retained the pending decision. A revision note was submitted; the updated plan was reviewed and explicitly approved for Execute.
3. The agent refreshed evidence, applied a one-line patch, and updated the three saved task statuses.
4. Structured build verification completed with exit 0 and no diagnostics. Structured tests completed with exit 0 and **4 passed, 0 failed**. An independent test run confirmed the same result.
5. `artifact_search` found the passing result in retained test output. The agent compared the note/test hashes and inspected Git changes; unrelated content and staged state were preserved.
6. Restart showed one approval, four plan revisions, three completed steps, and retained artifacts.
7. `/undo d85231267f374b3da7feb384e4c4dbb5` restored the original calculator SHA-256 `c962a57a6d7df06c35f48a50af3c997be5b352158e0d0fdaa843cee13582dfae`. The unrelated note and tests retained their original hashes.

Local ignored evidence: `artifacts/live-plan-final-question.jsonl`, `live-plan-final-review.jsonl`, `live-plan-final-revision.jsonl`, `live-plan-final-implementation.jsonl`, `live-plan-final-restart.jsonl`, and `live-plan-final-undo.jsonl` under `artifacts/`.

An earlier acceptance attempt exposed full-scope rewriting during progress updates; this now has a scope-preserving update form and explicit review feedback. That long conversation later hit the documented context limit and was safely paused/undone. Compact update acknowledgements avoid repeatedly adding full plan snapshots to model history; the successful fresh acceptance remained within the same context bound.

## Earlier milestone: Edit, Execute, Verify

The CLI now uses our C# engine for ChatGPT chat, workspace inspection, checkpointed file changes, builds/tests, process sessions, and recovery. New conversations default to Execute. Existing schema-v1 conversations migrate to Inspect. Enabled actions run without repeated approvals.

The [README](README.md) describes commands and limits. [DESIGN.md](DESIGN.md) retains the broader roadmap. This milestone includes a local Windows x64 publish only: no commit, remote repository, or public release.

## Delivery order and implementation

### 1. Registry, modes, and migration — complete

- Core defines capability metadata, prepared operations, and execution context with conversation/run identity, workspace, cancellation, persistence, and artifact access.
- ToolRegistry provides the definitions used by the model, CLI help, and `/tools`. Inspect enables reads/questions/Git; Edit adds file mutations; Execute adds processes.
- Tool calls are prepared and journaled before effects. Dispatch remains serial and follows persisted completion of the model response.
- `--mode` chooses a new session mode; `/mode` persists changes. Downgrading from Execute stops owned conversation processes.
- SQLite v1-to-v2 migration uses a consistent backup and a transaction. Original conversations, call identifiers, and durable questions survive.

### 2. File operations and checkpoints — complete

- UTF-8 create, exact unified patch, move, and single-file delete.
- Full-file SHA-256 from partial or complete reads; existing-file mutations reject stale hashes.
- BOM and consistent line endings preserved; unsupported/binary/oversized files rejected.
- Existing ancestors and workspace exclusions validated; link/junction traversal and overwrite collisions rejected. Missing parents can be created and are recorded.
- Staged writes with flush and atomic replacement where supported. Each operation is independent, with original/result bytes and hashes, metadata, identity, paths, and duration saved in the mutation journal.
- `/changes` reviews bounded checkpoint diffs. `/undo` journals the reverse change only when current content matches recorded results; it preserves unrelated work and removes owned parent directories only when empty.
- File checkpoints do not cover process side effects or all repository changes. Git reset/clean are not used.

### 3. Process lifecycle — complete

- Explicit executable resolution and separate argument array; PowerShell through `pwsh -NoProfile` with explicit script arguments.
- Windows native suspended launch, restricted pipe inheritance, kill-on-close Job Object assignment, then resume. Failed containment prevents command execution.
- Concurrent stdout/stderr draining, stdin and EOF, bounded cursor polling, four active processes per conversation, default ten-minute and maximum one-hour timeout.
- Retained JSONL logs capped at 20 MiB/process; output continues draining after truncation. Poll payloads stay below 32 KiB and advertise remaining retained output.
- Exit code, duration, reason, and termination confirmation persisted. Ctrl+C stops processes started by the current run; CLI exit/crash kills all owned trees.
- No terminal emulation, reattachment, or background daemon. Execute runs as the Windows user; working directories are not an OS sandbox.

### 4. Git, recovery, and terminal — complete

- Curated `git_status`, `git_diff`, `git_log`, and `git_show` with fixed arguments, repository checks, bounded output, no external diff/text conversion, fsmonitor, optional index writes, or pager.
- File recovery compares recorded pre/post content. Ambiguous outcomes remain unresolved; uncertain process launches and stdin writes are never replayed.
- Old process records become interrupted on restart. `/recover` inspects uncertainty; `/recover abandon` explicitly acknowledges it without repeating or undoing effects.
- `/tools`, `/mode`, `/changes`, `/undo`, `/processes`, `/stop`, and `/recover` integrated with the existing message guides and Markdown display. Unknown slash commands remain local errors. JSON remains structured.
- Questions remain durable and wait indefinitely for explicit answers.

### 5. Verification and local delivery

Release verification: **88 passed, 0 failed** — 66 unit/runtime/native-process tests and 22 CLI/terminal/integration tests. Existing OAuth, fragmented streaming, token exclusion, SQLite, question-picker, Markdown, cancellation, and correlation coverage remains passing.

New checks include mode enforcement and legacy migration; stale hashes, patch conflicts and header mismatches; encoding/BOM/line endings; collisions, bounds, and undo conflicts; injected failures before effects, after effects, and before result persistence; process argument quoting, spaces, stdin/EOF, stdout/stderr, nonzero exits, timeout, output caps, and cancellation by run; an HTTP development server; forced owner-process death with child/grandchild cleanup; and Git index preservation/external helper suppression.

Commands used:

```powershell
dotnet test AgentSmith.slnx -c Release --no-restore
dotnet publish src/AgentSmith.Cli -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64
.\artifacts\publish\win-x64\smith.exe --help
```

### Live ChatGPT coding acceptance — passed

Tested on 2026-10-09 with the signed-in account and `gpt-6.1-sol`. The disposable repository had a subtraction defect in `Calculator.Add`, four real xUnit cases, and a known unrelated unstaged edit in `notes.txt`. Its baseline was staged without making a commit.

1. Independent baseline run: 3 failed, 1 passed.
2. AgentSmith inspected files and Git status, read full-file hashes, and applied one exact patch using its file tool.
3. It started `dotnet test Checks/Checks.csproj -c Release` through process tools, polled to exit, and observed **4 passed, 0 failed, exit code 0**.
4. It inspected the final Git diff/status. The unrelated note and tests retained their hashes; no staging or commit occurred.
5. An independent test run confirmed 4 passing tests.
6. A fresh CLI resumed the conversation and executed `/undo` on the checkpoint. The calculator's original bytes/hash were restored; the unrelated note retained its exact hash.

Conversation: `19acd09bbfc249aeb44accb61a253788`.
Run: `f06e94ecc785455ca3bd80edc4c577cf`.
Checkpoint: `7e3e79ae0518454eae48fd0bb66ce72c`.
Undo checkpoint: `eb4fd36595994076b52e0a11c03094e7`.

Local ignored evidence: `artifacts/live-edit-acceptance.jsonl`, `artifacts/live-undo-acceptance.jsonl`, and `artifacts/live-edit-fixture`. The fixture intentionally contains the original defect again after successful undo.

### Live process ownership acceptance — passed

The self-contained `smith.exe` was exercised with a second live ChatGPT conversation, `d2ab647421e74b1ebd6de431297da1a0`. It read and launched two PowerShell scripts through process tools, delivered a line to the first script's stdin, and polled until the expected echo and server port were observed. An independent HTTP request received `AgentSmith live development server` from the second process.

The test forcibly killed only the CLI owner process. Both owned children exited within the verification timeout. A restarted CLI showed both saved records as Interrupted with unverified outcome, and issued no new tool calls. Separate automated tests verify explicit stop, timeout, and cleanup of a grandchild as well as a direct child.

Evidence: `artifacts/live-process-acceptance.jsonl`, `artifacts/live-process-acceptance-summary.json`, `artifacts/live-process-recovery.jsonl`, and `artifacts/published-cli-smoke.jsonl`. The user PATH already contains `artifacts/publish/win-x64`; the existing local deployment was refreshed.

### Earlier foundation acceptance — retained

Real browser sign-in, account-specific model discovery, file-tool inference, and restart continuation passed before this milestone. Conversation `603b1f7c8a35461c85804df8fad51879` returned the expected saved fixture value after restart with zero repeated tool calls. Evidence remains in `artifacts/live-first-fixed.jsonl` and `artifacts/live-resume.jsonl`.

## Remaining boundaries

Windows is the supported release platform. LM Studio and bounded worker delegation are implemented in the newer milestone above. API-key access, browser automation, MCP/plugins, desktop UI, and full terminal emulation remain deferred. Automatic compaction, improved activity display, active-run Escape cancellation, and queued follow-ups are implemented above. Current context uses model-aware budgets with a conservative character fallback and pauses when required context cannot be compacted safely. Mid-request steering and cross-instance process control remain outside this milestone.

Persistence uses a versioned aggregate with transactional projections; large-history optimization remains future work. File boundaries do not defend against an adversarial process racing filesystem changes. Process logs and conversations can contain workspace/user data. Checkpoints do not roll back commands.

Live evidence establishes the tested account and model route, not compatibility with every account or model. No automatic network retry, credential import from other applications, or API-billing fallback was added.


## Document and Review delivery

Implemented: Document/Review workflow policy; CLI entry points; parent report tools and durable revision receipts; documentation-only direct edits; read-only review and inherited worker restrictions; bounded pageable Git evidence with drift checks; checkpointed explicit report export; finalization barriers and automatic return to Work; context and terminal integration; schema v8 migration with a v7 backup.

Delivery verified on 2026-10-10 (local time): **250 Release tests passed** (193 core/runtime/storage/tool tests and 57 integration tests). Targeted workflow and renderer checks passed again after the final permission, coverage-warning, and presentation refinements. The existing self-contained Windows x64 folder was refreshed, staged/published assembly hashes matched, and published `chat --help` and conflicting-workflow option rejection were checked.

Live evidence: `artifacts/workflow-live-20261009-230748/summary.json` and `events.jsonl`, using ChatGPT `gpt-6.1-sol` and the exact Bonsai2 local model. Conversation: `6bc19216f0044fe9b17588d6da9b6980`.

- Review delegated a read-only audit to Bonsai, collected its report, and independently checked the known addition/subtraction regression. The parent rejected two unsupported worker claims and saved a defect plus a testing suggestion. Original workspace file contents remained unchanged. Explicit Markdown export succeeded.
- Document progress was interrupted immediately after a committed report update. A fresh CLI/store instance recovered the receipt, retained progress, and continued without duplicating the update. The queue remained held for restart.
- The first documentation worker exhausted its 8,192-token output budget and executed no incomplete proposed edits. The parent collected the failure and assigned a smaller README-only task; that worker completed and its report was collected.
- The parent inspected the README changes and verified compilation with structured `dotnet_build`, `process_wait`, and `verification_result`: exit 0, no warnings/errors. The documentation explicitly retained the unfixed arithmetic defect and labelled the example unexecuted; this was not a correctness-test claim.
- Both reports completed and returned to Work. Source and unrelated edits survived. Undoing the worker README checkpoint restored the original README. Reports, revisions, and artifacts remained available after reopening storage.

Restart acceptance used an injected interruption at the durable progress boundary followed by a fresh CLI/store instance, rather than an OS-killed interactive console. Physical keyboard/picker interaction was not repeated for this milestone. The final additional unread-diff coverage warning was verified automatically; the live audit itself ran before that presentation refinement. No project commit or public publication was made.
## Process-output readability — source implementation pending build

Related follow-up: actionable error presentation is also implemented in source and awaits the same build window. Tool results retain their legacy error string and add bounded structured reason/effect/next-action metadata. Specific cases cover missing paths, stale hashes, exact patch conflicts, reserved or unassigned files, unknown/inactive workers, and unavailable tools. Worker completion records now expose request-limit counts, applied file-operation counts, and report availability through events and `/agent`. Read, preparation, recovery, and SQLite failures no longer rely on one generic explanation; uncertain effects remain explicitly uncertain.

Added source tests cover persisted/correlated errors, redaction, storage categories, worker limits with retained edits, exact-path assignment rejection, inactive workers, no-color rendering, and legacy errors carrying checkpoint references. Tests and live verification have not been run, and no build or publish was performed. Existing running processes and live session data were left untouched.

Plan and source changes for the latest terminal report:

1. Filter complete terminal controls with independent, incremental stdout/stderr state. Preserve original retained logs; do not execute child terminal controls. Keep partial lines across output pages until the final retained page.
2. Frame process stream excerpts, shorten path-only listings by displaying their common directory once, and keep process state/exit status outside the bounded preview. Preserve prose and diagnostics verbatim apart from terminal-control filtering.
3. Show completed/total plan progress and abbreviated changed steps with `/tasks` for complete descriptions. Distinguish map depth, scan-count, path-character, exclusion and preview limits, and expose scan issues.
4. Add idle log access through `/artifact process:#number`, search, and explicit `read <cursor>` pagination. Resolve against the current conversation's persisted process records; retain existing artifact IDs and access checks.

Implemented in source. Added regression coverage for split ANSI/OSC controls, stream isolation and final-page handling, path-only shortening, and process-log lookup/pagination across restart with conversation ownership checks. `git diff --check` passed. Tests, compilation, visual acceptance and local publishing remain deferred at the user's request while their agent runs. No commit or schema migration.
