# AgentSmith CLI implementation plan

Updated: 2026-10-09

Delivery status: the continuous-conversation and LM Studio/worker milestones are implemented, verified, and published to the local Windows build folder. This documentation refresh accompanies the user-requested local source commit of both milestones. No remote push or public publication is included. Older delivery sections below retain their original test counts and no-commit statements as historical records; the latest verification total is 158 passing tests.

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

Windows is the supported release platform. LM Studio and bounded worker delegation are implemented in the newer milestone above. API-key access, Roslyn, browser/web tools, MCP/plugins, desktop UI, and full terminal emulation remain deferred. Automatic compaction, improved activity display, active-run Escape cancellation, and queued follow-ups are implemented above. Context still uses estimated character accounting and pauses when required context cannot be compacted safely. Mid-request steering and cross-instance process control remain outside this milestone.

Persistence uses a versioned aggregate with transactional projections; large-history optimization remains future work. File boundaries do not defend against an adversarial process racing filesystem changes. Process logs and conversations can contain workspace/user data. Checkpoints do not roll back commands.

Live evidence establishes the tested account and model route, not compatibility with every account or model. No automatic network retry, credential import from other applications, or API-billing fallback was added.
