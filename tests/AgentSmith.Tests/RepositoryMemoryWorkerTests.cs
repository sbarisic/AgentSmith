using System.Runtime.CompilerServices;
using AgentSmith.Core;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

public sealed class RepositoryMemoryWorkerTests
{
    private static ToolCall Call(string name, object args) => new() { Id = Guid.NewGuid().ToString("N"), Name = name, Arguments = Json.Serialize(args) };

    [Fact]
    public void MemorizeParentRejectsEditingAssignmentsButAllowsReadAssignments()
    {
        using var workspace = new TestDirectory(); using var data = new TestDirectory();
        var store = new SessionStore(data.Path); using var tools = new ToolRegistry(store);
        using var coordinator = new WorkerCoordinator(store, store, tools, _ => new ScriptedModel());
        var session = new Session { Workspace = workspace.Path, Mode = PermissionMode.Execute, Workflow = WorkflowMode.Memorize,
            EnabledWorkers = [new() { Name = "editor", Model = "test", Mode = PermissionMode.Edit }] };
        var run = new Run(); session.Runs.Add(run);
        var ctx = new ToolExecutionContext(session, run, store, default);
        var edit = Call("agent_spawn", new { profile = "editor", task = "Inspect sources", writePaths = new[] { "README.md" } });
        Assert.Throws<UserFacingException>(() => coordinator.Prepare(ctx, edit));
        var read = Call("agent_spawn", new { profile = "editor", task = "Inspect sources", writePaths = Array.Empty<string>() });
        Assert.NotNull(coordinator.Prepare(ctx, read));
        Assert.Empty(store.Workers(session.Id));
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, ".agentsmith")));
    }

    [Theory]
    [InlineData(WorkflowMode.Work)]
    [InlineData(WorkflowMode.Memorize)]
    public async Task WorkersCanReadRepositoryEvidenceButCannotScanOrCommit(WorkflowMode workflow)
    {
        using var workspace = new TestDirectory(); using var data = new TestDirectory();
        File.WriteAllText(Path.Combine(workspace.Path, "README.md"), "Widgets use a queue.\n");
        var store = new SessionStore(data.Path);
        var scan = store.ScanRepository(workspace.Path); var source = Assert.Single(scan.Sources);
        store.CommitRepository(workspace.Path, scan.Id, [new("widgets", 0, "Widget architecture", "Widgets use a queue.", ["widget"],
            [new(source.Path, source.Hash, source.StartLine, source.EndLine)])], "seed");
        var entry = Assert.Single(store.SearchRepositoryMemories(workspace.Path, "widget"));
        using var tools = new ToolRegistry(store);
        using var coordinator = new WorkerCoordinator(store, store, tools, _ => new ScriptedModel());
        var session = new Session { Workspace = workspace.Path, Mode = PermissionMode.Inspect, Workflow = workflow, ParentConversationId = "parent" };
        var run = new Run(); session.Runs.Add(run);
        store.Save(session, new("created", session.Id, run.Id, new { }));
        var ctx = new ToolExecutionContext(session, run, store, default);
        foreach (var call in new[] {
            Call("repository_memory_search", new { repositoryId = "", query = "widget" }),
            Call("repository_memory_read", new { repositoryId = "", id = entry.Id }),
            Call("repository_memory_discover", new { }), Call("repository_memory_status", new { }) })
        {
            Assert.Contains(coordinator.Available(session), d => d.Name == call.Name);
            run.Calls.Add(call); call.Prepared = coordinator.Prepare(ctx, call);
            var output = await coordinator.ExecuteAsync(ctx, call);
            Assert.False(string.IsNullOrWhiteSpace(output.Content));
        }
        foreach (var call in new[] { Call("repository_memory_scan", new { path = "", full = false }),
            Call("repository_memory_commit", new { scanId = scan.Id, proposals = Array.Empty<object>() }) })
        {
            Assert.DoesNotContain(coordinator.Available(session), d => d.Name == call.Name);
            Assert.Throws<UserFacingException>(() => coordinator.Prepare(ctx, call));
            await Assert.ThrowsAsync<UserFacingException>(() => coordinator.ExecuteAsync(ctx, call));
        }
        var pendingRead = Call("repository_memory_read", new { repositoryId = "", id = entry.Id });
        pendingRead.Prepared = coordinator.Prepare(ctx, pendingRead);
        store.SetMemoryEnabled(false);
        Assert.DoesNotContain(coordinator.Available(session), d => d.Name.StartsWith("repository_memory_"));
        Assert.Throws<UserFacingException>(() => coordinator.Prepare(ctx, pendingRead));
        await Assert.ThrowsAsync<UserFacingException>(() => coordinator.ExecuteAsync(ctx, pendingRead));
        store.SetMemoryEnabled(true);
        Assert.Equal(entry.Revision, store.ReadRepositoryMemory(workspace.Path, entry.Id)!.Revision);
        Assert.Equal("Widgets use a queue.\n", File.ReadAllText(Path.Combine(workspace.Path, "README.md")));
    }

    private static async Task<ToolOutput> Execute(WorkerCoordinator coordinator, ToolExecutionContext ctx, ToolCall call)
    {
        using var lease = await coordinator.AcquireToolLeaseAsync(ctx, call);
        ctx.Run.Calls.Add(call); call.Prepared = coordinator.Prepare(ctx, call);
        var output = await coordinator.ExecuteAsync(ctx, call); call.State = CallState.Completed; call.Result = output.Content;
        return output;
    }

    private sealed class WaitingModel : IModelClient
    {
        public TaskCompletionSource<ModelRequest> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Entered.TrySetResult(request);
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
    }

    [Theory]
    [InlineData(PermissionMode.Inspect, PermissionMode.Inspect)]
    [InlineData(PermissionMode.Inspect, PermissionMode.Execute)]
    [InlineData(PermissionMode.Edit, PermissionMode.Edit)]
    [InlineData(PermissionMode.Execute, PermissionMode.Execute)]
    public async Task SpawnedMemorizeWorkerInheritsWorkflowAndCannotExceedReadOnlyCeiling(PermissionMode parentMode, PermissionMode profileMode)
    {
        using var workspace = new TestDirectory(); using var data = new TestDirectory();
        var sourcePath = Path.Combine(workspace.Path, "README.md");
        File.WriteAllText(sourcePath, "Widgets use a queue.\n");
        var original = File.ReadAllBytes(sourcePath);
        var store = new SessionStore(data.Path); using var tools = new ToolRegistry(store);
        var model = new WaitingModel();
        using var coordinator = new WorkerCoordinator(store, store, tools, _ => model);
        var parent = new Session { Workspace = workspace.Path, Mode = parentMode, Workflow = WorkflowMode.Memorize, WebSearch = "off",
            WorkerPolicy = new() { MaxRequests = 4, MaxMinutes = 1 },
            EnabledWorkers = [new() { Name = "reader", Model = "test", Mode = profileMode }] };
        var run = new Run { State = RunState.Running }; parent.Runs.Add(run);
        store.Save(parent, new("created", parent.Id, run.Id, new { }));
        var ctx = new ToolExecutionContext(parent, run, store, default);
        try
        {
            // Exercise Execute's validation too, not only Prepare's rejection of editing assignments.
            var editing = Call("agent_spawn", new { profile = "reader", task = "Inspect sources", writePaths = new[] { "README.md" } });
            editing.Prepared = new(editing.Arguments);
            await Assert.ThrowsAsync<UserFacingException>(() => coordinator.ExecuteAsync(ctx, editing));
            Assert.Empty(store.Workers(parent.Id));
            await Execute(coordinator, ctx, Call("agent_spawn", new { profile = "reader", task = "Inspect sources", writePaths = Array.Empty<string>() }));
            await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var worker = Assert.Single(store.Workers(parent.Id));
            var child = store.Get(worker.ChildId)!;
            Assert.Equal(parent.Id, child.ParentConversationId);
            Assert.Equal(worker.Id, child.WorkerId);
            Assert.Equal(parent.Workspace, child.Workspace);
            Assert.Equal(WorkflowMode.Memorize, child.Workflow);
            Assert.Equal(PermissionMode.Inspect, child.Mode);
            Assert.Equal("off", child.WebSearch);
            Assert.Equal(parent.WorkerPolicy.MaxRequests, child.Execution.MaxRequests);
            Assert.Equal(parent.WorkerPolicy.MaxMinutes, child.Execution.MaxMinutes);
            Assert.Empty(child.EnabledWorkers);
            Assert.Empty(worker.WritePaths);
            Assert.False(worker.ReservationsHeld);
            var available = coordinator.Available(child);
            Assert.Contains(available, d => d.Name == "file_read");
            Assert.Contains(available, d => d.Name == "repository_memory_search");
            foreach (var name in new[] { "file_create", "file_apply_patch", "file_move", "file_delete", "process_start", "csharp_load",
                "dotnet_build", "dotnet_test", "agent_spawn", "plan_submit", "user_ask", "repository_memory_scan", "repository_memory_commit", "web_search" })
                Assert.DoesNotContain(available, d => d.Name == name);
            var childCtx = new ToolExecutionContext(child, child.Runs.Last(), store, default);
            foreach (var call in new[] { Call("file_create", new { path = "forbidden.txt", content = "must not be written" }),
                Call("repository_memory_scan", new { path = "", full = false }),
                Call("repository_memory_commit", new { scanId = "not-scanned", proposals = Array.Empty<object>() }) })
            {
                Assert.Throws<UserFacingException>(() => coordinator.Prepare(childCtx, call));
                call.Prepared = new(call.Arguments);
                await Assert.ThrowsAsync<UserFacingException>(() => coordinator.ExecuteAsync(childCtx, call));
            }
            Assert.Empty(child.Processes);
            Assert.Empty(child.Mutations);
            Assert.False(File.Exists(Path.Combine(workspace.Path, "forbidden.txt")));
            Assert.False(Directory.Exists(Path.Combine(workspace.Path, ".agentsmith")));
            Assert.Equal(original, File.ReadAllBytes(sourcePath));
        }
        finally { await coordinator.StopAllAsync(parent.Id).WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task SpawnedMemorizeWorkerReportsEvidenceButOnlyCollectedParentReconciles()
    {
        using var workspace = new TestDirectory(); using var data = new TestDirectory();
        var sourcePath = Path.Combine(workspace.Path, "README.md");
        File.WriteAllText(sourcePath, "Widgets use a queue.\n");
        var original = File.ReadAllBytes(sourcePath);
        var store = new SessionStore(data.Path); using var tools = new ToolRegistry(store);
        var model = new ScriptedModel(
            [RuntimeTests.Done(RuntimeTests.Call("read-source", "file_read", Json.Serialize(new { path = "README.md", startLine = 1, lineCount = 10 })))],
            [RuntimeTests.Done(RuntimeTests.Call("report", "agent_finish", Json.Serialize(new { status = "completed", summary = "Widgets use a queue.",
                limitations = "Only README.md inspected; no tests run.", evidence = new[] { "README.md" }, clarification = "" })))]);
        using var coordinator = new WorkerCoordinator(store, store, tools, _ => model);
        var parent = new Session { Workspace = workspace.Path, Mode = PermissionMode.Inspect, Workflow = WorkflowMode.Memorize,
            WorkerPolicy = new() { MaxRequests = 4, MaxMinutes = 1 },
            EnabledWorkers = [new() { Name = "reader", Model = "test", Mode = PermissionMode.Execute }] };
        var run = new Run { State = RunState.Running }; parent.Runs.Add(run);
        store.Save(parent, new("created", parent.Id, run.Id, new { }));
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ctx = new ToolExecutionContext(parent, run, store, default, e => { if (e.Type == "agent.finished") finished.TrySetResult(); });
        try
        {
            await Execute(coordinator, ctx, Call("agent_spawn", new { profile = "reader", task = "Read README.md and report architecture evidence without writing memory.", writePaths = Array.Empty<string>() }));
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var worker = Assert.Single(store.Workers(parent.Id));
            Assert.Equal("Completed", worker.State);
            Assert.False(worker.Collected);
            var child = store.Get(worker.ChildId)!;
            var childRun = Assert.Single(child.Runs);
            var read = Assert.Single(childRun.Calls, c => c.Name == "file_read");
            Assert.Equal(CallState.Completed, read.State);
            Assert.Contains("Widgets use a queue.", read.Result!);
            Assert.Equal(2, model.Requests.Count);
            Assert.Empty(store.SearchRepositoryMemories(workspace.Path, "widget"));
            Assert.False(Directory.Exists(Path.Combine(workspace.Path, ".agentsmith")));
            var scanCall = Call("repository_memory_scan", new { path = "", full = false });
            Assert.Throws<UserFacingException>(() => coordinator.Prepare(ctx, scanCall));
            scanCall.Prepared = new(scanCall.Arguments);
            await Assert.ThrowsAsync<UserFacingException>(() => coordinator.ExecuteAsync(ctx, scanCall));
            var result = await Execute(coordinator, ctx, Call("agent_result", new { agentId = worker.Id }));
            Assert.Contains("Widgets use a queue.", result.Content);
            Assert.True(Assert.Single(store.Workers(parent.Id)).Collected);
            var childCtx = new ToolExecutionContext(child, childRun, store, default);
            // Even a valid operation prepared by the parent cannot be replayed by its spawned child.
            scanCall.Prepared = coordinator.Prepare(ctx, scanCall);
            Assert.Throws<UserFacingException>(() => coordinator.Prepare(childCtx, scanCall));
            await Assert.ThrowsAsync<UserFacingException>(() => coordinator.ExecuteAsync(childCtx, scanCall));
            await Assert.ThrowsAsync<UserFacingException>(() => tools.ExecuteAsync(childCtx, scanCall));
            Assert.False(Directory.Exists(Path.Combine(workspace.Path, ".agentsmith")));
            var scanOutput = await coordinator.ExecuteAsync(ctx, scanCall);
            var scan = System.Text.Json.JsonSerializer.Deserialize<RepositoryScan>(scanOutput.Content, Json.Options)!;
            var source = Assert.Single(scan.Sources);
            // A deterministic, source-supported parent proposal; this does not exercise model synthesis.
            var commitCall = Call("repository_memory_commit", new { scanId = scan.Id, proposals = new[] { new {
                topicKey = "widgets", expectedRevision = 0, title = "Widget architecture", content = "Widgets use a queue.", tags = new[] { "widget" },
                sources = new[] { new { path = source.Path, hash = source.Hash, startLine = source.StartLine, endLine = source.EndLine } } } } });
            commitCall.Prepared = coordinator.Prepare(ctx, commitCall);
            Assert.Throws<UserFacingException>(() => coordinator.Prepare(childCtx, commitCall));
            await Assert.ThrowsAsync<UserFacingException>(() => coordinator.ExecuteAsync(childCtx, commitCall));
            await Assert.ThrowsAsync<UserFacingException>(() => tools.ExecuteAsync(childCtx, commitCall));
            Assert.Empty(store.SearchRepositoryMemories(workspace.Path, "widget"));
            var commitOutput = await coordinator.ExecuteAsync(ctx, commitCall);
            var commit = System.Text.Json.JsonSerializer.Deserialize<RepositoryCommitReport>(commitOutput.Content, Json.Options)!;
            Assert.Equal(1, commit.Created);
            Assert.Equal("Widgets use a queue.", Assert.Single(store.SearchRepositoryMemories(workspace.Path, "widget")).Content);
            Assert.Empty(child.Mutations);
            Assert.Empty(child.Processes);
            Assert.Equal(original, File.ReadAllBytes(sourcePath));
        }
        finally { await coordinator.StopAllAsync(parent.Id).WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollectionPersistenceFailureBlocksPreparedMemoryUpdatesUntilReloadRepair(bool failCollectedMarker)
    {
        using var workspace = new TestDirectory(); using var data = new TestDirectory();
        var sourcePath = Path.Combine(workspace.Path, "README.md");
        File.WriteAllText(sourcePath, "Widgets use a queue.\n");
        var original = File.ReadAllBytes(sourcePath);
        var memoryDirectory = Path.Combine(workspace.Path, ".agentsmith", "memory");
        var catalogDirectory = Path.Combine(data.Path, "repositories");
        var store = new SessionStore(data.Path); using var tools = new ToolRegistry(store);
        var model = new ScriptedModel(
            [RuntimeTests.Done(RuntimeTests.Call("read-source", "file_read", Json.Serialize(new { path = "README.md", startLine = 1, lineCount = 10 })))],
            [RuntimeTests.Done(RuntimeTests.Call("report", "agent_finish", Json.Serialize(new { status = "completed", summary = "Widgets use a queue.",
                limitations = "Only README.md inspected; no tests run.", evidence = new[] { "README.md" }, clarification = "" })))]);
        using var coordinator = new WorkerCoordinator(store, store, tools, _ => model);
        var parent = new Session { Workspace = workspace.Path, Mode = PermissionMode.Inspect, Workflow = WorkflowMode.Memorize,
            WorkerPolicy = new() { MaxRequests = 4, MaxMinutes = 1 },
            EnabledWorkers = [new() { Name = "reader", Model = "test", Mode = PermissionMode.Inspect }] };
        var run = new Run { State = RunState.Running }; parent.Runs.Add(run);
        store.Save(parent, new("created", parent.Id, run.Id, new { }));
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ctx = new ToolExecutionContext(parent, run, store, default, e => { if (e.Type == "agent.finished") finished.TrySetResult(); });
        try
        {
            var initialScanOutput = await Execute(coordinator, ctx, Call("repository_memory_scan", new { path = "", full = false }));
            var scan = System.Text.Json.JsonSerializer.Deserialize<RepositoryScan>(initialScanOutput.Content, Json.Options)!;
            var source = Assert.Single(scan.Sources);
            store.CommitRepository(workspace.Path, scan.Id, [new("widgets", 0, "Widget notes", "Widgets use a queue.", ["widget"],
                [new(source.Path, source.Hash, source.StartLine, source.EndLine)])], "seed");
            var seed = Assert.Single(store.SearchRepositoryMemories(workspace.Path, "widget"));
            Assert.Equal(1, seed.Revision);

            // Both calls were genuinely prepared while collection was clear. Execution must revalidate them later.
            var pendingScan = Call("repository_memory_scan", new { path = "", full = false });
            var pendingCommit = Call("repository_memory_commit", new { scanId = scan.Id, proposals = new[] { new {
                topicKey = "widgets", expectedRevision = 1, title = "Widget architecture", content = "Widgets use a queue.", tags = new[] { "widget" },
                sources = new[] { new { path = source.Path, hash = source.Hash, startLine = source.StartLine, endLine = source.EndLine } } } } });
            foreach (var call in new[] { pendingScan, pendingCommit })
            {
                call.Prepared = coordinator.Prepare(ctx, call); run.Calls.Add(call);
            }
            store.Save(parent, new("test.prepared", parent.Id, run.Id, new { }));
            var memoryBefore = SnapshotFiles(memoryDirectory); var catalogBefore = SnapshotFiles(catalogDirectory);
            await Execute(coordinator, ctx, Call("agent_spawn", new { profile = "reader", task = "Read README.md and report architecture evidence without writing memory.", writePaths = Array.Empty<string>() }));
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var worker = Assert.Single(store.Workers(parent.Id));
            Assert.Equal("Completed", worker.State); Assert.False(worker.Collected);
            Assert.Equal(2, model.Requests.Count);
            await AssertCollectionBlocksMemoryUpdates(coordinator, tools, ctx, pendingScan, pendingCommit);

            if (failCollectedMarker)
            {
                // The receipt is durable but the second write cannot mark the worker collected.
                using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(data.Path, "sessions.db"));
                db.Open(); using var command = db.CreateCommand();
                command.CommandText = "CREATE TRIGGER fail_collection BEFORE UPDATE ON workers WHEN json_extract(NEW.payload,'$.collected')=1 BEGIN SELECT RAISE(ABORT,'injected'); END";
                command.ExecuteNonQuery();
                try { await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => coordinator.CanCompleteAsync(ctx)); }
                finally { command.CommandText = "DROP TRIGGER fail_collection"; command.ExecuteNonQuery(); }
                Assert.Single(store.Get(parent.Id)!.Runs.Last().WorkerResults);
            }
            else
            {
                // Fail the parent receipt write, not worker execution or a repository-memory save.
                var failingContext = ctx with { Store = new CollectionReceiptFaultStore(store) };
                await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CanCompleteAsync(failingContext));
                Assert.Empty(run.WorkerResults);
                Assert.Empty(store.Get(parent.Id)!.Runs.Last().WorkerResults);
            }
            Assert.False(Assert.Single(store.Workers(parent.Id)).Collected);
            await AssertCollectionBlocksMemoryUpdates(coordinator, tools, ctx, pendingScan, pendingCommit);
            Assert.Equal(original, File.ReadAllBytes(sourcePath));
            Assert.Equal(memoryBefore, SnapshotFiles(memoryDirectory));
            Assert.Equal(catalogBefore, SnapshotFiles(catalogDirectory));
            Assert.Equal(1, store.ReadRepositoryMemory(workspace.Path, seed.Id)!.Revision);

            // Fresh stores/session/coordinator model a reload, without restarting the completed worker.
            var reloadedStore = new SessionStore(data.Path); using var reloadedTools = new ToolRegistry(reloadedStore);
            using var reloadedCoordinator = new WorkerCoordinator(reloadedStore, reloadedStore, reloadedTools,
                _ => throw new InvalidOperationException("Collection repair must not relaunch a worker."));
            var saved = reloadedStore.Get(parent.Id)!; var savedRun = saved.Runs.Last();
            var savedCtx = new ToolExecutionContext(saved, savedRun, reloadedStore, default);
            var savedScan = Assert.Single(savedRun.Calls, c => c.Id == pendingScan.Id);
            var savedCommit = Assert.Single(savedRun.Calls, c => c.Id == pendingCommit.Id);
            Assert.NotNull(savedScan.Prepared); Assert.NotNull(savedCommit.Prepared);
            var receiptBeforeRepair = failCollectedMarker ? Assert.Single(savedRun.WorkerResults).Value : null;
            var artifactIdsBeforeRepair = saved.Artifacts.Select(a => a.Id).ToArray();
            await AssertCollectionBlocksMemoryUpdates(reloadedCoordinator, reloadedTools, savedCtx, savedScan, savedCommit);
            Assert.Equal(original, File.ReadAllBytes(sourcePath));
            Assert.Equal(memoryBefore, SnapshotFiles(memoryDirectory));
            Assert.Equal(catalogBefore, SnapshotFiles(catalogDirectory));

            Assert.False(await reloadedCoordinator.CanCompleteAsync(savedCtx));
            Assert.True(Assert.Single(reloadedStore.Workers(parent.Id)).Collected);
            var receipt = Assert.Single(savedRun.WorkerResults);
            Assert.Equal(worker.Id, receipt.Key); Assert.Contains("Widgets use a queue.", receipt.Value);
            Assert.Equal(2, saved.Artifacts.Count);
            if (failCollectedMarker)
            {
                Assert.Equal(receiptBeforeRepair, receipt.Value);
                Assert.Equal(artifactIdsBeforeRepair, saved.Artifacts.Select(a => a.Id).ToArray());
            }
            var artifactIds = saved.Artifacts.Select(a => a.Id).ToArray();
            var collectedAgain = await Execute(reloadedCoordinator, savedCtx, Call("agent_result", new { agentId = worker.Id }));
            Assert.Equal(receipt.Value, collectedAgain.Content);
            Assert.Equal(artifactIds, saved.Artifacts.Select(a => a.Id).ToArray());
            Assert.Single(reloadedStore.Get(parent.Id)!.Runs.Last().WorkerResults);
            Assert.Equal(memoryBefore, SnapshotFiles(memoryDirectory));
            Assert.Equal(catalogBefore, SnapshotFiles(catalogDirectory));

            // Collection now permits the original prepared scan and the deterministic parent reconciliation.
            var scanOutput = await reloadedCoordinator.ExecuteAsync(savedCtx, savedScan);
            var repairedScan = System.Text.Json.JsonSerializer.Deserialize<RepositoryScan>(scanOutput.Content, Json.Options)!;
            Assert.NotEqual(scan.Id, repairedScan.Id);
            var output = await reloadedCoordinator.ExecuteAsync(savedCtx, savedCommit);
            var report = System.Text.Json.JsonSerializer.Deserialize<RepositoryCommitReport>(output.Content, Json.Options)!;
            Assert.Equal(1, report.Updated); Assert.Equal(0, report.Created); Assert.Equal(0, report.Conflicts);
            var entry = Assert.Single(reloadedStore.SearchRepositoryMemories(workspace.Path, "widget"));
            Assert.Equal(seed.Id, entry.Id); Assert.Equal(2, entry.Revision); Assert.Equal("Widget architecture", entry.Title);
            var memoryAfter = SnapshotFiles(memoryDirectory); var catalogAfter = SnapshotFiles(catalogDirectory);

            // Replaying the same durable call after another reload must reuse its commit receipt.
            var replayStore = new SessionStore(data.Path); using var replayTools = new ToolRegistry(replayStore);
            var replayParent = replayStore.Get(parent.Id)!; var replayRun = replayParent.Runs.Last();
            var replayCtx = new ToolExecutionContext(replayParent, replayRun, replayStore, default);
            var replayCall = Assert.Single(replayRun.Calls, c => c.Id == pendingCommit.Id);
            var replay = await replayTools.ExecuteAsync(replayCtx, replayCall);
            Assert.Equal(output.Content, replay.Content);
            Assert.Equal(memoryAfter, SnapshotFiles(memoryDirectory));
            Assert.Equal(catalogAfter, SnapshotFiles(catalogDirectory));
            Assert.Equal(2, replayStore.ReadRepositoryMemory(workspace.Path, seed.Id)!.Revision);
            Assert.Single(replayStore.Get(parent.Id)!.Runs.Last().WorkerResults);
            using var state = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(memoryDirectory, "state.json")));
            Assert.Equal(2, state.RootElement.GetProperty("receipts").GetArrayLength()); // Seed plus one reconciliation, not a replay receipt.
            Assert.Equal(2, state.RootElement.GetProperty("history").GetArrayLength());
            Assert.Equal(original, File.ReadAllBytes(sourcePath));
            Assert.Equal(2, model.Requests.Count);
        }
        finally { await coordinator.StopAllAsync(parent.Id).WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    private static string[] SnapshotFiles(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(directory, path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();

    private static async Task AssertCollectionBlocksMemoryUpdates(WorkerCoordinator coordinator, ToolRegistry tools,
        ToolExecutionContext ctx, params ToolCall[] calls)
    {
        foreach (var call in calls)
        {
            Assert.NotNull(call.Prepared);
            Assert.Contains("Collect and settle workers", Assert.Throws<UserFacingException>(() => coordinator.Prepare(ctx, call)).Message);
            Assert.Contains("Collect and settle workers", (await Assert.ThrowsAsync<UserFacingException>(() => coordinator.ExecuteAsync(ctx, call))).Message);
            Assert.Contains("Collect and settle workers", (await Assert.ThrowsAsync<UserFacingException>(() => tools.ExecuteAsync(ctx, call))).Message);
        }
    }

    private sealed class CollectionReceiptFaultStore(ISessionStore inner) : ISessionStore
    {
        private bool failed;
        public string DataDirectory => inner.DataDirectory;
        public IDisposable Acquire(string id) => inner.Acquire(id);
        public Session? Get(string id) => inner.Get(id);
        public IReadOnlyList<Session> List() => inner.List();
        public void SaveProcess(string id, ProcessRecord process) => inner.SaveProcess(id, process);
        public void Save(Session session, AgentEvent ev)
        {
            if (!failed && ev.Type == "agent.result.saved") { failed = true; throw new InvalidOperationException("Injected worker receipt persistence failure"); }
            inner.Save(session, ev);
        }
    }
}
