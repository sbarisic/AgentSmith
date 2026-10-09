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
            Parent = new() { Workspace = Dir.Path, EnabledWorkers = [new() { Name = "editor", Model = "local", Mode = PermissionMode.Edit, Connection = new() { Provider = ModelProvider.LmStudio } }] };
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
    private static JsonObject Finish(string status = "completed") => RuntimeTests.Call("finish", "agent_finish", Json.Serialize(new { status, summary = "Task inspected", limitations = "Tests not run", evidence = new[] { "a.txt" }, clarification = status == "blocked" ? "Which target?" : "" }));
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
    public async Task FinishPersistsReportSkipsTailAndParentMustCollect()
    {
        using var f = new Fixture(_ => new ScriptedModel([RuntimeTests.Done(Finish("blocked"), RuntimeTests.Call("tail", "file_create", Json.Serialize(new { path = "a.txt", content = "never" })))]));
        await f.Spawn(["a.txt"]); Assert.False(await f.Agents.CanCompleteAsync(f.Context()));
        var worker = Assert.Single(f.Store.Workers(f.Parent.Id)); Assert.Equal("Blocked", worker.State); Assert.False(File.Exists(Path.Combine(f.Dir.Path, "a.txt")));
        var result = await f.Invoke("agent_result", new { agentId = worker.Id }); Assert.Contains("Which target?", result.Content);
        Assert.True(await f.Agents.CanCompleteAsync(f.Context())); Assert.Equal(2, f.Parent.Artifacts.Count);
        Assert.Throws<UserFacingException>(() => f.Agents.Find(new Session(), worker.Id));
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
    public async Task EightLaunchLimitAndMissingReportCannotBecomeSuccessful()
    {
        using var f = new Fixture(_ => new ScriptedModel([RuntimeTests.Done(RuntimeTests.Message("No structured report"))]));
        for (var i = 0; i < 8; i++)
        {
            await f.Spawn([]);
            var worker = f.Store.Workers(f.Parent.Id).Last();
            while (f.Store.Workers(f.Parent.Id).Single(w => w.Id == worker.Id).Active) await Task.Delay(5);
            Assert.Equal("Failed", f.Store.Workers(f.Parent.Id).Single(w => w.Id == worker.Id).State);
        }
        await Assert.ThrowsAsync<UserFacingException>(() => f.Spawn([]));
    }
    [Fact]
    public async Task SharedArtifactsRequireExplicitRegisteredIdsAndUseChildLocalCopies()
    {
        using var f = new Fixture();
        await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("agent_spawn", new { profile = "editor", task = "Read [artifact:foreign]", writePaths = Array.Empty<string>() }));
        var ctx = f.Context(); Directory.CreateDirectory(ctx.ArtifactDirectory);
        var file = Path.Combine(ctx.ArtifactDirectory, "evidence.txt"); File.WriteAllText(file, "Registered evidence");
        var artifact = ArtifactAccess.Register(ctx, file, "verification");
        await f.Invoke("agent_spawn", new { profile = "editor", task = "Read [artifact:" + artifact.Id + "]", writePaths = Array.Empty<string>() });
        var child = f.Store.Get(f.Store.Workers(f.Parent.Id)[0].ChildId)!; var copy = Assert.Single(child.Artifacts);
        Assert.NotEqual(artifact.Id, copy.Id); Assert.Contains(child.Id, copy.Path);
        var read = ArtifactAccess.Execute(f.Context(child), "artifact_read", new JsonObject { ["artifactId"] = copy.Id, ["cursor"] = 0, ["maxCharacters"] = 4000 }); Assert.Contains("Registered evidence", read.Content);
        Assert.Throws<UserFacingException>(() => ArtifactAccess.Execute(f.Context(child), "artifact_read", new JsonObject { ["artifactId"] = artifact.Id, ["cursor"] = 0, ["maxCharacters"] = 4000 }));
        var worker = f.Store.Workers(f.Parent.Id).Single(); await f.Agents.CancelAsync(worker.Id);
        var collected = JsonNode.Parse((await f.Invoke("agent_result", new { agentId = worker.Id })).Content)!;
        var imported = collected["imported"]!.AsArray().Single()!;
        Assert.Equal(child.Id, imported["sourceConversationId"]!.ToString());
        Assert.Equal(copy.Id, imported["sourceArtifactId"]!.ToString());
        var parentRead = ArtifactAccess.Execute(f.Context(), "artifact_read", new JsonObject { ["artifactId"] = imported["artifactId"]!.ToString(), ["cursor"] = 0, ["maxCharacters"] = 4000 }); Assert.Contains("Registered evidence", parentRead.Content);
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
        await f.Spawn(["effect.txt"], "durable-spawn"); await f.Agents.CanCompleteAsync(f.Context());
        Assert.Equal(changed, File.Exists(Path.Combine(f.Dir.Path, "effect.txt")));
        f.Agents.RecoverChildren(f.Parent); await f.Spawn(["effect.txt"], "durable-spawn");
        Assert.Single(f.Store.Workers(f.Parent.Id)); Assert.Equal(changed, File.Exists(Path.Combine(f.Dir.Path, "effect.txt")));
        if (changed) Assert.Equal("one effect", File.ReadAllText(Path.Combine(f.Dir.Path, "effect.txt")));
        Assert.False(f.Store.Workers(f.Parent.Id).Single().ReservationsHeld);
    }
}
