using AgentSmith.Core;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

public sealed class MemorizeRevalidationTests
{
    public static IEnumerable<object[]> StateChanges()
    {
        foreach (var commit in new[] { false, true })
        foreach (var change in new[] { "workflow-exit", "memory-disabled", "recovery-run", "prepared-mutation",
            "pending-plan-review", "pending-question", "awaiting-user", "became-child", "active-worker", "uncollected-worker", "reserved-worker" })
            yield return new object[] { commit, change };
    }

    [Theory]
    [MemberData(nameof(StateChanges))]
    public async Task PreparedRepositoryWriteRechecksStateBeforeAnyMutation(bool commit, string change)
    {
        using var workspace = new TestDirectory();
        using var data = new TestDirectory();
        File.WriteAllText(Path.Combine(workspace.Path, "README.md"), "# Widget architecture\nWidgets use a queue.\n");
        var store = new SessionStore(data.Path);
        using var tools = new ToolRegistry(store);
        var ctx = Context(store, workspace.Path);
        var call = WriteCall(store, workspace.Path, commit);

        Assert.Contains(tools.Available(ctx.Session), definition => definition.Name == call.Name);
        ctx.Run.Calls.Add(call);
        call.Prepared = tools.Prepare(ctx, call);
        Assert.NotNull(call.Prepared);
        var filesBefore = Files(workspace.Path);
        var directoriesBefore = Directories(workspace.Path);
        var registrationsBefore = Json.Serialize(store.DiscoverRepositories());
        Assert.Equal(commit, Directory.Exists(Path.Combine(workspace.Path, ".agentsmith")));

        // The call remains prepared: do not re-prepare it after the eligibility change.
        var expectedReason = ChangeState(store, ctx, change);
        var error = await Assert.ThrowsAsync<UserFacingException>(() => tools.ExecuteAsync(ctx, call));

        Assert.Contains(expectedReason, error.Message);
        Assert.Equal(directoriesBefore, Directories(workspace.Path));
        var filesAfter = Files(workspace.Path);
        Assert.Equal(filesBefore.Keys.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            filesAfter.Keys.OrderBy(path => path, StringComparer.Ordinal).ToArray());
        foreach (var (path, bytes) in filesBefore)
            Assert.Equal(bytes, filesAfter[path]);
        // Inspect the catalog directly: disabled-memory discovery intentionally returns no entries.
        Assert.Equal(registrationsBefore,
            Json.Serialize(RepositoryMemoryStore.Discover(Path.Combine(data.Path, "repositories"))));
        Assert.Equal(commit, Directory.Exists(Path.Combine(workspace.Path, ".agentsmith")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PreparedRepositoryWriteExecutesWhenStateRemainsEligible(bool commit, bool answeredQuestion)
    {
        using var workspace = new TestDirectory();
        using var data = new TestDirectory();
        var sourcePath = Path.Combine(workspace.Path, "README.md");
        File.WriteAllText(sourcePath, "# Widget architecture\nWidgets use a queue.\n");
        var original = File.ReadAllBytes(sourcePath);
        var store = new SessionStore(data.Path);
        using var tools = new ToolRegistry(store);
        var ctx = Context(store, workspace.Path);
        var call = WriteCall(store, workspace.Path, commit);
        ctx.Run.Calls.Add(call);
        call.Prepared = tools.Prepare(ctx, call);

        if (answeredQuestion)
        {
            ChangeState(store, ctx, "pending-question");
            ctx.Run.Question!.Answer = "Continue";
            ctx.Run.State = RunState.Running;
        }

        var output = await tools.ExecuteAsync(ctx, call);

        if (commit)
        {
            var report = System.Text.Json.JsonSerializer.Deserialize<RepositoryCommitReport>(output.Content, Json.Options)!;
            Assert.Equal(1, report.Updated);
            Assert.Equal(2, Assert.Single(store.SearchRepositoryMemories(workspace.Path, "widget")).Revision);
        }
        else
        {
            var scan = System.Text.Json.JsonSerializer.Deserialize<RepositoryScan>(output.Content, Json.Options)!;
            Assert.Single(scan.Sources);
        }
        Assert.True(Directory.Exists(Path.Combine(workspace.Path, ".agentsmith")));
        Assert.Equal(original, File.ReadAllBytes(sourcePath));
    }

    private static ToolExecutionContext Context(SessionStore store, string workspace)
    {
        var session = new Session { Workspace = workspace, Mode = PermissionMode.Inspect, Workflow = WorkflowMode.Memorize };
        var run = new Run();
        session.Runs.Add(run);
        store.Save(session, new("created", session.Id, run.Id, new { }));
        return new(session, run, store, default);
    }

    private static ToolCall WriteCall(SessionStore store, string workspace, bool commit)
    {
        if (!commit) return Call("repository_memory_scan", new { path = "", full = false });
        var scan = store.ScanRepository(workspace);
        var source = Assert.Single(scan.Sources);
        var provenance = new RepositoryMemorySource(source.Path, source.Hash, source.StartLine, source.EndLine);
        store.CommitRepository(workspace, scan.Id,
            [new("widgets", 0, "Widget architecture", "Widgets use a queue.", ["widget"], [provenance])], "seed");
        var entry = Assert.Single(store.SearchRepositoryMemories(workspace, "widget"));
        return Call("repository_memory_commit", new { scanId = scan.Id, proposals = new[] { new {
            topicKey = "widgets", expectedRevision = entry.Revision, title = "Widget architecture",
            content = "Widgets use a queue described in README.", tags = new[] { "widget" },
            sources = new[] { new { path = source.Path, hash = source.Hash,
                startLine = source.StartLine, endLine = source.EndLine } } } } });
    }

    private static string ChangeState(SessionStore store, ToolExecutionContext ctx, string change)
    {
        switch (change)
        {
            case "workflow-exit":
                ctx.Session.Workflow = WorkflowMode.Work;
                return "disabled";
            case "memory-disabled":
                store.SetMemoryEnabled(false);
                return "disabled";
            case "recovery-run":
                // Recovery on another run must block writes in the currently prepared run too.
                ctx.Session.Runs.Add(new Run { State = RunState.RecoveryRequired });
                return "Resolve recovery";
            case "prepared-mutation":
                ctx.Session.Mutations.Add(new() { Kind = "file_create", State = "Prepared" });
                return "Resolve recovery";
            case "pending-question":
                // Direct-dispatch defense in depth: the normal runner already waits for an answer.
                ctx.Run.Question = new() { CallId = "question-after-prepare" };
                return "Resolve recovery";
            case "awaiting-user":
                // Isolate the state barrier even if the question payload is absent.
                ctx.Run.State = RunState.AwaitingUser;
                return "Resolve recovery";

            case "pending-plan-review":
                // Persisted-state fixture: keep Memorize active to isolate its pending-review guard.
                // This is not a claim that a normal CLI transition creates this combination.
                var plan = new WorkPlan { State = "Review" };
                plan.Revisions.Add(new() { Revision = 1, Goal = "Review fixture" });
                plan.Reviews.Add(new() { Revision = 1 });
                ctx.Session.Plans.Add(plan);
                ctx.Session.ActivePlanId = plan.Id;
                return "Resolve recovery";
            case "became-child":
                ctx.Session.ParentConversationId = "parent-after-prepare";
                return "Tool is disabled in this workflow/mode";
            case "active-worker":
            case "uncollected-worker":
            case "reserved-worker":
                var child = new Session { Workspace = ctx.Workspace, Mode = PermissionMode.Inspect,
                    Workflow = WorkflowMode.Memorize, ParentConversationId = ctx.Session.Id };
                // Isolate each disjunct, including an active record marked collected: no one flag
                // should allow a prepared write to bypass the other worker settlement checks.
                store.CreateWorker(new WorkerRecord {
                    ParentId = ctx.Session.Id, ParentRunId = ctx.Run.Id, CallId = "worker-after-prepare",
                    ChildId = child.Id, State = change == "active-worker" ? "Running" : "Completed",
                    Collected = change != "uncollected-worker", ReservationsHeld = change == "reserved-worker"
                }, child);
                Assert.Single(store.Workers(ctx.Session.Id));
                return "Collect and settle workers";
            default:
                throw new ArgumentOutOfRangeException(nameof(change), change, "Unknown state change.");
        }
    }

    private static ToolCall Call(string name, object args) => new() {
        Id = Guid.NewGuid().ToString("N"), Name = name, Arguments = Json.Serialize(args)
    };

    private static Dictionary<string, byte[]> Files(string workspace) =>
        Directory.GetFiles(workspace, "*", SearchOption.AllDirectories).ToDictionary(
            path => Path.GetRelativePath(workspace, path), File.ReadAllBytes, StringComparer.Ordinal);

    private static string[] Directories(string workspace) =>
        Directory.GetDirectories(workspace, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(workspace, path)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
}
