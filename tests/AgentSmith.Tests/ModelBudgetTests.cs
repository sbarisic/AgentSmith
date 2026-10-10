using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentSmith.Tests;

public sealed class ModelBudgetTests
{
    [Fact]
    public async Task LocalEndpointAliasesShareCapacityAndCancelledWaitDoesNotDispatch()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var model = new BlockingModel();
        var a = new Session { Connection = new() { Provider = ModelProvider.Local, Endpoint = "http://127.0.0.1:58321/v1" } };
        var b = new Session { Connection = new() { Provider = ModelProvider.Local, Endpoint = "http://localhost:58321/v1" } };
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Drain(new SessionModelClient(store, a, model), default); await model.Started.Task;
        using var cancel = new CancellationTokenSource();
        var second = Drain(new SessionModelClient(store, b, model, _ => waiting.SetResult()), cancel.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(1, model.Calls);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        model.Release.SetResult(); await first; Assert.Equal(1, model.Calls);
        await Drain(new SessionModelClient(store, b, model), default); Assert.Equal(2, model.Calls);
        static async Task Drain(IModelClient client, CancellationToken ct) { await foreach (var _ in client.StreamAsync(new("test", "", [], []), ct)) { } }
    }
    private sealed class BlockingModel : IModelClient
    {
        public int Calls; public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest r, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { Interlocked.Increment(ref Calls); Started.TrySetResult(); await Release.Task.WaitAsync(ct); yield return RuntimeTests.Done(RuntimeTests.Message("done")); }
    }
    [Fact]
    public void LoadedCapacityAndOutputReserveBoundBothPolicies()
    {
        var connection = new ModelConnection { LoadedContextTokens = 32768, MaxOutputTokens = 8192 };
        var policy = new ContextPolicy(); Assert.Equal(22937, policy.WorkingTokens(connection));
        connection.Capabilities.ContextTokens = 1050000; Assert.Equal(128000, policy.WorkingTokens(connection));
        policy.Budget = "max"; Assert.Equal(989308, policy.WorkingTokens(connection));
        policy.Budget = "1050000"; Assert.Throws<UserFacingException>(() => policy.WorkingTokens(connection));
        connection.Capabilities.ContextTokens = null; connection.LoadedContextTokens = null; policy.Budget = "balanced"; Assert.Null(policy.WorkingTokens(connection));
    }
    [Fact]
    public void ImageAccountingDoesNotCountBase64AsText()
    {
        var policy = new ContextPolicy();
        ModelRequest Request(int count) => new("test", "", [new() { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_image", ["image_url"] = new string('x', count) }) }], []);
        Assert.Equal(policy.Estimate(Request(10)).Tokens, policy.Estimate(Request(100000)).Tokens);
        Assert.False(policy.Estimate(Request(10)).Exact);
        using var dir = new TestDirectory(); var builder = new ContextBuilder(new SessionStore(dir.Path), new ScriptedModel(), new ConstantTools());
        Assert.Equal(builder.Size("", Request(10).Input, []), builder.Size("", Request(100000).Input, []));
        Assert.True(builder.Size("", Request(10).Input, []) > 16384);
    }
    [Fact]
    public async Task ParentCanPassThirtyRequestsAndLimitsAreStructured()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = new Session { Workspace = dir.Path };
        var replies = Enumerable.Range(0, 35).Select(i => new[] { RuntimeTests.Done(new JsonObject { ["type"] = "function_call", ["call_id"] = "c" + i, ["name"] = "count", ["arguments"] = Json.Serialize(new { n = i }) }) }).ToList();
        replies.Add([RuntimeTests.Done(RuntimeTests.Message("done"))]);
        var model = new ScriptedModel(replies.ToArray());
        var run = await new AgentRunner(store, model, new ConstantTools()).RunAsync(s, "Long task", null, _ => { }, default);
        Assert.Equal(RunState.Completed, run.State); Assert.Equal(36, run.Steps);
        var limited = new Session { Workspace = dir.Path, Execution = new() { MaxRequests = 1 } };
        var stop = await new AgentRunner(store, new ScriptedModel(replies[0]), new ConstantTools()).RunAsync(limited, "bounded", null, _ => { }, default);
        Assert.Equal(PauseReason.RequestLimit, stop.PauseReason); Assert.DoesNotContain("/compact", PauseGuidance.For(stop));
    }
    [Fact]
    public async Task UnchangedRoundsPauseWithoutRepeatingEffectsForever()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = new Session { Workspace = dir.Path };
        var replies = Enumerable.Range(0, 4).Select(i => new[] { RuntimeTests.Done(new JsonObject { ["type"] = "function_call", ["call_id"] = "c" + i, ["name"] = "count", ["arguments"] = "{}" }) }).ToArray();
        var run = await new AgentRunner(store, new ScriptedModel(replies), new ConstantTools()).RunAsync(s, "repeat", null, _ => { }, default);
        Assert.Equal(PauseReason.NoProgress, run.PauseReason); Assert.Equal(3, run.Steps);
    }
    private sealed class ConstantTools : IToolDispatcher
    {
        public IReadOnlyList<ToolDefinition> Definitions => [new("count", "test", new())];
        public string Instructions(string workspace) => "";
        public Task<ToolOutput> ExecuteAsync(string workspace, ToolCall call, CancellationToken ct) => Task.FromResult(new ToolOutput("same"));
    }
    [Fact]
    public void ProjectionPreservesOriginalAndCorrelatedArtifact()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = new Session { Workspace = dir.Path };
        var raw = Json.Serialize(new { state = "Exited", exitCode = 1, error = "build failed", text = new string('x', 80000) });
        var call = new ToolCall { Id = "c", Name = "process_poll", State = CallState.Completed, Result = raw }; s.Runs.Add(new() { Calls = [call] });
        s.History.Add(new() { ["type"] = "function_call_output", ["call_id"] = "c", ["output"] = raw });
        Assert.True(ToolResultProjection.Prepare(s, store)); var projected = ToolResultProjection.Apply(s, s.History).Single();
        Assert.Equal(raw, s.History[0]["output"]!.ToString()); Assert.Equal(raw, call.Result);
        Assert.Contains("build failed", projected["output"]!.ToString()); Assert.True(System.Text.Encoding.UTF8.GetByteCount(projected["output"]!.ToString()) < 8192);
        Assert.Equal(raw, File.ReadAllText(Assert.Single(s.Artifacts).Path)); Assert.False(ToolResultProjection.Prepare(s, store));
    }
    [Fact]
    public void V6MigrationPreservesQueuePolicyAndMakesConsistentBackup()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = new Session { Workspace = dir.Path, Mode = PermissionMode.Edit, Workflow = WorkflowMode.Plan };
        store.Save(s, new("created", s.Id, null, new { })); store.Enqueue(s.Id, "pending"); store.HoldQueue(s.Id, "Paused");
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(dir.Path, "sessions.db")))
        { db.Open(); using var c = db.CreateCommand(); c.CommandText = "DROP TABLE attachments; DROP TABLE workflow_reports; DROP TABLE workflow_revisions; DROP TABLE steering; UPDATE schema_version SET version=6"; c.ExecuteNonQuery(); }
        var migrated = new SessionStore(dir.Path); var saved = migrated.Get(s.Id)!;
        Assert.Equal(PermissionMode.Edit, saved.Mode); Assert.Equal(WorkflowMode.Plan, saved.Workflow); Assert.Equal(150, saved.Execution.MaxRequests);
        Assert.Equal("Paused", migrated.Queue(s.Id).HoldReason); Assert.Single(migrated.Queue(s.Id).Items);
        Assert.True(Directory.GetFiles(dir.Path, "sessions-v6-*.backup.db").Length >= 1);
    }
}
