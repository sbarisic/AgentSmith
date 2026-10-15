# Repository Memorize — local acceptance record, 2026-10-14

## Implementation

The implementation exposes `/memorize`, `/mode memorize` and `--workflow memorize`, bounded source inspection, repository-local memory/provenance/history, metadata-only global discovery, scoped reads and automatic current-repository retrieval. Memorize permits dedicated state updates, not source edits, project evaluation, processes/builds/tests or HTTP writes. Parent reconciliation remains gated on worker collection/recovery. Refresh rescans bounded eligible sources and reconciles topics; it is not a changed-file-only scanner optimization.

## Verification evidence

Structured records directly re-read during closeout:

- Release solution tests: **802/802 passed**, no failures/skips, exit **0**; verification `2e0f2ab8cb6b4a7c9c396bdc315b8919`. A fresh closeout rerun also passed **802/802**, exit **0**, verification `5bd98bcd3e4a4d0eada9ea14d79ef94f`; both collection-persistence failure cases were individually confirmed passed in its structured diagnostics. `git diff --check` exited **0** (line-ending conversion warnings only).
- Release solution build: exit **0**, zero reported warnings/errors; verification `d9d68fcad0fe4eca890f769fa3fb76df`.
- Recovery regression additions in `tests/AgentSmith.Tests/RepositoryMemoryWorkerTests.cs:235–406` cover receipt-save and collected-marker-save failures, refusal of prepared mutations until repair, fresh-object reload, evidence reuse and idempotent replay. These are same-process scripted fixtures, not OS-crash or live-synthesis tests.

The prior execution continuation records live Release CLI acceptance using configured ChatGPT in Inspect mode on `tests/fixtures/memorize-live`: initial creation of one queue-policy memory (capacity 17), fresh Work-session recall, unchanged reconciliation preserving revision/history/UpdatedAt, then a parent-authored fixture change to capacity 23, same-entry refresh to revision/history 2, and another fresh Work-session recall. Recorded process exits were 0. The fixture source change was made by the harness, not Memorize. The continuation reports no tool calls during either recall and explicit historical-evidence disclaimers. Original live logs were not accessible to the closeout conversation; these live outcomes are historical reported evidence, distinct from the directly re-read structured regression results.

## Limits

This is bounded fixture acceptance, not all-provider, full filesystem-effect, lifecycle-interleaving or adversarial concurrency verification. Compatible local-model availability was not established; live runs did not use an isolated data directory. Static path/link checks do not establish race-free containment against concurrent path replacement, and sources are not locked through final save. No reproduced escape or incorrect commit was established by the bounded review. An earlier intermittent SQLite failure has no proven cause; no speculative production pooling change was made.

Documentation distinguishes bounded live acceptance from scripted tests, explains refresh traversal, and retains storage/privacy/disablement/recovery limitations. No watcher, vector database, cloud sync, automatic source repair/execution or relink CLI was added. No commit, publish or unrelated composer acceptance is claimed here. Older shared-memory acceptance entries in `CLI_IMPLEMENTATION_PLAN.md` describe a different storage feature and are not repository Memorize evidence. This separate record avoids rewriting that historical document's mixed line endings.
