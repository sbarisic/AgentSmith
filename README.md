# AgentSmith

AgentSmith is a Windows-first C# coding assistant with a terminal interface, ChatGPT and local-model providers, delegated workers, checkpointed file editing, process execution, and durable conversations. Work, Plan, Document, and Review workflows share permission checks and saved task state.

Licensed under the [MIT License](LICENSE). This repository distributes source, tests, and documentation only. It contains no binaries, model weights, local session data, or CI/CD workflows. Build locally with the .NET SDK pinned in `global.json`; local model servers and credentials are configured separately.

Recent source-only changes are identified below and have not yet been compiled or exercised. Earlier verification records describe the revisions tested at that time, not a guarantee for the current source snapshot.

Steering/worker display follow-up (source-only): worker activity uses `› Spawn Agent #n`, `› Wait for Agent #n`, and `› Message Agent #n`. Saved steering shows its recipient, sender (You or Parent agent), and a quoted, redacted preview. Subsequent delivery notices reference the same message without repeating its body in compact mode. `/steering` lists previews up to 1,000 characters and full message IDs for removal; verbose activity also shows the expanded bounded preview. These presentation changes do not alter delivery or execution and have not been compiled or published.

## Actionable errors — source update pending build

New tool failures identify the action and target, then show **Reason**, **Effect**, and **Next**, with a stable error code and `/details <call-id>` link. Missing paths, stale hashes, patch conflicts, reserved files, unknown workers, and inactive workers have distinct explanations. Worker assignment errors show the attempted path and assigned exact files: naming a folder does not authorize its descendants.

Worker failures report the observed request/context/time limit where recorded, retained file-operation count, and completion-report availability. `/agent #number` displays these details; `/changes` exposes recorded edits. A failed worker can leave useful edits behind. Uncertain effects retain recovery requirements, and older events do not invent missing causes or edit counts. SQLite storage errors have a distinct category and do not promise that the latest state was saved.

This is source-only: regression tests have been added but not run. No compilation, publish, live data changes, or interruption of the running agent is included.

## Compact activity and fetched-content display — source updates pending build

Web-read excerpts and HTTP response bodies now use nested square content blocks with white text on a subtle dark background. Web blocks identify the source, preserve code indentation, and keep wrapped lines and omission notices inside the block. Retained-artifact links remain below it. Plain output keeps the same guides without color. This display change has not been compiled or published while the user's agent is running.

Compact activity uses one worker-start block with a bounded task description and a vertical assignment list. Worker waits/results identify conversation-local aliases, wait limits use minutes, and completed workers expose their recorded reason and report/transcript commands. Short status badges carry background colors while surrounding text stays white. Routine reads, listings and searches combine their result and call duration on one line; `/display verbose` retains separate call timings. The busy composer identifies the current activity and distinguishes total run time from worker/process duration. The worker-report renderer now handles artifact ID references directly instead of emitting empty rows. These source changes have only received static review; compilation, tests and publishing are deferred until the running agent exits.

The next readability pass follows three steps: clean and frame process streams, shorten plan/map summaries, then expose direct retained-log navigation. Source implementation strips terminal control sequences across stdout/stderr chunks, groups output in bounded content blocks, and factors repeated directories out of path-only listings. Raw logs remain unchanged. Process completion and exit code stay outside the preview budget. Plan updates show completed/total counts and abbreviated changed steps; `/tasks` preserves full descriptions and verification criteria. Map notices distinguish depth/scan budgets, excluded entries, scan issues and preview omissions.

While idle, open a specific process log with `/artifact process:#15`, search it with `/artifact process:#15 search error`, or page it with `/artifact process:#15 read <cursor>`. Process aliases use the saved conversation's numbering and survive restart. Ordinary artifact IDs remain supported; the next-page command uses the returned character cursor. No new artifact numbering or schema migration is needed. Regression coverage for fragmented control sequences, path-only shortening and log access is included in source but has not been run; this pass is not compiled or published yet.

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

Delivery and observed verification are recorded in [the implementation record](CLI_IMPLEMENTATION_PLAN.md#live-steering-and-worker-messaging--2026-10-10).

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

The model can call `web_search(query)` and `web_read(url)` through the same registry with either ChatGPT or local models. Parent agents and workers can use them in Work, Plan, Document, and Review, including Inspect permission. Ask normally: "Search the official .NET documentation for this API, open the relevant page, and cite the source." `/tools` lists availability. No new slash command or search API key is required.

- Search uses [DuckDuckGo's non-JavaScript HTML search](https://duckduckgo.com/duckduckgo-help-pages/features/non-javascript), returning up to five titles, source URLs, and bounded snippets. Queries are sent to DuckDuckGo; prompts instruct the model to use public terms rather than private project/memory content. Recognized credential patterns are rejected before sending, without claiming perfect secret detection.
- Page reads accept public HTTP/HTTPS HTML and text on default ports. A separate client sends no ChatGPT tokens, browser cookies, or user authentication. Scripts and subresources do not run. Private/reserved addresses, credential-bearing URLs, and private redirect destinations are rejected; DNS addresses are checked and the connection uses the validated address.
- Reads are bounded to 30 seconds, five redirects, and 2 MiB decompressed input. Up to 100,000 extracted characters are retained as a conversation artifact; the model initially receives up to 6,000 characters and five links. Artifacts retain the source URL, retrieval time, truncation state, and up to thirty links. Use `/artifacts` and `/artifact <id>` to inspect them; the model can page/search them with artifact tools. Source text is historical evidence, never authorization.
- The lightweight reader extracts the main/article text where available and removes scripts, forms, navigation, and hidden markup. It is not a full browser or a guarantee of complete page coverage. PDF, images, OCR, authenticated pages, JavaScript rendering, and browser automation remain unsupported. CAPTCHA, unsupported responses, timeouts, and unrecognized search layouts return explicit errors. There is no hidden provider fallback.

Verification so far: a separate PowerShell HTTP probe returned ten DuckDuckGo HTML result links with HTTP 200 and no challenge on 2026-10-10. This verifies endpoint availability only. The C# code and regression cases now compile and passed the baseline suite after upgrading AngleSharp to 1.5.0. Live parent/worker public-web research acceptance remains pending.

## Clipboard, context, and larger tasks

The locally published 2026-10-10 milestone adds Alt+V text/image input, model-aware context budgets, longer execution batches, and proactive local delegation. Release verification passed 231 tests. Live ChatGPT/Bonsai image sharing, structured tests, checkpoint undo, queued-image reopening, compaction and a 34-request task passed. Forced delegation increased cloud usage on the tiny comparison fixture; no fixed savings is claimed. Delivery evidence and remaining live checks are recorded in [the implementation record](CLI_IMPLEMENTATION_PLAN.md#clipboard-input-larger-tasks-and-local-delegation).

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

Enabled-worker conversations default to `/delegation proactive`; use `--delegation manual` or `/delegation manual` to delegate only when requested. Suitable tasks include investigation, report diagnosis, focused edits, test writing, patch review, documentation and shared screenshot analysis. Parents retain integration, test execution, and compaction. Workers cannot execute processes or approve decisions. Up to two workers are admitted, with 16 launches per parent run and 20 requests/10 minutes per worker, within the parent deadline. A one-slot endpoint serializes inference and displays waiting status. `agent_wait` supports up to three minutes (180,000 ms), returning early when a selected worker finishes or the run is cancelled. `process_wait` remains capped at 60 seconds. Waiting itself makes no extra model calls. `/usage` distinguishes purpose, outcome, waiting, reported cached input and compaction overhead; local tokens are not exact cloud savings.

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

The v6 migration makes a SQLite-consistent v5 backup and starts with an empty library. The [memory delivery record](CLI_IMPLEMENTATION_PLAN.md#automatic-shared-memory) records verification and live acceptance.

Verified and locally published on 2026-10-09: **219 automated tests passed** plus live ChatGPT saving, cross-project recall, worker read-only access, corrections, forgetting and disabled-memory checks. Published command checks passed. The live worker used ChatGPT; local-provider memory inference was not separately exercised. The normal data directory starts with memory enabled and no imported fixture entries.

The CLI also includes compact tool activity with argument summaries, automatic context compaction, Escape cancellation, and a durable FIFO prompt queue. A parent agent can delegate small tasks to explicitly enabled worker profiles using different models. See the [current delivery and acceptance record](CLI_IMPLEMENTATION_PLAN.md#delivered-milestone-lm-studio-and-model-specific-subagents).

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

The adapter preserves streamed tool-call IDs and arguments, sends results back as tool messages, keeps separate reasoning out of answer text, and requires a successful finish plus the stream terminator before dispatch. Resume, workers, and compaction use the same persisted provider selection. See the [Bonsai 2 delivery record](CLI_IMPLEMENTATION_PLAN.md#bonsai-2-chat-completions-endpoint).

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

Use a model listed by `smith models`. Multiple tasks can use the same profile concurrently; profile names describe capabilities, not dedicated slots. The parent prefers enabled local profiles for routine work and enabled GPT profiles for difficult analysis, implementation, or review. It starts independent tasks before waiting and remains responsible for integration and verification. Proactive delegation does not spawn unnecessary tasks just to fill capacity.

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

Earlier attempts on compact one-line code produced rejected patches and worker limits. Tool-use metadata does not guarantee model reliability. See the [acceptance record](CLI_IMPLEMENTATION_PLAN.md#observed-acceptance) for evidence and test limitations.

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

Use `/mode` to see both the workflow and permission level. `/mode work|plan|document|review` selects a workflow without starting inference; type your task afterward. `/mode planning` and `/mode documentation` are aliases. `/mode inspect|edit|execute` changes permissions without bypassing workflow restrictions or plan approval. `/work` returns to Work, subject to the same unfinished-work checks as leaving a workflow.

To start immediately, use `/plan Make a plan to implement player movement`, `/document Update the installation instructions`, or `/review`. `/plan new <task>` remains supported; `/plan` alone shows the saved plan. Reserved plan commands are `new`, `show`, `revise`, `implement`, and `leave`; use `/plan new <task>` if your task begins with one of those words. `/mode plan <task>` and `/mode document <task>` also start tasks immediately; `/mode review --path <path>` starts a scoped review. Existing `/workflow`, `/plan leave`, and permission commands remain available.

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
/plan revise <instruction>  Revise the active plan
/plan implement             Open the implementation review picker
/plan leave                 Return to Work without approving a draft
/tasks                      Display step statuses and verification notes
```

Plans are saved in AgentSmith's SQLite state, not automatically written into the workspace. They retain revisions, stable step IDs, verification criteria, notes, references, and approval history. At most one step is In progress. Finishing a response does not automatically complete its tasks.

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

Readable/Continuous Conversations validation on 2026-10-09: **131 tests passed** in Release. Live ChatGPT acceptance covered automatic compaction, FIFO follow-ups, explicit question/review barriers, editing with passing build/tests, artifact search, Escape cancellation, crash cleanup without replay, restart, and checkpoint undo. The self-contained Windows build in `artifacts/publish/win-x64` was refreshed and smoke-tested. Start a new `smith` process to use it. Detailed measurements and conversation IDs are in the [delivery record](CLI_IMPLEMENTATION_PLAN.md#final-verification-and-local-delivery--2026-10-09).

Plan/Track/Verify validation on 2026-10-09: **111 tests passed** in Release. Live ChatGPT planning, clarification, revision, explicit implementation approval, structured build/test results (4/4 passed), artifact search, restart persistence, and checkpoint undo passed. The unrelated note and test-file hashes were preserved. See [implementation evidence](CLI_IMPLEMENTATION_PLAN.md) for conversation IDs, report details, and limits.

Automated tests use synthetic tokens, scripted model responses, temporary workspaces, and separate CLI processes. They cover stream fragmentation/failure, callback and identity validation, DPAPI storage, tool correlation, pending questions across EOF/restart, cancellation, limits, junction rejection, conversation locks, and secret-free account output.

Verification on 2026-10-09 includes live ChatGPT editing with `gpt-6.1-sol`: AgentSmith repaired a disposable C# project, ran its tests (4 passed, exit 0), inspected Git changes, and preserved an unrelated edit. A restarted CLI undid the checkpoint and restored the original file hash. Automated checks also exercise native stdin/stdout/stderr, an HTTP development server, timeout and process-tree cleanup after forcibly killing the owner, migration, mutation failure injection, and conflicting undo. Exact suite results and evidence are in [CLI_IMPLEMENTATION_PLAN.md](CLI_IMPLEMENTATION_PLAN.md). This establishes the tested account/model path, not every possible account configuration.

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

See [current delivery evidence](CLI_IMPLEMENTATION_PLAN.md#delivered-milestone-lm-studio-and-model-specific-subagents) and [runtime architecture](DESIGN.md). This milestone updates the local Windows build without a commit. No remote push or public release is included.

## Later roadmap

API-key access, remote/authenticated LM Studio servers, automatic model loading, full terminal emulation, Roslyn, browser automation, MCP/plugins, desktop UI, nested agents, worker process execution, and public distribution remain deferred. Public web search/read tools are implemented as described above. LM Studio and bounded model-specific workers are implemented.

Protocol references: [ChatGPT sign-in](https://developers.openai.com/siwc/token-sharing-open-source/sign-in), [inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference), and [preview requirements](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations). Reviewed on 2026-10-09.


## Document and Review workflows

Start a task with `/document <task>` or `/review`. Review defaults to staged, unstaged, and untracked changes; select a different target with `/review --base <ref>`, `/review --commit <ref>`, or `/review --path <path>`. A base comparison uses the merge base against HEAD; a commit comparison uses its first parent (or the root commit). Path audits work without Git. New conversations and single-request runs accept `--workflow document|review`; `--workflow plan` is equivalent to `--plan`, and contradictory options are rejected.

Document can edit `.md`, `.markdown`, `.rst`, `.adoc`, `.asciidoc`, and `.txt` files with Edit/Execute permission. It inspects source as evidence but cannot directly edit source comments, MDX, notebooks, or configuration. Inspect produces a saved draft. Execute permits documentation builds and example checks; processes run project code under your account and are not restricted to documentation files. Checkpoints and conflict-safe undo still apply.

Review permits reads, curated Git inspection, internal reports, and memory operations. It blocks file edits, undo, arbitrary processes, and build/test launches regardless of `/mode`. Workers inherit that restriction. Findings distinguish defects and risks from broader quality suggestions. Review captures comparison artifacts and workspace hashes, then rechecks them before completion. Changed, excluded, oversized, binary, or uncaptured evidence is reported as a coverage limitation. Read complete diff pages through artifact tools instead of treating previews as complete evidence.

Both workflows retain progress, revisions, findings, evidence, and limitations in `sessions.db`. Use `/reports`, `/report <id>`, and `/workflow` to inspect them. `/report export <id> <new-path.md>` explicitly creates a checkpointed Markdown file; it never overwrites an existing file or grants model editing permissions. Export is available while idle even in Review/Inspect. Completed reports also appear in `/artifacts`.

The parent must save and finish a report after collecting worker results. Successful completion returns to Work before queued follow-ups start, preserving permissions and plan approvals. Questions, cancellation, limits, recovery, and restart retain unfinished workflow state. `/workflow work` explicitly leaves a settled unfinished report as abandoned; resolve an active run first. Plan decisions must be resolved or explicitly left before entering these workflows.

Schema v8 backs up v7 before adding report/revision storage. This release also includes green terminal styling, compact square message guides, and minute/second duration displays. Live verification outcomes are recorded in CLI_IMPLEMENTATION_PLAN.md.

Document/Review delivery verified on 2026-10-10: **250 automated tests passed**. Live ChatGPT/Bonsai review, documentation edits, saved-progress recovery, structured compilation, report export, unrelated-edit preservation, and worker checkpoint undo passed. One local documentation attempt exhausted its output budget; a smaller retry succeeded. The Windows publish folder is refreshed. The detailed acceptance record above distinguishes the injected restart check from a physical console crash test.
