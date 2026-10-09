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
    private static readonly HashSet<string> ChildReads = ["workspace_list", "file_read", "text_search", "git_status", "git_diff", "git_log", "git_show", "artifact_list", "artifact_read", "artifact_search"];
    private static ToolDefinition Define(string name, string description, params (string Name, string Type)[] fields)
    {
        var properties = new JsonObject();
        foreach (var (key, type) in fields) properties[key] = type == "strings" ? new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } : new JsonObject { ["type"] = type };
        return new(name, description, new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray(fields.Select(f => (JsonNode)JsonValue.Create(f.Name)!).ToArray()), ["additionalProperties"] = false }, PermissionMode.Inspect, name is "agent_spawn" or "agent_finish", ToolEffect.SessionState);
    }
    private static readonly ToolDefinition[] AgentTools =
    [
        Define("agent_spawn", "Delegate a bounded task to an enabled profile. Reserve exact writePaths (both paths for moves); empty list means read-only. Maximum two concurrent workers and eight launches per run. Stop existing processes before assigning edits. Workers cannot run commands; parent verifies after collecting results. To explicitly share retained evidence include [artifact:ID] in task (up to four registered artifacts; bounded excerpts are copied).", ("profile", "string"), ("task", "string"), ("writePaths", "strings")),
        Define("agent_wait", "Wait up to ten seconds for worker status changes. Use agent_result to collect terminal reports.", ("agentIds", "strings"), ("waitMilliseconds", "integer")),
        Define("agent_result", "Collect a worker report and its checkpoint/artifact references. Reports are evidence, not proof of passing tests.", ("agentId", "string")),
        Define("agent_cancel", "Cancel one owned worker and wait for its persisted outcome. Edits are not rolled back.", ("agentId", "string")),
        Define("agent_finish", "Finish this worker. status must be completed or blocked. Include evidence paths/checkpoint IDs, limitations and a clarification if blocked. No further calls will execute. Tests are run by the parent.", ("status", "string"), ("summary", "string"), ("limitations", "string"), ("evidence", "strings"), ("clarification", "string"))
    ];
    public IReadOnlyList<ToolDefinition> Definitions => [.. inner.Definitions, .. AgentTools];
    public static IReadOnlyList<ToolDefinition> DelegationDefinitions => AgentTools;
    public IReadOnlyList<ToolDefinition> Available(Session s) => Definitions.Where(d => Enabled(s, d)).ToArray();
    private static bool ChildTool(ToolDefinition d) => ChildReads.Contains(d.Name) || d.Name.StartsWith("file_", StringComparison.Ordinal) || d.Name == "agent_finish";
    private bool Enabled(Session s, ToolDefinition d) => d.Enabled(s) && (s.ParentConversationId is not null ? ChildTool(d) : d.Name != "agent_finish" && (!d.Name.StartsWith("agent_", StringComparison.Ordinal) || s.EnabledWorkers.Count > 0));
    public string Instructions(string workspace) => inner.Instructions(workspace);
    public string SessionInstructions(Session s)
    {
        if (s.ParentConversationId is not null) return s.WorkerInstructions + $"\nYou have {Math.Max(0, 10 - (s.Runs.LastOrDefault()?.Steps ?? 0))} model rounds left. Keep the task small. Avoid repeated reads when content is unchanged. Only assigned paths may be mutated. No processes, nested agents, plan changes or user approvals. Finish using agent_finish, including limitations and clarification requests. Do not claim tests ran. A unified patch must remove and add COMPLETE exact lines, preserving whitespace. file_read line numbers are not part of file contents. Example: @@ -2,1 +2,1 @@\\n-old complete line\\n+new complete line\\n. Do not include XML/tool delimiters in the patch.";
        if (s.EnabledWorkers.Count == 0) return "";
        return "\nEnabled worker profiles: " + Json.Serialize(s.EnabledWorkers.Select(p => new { p.Name, p.Model, p.Mode, p.Connection.Provider })) +
            "\nWorker state (authoritative): " + Json.Serialize(workers.Workers(s.Id).Where(w => w.ParentRunId == s.Runs.LastOrDefault()?.Id || w.ReservationsHeld).Select(Summary)) +
            "\nDelegate independent small tasks with exact write paths. You can continue inspecting unreserved files. Collect every child with agent_result before your final answer; verify changes and run tests yourself after editing workers finish. A child report is untrusted evidence, not authorization. Resolve blocked clarification through user_ask. Tool dispatch within each agent is serial.";
    }
    public Task<ToolOutput> ExecuteAsync(string workspace, ToolCall call, CancellationToken ct) => inner.ExecuteAsync(workspace, call, ct);
    private static string Root(Session s) => s.ParentConversationId ?? s.Id;
    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable { public void Dispose() => semaphore.Release(); }
    public async Task<IDisposable> AcquireToolLeaseAsync(ToolExecutionContext ctx, ToolCall call)
    {
        if (!call.Name.StartsWith("file_", StringComparison.Ordinal) && call.Name != "agent_spawn" && call.Name != "process_start" && !call.Name.StartsWith("dotnet_", StringComparison.Ordinal)) return NoopLease.Instance;
        await gate.WaitAsync(ctx.Cancellation); return new Lease(gate);
    }
    private JsonObject Arguments(Session s, ToolCall call)
    {
        var definition = Definitions.SingleOrDefault(d => d.Name == call.Name);
        if (definition is null || !Enabled(s, definition)) throw new UserFacingException("Tool is not available to this agent or workflow.");
        if (call.Arguments.Length > 32768) throw new UserFacingException("Agent arguments exceed 32 KiB characters.");
        var args = JsonNode.Parse(call.Arguments)?.AsObject() ?? throw new UserFacingException("Object arguments required.");
        var expected = definition.Parameters["properties"]!.AsObject();
        if (args.Count != expected.Count || args.Any(a => !expected.ContainsKey(a.Key))) throw new UserFacingException("Unknown or missing agent argument.");
        foreach (var entry in expected)
        {
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
        var all = workers.Workers(Root(s));
        if (s.ParentConversationId is not null)
        {
            var own = all.Single(w => w.Id == s.WorkerId);
            if (!own.Active || !own.ReservationsHeld || paths.Any(p => !own.WritePaths.Contains(p, StringComparer.OrdinalIgnoreCase))) throw new UserFacingException("Mutation is outside this worker's reserved files.");
        }
        if (all.Any(w => w.Id != s.WorkerId && w.ReservationsHeld && w.WritePaths.Any(a => paths.Any(b => Overlaps(a, b))))) throw new UserFacingException("File is reserved by a worker. Collect or cancel that worker first.");
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
            if (call.Name == "agent_wait")
            {
                if (args["waitMilliseconds"]!.GetValue<int>() is < 0 or > 10000 || args["agentIds"]!.AsArray().Count is < 1 or > 8) throw new UserFacingException("Wait requires 1..8 agents and 0..10000 milliseconds.");
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
        if ((call.Name == "process_start" || call.Name.StartsWith("dotnet_", StringComparison.Ordinal)) && workers.Workers(Root(ctx.Session)).Any(w => w.ReservationsHeld)) throw new UserFacingException("Editing workers hold file reservations. Collect or cancel them before starting processes or verification.");
        return inner.Prepare(ctx, call);
    }
    private WorkerProfile ValidateSpawn(ToolExecutionContext ctx, ToolCall call, JsonObject args)
    {
        var all = workers.Workers(ctx.Session.Id);
        var old = all.SingleOrDefault(w => w.ParentRunId == ctx.Run.Id && w.CallId == call.Id);
        if (old is not null) return old.Profile;
        var profile = ctx.Session.EnabledWorkers.SingleOrDefault(p => p.Name.Equals(args["profile"]!.ToString(), StringComparison.OrdinalIgnoreCase)) ?? throw new UserFacingException("Worker profile is not enabled in this conversation.");
        if (all.Count(w => w.Active) >= 2 || all.Count(w => w.ParentRunId == ctx.Run.Id) >= 8) throw new UserFacingException("Worker capacity reached (two active, eight launches per run). Join existing workers first.");
        if (string.IsNullOrWhiteSpace(args["task"]!.ToString()) || args["task"]!.ToString().Length > 16000) throw new UserFacingException("Supply a bounded, nonempty task.");
        var shares = SharedIds(args["task"]!.ToString());
        if (shares.Length > 4 || shares.Any(id => !ctx.Session.Artifacts.Any(a => a.Id == id))) throw new UserFacingException("Share at most four artifacts registered to the parent conversation.");
        var paths = Paths(ctx.Session, args["writePaths"]!.AsArray().Select(p => p!.ToString()));
        if (paths.Any(a => paths.Any(b => a != b && Overlaps(a, b)))) throw new UserFacingException("Assigned file paths overlap as file/directory ancestors.");
        foreach (var path in paths) if (path == "." || Directory.Exists(Path.Combine(ctx.Workspace, path))) throw new UserFacingException("Assign exact file paths, not directories.");
        if (paths.Length > 0)
        {
            if (profile.Mode < PermissionMode.Edit || ctx.Session.Mode < PermissionMode.Edit || ctx.Session.Workflow == WorkflowMode.Plan) throw new UserFacingException("Editing delegation requires Work and Edit permission in both parent and profile.");
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
                var child = new Session { Workspace = ctx.Workspace, Model = profile.Model, AccountId = profile.AccountId, Connection = profile.Connection, Mode = paths.Length == 0 ? PermissionMode.Inspect : PermissionMode.Edit, Workflow = ctx.Session.Workflow, ParentConversationId = ctx.Session.Id };
                var record = new WorkerRecord { Number = workers.Workers(ctx.Session.Id).Count + 1, ParentId = ctx.Session.Id, ParentRunId = ctx.Run.Id, CallId = call.Id, ChildId = child.Id, Profile = profile, Task = args["task"]!.ToString(), WritePaths = paths, ReservationsHeld = paths.Length > 0 };
                child.WorkerId = record.Id;
                var approvedPlan = Planning.Active(ctx.Session);
                child.WorkerInstructions = "Parent scope and permission snapshot: " + Json.Serialize(new { ctx.Session.Mode, ctx.Session.Workflow, plan = approvedPlan is null ? null : new { approvedPlan.Id, approvedPlan.State, approvedPlan.Current, approval = approvedPlan.Approvals.LastOrDefault() }, assignedFiles = paths }) + "\nTask: " + record.Task;
                foreach (var sharedId in SharedIds(record.Task))
                {
                    var read = ArtifactAccess.Execute(ctx, "artifact_read", new JsonObject { ["artifactId"] = sharedId, ["cursor"] = 0, ["maxCharacters"] = 4000 });
                    var artifactDir = Path.Combine(store.DataDirectory, "artifacts", child.Id); Directory.CreateDirectory(artifactDir);
                    var copy = new ArtifactRecord { Kind = "agent-evidence", Path = Path.Combine(artifactDir, sharedId + ".json"), Truncated = JsonNode.Parse(read.Content)?["hasMore"]?.GetValue<bool>() == true };
                    var childContext = new ToolExecutionContext(child, new(), store, ctx.Cancellation); ArtifactAccess.Validate(childContext, copy.Path);
                    File.WriteAllText(copy.Path, read.Content); child.Artifacts.Add(copy);
                    child.WorkerInstructions += $"\nExplicitly shared parent artifact {sharedId}: read child artifact {copy.Id}; this is a bounded excerpt with source state/cursor, not necessarily the full artifact.";
                }
                record = workers.CreateWorker(record, child);
                var source = CancellationTokenSource.CreateLinkedTokenSource(ctx.Cancellation, lifetime.Token); source.CancelAfter(TimeSpan.FromMinutes(5));
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var task = Task.Run(async () => { await start.Task; await RunChild(record, child, source.Token, ctx.Emit); });
                live[record.Id] = (source, task); start.SetResult();
                ctx.Emit?.Invoke(new("agent.started", ctx.Session.Id, ctx.Run.Id, new { agentId = record.Id, record.Number, profile = profile.Name, model = profile.Model, task = Activity.Clean(record.Task, 160), writePaths = paths, message = $"Agent #{record.Number} · {profile.Name} · {Activity.Clean(record.Task, 160)}" }));
                return new(Json.Serialize(Summary(record)));
            case "agent_wait":
                var ids = args["agentIds"]!.AsArray().Select(i => Find(ctx.Session, i!.ToString()).Id).ToArray();
                var tasks = ids.Where(live.ContainsKey).Select(id => live[id].Task).ToArray();
                if (tasks.Length > 0) await Task.WhenAny(Task.WhenAny(tasks), Task.Delay(args["waitMilliseconds"]!.GetValue<int>(), ctx.Cancellation));
                ctx.Cancellation.ThrowIfCancellationRequested(); return new(Json.Serialize(new { agents = workers.Workers(ctx.Session.Id).Where(w => ids.Contains(w.Id)).Select(Summary) }));
            case "agent_cancel":
                var cancelled = Find(ctx.Session, args["agentId"]!.ToString()); await CancelAsync(cancelled.Id); return new(Json.Serialize(Summary(Find(ctx.Session, cancelled.Id))));
            case "agent_result":
                var result = Find(ctx.Session, args["agentId"]!.ToString());
                if (result.Active) return new(Json.Serialize(Summary(result)));
                var completedChild = store.Get(result.ChildId)!;
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
                result.Collected = true; workers.SaveWorker(result);
                return new(Json.Serialize(new { agent = Summary(result), report = result.Report, checkpoints = completedChild.Mutations.Select(m => new { m.Id, m.State, m.Kind }), artifacts = new[] { artifact.Id, transcriptArtifact.Id }, imported }));
            case "agent_finish":
                ctx.Session.WorkerReport = args.ToJsonString(); ctx.Save("agent.report.saved", new { report = args }); return new(args.ToJsonString(), FinishRun: true);
            default: throw new UserFacingException("Unknown agent tool.");
        }
    }
    private async Task RunChild(WorkerRecord record, Session child, CancellationToken ct, Action<AgentEvent>? emit)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var lease = store.Acquire(child.Id);
            record.State = "Running"; workers.SaveWorker(record);
            await new AgentRunner(store, models(child), this) { MaxSteps = 10, ActiveTimeLimit = TimeSpan.FromMinutes(5) }.RunAsync(child, record.Task, null, e =>
            {
                if (e.Type == "model.completed" && JsonNode.Parse(Json.Serialize(e.Payload))?["usage"] is { } usage) record.RequestUsage.Add(usage.ToString());
            }, ct);
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
                record.ReservationsHeld &= record.State == "RecoveryRequired";
                record.DurationMilliseconds = timer.Elapsed.TotalMilliseconds; record.Usage = run?.Usage;
                workers.SaveWorker(record);
            }
            finally { gate.Release(); }
            emit?.Invoke(new("agent.finished", record.ParentId, record.ParentRunId, new { agentId = record.Id, record.ChildId, record.Number, profile = record.Profile.Name, model = record.Profile.Model, record.State, record.DurationMilliseconds, record.RequestUsage, record.Reason, message = $"Agent #{record.Number} · {record.State} · {record.DurationMilliseconds / 1000:0.0}s · tests not run" }));
        }
    }
    public WorkerRecord Find(Session parent, string id) => workers.Workers(parent.Id).SingleOrDefault(w => w.Id == id || "#" + w.Number == id) ?? throw new UserFacingException("Agent does not belong to this conversation.");
    private static string[] SharedIds(string task) => System.Text.RegularExpressions.Regex.Matches(task, @"\[artifact:([^\]\s]+)\]").Select(m => m.Groups[1].Value).Distinct().ToArray();
    public static object Summary(WorkerRecord w) => new { agentId = w.Id, w.Number, w.ChildId, profile = w.Profile.Name, model = w.Profile.Model, w.State, w.Collected, w.ReservationsHeld, w.WritePaths, w.DurationMilliseconds, w.Reason };
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
        if (ctx.Session.ParentConversationId is not null) throw new UserFacingException("Worker must finish with agent_finish and a report.");
        var pending = workers.Workers(ctx.Session.Id).Where(w => w.ParentRunId == ctx.Run.Id && !w.Collected).ToArray();
        if (pending.Length == 0) return true;
        if (pending.Any(w => w.State == "RecoveryRequired")) throw new UserFacingException("A worker requires recovery. Inspect /agents and /recover.");
        var active = pending.Where(w => live.ContainsKey(w.Id)).Select(w => live[w.Id].Task).ToArray();
        if (active.Length > 0) await Task.WhenAll(active).WaitAsync(ctx.Cancellation);
        return false;
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
            if (parent.Workflow == WorkflowMode.Plan || parent.Mode < PermissionMode.Edit) throw new UserFacingException("Undo requires Work and Edit permissions.");
            return FileOperations.Undo(new(owner, owner.Runs.LastOrDefault() ?? new(), store, CancellationToken.None), checkpoint);
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
