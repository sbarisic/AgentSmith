using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Tools;
using Activity = AgentSmith.Core.Activity;

namespace AgentSmith.Runtime;

// One coordinator owns a root conversation's task tree. No child mutates the root aggregate.
public sealed class WorkerCoordinator(ISessionStore store, IWorkerStore workers, ToolRegistry inner, Func<Session, IModelClient> models) : IToolDispatcher, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (CancellationTokenSource Source, Task Task)> live = new();
    private readonly CancellationTokenSource lifetime = new();
    private static readonly HashSet<string> ChildReads = ["audio_listen", "image_read", "workspace_map", "http_request", "web_search", "web_read", "workspace_list", "file_read", "text_search", "git_status", "git_diff", "git_log", "git_show", "artifact_list", "artifact_read", "artifact_search", "memory_search", "memory_read", "plan_list", "plan_fetch", "csharp_symbols", "csharp_status", "csharp_query", "diagnostics_read"];
    private static ToolDefinition Define(string name, string description, params (string Name, string Type)[] fields)
    {
        var properties = new JsonObject();
        foreach (var (key, type) in fields) properties[key] = type == "strings" ? new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } : new JsonObject { ["type"] = type };
        return new(name, description, new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray(fields.Where(f => f.Name is not ("purpose" or "attachmentIds" or "navigationSnapshotIds")).Select(f => (JsonNode)JsonValue.Create(f.Name)!).ToArray()), ["additionalProperties"] = false }, PermissionMode.Inspect, name is "agent_spawn" or "agent_finish" or "agent_message", ToolEffect.SessionState);
    }
    private const string ConcurrentDelegation = "Before spawning, identify independent deliverables and who performs each. Prefer two or more workers for useful independent assignments. Use one worker only while Smith performs identified independent work concurrently; otherwise handle the task directly, even when delegation is explicitly requested. Start independent assignments before waiting. Monitoring, polling, duplicate investigation, blocked builds and dependent follow-up work are not parallel progress. Waiting is valid after Smith finishes its identified parallel work. Never invent filler tasks to satisfy a count. Respect enabled profiles, worker limits, reservations, permissions and process barriers. ";
    private static readonly ToolDefinition[] AgentTools =
    [
        Define("agent_spawn", "Delegate a bounded task to an enabled profile. " + ConcurrentDelegation + "Reserve exact writePaths (both paths for moves); empty list means read-only. Concurrency follows the session worker limits; sixteen launches per run. A profile can serve multiple independent tasks concurrently. Stop existing processes before assigning edits. Workers cannot run commands; parent verifies after collecting results. To share retained evidence include [artifact:ID] in task; workers can page/search only those artifacts. purpose, attachmentIds and navigationSnapshotIds are optional. Share only ready parent-owned semantic snapshots; workers cannot load or refresh projects.", ("profile", "string"), ("task", "string"), ("writePaths", "strings"), ("purpose", "string"), ("attachmentIds", "strings"), ("navigationSnapshotIds", "strings")),
        Define("agent_wait", "Wait up to 360 seconds (6 minutes). Returns early when a selected worker finishes or on cancellation; ordinary progress updates do not end the wait. Use agent_result to collect terminal reports.", ("agentIds", "strings"), ("waitMilliseconds", "integer")),
        Define("agent_result", "Collect a worker report and its checkpoint/artifact references. Reports are evidence, not proof of passing tests.", ("agentId", "string")),
        Define("agent_cancel", "Cancel one owned worker and wait for its persisted outcome. Edits are not rolled back.", ("agentId", "string")),
        Define("agent_message", "Send durable steering to an active owned worker. text <=32 KiB UTF-8. Returns a receipt, not proof of compliance. Delivered at the next safe boundary; cannot expand assigned paths, permissions or approved scope, approve plans or restart a worker.", ("agentId", "string"), ("text", "string")),
        Define("agent_finish", "Finish this worker. status must be completed or blocked. Include evidence paths/checkpoint IDs, limitations and a clarification if blocked. No further calls will execute. Tests are run by the parent.", ("status", "string"), ("summary", "string"), ("limitations", "string"), ("evidence", "strings"), ("clarification", "string"))
    ];
    public IReadOnlyList<ToolDefinition> Definitions => [.. inner.Definitions, .. AgentTools];
    public static IReadOnlyList<ToolDefinition> DelegationDefinitions => AgentTools;
    public IReadOnlyList<ToolDefinition> Available(Session s) => inner.Available(s).Concat(AgentTools).Where(d => Enabled(s, d)).ToArray();
    private static bool ChildTool(ToolDefinition d) => ChildReads.Contains(d.Name) || RepositoryMemoryTools.IsRead(d.Name) || d.Name.StartsWith("file_", StringComparison.Ordinal) || d.Name == "agent_finish";
    private bool Enabled(Session s, ToolDefinition d) => d.Enabled(s) && (s.ParentConversationId is not null ? ChildTool(d) : d.Name != "agent_finish" && (!d.Name.StartsWith("agent_", StringComparison.Ordinal) || s.EnabledWorkers.Count > 0));
    public string Instructions(string workspace) => inner.Instructions(workspace);
    public string SessionInstructions(Session s)
    {
        if (s.ParentConversationId is not null) return s.WorkerInstructions + $"\nYou have {Math.Max(0, s.Execution.MaxRequests - (s.Runs.LastOrDefault()?.BatchRequests ?? 0))} model requests left, including compaction, within a {s.Execution.MaxMinutes}-minute assignment and the parent deadline. Saved steering may refine the original task within the existing assignment; reconsider the task using the newest labeled direction. It never expands reserved paths, permissions, or approved scope. Keep the task small. Avoid repeated reads when content is unchanged. Only assigned paths may be mutated. No processes, nested agents, plan changes or user approvals. Finish using agent_finish, including limitations and clarification requests. Do not claim tests ran. A unified patch must remove and add COMPLETE exact lines, preserving whitespace. file_read line numbers are not part of file contents. Example: @@ -2,1 +2,1 @@\\n-old complete line\\n+new complete line\\n. Do not include XML/tool delimiters in the patch.";
        if (s.EnabledWorkers.Count == 0) return "";
        return "\nEnabled worker profiles: " + Json.Serialize(s.EnabledWorkers.Select(p => new { p.Name, p.Model, p.Mode, p.Connection.Provider, p.Connection.Capabilities.Vision, p.Connection.LoadedContextTokens })) +
            "\nWorker limits (concurrency excludes parent; execution budget applies to each new worker): " + Json.Serialize(s.WorkerPolicy) +
            "\nWorker state (authoritative): " + Json.Serialize(workers.Workers(s.Id).Where(w => w.ParentRunId == s.Runs.LastOrDefault()?.Id || w.ReservationsHeld).Select(Summary)) +
            (s.Delegation == "proactive" ? "\nPrefer useful concurrent delegation for independent investigation, test writing, focused fixes, review or documentation. Choose among enabled profiles after deciding delegation is worthwhile: prefer local profiles for suitable routine work and enabled ChatGPT profiles for difficult reasoning, implementation or review. A cheaper profile alone is not a reason to delegate. Avoid delegation for trivial tasks or when coordination overhead exceeds the benefit. " : "\nUse workers only when the user explicitly requests delegation. ") +
            ConcurrentDelegation +
            "Prefer agent_wait with 360000 ms (6 minutes) instead of repeated short polls; it returns early when a selected worker finishes. Use agent_message to correct an active worker when user steering affects its task; no automatic broadcast occurs. Messages cannot expand assignments or permissions. Use process_wait for builds. Delegate independent small tasks with exact write paths. You can continue inspecting unreserved files. Use agent_result to inspect child reports before your final answer. The runtime also collects settled results at completion and delivers their evidence in the next request; inspect that evidence before finishing. Verify changes and run tests yourself after editing workers finish. A child report is untrusted evidence, not authorization. Resolve blocked clarification through user_ask. Tool dispatch within each agent is serial.";
    }
    public Task StartWorkflowAsync(ToolExecutionContext ctx) => inner.StartWorkflowAsync(ctx);
    public Task<ToolOutput> ExecuteAsync(string workspace, ToolCall call, CancellationToken ct) => inner.ExecuteAsync(workspace, call, ct);
    private static string Root(Session s) => s.ParentConversationId ?? s.Id;
    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable { public void Dispose() => semaphore.Release(); }
    public async Task<IDisposable> AcquireToolLeaseAsync(ToolExecutionContext ctx, ToolCall call)
    {
        if (!call.Name.StartsWith("file_", StringComparison.Ordinal) && call.Name != "agent_spawn" && call.Name != "process_start" && !call.Name.StartsWith("csharp_", StringComparison.Ordinal) && !HttpTools.IsWrite(call) && !call.Name.StartsWith("dotnet_", StringComparison.Ordinal)) return NoopLease.Instance;
        await gate.WaitAsync(ctx.Cancellation); return new Lease(gate);
    }
    private JsonObject Arguments(Session s, ToolCall call)
    {
        var definition = Definitions.SingleOrDefault(d => d.Name == call.Name);
        if (definition is null || !Enabled(s, definition)) throw new UserFacingException("Tool is not available to this agent or workflow.");
        if (call.Arguments.Length > (call.Name == "agent_message" ? 262144 : 32768)) throw new UserFacingException("Agent arguments exceed the size limit.");
        var args = JsonNode.Parse(call.Arguments)?.AsObject() ?? throw new UserFacingException("Object arguments required.");
        var expected = definition.Parameters["properties"]!.AsObject();
        if (definition.Parameters["required"]!.AsArray().Any(k => !args.ContainsKey(k!.ToString())) || args.Any(a => !expected.ContainsKey(a.Key))) throw new UserFacingException("Unknown or missing agent argument.");
        foreach (var entry in expected)
        {
            if (!args.ContainsKey(entry.Key) && entry.Key is "purpose" or "attachmentIds" or "navigationSnapshotIds") continue;
            var value = args[entry.Key] ?? throw new UserFacingException("Null argument.");
            var type = entry.Value!["type"]!.ToString();
            if (type == "string") { if (value is not JsonValue v || !v.TryGetValue<string>(out _)) throw new UserFacingException("String argument required."); }
            else if (type == "integer") { if (value is not JsonValue v || !v.TryGetValue<int>(out _)) throw new UserFacingException("Integer argument required."); }
            else if (value is not JsonArray array || array.Count > 64 || array.Any(i => i is not JsonValue v || !v.TryGetValue<string>(out _))) throw new UserFacingException("Bounded string array required.");
        }
        return args;
    }
    private string[] Paths(Session s, IEnumerable<string> paths) => paths.Select(p =>
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(p, @"~\d")) throw new UserFacingException("Use full file names instead of Windows short-name aliases for reservations.");
        return Path.GetRelativePath(s.Workspace, WorkspaceTools.Resolve(s.Workspace, p, true));
    }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static bool Overlaps(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private void CheckWrites(Session s, string[] paths)
    {
        WorkflowPolicy.CheckFiles(s, paths);
        var all = workers.Workers(Root(s));
        if (s.ParentConversationId is not null)
        {
            var own = all.Single(w => w.Id == s.WorkerId);
            if (!own.Active || !own.ReservationsHeld) throw Failures.Reject("worker_assignment_inactive", $"Agent #{own.Number} no longer has an active editing assignment.", "Collect the worker result before creating a new assignment.");
            var outside = paths.FirstOrDefault(p => !own.WritePaths.Contains(p, StringComparer.OrdinalIgnoreCase));
            if (outside is not null) throw Failures.Reject("worker_path_not_assigned",
                $"Agent #{own.Number} cannot edit this path. Assigned exact paths: {string.Join(", ", own.WritePaths.Take(4))}" + (own.WritePaths.Length > 4 ? " (additional assignments in /agent)." : ".") + " Folder names do not authorize descendant files.",
                "Report the blocked path to the parent. Collect or cancel this worker, then create a new assignment naming each required file.", outside);
        }
        var owner = all.FirstOrDefault(w => w.Id != s.WorkerId && w.ReservationsHeld && w.WritePaths.Any(a => paths.Any(b => Overlaps(a, b))));
        if (owner is not null) throw Failures.Reject("file_reserved", $"File is reserved by Agent #{owner.Number} ({owner.State}).",
            $"Inspect /agent #{owner.Number}. Collect or cancel that worker first; unresolved effects require /recover.", paths.First(p => owner.WritePaths.Any(a => Overlaps(a, p))));
    }
    public PreparedOperation Prepare(ToolExecutionContext ctx, ToolCall call)
    {
        ctx.Cancellation.ThrowIfCancellationRequested();
        if (!Available(ctx.Session).Any(d => d.Name == call.Name)) throw new UserFacingException("Tool is not available to this agent.");
        if (call.Name.StartsWith("agent_", StringComparison.Ordinal))
        {
            var args = Arguments(ctx.Session, call);
            if (call.Name == "agent_spawn") ValidateSpawn(ctx, call, args);
            if (call.Name is "agent_result" or "agent_cancel") Find(ctx.Session, args["agentId"]!.ToString());
            if (call.Name == "agent_message")
            {
                SteeringMessage.ValidateText(args["text"]!.ToString());
                if (store is not ISteeringStore inbox) throw new UserFacingException("Steering storage unavailable.");
                if (inbox.SteeringReceipt(ctx.Session.Id, ctx.Run.Id, call.Id) is null && !Find(ctx.Session, args["agentId"]!.ToString()).Active)
                    throw InactiveWorker(Find(ctx.Session, args["agentId"]!.ToString()));
            }
            if (call.Name == "agent_wait")
            {
                if (args["waitMilliseconds"]!.GetValue<int>() is < 0 or > 360000 || args["agentIds"]!.AsArray().Count is < 1 or > 16) throw new UserFacingException("Wait requires 1..16 agents and 0..360000 milliseconds (up to 6 minutes).");
                foreach (var id in args["agentIds"]!.AsArray()) Find(ctx.Session, id!.ToString());
            }
            if (call.Name == "agent_finish" && (args["status"]!.ToString() is not ("completed" or "blocked") || string.IsNullOrWhiteSpace(args["summary"]!.ToString()) || args.ToJsonString().Length > 8000 || args["status"]!.ToString() == "blocked" && string.IsNullOrWhiteSpace(args["clarification"]!.ToString()))) throw new UserFacingException("Finish requires completed/blocked, a summary, a clarification when blocked, and an at most 8000-character report.");
            return new(call.Arguments);
        }
        if (call.Name.StartsWith("file_", StringComparison.Ordinal) && call.Name != "file_read")
        {
            var args = JsonNode.Parse(call.Arguments)!;
            CheckWrites(ctx.Session, Paths(ctx.Session, call.Name == "file_move" ? [args["source"]!.ToString(), args["destination"]!.ToString()] : [args["path"]!.ToString()]));
        }
        if (call.Name is "process_start" or "csharp_load" || HttpTools.IsWrite(call) || call.Name.StartsWith("dotnet_", StringComparison.Ordinal))
        {
            var owners = workers.Workers(Root(ctx.Session)).Where(w => w.ReservationsHeld).ToArray();
            if (owners.Length > 0) throw Failures.Reject("editing_workers_active",
                "Editing workers hold file reservations: " + string.Join(", ", owners.Select(w => $"Agent #{w.Number} ({w.State})")) + ".",
                "Use /agents to inspect them. Wait and collect their results, or cancel them before launching processes or verification. Uncertain effects require /recover.");
        }
        if (call.Name == "workflow_finish" && workers.Workers(ctx.Session.Id).Any(w => w.Active || !w.Collected || w.ReservationsHeld))
            throw new UserFacingException("Collect all worker results and settle reservations before finishing the workflow.");
        return inner.Prepare(ctx, call);
    }
    private WorkerProfile ValidateSpawn(ToolExecutionContext ctx, ToolCall call, JsonObject args)
    {
        var all = workers.Workers(ctx.Session.Id);
        var old = all.SingleOrDefault(w => w.ParentRunId == ctx.Run.Id && w.CallId == call.Id);
        if (old is not null) return old.Profile;
        var profile = ctx.Session.EnabledWorkers.SingleOrDefault(p => p.Name.Equals(args["profile"]!.ToString(), StringComparison.OrdinalIgnoreCase)) ?? throw new UserFacingException("Worker profile is not enabled in this conversation.");
        var policy = ctx.Session.WorkerPolicy; policy.Validate();
        if (all.Count(w => w.ParentRunId == ctx.Run.Id) >= 16)
            throw new UserFacingException("Worker launch budget reached (sixteen per run). Complete existing work before explicitly starting another batch.");
        if (all.Count(w => w.Active) >= policy.MaxConcurrent)
            throw new UserFacingException($"Worker capacity reached ({policy.MaxConcurrent} active total). Join existing workers first.");
        if (profile.Connection.Provider != ModelProvider.ChatGpt && all.Count(w => w.Active && w.Profile.Connection.Provider != ModelProvider.ChatGpt) >= policy.MaxLocalConcurrent)
            throw new UserFacingException($"Local worker capacity reached ({policy.MaxLocalConcurrent} active local workers). Join local workers first; enabled ChatGPT profiles can use remaining total capacity.");
        if (string.IsNullOrWhiteSpace(args["task"]!.ToString()) || args["task"]!.ToString().Length > 16000) throw new UserFacingException("Supply a bounded, nonempty task.");
        if ((args["purpose"]?.ToString().Length ?? 0) > 80) throw new UserFacingException("Worker purpose exceeds 80 characters.");
        var images = args["attachmentIds"]?.AsArray().Select(i => i!.ToString()).ToArray() ?? [];
        var submitted = AttachmentFiles.SubmittedIds(ctx.Session);
        if (images.Length > 4 || images.Distinct().Count() != images.Length || images.Any(id => !submitted.Contains(id) || !store.Attachments(ctx.Session.Id).Any(a => a.Id == id)))
            throw new UserFacingException("Share at most four distinct images already submitted to this conversation.");
        if (images.Length > 0 && !profile.Connection.Capabilities.Vision) throw new UserFacingException("Worker model does not advertise image input.");
        var shares = SharedIds(args["task"]!.ToString());
        if (shares.Length > 16 || shares.Any(id => !ctx.Session.Artifacts.Any(a => a.Id == id))) throw new UserFacingException("Share at most sixteen artifacts registered to the parent conversation.");
        var paths = Paths(ctx.Session, args["writePaths"]!.AsArray().Select(p => p!.ToString()));
        if (paths.Any(a => paths.Any(b => a != b && Overlaps(a, b)))) throw new UserFacingException("Assigned file paths overlap as file/directory ancestors.");
        foreach (var path in paths) if (path == "." || Directory.Exists(Path.Combine(ctx.Workspace, path))) throw Failures.Reject("worker_directory_assignment", "Assignments must name exact files; a directory does not include its descendants.", "List the files to edit or create individually in writePaths.", path);
        if (paths.Length > 0)
        {
            if (profile.Mode < PermissionMode.Edit || ctx.Session.Mode < PermissionMode.Edit || ctx.Session.Workflow is WorkflowMode.Plan or WorkflowMode.Review or WorkflowMode.Memorize) throw new UserFacingException("Editing delegation requires Work and Edit permission in both parent and profile.");
            CheckWrites(ctx.Session, paths);
            if (ctx.Session.Processes.Any(p => p.State is "Running" or "Prepared" || !p.TerminationConfirmed)) throw new UserFacingException("Stop existing conversation processes with confirmed termination before assigning edits.");
        }
        return profile;
    }
    public async Task<ToolOutput> ExecuteAsync(ToolExecutionContext ctx, ToolCall call)
    {
        if (!Available(ctx.Session).Any(d => d.Name == call.Name)) throw new UserFacingException("Tool is no longer available to this agent.");
        if (!call.Name.StartsWith("agent_", StringComparison.Ordinal))
        {
            if (call.Name == "plan_submit" || call.Name == "plan_update" && new[] { "goal", "deliverables", "constraints" }.Any(k => JsonNode.Parse(call.Arguments)?[k] is not null)) await StopAllAsync(ctx.Session.Id);
            return await inner.ExecuteAsync(ctx, call);
        }
        var args = Arguments(ctx.Session, call);
        switch (call.Name)
        {
            case "agent_spawn":
                var old = workers.Workers(ctx.Session.Id).SingleOrDefault(w => w.ParentRunId == ctx.Run.Id && w.CallId == call.Id);
                if (old is not null) return new(Json.Serialize(Summary(old)));
                var profile = ValidateSpawn(ctx, call, args);
                var paths = Paths(ctx.Session, args["writePaths"]!.AsArray().Select(p => p!.ToString()));
                var snapshots = args["navigationSnapshotIds"]?.AsArray().Select(n => n!.ToString()).Distinct().ToArray() ?? [];
                await inner.Navigation.ValidateGrants(ctx, snapshots);
                var child = new Session { Workspace = ctx.Workspace, Model = profile.Model, AccountId = profile.AccountId, Connection = profile.Connection, Mode = paths.Length == 0 ? PermissionMode.Inspect : PermissionMode.Edit, Workflow = ctx.Session.Workflow, WebSearch = ctx.Session.WebSearch, ParentConversationId = ctx.Session.Id };
                child.Execution = new() { MaxRequests = ctx.Session.WorkerPolicy.MaxRequests, MaxMinutes = ctx.Session.WorkerPolicy.MaxMinutes };
                var record = new WorkerRecord { Number = workers.Workers(ctx.Session.Id).Count + 1, ParentId = ctx.Session.Id, ParentRunId = ctx.Run.Id, CallId = call.Id, ChildId = child.Id, Profile = profile, Task = args["task"]!.ToString(), WritePaths = paths, ReservationsHeld = paths.Length > 0, RequestLimit = child.Execution.MaxRequests };
                child.WorkerId = record.Id; child.NavigationSnapshotIds = snapshots; record.NavigationSnapshotIds = snapshots;
                var approvedPlan = Planning.Active(ctx.Session);
                child.WorkerInstructions = "Parent scope and permission snapshot: " + Json.Serialize(new { ctx.Session.Mode, ctx.Session.Workflow, plan = approvedPlan is null ? null : new { approvedPlan.Id, approvedPlan.State, approvedPlan.Current, approval = approvedPlan.Approvals.LastOrDefault() }, assignedFiles = paths }) + "\nTask: " + record.Task;
                child.WorkerInstructions += "\nRead-only semantic snapshot grants: " + string.Join(", ", snapshots) + ". Use csharp_status/query; do not load projects.";
                record.Purpose = args["purpose"]?.ToString() ?? "general";
                if (record.Purpose.Length > 80) throw new UserFacingException("Worker purpose exceeds 80 characters.");
                foreach (var sharedId in SharedIds(record.Task))
                {
                    child.ArtifactGrants.Add(new(ctx.Session.Id, sharedId));
                    child.WorkerInstructions += $"\nShared parent artifact {sharedId}: use artifact_read/search to inspect retained evidence. Source conversation: {ctx.Session.Id}.";
                }
                var images = args["attachmentIds"]?.AsArray().Select(i => i!.ToString()).ToArray() ?? [];
                if (images.Length > 4) throw new UserFacingException("Share at most four images.");
                foreach (var imageId in images)
                {
                    if (!child.Connection.Capabilities.Vision) throw new UserFacingException("Worker model does not advertise image input.");
                    var image = store.Attachments(ctx.Session.Id).SingleOrDefault(a => a.Id == imageId) ?? throw new UserFacingException("Image is not owned by the parent.");
                    var copy = AttachmentFiles.Import(store, child.Id, AttachmentFiles.Read(store, image), image.Width, image.Height, ctx.Session.Id, image.Id);
                    child.Attachments.Add(copy);
                    child.WorkerInstructions += $"\nAssigned image {copy.Id} comes from parent attachment {imageId}.";
                }
                record = workers.CreateWorker(record, child);
                var source = CancellationTokenSource.CreateLinkedTokenSource(ctx.Cancellation, lifetime.Token); source.CancelAfter(TimeSpan.FromMinutes(child.Execution.MaxMinutes));
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var task = Task.Run(async () => { await start.Task; await RunChild(record, child, source.Token, ctx.Emit); });
                live[record.Id] = (source, task); start.SetResult();
                ctx.Emit?.Invoke(new("agent.started", ctx.Session.Id, ctx.Run.Id, new { agentId = record.Id, record.Number, profile = profile.Name, model = profile.Model, task = Activity.Clean(record.Task, 160), writePaths = paths, message = $"Agent #{record.Number} · {profile.Name} · {Activity.Clean(record.Task, 160)}" }));
                return new(Json.Serialize(Summary(record)));
            case "agent_wait":
                var ids = args["agentIds"]!.AsArray().Select(i => Find(ctx.Session, i!.ToString()).Id).ToArray();
                var tasks = ids.Where(live.ContainsKey).Select(id => live[id].Task).ToArray();
                using (var waitStop = CancellationTokenSource.CreateLinkedTokenSource(ctx.Cancellation))
                {
                    try { if (tasks.Length > 0) await SteeringWake.WaitOrSteeringAsync(ctx, Task.WhenAny(Task.WhenAny(tasks), Task.Delay(args["waitMilliseconds"]!.GetValue<int>(), waitStop.Token))); }
                    finally { waitStop.Cancel(); }
                }
                ctx.Cancellation.ThrowIfCancellationRequested(); return new(Json.Serialize(new { agents = workers.Workers(ctx.Session.Id).Where(w => ids.Contains(w.Id)).Select(Summary), interruptedBy = ctx.SteeringPending ? "steering" : null }));
            case "agent_message":
                var inbox = (ISteeringStore)store;
                var receipt = inbox.SteeringReceipt(ctx.Session.Id, ctx.Run.Id, call.Id);
                if (receipt is null)
                {
                    var target = Find(ctx.Session, args["agentId"]!.ToString());
                    if (!target.Active) throw InactiveWorker(target);
                    receipt = inbox.SendSteering(ctx.Session.Id, target.ChildId, args["text"]!.ToString(), "parent", ctx.Run.Id, call.Id);
                }
                var recipient = workers.Workers(ctx.Session.Id).FirstOrDefault(w => w.ChildId == receipt.ConversationId);
                ctx.Emit?.Invoke(new("steering.saved", ctx.Session.Id, ctx.Run.Id, SteeringNotice.Create(receipt, "Steering saved", recipient?.Number)));
                return new(Json.Serialize(new { messageId = receipt.Id, targetId = receipt.ConversationId, state = receipt.State, note = "Saved for delivery, not confirmation of compliance." }));
            case "agent_cancel":
                var cancelled = Find(ctx.Session, args["agentId"]!.ToString()); await CancelAsync(cancelled.Id); return new(Json.Serialize(Summary(Find(ctx.Session, cancelled.Id))));
            case "agent_result":
                var result = Find(ctx.Session, args["agentId"]!.ToString());
                if (result.Active) return new(Json.Serialize(Summary(result)));
                return CollectResult(ctx, result);
            case "agent_finish":
                ctx.Session.WorkerReport = args.ToJsonString(); ctx.Save("agent.report.saved", new { report = args }); return new(args.ToJsonString(), FinishRun: true);
            default: throw new UserFacingException("Unknown agent tool.");
        }
    }
    private ToolOutput CollectResult(ToolExecutionContext ctx, WorkerRecord result)
    {
        if (result.ParentId != ctx.Session.Id || ctx.Session.ParentConversationId is not null)
            throw new UserFacingException("Worker result is outside this parent conversation.");
        if (result.Active || result.ReservationsHeld || result.State == "RecoveryRequired")
            throw new UserFacingException("Worker effects must settle before collecting its result. Inspect /agents and /recover.");
        if (ctx.Run.WorkerResults.TryGetValue(result.Id, out var receipt))
        {
            if (!result.Collected) { result.Collected = true; workers.SaveWorker(result); }
            return new(receipt);
        }
        var completedChild = store.Get(result.ChildId) ?? throw new UserFacingException("The saved worker session is missing. Its result was not collected; inspect local storage before continuing.");
        var completedContext = new ToolExecutionContext(completedChild, completedChild.Runs.LastOrDefault() ?? new(), store, ctx.Cancellation);
        ArtifactAccess.Backfill(completedContext);
        Directory.CreateDirectory(ctx.ArtifactDirectory);
        var imported = new List<object>();
        foreach (var sourceArtifact in completedChild.Artifacts.Take(100))
        {
            var sourcePath = ArtifactAccess.Validate(completedContext, sourceArtifact.Path);
            var destination = ArtifactAccess.Validate(ctx, Path.Combine(ctx.ArtifactDirectory, "agent-" + result.Id + "-" + sourceArtifact.Id + ".data"));
            if (File.Exists(sourcePath)) File.Copy(sourcePath, destination, true);
            var registered = ArtifactAccess.Register(ctx, destination, sourceArtifact.Kind == "process-log" ? "process-log" : "agent-report");
            registered.Truncated = sourceArtifact.Truncated;
            imported.Add(new { sourceConversationId = completedChild.Id, sourceArtifactId = sourceArtifact.Id, artifactId = registered.Id });
        }
        var path = ArtifactAccess.Validate(ctx, Path.Combine(ctx.ArtifactDirectory, "agent-" + result.Id + ".json"));
        File.WriteAllText(path, Json.Serialize(new { agent = result, checkpoints = completedChild.Mutations.Select(m => new { m.Id, m.State, m.Kind }), imported, provider = result.Profile.Connection.Provider.ToString() }));
        var artifact = ArtifactAccess.Register(ctx, path, "agent-report");
        var transcript = ArtifactAccess.Validate(ctx, Path.Combine(ctx.ArtifactDirectory, "agent-" + result.Id + "-transcript.json"));
        File.WriteAllText(transcript, Json.Serialize(new { childId = completedChild.Id, completedChild.Model, completedChild.History }));
        var transcriptArtifact = ArtifactAccess.Register(ctx, transcript, "agent-transcript");
        result.Collected = true;
        var content = Json.Serialize(new { agent = Summary(result), report = result.Report, checkpoints = completedChild.Mutations.Select(m => new { m.Id, m.State, m.Kind }), artifacts = new[] { artifact.Id, transcriptArtifact.Id }, imported });
        ctx.Run.WorkerResults.Add(result.Id, content);
        // Write the parent receipt before marking the worker collected. After a crash the
        // receipt can complete that marker without reimporting or losing delivered evidence.
        try { ctx.Save("agent.result.saved", new { agentId = result.Id, result.Number }); }
        catch { ctx.Run.WorkerResults.Remove(result.Id); result.Collected = false; throw; }
        workers.SaveWorker(result);
        return new(content);
    }

    private async Task RunChild(WorkerRecord record, Session child, CancellationToken ct, Action<AgentEvent>? emit)
    {
        var timer = Stopwatch.StartNew();
        record.RequestLimit = child.Execution.MaxRequests;
        try
        {
            using var lease = store.Acquire(child.Id);
            record.State = "Running"; workers.SaveWorker(record);
            var prompt = new UserPrompt { Parts = [new("text", record.Task), .. child.Attachments.Select(a => new PromptPart("image", a.Id))] };
            await new AgentRunner(store, models(child), this) { MaxSteps = child.Execution.MaxRequests, ActiveTimeLimit = TimeSpan.FromMinutes(child.Execution.MaxMinutes) }.RunAsync(child, record.Task, null, e =>
            {
                if (e.Type.StartsWith("steering.", StringComparison.Ordinal))
                    emit?.Invoke(e.Payload is SteeringNotice notice ? e with { Payload = notice with { Target = $"Agent #{record.Number}" } } : e);
                if (e.Type == "model.completed" && JsonNode.Parse(Json.Serialize(e.Payload))?["usage"] is { } usage) record.RequestUsage.Add(usage.ToString());
            }, ct, child.Attachments.Count > 0 ? prompt : null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { record.Reason = "Worker interrupted; inspect saved evidence. No automatic retry."; }
        finally
        {
            await inner.CancelProcessesAsync(new(child, child.Runs.LastOrDefault() ?? new(), store, CancellationToken.None));
            await gate.WaitAsync(CancellationToken.None);
            try
            {
                var persisted = store.Get(child.Id) ?? child;
                var run = persisted.Runs.LastOrDefault();
                record.Report = persisted.WorkerReport;
                record.State = persisted.Mutations.Any(m => m.State == "Prepared") || run?.State == RunState.RecoveryRequired ? "RecoveryRequired" : ct.IsCancellationRequested ? "Cancelled" : run?.State == RunState.Completed && record.Report is not null ? (JsonNode.Parse(record.Report)?["status"]?.ToString() == "blocked" ? "Blocked" : "Completed") : "Failed";
                record.Reason ??= ct.IsCancellationRequested ? "Worker cancelled or its task deadline expired; completed edits were retained." : run?.Reason ?? (record.Report is null ? "Worker did not submit a completed report." : null);
                record.AppliedCheckpointCount = persisted.Mutations.Count(m => m.State == "Applied");
                record.RequestsUsed = run?.BatchRequests;
                if (record.State is "Failed" or "RecoveryRequired") record.Failure = Failures.Worker(record, run?.PauseReason ?? PauseReason.None, run?.BatchRequests ?? 0);
                record.ReservationsHeld &= record.State == "RecoveryRequired";
                record.WaitingMilliseconds = child.ModelWaitMilliseconds;
                record.DurationMilliseconds = timer.Elapsed.TotalMilliseconds; record.Usage = run?.Usage;
                workers.SaveWorker(record);
            }
            finally { gate.Release(); }
            emit?.Invoke(new("agent.finished", record.ParentId, record.ParentRunId, new { agentId = record.Id, record.ChildId, record.Number, profile = record.Profile.Name, model = record.Profile.Model, record.State, record.DurationMilliseconds, record.RequestUsage, record.Reason, record.Failure, record.AppliedCheckpointCount, record.RequestsUsed, record.RequestLimit, message = $"Agent #{record.Number} · {record.State} · {record.DurationMilliseconds / 1000:0.0}s · tests not run" }));
        }
    }
    public WorkerRecord Find(Session parent, string id) => workers.Workers(parent.Id).SingleOrDefault(w => w.Id == id || "#" + w.Number == id)
        ?? throw Failures.Reject("worker_not_found", "No worker with this ID exists in the current conversation.", "Use /agents and select an ID or #number from this conversation.", id);
    public static UserFacingException InactiveWorker(WorkerRecord worker) => Failures.Reject("worker_inactive",
        $"Agent #{worker.Number} is {worker.State}; it cannot receive new messages. Messages cannot relaunch a worker.",
        $"Inspect /agent #{worker.Number} and collect its result. Create a new assignment if more work is needed.", $"Agent #{worker.Number}");
    private static string[] SharedIds(string task) => System.Text.RegularExpressions.Regex.Matches(task, @"\[artifact:([^\]\s]+)\]").Select(m => m.Groups[1].Value).Distinct().ToArray();
    public static object Summary(WorkerRecord w) => new { agentId = w.Id, w.Number, w.ChildId, profile = w.Profile.Name, model = w.Profile.Model, provider = w.Profile.Connection.Provider.ToString(), w.State, w.Collected, w.ReservationsHeld, w.WritePaths, w.DurationMilliseconds, w.WaitingMilliseconds, w.Purpose, w.Reason, w.Failure, w.AppliedCheckpointCount, w.RequestsUsed, w.RequestLimit };
    public async Task CancelAsync(string id)
    {
        if (live.TryGetValue(id, out var entry)) { entry.Source.Cancel(); await entry.Task; }
    }
    public async Task StopAllAsync(string parentId)
    {
        var ids = workers.Workers(parentId).Where(w => w.Active).Select(w => w.Id).ToArray();
        foreach (var id in ids) if (live.TryGetValue(id, out var entry)) entry.Source.Cancel();
        foreach (var id in ids) if (live.TryGetValue(id, out var entry)) await entry.Task;
    }
    public async Task<bool> CanCompleteAsync(ToolExecutionContext ctx)
    {
        if (ctx.Session.ParentConversationId is not null) return false;
        var pending = workers.Workers(ctx.Session.Id).Where(w => w.ParentRunId == ctx.Run.Id && !w.Collected).ToArray();
        if (pending.Length == 0) return await inner.CanCompleteAsync(ctx);
        if (pending.Any(w => w.State == "RecoveryRequired")) throw new UserFacingException("A worker requires recovery. Inspect /agents and /recover.");
        var active = pending.Where(w => live.ContainsKey(w.Id)).Select(w => live[w.Id].Task).ToArray();
        if (active.Length > 0) await SteeringWake.WaitOrSteeringAsync(ctx, Task.WhenAll(active));
        ctx.Cancellation.ThrowIfCancellationRequested();
        if (ctx.SteeringPending) return false;
        foreach (var result in workers.Workers(ctx.Session.Id).Where(w => w.ParentRunId == ctx.Run.Id && !w.Collected && !w.Active))
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var output = CollectResult(ctx, result);
            var artifactId = JsonNode.Parse(output.Content)?["artifacts"]?[0]?.ToString();
            ctx.Emit?.Invoke(new("agent.collected", ctx.Session.Id, ctx.Run.Id, new { agentId = result.Id, result.Number, result.State, artifactId, automatic = true }));
        }
        // Give the model one request containing the collected evidence before accepting its
        // final answer. Collection is not verification, approval, or a successful worker outcome.
        return false;
    }
    public CompletionRequirement? GetCompletionRequirement(ToolExecutionContext ctx)
    {
        if (ctx.Session.ParentConversationId is not null) return new("worker_report_pending",
            "This worker has not submitted its completion report.",
            "Call agent_finish with status completed or blocked, an honest summary, evidence and limitations. If blocked, include the clarification needed. A prose answer does not submit the report.");
        var pending = workers.Workers(ctx.Session.Id).Where(w => w.ParentRunId == ctx.Run.Id && !w.Collected).ToArray();
        if (pending.Length == 0) return inner.GetCompletionRequirement(ctx);
        return new("worker_results_pending",
            $"{pending.Length} worker result(s) still need collection: " + string.Join(", ", pending.Take(8).Select(w => $"Agent #{w.Number} ({w.State})")) + ".",
            "For active workers use agent_wait; for terminal workers call agent_result to collect their reports, including failed or cancelled workers. Do not relaunch them merely to complete collection. " +
            string.Join("; ", pending.Take(8).Select(w => $"Agent #{w.Number}: agentId={w.Id}")) +
            ". Inspect collected evidence and then finish the task or report its limitations. Do not repeat the final answer before collection.");
    }
    public async Task RunEndedAsync(ToolExecutionContext ctx)
    {
        if (ctx.Session.ParentConversationId is not null) return;
        await StopAllAsync(ctx.Session.Id);
        if (workers.Workers(ctx.Session.Id).Any(w => w.State == "RecoveryRequired"))
        {
            ctx.Run.State = RunState.RecoveryRequired; ctx.Run.Reason = "Worker file effects require recovery. Inspect /agents and /recover.";
            ctx.Save("run.outcome", new { state = ctx.Run.State.ToString(), ctx.Run.Reason });
        }
    }
    public Task CancelProcessesAsync(ToolExecutionContext ctx) => inner.CancelProcessesAsync(ctx);
    public ToolOutput? Recover(ToolExecutionContext ctx, ToolCall call)
    {
        if (call.Name == "agent_message")
        {
            var receipt = (store as ISteeringStore)?.SteeringReceipt(ctx.Session.Id, ctx.Run.Id, call.Id);
            return new(Json.Serialize(receipt is null ? (object)new { error = "No committed steering message exists. This call was not replayed." } : new { messageId = receipt.Id, targetId = receipt.ConversationId, state = receipt.State }));
        }
        if (call.Name == "agent_spawn")
        {
            var record = workers.Workers(ctx.Session.Id).SingleOrDefault(w => w.ParentRunId == ctx.Run.Id && w.CallId == call.Id);
            return record is null ? new(Json.Serialize(new { error = "No durable worker launch exists; it was not replayed." })) : new(Json.Serialize(Summary(record)));
        }
        if (call.Name == "agent_finish") return ctx.Session.WorkerReport is null ? null : new(ctx.Session.WorkerReport, FinishRun: true);
        return inner.Recover(ctx, call);
    }
    public void RecoverChildren(Session parent, bool abandon = false)
    {
        foreach (var record in workers.Workers(parent.Id).Where(w => w.Active || w.ReservationsHeld))
        {
            if (live.TryGetValue(record.Id, out var running) && !running.Task.IsCompleted) continue;
            var child = store.Get(record.ChildId) ?? throw new UserFacingException("Worker session is missing; preserve reservations and inspect storage.");
            var ctx = new ToolExecutionContext(child, child.Runs.LastOrDefault() ?? new(), store, CancellationToken.None);
            inner.RecoverSession(ctx);
            var uncertain = child.Mutations.Any(m => m.State == "Prepared");
            if (abandon) { foreach (var m in child.Mutations.Where(m => m.State == "Prepared")) m.State = "Abandoned"; uncertain = false; }
            record.State = uncertain ? "RecoveryRequired" : "Interrupted"; record.ReservationsHeld &= uncertain; record.Reason = abandon ? "Explicitly abandoned; no effects replayed." : "Previous CLI instance ended. Worker will not restart automatically.";
            if (child.Runs.LastOrDefault() is { } run) { run.State = uncertain ? RunState.RecoveryRequired : RunState.Cancelled; run.Reason = record.Reason; }
            ctx.Save("agent.interrupted", new { record.Id, record.State }); workers.SaveWorker(record);
        }
    }
    public IEnumerable<Session> ChildSessions(Session parent) => workers.Workers(parent.Id).Select(w => store.Get(w.ChildId)).OfType<Session>();
    public async Task<ToolOutput> UndoAsync(Session parent, string checkpoint)
    {
        await gate.WaitAsync();
        try
        {
            var owner = new[] { parent }.Concat(ChildSessions(parent)).SingleOrDefault(s => s.Mutations.Any(m => m.Id == checkpoint)) ?? throw new UserFacingException("Checkpoint does not belong to this conversation tree.");
            var mutation = owner.Mutations.Single(m => m.Id == checkpoint);
            CheckWrites(parent, Paths(parent, mutation.Files.Select(f => f.Path)));
            WorkflowPolicy.CheckFiles(parent, mutation.Files.Select(f => f.Path));
            var context = new ToolExecutionContext(owner, owner.Runs.LastOrDefault() ?? new(), store, CancellationToken.None);
            var output = FileOperations.Undo(context, checkpoint);
            inner.Navigation.Invalidate(context, mutation.Files.Select(f => f.Path));
            return output;
        }
        finally { gate.Release(); }
    }
    public async Task<ToolOutput> ExportReportAsync(Session parent, string id, string destination)
    {
        await gate.WaitAsync();
        try
        {
            var report = parent.Reports.SingleOrDefault(r => r.Id == id) ?? throw new UserFacingException("Report does not belong to this conversation.");
            if (!destination.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) throw new UserFacingException("Report export requires a new .md file.");
            var path = Paths(parent, [destination]).Single();
            if (workers.Workers(parent.Id).Any(w => w.ReservationsHeld && w.WritePaths.Any(p => Overlaps(p, path)))) throw new UserFacingException("Export path is reserved by a worker.");
            var ctx = new ToolExecutionContext(parent, parent.Runs.LastOrDefault() ?? new(), store, CancellationToken.None);
            var call = new ToolCall { Id = "export-" + Guid.NewGuid().ToString("N"), Name = "file_create" };
            var args = new JsonObject { ["path"] = path, ["content"] = WorkflowTools.Markdown(report) };
            var record = FileOperations.Prepare(ctx, call, args, userExport: true);
            ctx.Save("report.export.prepared", new { report.Id, checkpointId = record.Id, path });
            return FileOperations.Apply(ctx, record, userExport: true);
        }
        finally { gate.Release(); }
    }
    public void Dispose()
    {
        lifetime.Cancel();
        try { Task.WhenAll(live.Values.Select(v => v.Task)).GetAwaiter().GetResult(); }
        finally { foreach (var entry in live.Values) entry.Source.Dispose(); lifetime.Dispose(); gate.Dispose(); }
    }
}
