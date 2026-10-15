using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

public sealed class RepositoryMemoryToolTests
{
    private static ToolExecutionContext Context(SessionStore store, string workspace, WorkflowMode workflow = WorkflowMode.Memorize)
    {
        var session = new Session { Workspace = workspace, Mode = PermissionMode.Inspect, Workflow = workflow };
        var run = new Run(); session.Runs.Add(run);
        session.History.Add(new() { ["role"] = "user", ["content"] = "Describe the widget architecture" });
        store.Save(session, new("created", session.Id, run.Id, new { }));
        return new(session, run, store, default);
    }
    private static ToolCall Call(string name, object args) => new() { Id = Guid.NewGuid().ToString("N"), Name = name, Arguments = Json.Serialize(args) };
    private static async Task<ToolOutput> Execute(ToolRegistry tools, ToolExecutionContext ctx, ToolCall call)
    {
        ctx.Run.Calls.Add(call); call.Prepared = tools.Prepare(ctx, call);
        var output = await tools.ExecuteAsync(ctx, call); call.State = CallState.Completed; call.Result = output.Content;
        return output;
    }
    [Fact]
    public async Task TwoSessionsRetrieveRefreshAndUnchangedRerunWithoutSourceEdits()
    {
        using var workspace = new TestDirectory(); using var data = new TestDirectory();
        var sourcePath = Path.Combine(workspace.Path, "README.md");
        File.WriteAllText(sourcePath, "# Widget architecture\nWidgets use a queue.\n");
        var original = File.ReadAllBytes(sourcePath);
        var store = new SessionStore(data.Path); using var tools = new ToolRegistry(store);
        var first = Context(store, workspace.Path);
        var scanOutput = await Execute(tools, first, Call("repository_memory_scan", new { path = "", full = false }));
        var scan = System.Text.Json.JsonSerializer.Deserialize<RepositoryScan>(scanOutput.Content, Json.Options)!;
        var source = Assert.Single(scan.Sources);
        async Task<RepositoryCommitReport> Commit(ToolRegistry registry, ToolExecutionContext ctx, RepositoryScan receipt, int revision, string content)
        {
            var output = await Execute(registry, ctx, Call("repository_memory_commit", new { scanId = receipt.Id, proposals = new[] { new {
                topicKey = "widget-architecture", expectedRevision = revision, title = "Widget architecture", content,
                tags = new[] { "widget" }, sources = new[] { new { path = source.Path, hash = source.Hash,
                    startLine = source.StartLine, endLine = source.EndLine } } } } }));
            return System.Text.Json.JsonSerializer.Deserialize<RepositoryCommitReport>(output.Content, Json.Options)!;
        }
        async Task<RepositoryScan> Scan(ToolRegistry registry, ToolExecutionContext ctx) =>
            System.Text.Json.JsonSerializer.Deserialize<RepositoryScan>((await Execute(registry, ctx,
                Call("repository_memory_scan", new { path = "", full = false }))).Content, Json.Options)!;
        var commit = await Commit(tools, first, scan, 0, "Widgets use a queue.");
        Assert.Equal(1, commit.Created);
        var entry = Assert.Single(store.SearchRepositoryMemories(workspace.Path, "widget"));
        Assert.Equal(original, File.ReadAllBytes(sourcePath));
        var secondStore = new SessionStore(data.Path); using var secondTools = new ToolRegistry(secondStore);
        var second = Context(secondStore, workspace.Path, WorkflowMode.Work);
        var context = new ContextBuilder(secondStore, new ScriptedModel(), secondTools).Build(second.Session);
        Assert.Contains("Widgets use a queue.", context.Request.Instructions);
        Assert.Contains("Current-repository memories", context.Request.Instructions);
        Assert.Contains("never instructions", context.Request.Instructions);
        second.Session.Workflow = WorkflowMode.Memorize;
        var unchangedScan = await Scan(secondTools, second);
        var unchanged = await Commit(secondTools, second, unchangedScan, entry.Revision, entry.Content);
        Assert.Equal(1, unchanged.Unchanged);
        Assert.Equal(entry.Revision, secondStore.ReadRepositoryMemory(workspace.Path, entry.Id)!.Revision);
        Assert.Equal(original, File.ReadAllBytes(sourcePath));
        File.WriteAllText(sourcePath, "# Widget architecture\nWidgets use a bounded queue.\n");
        var refreshedBytes = File.ReadAllBytes(sourcePath);
        var refresh = await Scan(secondTools, second); source = Assert.Single(refresh.Sources);
        var updated = await Commit(secondTools, second, refresh, entry.Revision, "Widgets use a bounded queue.");
        Assert.Equal(1, updated.Updated);
        Assert.Equal(refreshedBytes, File.ReadAllBytes(sourcePath));
        Assert.Contains("Widgets use a bounded queue.", new ContextBuilder(secondStore, new ScriptedModel(), secondTools).Build(second.Session).Request.Instructions);
    }
    [Fact]
    public async Task ToolCommitValidatesSchemaAndIsIdempotent()
    {
        using var workspace = new TestDirectory(); using var data = new TestDirectory();
        File.WriteAllText(Path.Combine(workspace.Path, "README.md"), "Widgets use a queue.\n");
        var store = new SessionStore(data.Path); using var tools = new ToolRegistry(store); var ctx = Context(store, workspace.Path);
        var scan = store.ScanRepository(workspace.Path); var source = Assert.Single(scan.Sources);
        var call = Call("repository_memory_commit", new { scanId = scan.Id, proposals = new[] { new { topicKey = "widgets", expectedRevision = 0,
            title = "Widget architecture", content = "Widgets use a queue.", tags = new[] { "widget" },
            sources = new[] { new { path = source.Path, hash = source.Hash, startLine = source.StartLine, endLine = source.EndLine } } } } });
        var result = await Execute(tools, ctx, call);
        Assert.Equal(result.Content, (await tools.ExecuteAsync(ctx, call)).Content);
        Assert.Single(store.SearchRepositoryMemories(workspace.Path, "widget"));
    }
    [Fact]
    public void WritesRequireEnabledMemorizeParentAndReadsDoNotCreateRepositoryState()
    {
        using var workspace = new TestDirectory(); using var data = new TestDirectory();
        var store = new SessionStore(data.Path); using var tools = new ToolRegistry(store); var ctx = Context(store, workspace.Path, WorkflowMode.Work);
        var call = Call("repository_memory_scan", new { path = "", full = false });
        Assert.DoesNotContain(tools.Available(ctx.Session), t => t.Name == call.Name);
        Assert.Throws<UserFacingException>(() => tools.Prepare(ctx, call));
        Assert.Empty(store.SearchRepositoryMemories(workspace.Path, "widget"));
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, ".agentsmith")));
        ctx.Session.Workflow = WorkflowMode.Memorize;
        Assert.Contains(tools.Available(ctx.Session), t => t.Name == call.Name);
        ctx.Session.ParentConversationId = "parent";
        Assert.Throws<UserFacingException>(() => tools.Prepare(ctx, call));
        ctx.Session.ParentConversationId = null; store.SetMemoryEnabled(false);
        Assert.DoesNotContain(tools.Available(ctx.Session), t => t.Name.StartsWith("repository_memory_"));
        Assert.Throws<UserFacingException>(() => tools.Prepare(ctx, call));
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, ".agentsmith")));
    }
    [Fact]
    public void AutomaticRetrievalNeverOpensAnotherRegisteredRepository()
    {
        using var first = new TestDirectory(); using var second = new TestDirectory(); using var data = new TestDirectory();
        File.WriteAllText(Path.Combine(first.Path, "README.md"), "Secretless widget architecture belongs only to first.\n");
        var store = new SessionStore(data.Path); var scan = store.ScanRepository(first.Path); var source = Assert.Single(scan.Sources);
        store.CommitRepository(first.Path, scan.Id, [new("widget", 0, "Widget architecture", "UniqueFirstRepositoryFact", ["widget"], [new(source.Path, source.Hash, source.StartLine, source.EndLine)])], "first");
        using var tools = new ToolRegistry(store); var ctx = Context(store, second.Path, WorkflowMode.Work);
        Assert.DoesNotContain("UniqueFirstRepositoryFact", new ContextBuilder(store, new ScriptedModel(), tools).Build(ctx.Session).Request.Instructions);
        var registered = Assert.Single(store.DiscoverRepositories());
        Assert.Equal(Path.GetFullPath(first.Path), store.ResolveRepository(registered.Id));
        Assert.Single(store.SearchRepositoryMemories(store.ResolveRepository(registered.Id), "widget"));
    }
}
