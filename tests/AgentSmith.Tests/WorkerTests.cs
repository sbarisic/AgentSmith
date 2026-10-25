using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

public sealed class WorkerTests
{
    [Theory]
    [InlineData("native")]
    [InlineData("basic")]
    [InlineData("off")]
    public async Task NewWorkersInheritSearchPolicy(string policy)
    {
        using var f = new Fixture(); f.Parent.WebSearch = policy;
        await f.Spawn([]);
        var child = f.Store.Get(Assert.Single(f.Store.Workers(f.Parent.Id)).ChildId)!;
        Assert.Equal(policy, child.WebSearch);
        Assert.Equal(policy != "off", f.Agents.Available(child).Any(t => t.Name == "web_search"));
        await f.Agents.StopAllAsync(f.Parent.Id);
    }
    [Fact]
    public async Task SemanticLoadingObeysEditingWorkerBarrierAndWorkersOnlyGetReadTools()
    {
        using var f = new Fixture(); await f.Spawn(["C.cs"]);
        var failure = await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("csharp_load", new { project = "A.csproj", configuration = "", framework = "" }));
        Assert.Contains("reservations", failure.Message);
        var child = f.Store.Get(f.Store.Workers(f.Parent.Id).Single().ChildId)!;
        Assert.Contains(f.Agents.Available(child), d => d.Name == "csharp_symbols");
        Assert.Contains(f.Agents.Available(child), d => d.Name == "diagnostics_read");
        Assert.DoesNotContain(f.Agents.Available(child), d => d.Name == "csharp_load");
        await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("csharp_status", new { snapshotId = "unshared" }, child));
        await f.Agents.StopAllAsync(f.Parent.Id);
    }
    private sealed class WaitingModel : IModelClient
    {
        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
        { await Task.Delay(Timeout.Infinite, ct); yield break; }
    }
    private sealed class Fixture : IDisposable
    {
        public readonly TestDirectory Dir = new();
        public readonly SessionStore Store;
        public readonly ToolRegistry Registry = new();
        public readonly WorkerCoordinator Agents;
        public readonly Session Parent;
        public readonly Run Run = new() { State = RunState.Running };
        public Fixture(Func<Session, IModelClient>? model = null, Func<SessionStore, ISessionStore>? storeFactory = null)
        {
            Store = new(Path.Combine(Dir.Path, "data"));
            Parent = new() { Workspace = Dir.Path, WorkerPolicy = new() { MaxLocalConcurrent = 2 }, EnabledWorkers = [new() { Name = "editor", Model = "local", Mode = PermissionMode.Edit, Connection = new() { Provider = ModelProvider.LmStudio } }] };
            Parent.Runs.Add(Run); Store.Save(Parent, new("created", Parent.Id, Run.Id, new { }));
            Agents = new(storeFactory?.Invoke(Store) ?? Store, Store, Registry, model ?? (_ => new WaitingModel()));
        }
        public ToolExecutionContext Context(Session? s = null) => new(s ?? Parent, s?.Runs.LastOrDefault() ?? Run, Store, CancellationToken.None);
        public async Task<ToolOutput> Invoke(string name, object args, Session? s = null, string? id = null)
        {
            var ctx = Context(s); var call = new ToolCall { Name = name, Id = id ?? Guid.NewGuid().ToString("N"), Arguments = Json.Serialize(args) };
            using var lease = await Agents.AcquireToolLeaseAsync(ctx, call);
            call.Prepared = Agents.Prepare(ctx, call); ctx.Save("prepared", new { }); return await Agents.ExecuteAsync(ctx, call);
        }
        public Task<ToolOutput> Spawn(string[] paths, string? id = null) => Invoke("agent_spawn", new { profile = "editor", task = "Small test task", writePaths = paths }, id: id);
        public void Dispose() { Agents.Dispose(); Registry.Dispose(); Dir.Dispose(); }
    }
    [Fact]
    public async Task WorkerMessagesAreDurableIdempotentAndParentWaitWakesWithoutCancellingChild()
    {
        using var f = new Fixture(); await f.Spawn([]);
        var worker = Assert.Single(f.Store.Workers(f.Parent.Id));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (f.Store.Get(worker.ChildId)!.Runs.Count == 0) await Task.Delay(10, deadline.Token);
        var message = await f.Invoke("agent_message", new { agentId = worker.Id, text = "Focus on the parser; do not edit." }, id: "send-once");
        var repeat = await f.Invoke("agent_message", new { agentId = worker.Id, text = "Focus on the parser; do not edit." }, id: "send-once");
        // The worker can incorporate the message between reads; identity, not delivery state, is idempotent.
        var originalReceipt = JsonNode.Parse(message.Content)!; var repeatedReceipt = JsonNode.Parse(repeat.Content)!;
        Assert.Equal(originalReceipt["messageId"]!.ToString(), repeatedReceipt["messageId"]!.ToString());
        Assert.Equal(originalReceipt["targetId"]!.ToString(), repeatedReceipt["targetId"]!.ToString());
        Assert.Contains(repeatedReceipt["state"]!.ToString(), new[] { "Pending", "Incorporated" });
        var saved = Assert.Single(f.Store.Steering(f.Parent.Id)); Assert.Equal("parent", saved.Sender); Assert.Equal(worker.ChildId, saved.ConversationId);
        var child = f.Store.Get(worker.ChildId)!;
        Assert.DoesNotContain(f.Agents.Available(child), t => t.Name == "agent_message");
        await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("agent_message", new { agentId = worker.Id, text = "illegal" }, child));
        await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("agent_wait", new { agentIds = new[] { worker.Id }, waitMilliseconds = 360001 }));
        await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("agent_wait", new { agentIds = new[] { worker.Id }, waitMilliseconds = -1 }));
        var waiting = f.Invoke("agent_wait", new { agentIds = new[] { worker.Id }, waitMilliseconds = 360000 });
        f.Store.SendSteering(f.Parent.Id, f.Parent.Id, "Stop waiting and inspect current status.");
        Assert.Contains("steering", (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Content);
        Assert.True(f.Store.Workers(f.Parent.Id).Single().Active);
        await f.Agents.CancelAsync(worker.Id);
        var recovered = f.Agents.Recover(f.Context(), new ToolCall { Id = "send-once", Name = "agent_message" });
        Assert.Contains(saved.Id, recovered!.Content); Assert.Single(f.Store.Steering(f.Parent.Id), i => i.Sender == "parent");
        await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("agent_message", new { agentId = worker.Id, text = "restart" }));
    }
    [Fact]
    public async Task WorkerReceivesParentDirectionWithoutChangingAssignments()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new MessageModel(entered, release);
        using var f = new Fixture(_ => model); await f.Spawn(["assigned.txt"]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var worker = Assert.Single(f.Store.Workers(f.Parent.Id));
        await f.Invoke("agent_message", new { agentId = worker.Id, text = "Only review the assignment; report a limitation." });
        release.SetResult();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (f.Store.Workers(f.Parent.Id).Single().Active) await Task.Delay(25, deadline.Token);
        Assert.Equal(2, model.Requests.Count);
        Assert.Contains("Direction from the parent agent", Json.Serialize(model.Requests[1].Input));
        Assert.Equal("Sent", Assert.Single(f.Store.Steering(f.Parent.Id)).State);
        Assert.Equal(new[] { "assigned.txt" }, f.Store.Workers(f.Parent.Id).Single().WritePaths);
        Assert.False(File.Exists(Path.Combine(f.Dir.Path, "assigned.txt")));
    }
    private sealed class MessageModel(TaskCompletionSource entered, TaskCompletionSource release) : IModelClient
    {
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                entered.SetResult(); await release.Task.WaitAsync(ct);
                yield return RuntimeTests.Done(RuntimeTests.Call("old-edit", "file_create", "{\"path\":\"assigned.txt\",\"content\":\"old intent\"}"));
            }
            else yield return RuntimeTests.Done(Finish());
        }
    }
    [Fact]
    public async Task EditingWorkersBlockParentHttpWritesButAllowReads()
    {
        using var f = new Fixture(); await f.Spawn(["owned.txt"]);
        var write = new ToolCall { Name = "http_request", Arguments = Json.Serialize(new { method="POST", url="http://localhost:1234/test", headers=Array.Empty<object>(), body="{}", timeoutSeconds=10 }) };
        var ex = Assert.Throws<UserFacingException>(() => f.Agents.Prepare(f.Context(), write));
        Assert.Contains("reservations",ex.Message);
        var read = new ToolCall { Name="http_request", Arguments=Json.Serialize(new { method="GET",url="http://localhost:1234/test",headers=Array.Empty<object>(),body="",timeoutSeconds=10 }) };
        Assert.NotNull(f.Agents.Prepare(f.Context(),read));
        var record = Assert.Single(f.Store.Workers(f.Parent.Id));var child=f.Store.Get(record.ChildId)!;
        Assert.Contains(f.Agents.Available(child),t=>t.Name=="workspace_map");
        Assert.Throws<UserFacingException>(()=>f.Agents.Prepare(f.Context(child),write));
        await f.Agents.CancelAsync(record.Id);
    }
    [Fact]
    public async Task DocumentAndReviewWorkersRespectWorkflowAndFinalizationBarriers()
    {
        using var f = new Fixture(); f.Parent.Workflow = WorkflowMode.Review;
        await Assert.ThrowsAsync<UserFacingException>(() => f.Spawn(["README.md"]));
        f.Parent.Workflow = WorkflowMode.Document;
        await Assert.ThrowsAsync<UserFacingException>(() => f.Spawn(["Source.cs"]));
        var report = WorkflowPolicy.Begin(f.Parent, "Document");
        report.Revisions.Add(new() { Revision = 1, Summary = "Draft", Coverage = "Assigned docs" });
        await f.Spawn(["README.md"]);
        await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("workflow_finish", new { expectedRevision = 1 }));
        await Assert.ThrowsAsync<UserFacingException>(() => f.Agents.ExportReportAsync(f.Parent, report.Id, "README.md"));
        var record = Assert.Single(f.Store.Workers(f.Parent.Id));
        var child = f.Store.Get(record.ChildId)!;
        Assert.DoesNotContain(f.Agents.Available(child), t => t.Name.StartsWith("workflow_"));
        await f.Agents.CancelAsync(record.Id);
    }
    private static JsonObject Finish(string status = "completed") => RuntimeTests.Call("finish", "agent_finish", Json.Serialize(new { status, summary = "Task inspected", limitations = "Tests not run", evidence = new[] { "a.txt" }, clarification = status == "blocked" ? "Which target?" : "" }));
    [Fact]
    public async Task WorkerLimitFailurePersistsCauseAndSavedEditCount()
    {
        var responses = Enumerable.Range(0, 20).Select(i => new[] { RuntimeTests.Done(i == 0
            ? RuntimeTests.Call("create", "file_create", Json.Serialize(new { path = "a.txt", content = "saved edit" }))
            : RuntimeTests.Call("read-" + i, "file_read", Json.Serialize(new { path = "a.txt", startLine = i, lineCount = 1 }))) }).ToArray();
        using var f = new Fixture(_ => new ScriptedModel(responses));
        f.Parent.WorkerPolicy.MaxRequests = 20;
        await f.Spawn(["a.txt"]);
        var worker = Assert.Single(f.Store.Workers(f.Parent.Id));
        await f.Invoke("agent_wait", new { agentIds = new[] { worker.Id }, waitMilliseconds = 360000 });
        var saved = Assert.Single(new SessionStore(f.Store.DataDirectory).Workers(f.Parent.Id));
        Assert.Equal("Failed", saved.State);
        Assert.Equal("worker_request_limit", saved.Failure?.Code);
        Assert.Equal(20, saved.RequestsUsed); Assert.Equal(20, saved.RequestLimit);
        Assert.Equal(1, saved.AppliedCheckpointCount);
        Assert.Equal("saved edit", File.ReadAllText(Path.Combine(f.Dir.Path, "a.txt")));
    }

    [Fact]
    public async Task WorkerCanCompleteBeyondTwentyRequestsWithPersistedAssignmentBudget()
    {
        var responses = Enumerable.Range(0, 25).Select(i => new[] { RuntimeTests.Done(
            RuntimeTests.Call("read-" + i, "file_read", Json.Serialize(new { path = "a.txt", startLine = i + 1, lineCount = 1 }))) })
            .Append(new[] { RuntimeTests.Done(Finish()) }).ToArray();
        using var f = new Fixture(_ => new ScriptedModel(responses));
        File.WriteAllLines(Path.Combine(f.Dir.Path, "a.txt"), Enumerable.Range(1, 30).Select(i => "Line " + i));
        await f.Spawn([]);
        var worker = Assert.Single(f.Store.Workers(f.Parent.Id));
        // Later parent settings must not alter the independent child snapshot.
        f.Parent.WorkerPolicy.MaxRequests = 2;
        f.Parent.WorkerPolicy.MaxMinutes = 1;
        await f.Invoke("agent_wait", new { agentIds = new[] { worker.Id }, waitMilliseconds = 360000 });
        var saved = Assert.Single(f.Store.Workers(f.Parent.Id));
        Assert.Equal("Completed", saved.State);
        Assert.Equal(26, saved.RequestsUsed); Assert.Equal(100, saved.RequestLimit);
        var child = f.Store.Get(saved.ChildId)!;
        Assert.Equal(100, child.Execution.MaxRequests); Assert.Equal(30, child.Execution.MaxMinutes);
        child.Runs[^1].BatchRequests = 40;
        Assert.Contains("60 model requests left", f.Agents.SessionInstructions(child));
    }

    [Fact]
    public void OlderWorkerPolicyGetsNewDefaultsWithoutChangingConcurrency()
    {
        var policy = System.Text.Json.JsonSerializer.Deserialize<WorkerPolicy>("{\"maxConcurrent\":8,\"maxLocalConcurrent\":1}", Json.Options)!;
        Assert.Equal(8, policy.MaxConcurrent); Assert.Equal(1, policy.MaxLocalConcurrent);
        Assert.Equal(100, policy.MaxRequests); Assert.Equal(30, policy.MaxMinutes);
        policy.Validate();
    }

    [Fact]
    public async Task WorkerRejectionsIdentifyUnknownInactiveAndUnassignedTargets()
    {
        using var f = new Fixture();
        var missing = Assert.Throws<UserFacingException>(() => f.Agents.Find(f.Parent, "unknown"));
        Assert.Equal("worker_not_found", missing.Detail?.Code); Assert.Equal("unknown", missing.Detail?.Target);
        await f.Spawn(["new-folder"]);
        var worker = Assert.Single(f.Store.Workers(f.Parent.Id));
        var child = f.Store.Get(worker.ChildId)!;
        var outside = await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("file_create", new { path = "new-folder/Host.cs", content = "denied" }, child));
        Assert.Equal("worker_path_not_assigned", outside.Detail?.Code);
        Assert.Contains("Folder names do not authorize descendant files", outside.Detail!.Reason);
        Assert.EndsWith("Host.cs", outside.Detail.Target);
        Assert.False(File.Exists(Path.Combine(f.Dir.Path, "new-folder", "Host.cs")));
        await f.Agents.CancelAsync(worker.Id);
        var inactive = await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("agent_message", new { agentId = worker.Id, text = "Try again" }));
        Assert.Equal("worker_inactive", inactive.Detail?.Code); Assert.Contains("Cancelled", inactive.Detail!.Reason);
    }

    [Fact]
    public async Task MixedProvidersCanRunSixWorkersWithSeparateLocalAdmissionLimit()
    {
        using var f = new Fixture();
        f.Parent.WorkerPolicy = new() { MaxConcurrent = 6, MaxLocalConcurrent = 2 };
        f.Parent.EnabledWorkers.Add(new() { Name = "gpt", Model = "cloud", AccountId = "account-reference", Mode = PermissionMode.Edit, Connection = new() { Provider = ModelProvider.ChatGpt } });
        await f.Spawn(["local-a.txt"]); await f.Spawn(["local-b.txt"]);
        var localFull = await Assert.ThrowsAsync<UserFacingException>(() => f.Spawn(["local-c.txt"]));
        Assert.Contains("Local worker capacity", localFull.Message);
        for (var i = 0; i < 4; i++)
            await f.Invoke("agent_spawn", new { profile = "gpt", task = "Independent analysis", writePaths = new[] { $"cloud-{i}.txt" } }, id: "cloud-" + i);
        var all = f.Store.Workers(f.Parent.Id);
        Assert.Equal(6, all.Count); Assert.All(all, w => Assert.True(w.Active));
        Assert.Equal(6, all.Select(w => w.ChildId).Distinct().Count());
        Assert.All(all.Where(w => w.Profile.Connection.Provider == ModelProvider.ChatGpt), w =>
        {
            var child = f.Store.Get(w.ChildId)!;
            Assert.Equal(ModelProvider.ChatGpt, child.Connection.Provider); Assert.Equal("account-reference", child.AccountId);
            Assert.DoesNotContain(f.Agents.Available(child), t => t.Name is "process_start" or "agent_spawn");
        });
        await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("agent_spawn", new { profile = "gpt", task = "Too many", writePaths = Array.Empty<string>() }));
        // Reconciliation still succeeds when capacity is full.
        await f.Invoke("agent_spawn", new { profile = "gpt", task = "Independent analysis", writePaths = new[] { "cloud-0.txt" } }, id: "cloud-0");
        Assert.Equal(6, f.Store.Workers(f.Parent.Id).Count);
        await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("file_create", new { path = "cloud-0.txt", content = "conflict" }));
        await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("process_start", new { }));
        await f.Invoke("agent_wait", new { agentIds = all.Select(w => w.Id).ToArray(), waitMilliseconds = 0 });
        await f.Agents.StopAllAsync(f.Parent.Id);
        Assert.All(f.Store.Workers(f.Parent.Id), w => { Assert.Equal("Cancelled", w.State); Assert.False(w.ReservationsHeld); });
    }
    [Fact]
    public async Task HigherLimitsStillRejectRacingSpawnsAndPreservePlanRestrictions()
    {
        using var f = new Fixture(); f.Parent.WorkerPolicy = new() { MaxConcurrent = 4, MaxLocalConcurrent = 4 };
        f.Parent.Workflow = WorkflowMode.Plan;
        f.Parent.EnabledWorkers[0].Connection.Provider = ModelProvider.ChatGpt;
        await Assert.ThrowsAsync<UserFacingException>(() => f.Spawn(["edit.txt"]));
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            try { await f.Spawn([]); return true; }
            catch (UserFacingException) { return false; }
        }));
        Assert.Equal(4, attempts.Count(success => success)); Assert.Equal(4, f.Store.Workers(f.Parent.Id).Count);
    }
    [Fact]
    public void WorkerPolicySurvivesStorageAndLegacySessionsDefaultToOneLocalSlot()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<Session>("{}", Json.Options)!;
        Assert.Equal(2, legacy.WorkerPolicy.MaxConcurrent); Assert.Equal(1, legacy.WorkerPolicy.MaxLocalConcurrent);
        using var f = new Fixture(); f.Parent.WorkerPolicy = new() { MaxConcurrent = 12, MaxLocalConcurrent = 1 };
        f.Store.Save(f.Parent, new("policy", f.Parent.Id, null, new { }));
        var saved = new SessionStore(f.Store.DataDirectory).Get(f.Parent.Id)!;
        Assert.Equal(12, saved.WorkerPolicy.MaxConcurrent); Assert.Equal(1, saved.WorkerPolicy.MaxLocalConcurrent);
        var instructions = f.Agents.SessionInstructions(saved);
        Assert.Contains("enabled ChatGPT profiles", instructions); Assert.Contains("maxConcurrent\":12", instructions);
    }
    [Theory]
    [InlineData(0, 2)]
    [InlineData(17, 2)]
    [InlineData(6, 0)]
    [InlineData(6, 17)]
    public void InvalidWorkerLimitsAreRejected(int total, int local)
        => Assert.Throws<UserFacingException>(() => new WorkerPolicy { MaxConcurrent = total, MaxLocalConcurrent = local }.Validate());
    [Fact]
    public async Task TwoWorkersHaveIndependentSessionsAndThirdIsRejected()
    {
        using var f = new Fixture(); await f.Spawn(["a.txt"]); await f.Spawn(["b.txt"]);
        await Assert.ThrowsAsync<UserFacingException>(() => f.Spawn(["c.txt"]));
        var workers = f.Store.Workers(f.Parent.Id); Assert.Equal(2, workers.Count); Assert.Equal(2, workers.Select(w => w.ChildId).Distinct().Count());
        Assert.All(workers, w => Assert.Equal(f.Parent.Id, f.Store.Get(w.ChildId)!.ParentConversationId)); Assert.Empty(f.Parent.History);
    }
    [Fact]
    public async Task DuplicateSpawnReturnsExistingChildAndCancellationReleasesReservation()
    {
        using var f = new Fixture(); await f.Spawn(["a.txt"], "same"); await f.Spawn(["a.txt"], "same");
        var worker = Assert.Single(f.Store.Workers(f.Parent.Id)); await f.Agents.CancelAsync(worker.Id);
        var saved = Assert.Single(f.Store.Workers(f.Parent.Id)); Assert.Equal("Cancelled", saved.State); Assert.False(saved.ReservationsHeld);
    }
    [Fact]
    public async Task ParentAndSiblingWritesCaseAliasesMovesAndProcessesRespectReservations()
    {
        using var f = new Fixture(); await f.Spawn(["a.txt"]);
        await Assert.ThrowsAsync<UserFacingException>(() => f.Spawn(["A.TXT"]));
        await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("file_create", new { path = "A.TXT", content = "bad" }));
        await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("file_move", new { source = "b.txt", destination = "a.txt", expectedHash = "x" }));
        await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("process_start", new { }));
        await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("dotnet_build", new { }));
        await f.Invoke("file_create", new { path = "other.txt", content = "allowed" }); Assert.True(File.Exists(Path.Combine(f.Dir.Path, "other.txt")));
    }
    [Fact]
    public async Task WorkerScopeAndProfilePermissionCannotBeExpanded()
    {
        using var f = new Fixture(); await f.Spawn(["a.txt"]); var child = f.Store.Get(f.Store.Workers(f.Parent.Id)[0].ChildId)!;
        await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("file_create", new { path = "outside.txt", content = "bad" }, child));
        Assert.DoesNotContain(f.Agents.Available(child), t => t.Name is "process_start" or "plan_update" or "user_ask" or "agent_spawn");
        await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("agent_spawn", new { }, child));
        await Assert.ThrowsAsync<UserFacingException>(() => f.Spawn(["../escape.txt"]));
        await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("agent_spawn", new { profile = "unknown", task = "x", writePaths = Array.Empty<string>() }));
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task PlanAndInspectCannotDelegateEdits(bool plan)
    {
        using var f = new Fixture(); if (plan) f.Parent.Workflow = WorkflowMode.Plan; else f.Parent.Mode = PermissionMode.Inspect;
        await Assert.ThrowsAsync<UserFacingException>(() => f.Spawn(["a.txt"])); await f.Spawn([]);
        var child = f.Store.Get(f.Store.Workers(f.Parent.Id)[0].ChildId)!; Assert.Equal(PermissionMode.Inspect, child.Mode);
    }
    [Fact]
    public async Task RunningOrUnconfirmedProcessPreventsEditingWorker()
    {
        using var f = new Fixture(); f.Parent.Processes.Add(new() { State = "Running" });
        await Assert.ThrowsAsync<UserFacingException>(() => f.Spawn(["a.txt"])); await f.Spawn([]);
    }
    [Fact]
    public async Task FinishPersistsReportSkipsTailAndCompletionCollectsIt()
    {
        using var f = new Fixture(_ => new ScriptedModel([RuntimeTests.Done(Finish("blocked"), RuntimeTests.Call("tail", "file_create", Json.Serialize(new { path = "a.txt", content = "never" })))]));
        await f.Spawn(["a.txt"]); Assert.False(await f.Agents.CanCompleteAsync(f.Context()));
        var worker = Assert.Single(f.Store.Workers(f.Parent.Id)); Assert.Equal("Blocked", worker.State); Assert.False(File.Exists(Path.Combine(f.Dir.Path, "a.txt")));
        Assert.True(worker.Collected); Assert.Single(f.Run.WorkerResults);
        var result = await f.Invoke("agent_result", new { agentId = worker.Id }); Assert.Contains("Which target?", result.Content);
        Assert.True(await f.Agents.CanCompleteAsync(f.Context())); Assert.Equal(2, f.Parent.Artifacts.Count);
        Assert.Null(f.Agents.GetCompletionRequirement(f.Context()));
        Assert.Throws<UserFacingException>(() => f.Agents.Find(new Session(), worker.Id));
    }
    [Fact]
    public async Task ParentFinishesAfterAutomaticCollectionEvenIfItNeverCallsAgentResult()
    {
        using var f = new Fixture(_ => new ScriptedModel([RuntimeTests.Done(Finish())]));
        f.Parent.History.Add(new() { ["role"] = "user", ["content"] = "Inspect the worker result" });
        await f.Spawn([]);
        var worker = Assert.Single(f.Store.Workers(f.Parent.Id));
        var model = new ScriptedModel([RuntimeTests.Done(RuntimeTests.Message("Done too soon"))],
            [RuntimeTests.Done(RuntimeTests.Message("Collected and inspected the worker report."))]);
        var events = new List<AgentEvent>();
        var run = await new AgentRunner(f.Store, model, f.Agents).RunAsync(f.Parent, null, null, events.Add, default);
        Assert.Equal(RunState.Completed, run.State);
        Assert.Contains("Runtime-collected worker results", model.Requests[1].Instructions); Assert.Contains(worker.Id, model.Requests[1].Instructions);
        Assert.DoesNotContain("Authoritative completion feedback", model.Requests[1].Instructions);
        Assert.Single(events, e => e.Type == "agent.collected"); Assert.Empty(run.Calls); Assert.Equal(2, model.Requests.Count);
        Assert.True(f.Store.Workers(f.Parent.Id).Single().Collected); Assert.Null(f.Store.Get(f.Parent.Id)!.Runs.Last().CompletionRequirement);
    }

    [Fact]
    public async Task CancelledWorkerWithNoReportIsCollectedWithoutRelaunchOrDuplicateArtifacts()
    {
        using var f = new Fixture(); await f.Spawn([]);
        var worker = f.Store.Workers(f.Parent.Id).Single(); await f.Agents.CancelAsync(worker.Id);
        Assert.False(await f.Agents.CanCompleteAsync(f.Context()));
        Assert.True(await f.Agents.CanCompleteAsync(f.Context()));
        var saved = f.Store.Get(f.Parent.Id)!; var receipt = Assert.Single(saved.Runs.Last().WorkerResults);
        Assert.Contains("Cancelled", receipt.Value); Assert.Equal(2, saved.Artifacts.Count);
        var repeated = await f.Invoke("agent_result", new { agentId = worker.Id }); Assert.Equal(receipt.Value, repeated.Content);
        Assert.Equal(2, f.Parent.Artifacts.Count); Assert.Single(f.Store.Workers(f.Parent.Id));
    }

    [Fact]
    public async Task CollectionSaveFailureLeavesWorkerUncollectedAndCanRetry()
    {
        using var f = new Fixture(_ => new ScriptedModel([RuntimeTests.Done(Finish())])); await f.Spawn([]);
        var ctx = f.Context() with { Store = new FaultStore(f.Store, "agent.result.saved") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Agents.CanCompleteAsync(ctx));
        Assert.False(f.Store.Workers(f.Parent.Id).Single().Collected); Assert.Empty(f.Run.WorkerResults);
        Assert.False(await f.Agents.CanCompleteAsync(f.Context()));
        Assert.True(f.Store.Workers(f.Parent.Id).Single().Collected); Assert.Single(f.Store.Get(f.Parent.Id)!.Runs.Last().WorkerResults);
        Assert.Equal(2, f.Parent.Artifacts.Count);
    }

    [Fact]
    public async Task RestartBetweenParentReceiptAndWorkerMarkerReusesSavedResult()
    {
        using var f = new Fixture(); await f.Spawn([]);
        var worker = f.Store.Workers(f.Parent.Id).Single(); await f.Agents.CancelAsync(worker.Id);
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(f.Store.DataDirectory, "sessions.db")))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_collection BEFORE UPDATE ON workers WHEN json_extract(NEW.payload,'$.collected')=1 BEGIN SELECT RAISE(ABORT,'injected'); END";
            command.ExecuteNonQuery();
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => f.Agents.CanCompleteAsync(f.Context()));
            Assert.False(f.Store.Workers(f.Parent.Id).Single().Collected);
            Assert.Single(f.Store.Get(f.Parent.Id)!.Runs.Last().WorkerResults);
            command.CommandText = "DROP TRIGGER fail_collection"; command.ExecuteNonQuery();
        }
        var saved = f.Store.Get(f.Parent.Id)!;
        var context = f.Context() with { Session = saved, Run = saved.Runs.Last() };
        Assert.False(await f.Agents.CanCompleteAsync(context)); Assert.True(await f.Agents.CanCompleteAsync(context));
        Assert.True(f.Store.Workers(f.Parent.Id).Single().Collected); Assert.Equal(2, saved.Artifacts.Count);
        Assert.Single(f.Store.Get(f.Parent.Id)!.Runs.Last().WorkerResults);
    }

    [Fact]
    public async Task WorkerProseReceivesFinishReportFeedbackInsteadOfFailing()
    {
        var model = new ScriptedModel([RuntimeTests.Done(RuntimeTests.Message("My investigation is complete."))], [RuntimeTests.Done(Finish())]);
        using var f = new Fixture(_ => model); await f.Spawn([]);
        Assert.False(await f.Agents.CanCompleteAsync(f.Context()));
        Assert.Equal("Completed", f.Store.Workers(f.Parent.Id).Single().State);
        Assert.Contains("worker_report_pending", model.Requests[1].Instructions);
        Assert.Contains("agent_finish", model.Requests[1].Instructions);
    }

    [Fact]
    public async Task ChildEditCheckpointsAreUndoableFromParentAndConflictSafe()
    {
        using var f = new Fixture(_ => new ScriptedModel([RuntimeTests.Done(RuntimeTests.Call("write", "file_create", Json.Serialize(new { path = "new.txt", content = "worker edit" })), Finish())]));
        await f.Spawn(["new.txt"]); await f.Agents.CanCompleteAsync(f.Context());
        var child = f.Store.Get(f.Store.Workers(f.Parent.Id)[0].ChildId)!; var mutation = Assert.Single(child.Mutations);
        File.WriteAllText(Path.Combine(f.Dir.Path, "new.txt"), "external edit");
        await Assert.ThrowsAsync<UserFacingException>(() => f.Agents.UndoAsync(f.Parent, mutation.Id));
        File.WriteAllText(Path.Combine(f.Dir.Path, "new.txt"), "worker edit"); await f.Agents.UndoAsync(f.Parent, mutation.Id); Assert.False(File.Exists(Path.Combine(f.Dir.Path, "new.txt")));
    }
    [Fact]
    public void RestartDoesNotLaunchPreparedWorkerAndKeepsAmbiguousReservations()
    {
        using var f = new Fixture(_ => throw new InvalidOperationException("Must not launch"));
        var child = new Session { Workspace = f.Dir.Path, ParentConversationId = f.Parent.Id, Mutations = [new() { Kind = "file_create", State = "Prepared", Files = [new() { Path = "a.txt", Before = null, After = "planned"u8.ToArray() }] }] };
        File.WriteAllText(Path.Combine(f.Dir.Path, "a.txt"), "unrelated");
        var record = new WorkerRecord { ParentId = f.Parent.Id, ParentRunId = f.Run.Id, CallId = "before-crash", ChildId = child.Id, ReservationsHeld = true, WritePaths = ["a.txt"] };
        f.Store.CreateWorker(record, child); f.Agents.RecoverChildren(f.Parent);
        Assert.Equal("RecoveryRequired", f.Store.Workers(f.Parent.Id)[0].State); Assert.True(f.Store.Workers(f.Parent.Id)[0].ReservationsHeld);
        f.Agents.RecoverChildren(f.Parent, true); Assert.False(f.Store.Workers(f.Parent.Id)[0].ReservationsHeld); Assert.Equal("unrelated", File.ReadAllText(Path.Combine(f.Dir.Path, "a.txt")));
    }
    [Fact]
    public void ProfilesPersistSeparatelyAndRemovingDoesNotChangeEnabledSnapshot()
    {
        using var f = new Fixture(); var profile = f.Parent.EnabledWorkers[0]; f.Store.PutProfile(profile);
        f.Store.RemoveProfile(profile.Name); Assert.Empty(new SessionStore(f.Store.DataDirectory).Profiles()); Assert.Single(f.Store.Get(f.Parent.Id)!.EnabledWorkers);
        Assert.Throws<UserFacingException>(() => f.Store.PutProfile(new() { Name = "bad", Model = "model", Mode = PermissionMode.Execute }));
    }
    [Fact]
    public async Task ParentCompletionWaitsForWorkersAndCancellationSettlesTree()
    {
        using var f = new Fixture(); await f.Spawn([]); await f.Spawn([]);
        using var timeout = new CancellationTokenSource();
        var waiting = f.Agents.CanCompleteAsync(f.Context() with { Cancellation = timeout.Token });
        Assert.False(waiting.IsCompleted); timeout.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await f.Agents.RunEndedAsync(f.Context()); Assert.All(f.Store.Workers(f.Parent.Id), w => Assert.Equal("Cancelled", w.State));
    }
    [Fact]
    public async Task FileDirectoryAncestorAssignmentsAndShortAliasesAreRejected()
    {
        using var f = new Fixture(); await f.Spawn(["new/child.txt"]);
        await Assert.ThrowsAsync<UserFacingException>(() => f.Spawn(["new"]));
        await Assert.ThrowsAsync<UserFacingException>(() => f.Spawn(["LONGFI~1.TXT"]));
        await Assert.ThrowsAsync<UserFacingException>(() => f.Spawn(["other", "other/child.txt"]));
    }
    [Fact]
    public async Task SixteenLaunchLimitAndMissingReportCannotBecomeSuccessful()
    {
        using var f = new Fixture(_ => new ScriptedModel([RuntimeTests.Done(RuntimeTests.Message("No structured report"))]));
        for (var i = 0; i < 16; i++)
        {
            await f.Spawn([]);
            var worker = f.Store.Workers(f.Parent.Id).Last();
            while (f.Store.Workers(f.Parent.Id).Single(w => w.Id == worker.Id).Active) await Task.Delay(5);
            Assert.Equal("Failed", f.Store.Workers(f.Parent.Id).Single(w => w.Id == worker.Id).State);
        }
        await Assert.ThrowsAsync<UserFacingException>(() => f.Spawn([]));
    }
    [Fact]
    public async Task SharedArtifactsRequireExplicitRegisteredIdsAndPermitBoundedLiveReads()
    {
        using var f = new Fixture();
        await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("agent_spawn", new { profile = "editor", task = "Read [artifact:foreign]", writePaths = Array.Empty<string>() }));
        var ctx = f.Context(); Directory.CreateDirectory(ctx.ArtifactDirectory);
        var file = Path.Combine(ctx.ArtifactDirectory, "evidence.txt"); File.WriteAllText(file, "Registered evidence");
        var artifact = ArtifactAccess.Register(ctx, file, "verification");
        await f.Invoke("agent_spawn", new { profile = "editor", task = "Read [artifact:" + artifact.Id + "]", writePaths = Array.Empty<string>() });
        var child = f.Store.Get(f.Store.Workers(f.Parent.Id)[0].ChildId)!;
        Assert.Empty(child.Artifacts); Assert.Equal(artifact.Id, Assert.Single(child.ArtifactGrants).ArtifactId);
        var read = ArtifactAccess.Execute(f.Context(child), "artifact_read", new JsonObject { ["artifactId"] = artifact.Id, ["cursor"] = 0, ["maxCharacters"] = 4000 }); Assert.Contains("Registered evidence", read.Content);
        File.AppendAllText(file, " and growing");
        var growth = ArtifactAccess.Execute(f.Context(child), "artifact_search", new JsonObject { ["artifactId"] = artifact.Id, ["query"] = "growing" }); Assert.Contains("growing", growth.Content);
        Assert.Throws<UserFacingException>(() => ArtifactAccess.Execute(f.Context(child), "artifact_read", new JsonObject { ["artifactId"] = "foreign", ["cursor"] = 0, ["maxCharacters"] = 4000 }));
        var worker = f.Store.Workers(f.Parent.Id).Single(); await f.Agents.CancelAsync(worker.Id);
        var collected = JsonNode.Parse((await f.Invoke("agent_result", new { agentId = worker.Id })).Content)!;
        Assert.Empty(collected["imported"]!.AsArray());

    }
    private sealed class FaultStore(SessionStore inner, string target) : ISessionStore
    {
        private bool failed;
        public string DataDirectory => inner.DataDirectory;
        public IDisposable Acquire(string id) => inner.Acquire(id);
        public Session? Get(string id) => inner.Get(id);
        public IReadOnlyList<Session> List() => inner.List();
        public void SaveProcess(string id, ProcessRecord p) => inner.SaveProcess(id, p);
        public void Save(Session session, AgentEvent ev)
        { if (!failed && ev.Type == target) { failed = true; throw new InvalidOperationException("Injected worker persistence failure"); } inner.Save(session, ev); }
    }
    [Theory]
    [InlineData("tool.started", false)]
    [InlineData("mutation.applied", true)]
    [InlineData("tool.completed", true)]
    public async Task WorkerFailuresAroundEffectsReconcileWithoutRepeatingSpawn(string fault, bool changed)
    {
        using var f = new Fixture(_ => new ScriptedModel([RuntimeTests.Done(RuntimeTests.Call("edit", "file_create", Json.Serialize(new { path = "effect.txt", content = "one effect" })), Finish())]), s => new FaultStore(s, fault));
        await f.Spawn(["effect.txt"], "durable-spawn");
        if (fault == "tool.completed") await f.Agents.CanCompleteAsync(f.Context());
        else
        {
            await Assert.ThrowsAsync<UserFacingException>(() => f.Agents.CanCompleteAsync(f.Context()));
            Assert.False(f.Store.Workers(f.Parent.Id).Single().Collected); Assert.Empty(f.Run.WorkerResults);
        }
        Assert.Equal(changed, File.Exists(Path.Combine(f.Dir.Path, "effect.txt")));
        f.Agents.RecoverChildren(f.Parent); await f.Spawn(["effect.txt"], "durable-spawn");
        Assert.Single(f.Store.Workers(f.Parent.Id)); Assert.Equal(changed, File.Exists(Path.Combine(f.Dir.Path, "effect.txt")));
        if (changed) Assert.Equal("one effect", File.ReadAllText(Path.Combine(f.Dir.Path, "effect.txt")));
        Assert.False(f.Store.Workers(f.Parent.Id).Single().ReservationsHeld);
    }
}
