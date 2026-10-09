# AgentSmith

A Windows C# coding agent with its own runtime, powered by ChatGPT account sign-in. AgentSmith can inspect a project, edit files with checkpoints, run builds and tests, and resume saved conversations.

It also supports a separate Plan workflow, persistent task progress, searchable retained artifacts, and structured .NET build/test reports.

Next planned milestone: clearer tool activity, automatic context compaction, Escape cancellation, and a durable queue for follow-up prompts. These features are **not implemented yet**. See [the detailed delivery plan](CLI_IMPLEMENTATION_PLAN.md#planned-milestone-readable-long-running-conversations).

## Build and run

The repository pins .NET SDK 10.0.401. From the repository directory:

```powershell
dotnet build AgentSmith.slnx
dotnet test AgentSmith.slnx
dotnet run --project src/AgentSmith.Cli -- auth login
dotnet run --project src/AgentSmith.Cli -- models
dotnet run --project src/AgentSmith.Cli -- chat --workspace . --model <model-id>
```

Use the model ID printed by `models`. Sign-in opens your system browser and waits for explicit consent. An eligible account and ChatGPT-plan permission are required. A valid identity alone does not enable inference. No API key or existing Codex credentials are used.

Build the self-contained Windows executable:

```powershell
dotnet publish src/AgentSmith.Cli -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64
.\artifacts\publish\win-x64\smith.exe --help
```

Keep the entire publish directory together; this is a self-contained folder deployment, not a single-file executable.

## Commands

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
smith resume <conversation-id>
smith sessions
smith inspect <run-id>
```

Global options: `--data-dir <path>`, `--json`, and `--no-color`.

Normal terminal output uses a compact workspace header, model/account lists, readable conversation history, brief tool activity, and a completion summary. Context manifests and detailed usage stay out of the conversation; use `/context`, `inspect`, or `--json` for diagnostics. `NO_COLOR` also disables color. Redirected output uses the same readable layout without ANSI styling; `--json` retains the original machine event format.

Message blocks have a vertical guide. Each active response segment shows elapsed execution time, including responses that pause for a question. Timing includes model and tool work and excludes time spent waiting for an answer; continuing after an answer starts a new segment. Historical messages are separated in the same way, but do not claim timings that were not saved.

Interactive commands: `/help`, `/status`, `/context`, `/paste`, `/markdown`, `/tools`, `/mode`, `/changes`, `/undo <checkpoint-id>`, `/processes`, `/stop <process-id>`, `/recover [abandon]`, and `/exit`. Finish multiline paste with `/end`. Ctrl+C cancels current work and stops processes started by that run. Currently, input typed while work is running is read at the next prompt; there is no managed prompt queue or Escape cancellation of active runs yet.

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

The agent uses `plan_read`, `plan_update`, and `plan_submit`. A submitted plan pauses tool dispatch and presents **Implement / Revise / Keep planning**. Implement then requires an explicit **Edit or Execute** selection. Revise accepts a note. Nothing is selected or approved on a timer. EOF, Escape, or restart leaves the decision pending. Redirected/JSON input accepts `implement edit`, `implement execute`, `revise <note>`, or `keep planning`; typing only `implement` opens the permission selection without approving.

Approval is tied to the displayed revision and permission mode. Implementation starts a new run and refreshes file evidence. Queued calls from the planning response are closed without executing. A saved decision interrupted before its new run starts requires explicit `continue` after restart.

Routine progress can change steps within the approved scope. In `plan_update`, null goal/deliverables/constraints preserve those fields verbatim. Changing them produces a draft and disables implementation; a resolved draft goes back to review. Increasing the implementation permission ceiling also requires review. Ordinary Work conversations can track tasks without a plan-approval gate.

Use `/recover abandon` before `/plan leave` when a planning run remains unfinished. Leaving does not approve or implement the draft. Planning still uses the bounded context budget; automatic compaction is planned for the next milestone.

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

The runtime allows 30 model steps per explicitly continued batch, 15 minutes of active work, and a conservative 120,000-character context budget including tool schemas. Character accounting is not exact token accounting. Oversized context pauses without automatic summarization. Responses streams are bounded; incomplete streams never dispatch their proposed tools.

## Storage and recovery

Default state: `%LOCALAPPDATA%\AgentSmith`. Use a separate `--data-dir` for experiments and tests. ChatGPT receives the conversation and file excerpts needed for inference; local storage does not mean local inference.

- `sessions.db`: schema v3 stores conversations, permissions/workflow, plans/revisions/reviews, idempotency receipts, artifacts and verification records, runs, protocol items, tool calls/results, questions, ordered events, mutation journals, original/result bytes and hashes, file metadata, and process records.
- `sessions-v1-*.backup.db`: SQLite-consistent backup made before transactional v1-to-v2 migration.
- `sessions-v2-*.backup.db`: SQLite-consistent backup before v3. Existing sessions retain their permission mode and migrate to Work workflow.
- `artifacts/<conversation-id>/<process-id>.jsonl`: retained process output with stream identity and cursors.
- `credentials.dpapi`: account registrations and tokens protected with current-user Windows DPAPI.
- `locks/`: exclusive conversation ownership. Credential operations also use an exclusive file lock to serialize refreshes.

Tokens are excluded from ordinary logs and account listings. Session history can contain private user messages and workspace excerpts; no automatic retention deletion is implemented. Do not commit or share the state directory.

Completed results are reused after restart. File operations interrupted around the effect/result boundary are reconciled against recorded pre/post states. Conflicting states remain unresolved. Uncertain process launches and stdin writes are never repeated. Old CLI process records become interrupted, with retained logs and no claim of successful completion. `/recover` shows unresolved state; `/recover abandon` explicitly acknowledges uncertainty and closes unfinished work. Abandonment does not undo side effects. History inspection never executes tools. Partial streamed text is displayed but is not retained as a completed assistant message after interruption.

Conversations remain bound to their saved account registration and model. `auth use` affects new conversations. `auth logout` retains registration metadata for future reauthorization, clears local tokens, and reports whether remote revocation succeeded.

## Verification

Plan/Track/Verify validation on 2026-10-09: **111 tests passed** in Release. Live ChatGPT planning, clarification, revision, explicit implementation approval, structured build/test results (4/4 passed), artifact search, restart persistence, and checkpoint undo passed. The unrelated note and test-file hashes were preserved. See [implementation evidence](CLI_IMPLEMENTATION_PLAN.md) for conversation IDs, report details, and limits.

Automated tests use synthetic tokens, scripted model responses, temporary workspaces, and separate CLI processes. They cover stream fragmentation/failure, callback and identity validation, DPAPI storage, tool correlation, pending questions across EOF/restart, cancellation, limits, junction rejection, conversation locks, and secret-free account output.

Verification on 2026-10-09 includes live ChatGPT editing with `gpt-6.1-sol`: AgentSmith repaired a disposable C# project, ran its tests (4 passed, exit 0), inspected Git changes, and preserved an unrelated edit. A restarted CLI undid the checkpoint and restored the original file hash. Automated checks also exercise native stdin/stdout/stderr, an HTTP development server, timeout and process-tree cleanup after forcibly killing the owner, migration, mutation failure injection, and conflicting undo. Exact suite results and evidence are in [CLI_IMPLEMENTATION_PLAN.md](CLI_IMPLEMENTATION_PLAN.md). This establishes the tested account/model path, not every possible account configuration.

## Planned: readable long-running conversations

The next milestone addresses noisy tool output and conversations that stop at the context limit. This section describes intended behavior, not commands available in the current build.

- **Tool activity:** show the action and relevant arguments, such as `Read src/Program.cs · lines 40–120` or `Run dotnet test · cwd E:\Projects\AgentSmith`. Use short process labels, readable task descriptions, compact result summaries, and clearly labeled durations. Suppress unchanged empty polls. Group stdout/stderr text across chunks, display bounded head/tail previews, and link to retained artifacts for the rest. Detailed output remains accessible; errors and truncation stay visible.
- **Automatic compaction:** before a request reaches its context budget, retain recent protocol-complete exchanges and replace older request context with a bounded, provenance-linked summary. Keep full history, artifacts, plan approvals, questions, and recovery records locally. Summaries cannot grant permissions or replace durable evidence. If compaction cannot fit or fails, pause with a recoverable explanation.
- **Escape cancellation:** Escape during active work cancels the current run and stops its owned processes, using the same cancellation path as Ctrl+C. Show cancellation progress and observed outcomes. Escape inside a question or review picker continues to leave that decision pending; it never submits an answer.
- **Queued prompts:** keep an input composer available during work. Enter saves a follow-up in FIFO order; it starts as a new run as soon as the preceding prompt finishes successfully. It does not interrupt or enter the active model request. Questions and reviews must be resolved explicitly before the queue advances. Cancellation, failure, a paused limit, recovery, or restart holds the queue until explicit continuation.

Proposed controls are `/display compact|verbose`, `/details <call-id>`, `/compact`, `/queue`, `/queue remove <id>`, `/queue clear`, and `/queue continue`. Queued input never serves as an automatic question answer or implementation approval. Single-request `smith run` remains single-request; redirected chat retains line-oriented input and JSON output retains structured events.

The [implementation plan](CLI_IMPLEMENTATION_PLAN.md#planned-milestone-readable-long-running-conversations) defines the storage changes, context policy, input state machine, delivery order, and acceptance tests. [DESIGN.md](DESIGN.md) describes the runtime boundaries. No new implementation, test pass, or executable publication is claimed by this documentation update.

## Later roadmap

LM Studio, API-key access, full terminal emulation, Roslyn, web/browser tools, MCP/plugins, desktop UI, multi-agent orchestration, and public distribution remain deferred beyond the planned conversation improvements. `IModelClient` is the future provider boundary; no local-model adapter is included.

Protocol references: [ChatGPT sign-in](https://developers.openai.com/siwc/token-sharing-open-source/sign-in), [inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference), and [preview requirements](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations). Reviewed on 2026-10-09.
