using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.OpenAI;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentSmith.Tests;

public sealed class ContinuityTests
{
    private static Session Conversation(SessionStore store)
    {
        var session = new Session { Workspace = store.DataDirectory, Model = "test" };
        store.Save(session, new("created", session.Id, null, new { })); return session;
    }
    private static JsonObject User(string text) => new() { ["role"] = "user", ["content"] = text };
    private static ModelEvent Summary(string text) => new("completed", Output: [new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }) }]);
    private static void LongHistory(Session s)
    {
        for (var n = 0; n < 6; n++) { s.History.Add(User("Request " + n + " " + new string('x', 5000))); s.History.Add(new JsonObject { ["role"] = "assistant", ["content"] = "Observed evidence " + n }); }
        s.History.Add(User("Keep my current request verbatim"));
    }
    [Fact]
    public async Task CompactionPersistsProjectionWithoutChangingHistoryAndUsesNoTools()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Conversation(store); LongHistory(s);
        var original = ContextBuilder.Hash(s.History); var model = new ScriptedModel([Summary("Goal, observations, unresolved task; sources 0..10.")]);
        var builder = new ContextBuilder(store, model, new CountingTools()) { Limit = 30000 };
        var context = await builder.PrepareAsync(s, new Run(), _ => { }, default);
        Assert.Equal(original, ContextBuilder.Hash(s.History)); Assert.Single(s.ContextSnapshots);
        Assert.Contains(context.Request.Input, i => i["content"]?.ToString() == "Keep my current request verbatim");
        Assert.All(model.Requests, r => Assert.Empty(r.Tools)); Assert.True(s.ContextSnapshots[0].After < 18000);
        var saved = store.Get(s.Id)!; Assert.Equal(s.ActiveContextId, saved.ActiveContextId); Assert.Equal(original, ContextBuilder.Hash(saved.History));
        Assert.Empty(ChatGptModelClient.BuildRequest(model.Requests[0])["tools"]!.AsArray());
        Assert.Equal(context.Request.Input.Count, builder.Build(saved).Request.Input.Count);
    }
    [Fact]
    public async Task InterruptedSummaryDoesNotActivateAndRequiresExplicitRetry()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Conversation(store); LongHistory(s);
        var model = new ScriptedModel([new("text", "incomplete")], [Summary("Preserved evidence")]);
        var builder = new ContextBuilder(store, model, new CountingTools()) { Limit = 30000 };
        await Assert.ThrowsAsync<ContextBudgetException>(() => builder.PrepareAsync(s, new(), _ => { }, default));
        Assert.Null(s.ActiveContextId); Assert.Empty(s.ContextSnapshots);
        Assert.True(builder.RequiresExplicitRetry(store.Get(s.Id)!));
        await Assert.ThrowsAsync<ContextBudgetException>(() => builder.PrepareAsync(s, new(), _ => { }, default)); Assert.Single(model.Requests);
        await builder.PrepareAsync(s, new(), _ => { }, default, true); Assert.NotNull(store.Get(s.Id)!.ActiveContextId);
        Assert.False(builder.RequiresExplicitRetry(store.Get(s.Id)!));
    }
    [Fact]
    public async Task OversizedSummaryAndMandatoryContextPauseWithoutLosingHistory()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Conversation(store); LongHistory(s);
        var original = ContextBuilder.Hash(s.History);
        var model = new ScriptedModel([Summary(new string('x', 8001))], [Summary(new string('x', 8001))]);
        var builder = new ContextBuilder(store, model, new CountingTools()) { Limit = 30000 };
        await Assert.ThrowsAsync<ContextBudgetException>(() => builder.PrepareAsync(s, new(), _ => { }, default));
        Assert.Equal(original, ContextBuilder.Hash(s.History)); Assert.Null(s.ActiveContextId);
        Assert.Equal(2, model.Requests.Count); Assert.True(builder.RequiresExplicitRetry(s));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedSummaryRetriesSameEvidenceOnceWithoutTruncating(bool streamed)
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Conversation(store); LongHistory(s);
        var original = ContextBuilder.Hash(s.History);
        var model = new ScriptedModel([streamed ? new ModelEvent("text", new string('x', 8001)) : Summary(new string('x', 8001))], [Summary("Verified observations; unresolved task; source records retained.")]);
        var events = new List<AgentEvent>();
        var builder = new ContextBuilder(store, model, new CountingTools()) { Limit = 30000 };
        await builder.PrepareAsync(s, new(), events.Add, default);
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(model.Requests[0].Input[0].ToJsonString(), model.Requests[1].Input[0].ToJsonString());
        Assert.Contains("2000 characters", model.Requests[1].Instructions);
        Assert.All(model.Requests, r => { Assert.Empty(r.Tools); Assert.True(r.Instructions.Length + r.Input.Sum(i => i.ToJsonString().Length) <= 48000); });
        Assert.Single(events, e => e.Type == "context.compaction.retry");
        Assert.Equal(original, ContextBuilder.Hash(store.Get(s.Id)!.History));
        Assert.Equal("Verified observations; unresolved task; source records retained.", ContextBuilder.Active(s)!.Summary);
        Assert.False(builder.RequiresExplicitRetry(s));
    }
    [Fact]
    public async Task ShorterSummaryRetriesRemainWithinEightRequestLimit()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Conversation(store);
        for (var n = 0; n < 12; n++) { s.History.Add(User(new string('x', 20000))); s.History.Add(new() { ["role"] = "assistant", ["content"] = "Evidence " + n }); }
        s.History.Add(User("Finish the task")); var original = ContextBuilder.Hash(s.History);
        var responses = Enumerable.Range(0, 8).Select(n => new[] { Summary(n % 2 == 0 ? new string('x', 8001) : "Observed evidence") }).ToArray();
        var model = new ScriptedModel(responses); var builder = new ContextBuilder(store, model, new CountingTools());
        var error = await Assert.ThrowsAsync<ContextBudgetException>(() => builder.PrepareAsync(s, new(), _ => { }, default));
        Assert.Contains("request limit", error.Message); Assert.Equal(8, model.Requests.Count);
        Assert.Null(s.ActiveContextId); Assert.Equal(original, ContextBuilder.Hash(store.Get(s.Id)!.History));
    }
    [Fact]
    public void ProtocolGroupsPreserveReasoningAllCallsAndAllResults()
    {
        JsonObject Item(string type, string? id = null) => new() { ["type"] = type, ["call_id"] = id };
        List<JsonObject> history = [User("request"), Item("reasoning"), Item("function_call", "a"), Item("function_call", "b"), Item("function_call_output", "b"), Item("function_call_output", "a"), Item("reasoning"), new() { ["role"] = "assistant", ["content"] = "done" }];
        Assert.Equal(new[] { (0, 1), (1, 6), (6, 8) }, ContextBuilder.Groups(history));
        history.RemoveAt(5); Assert.Throws<ContextBudgetException>(() => ContextBuilder.Groups(history));
    }
    [Fact]
    public void QueueClaimsExactlyOneRunAndKeepsIndependentEnqueuesAcrossSessionSaves()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Conversation(store);
        var first = store.Enqueue(s.Id, "first"); var second = store.Enqueue(s.Id, "second");
        store.Save(s, new("unrelated", s.Id, null, new { })); Assert.Equal(2, store.Queue(s.Id).Items.Count);
        var run = store.ClaimQueued(s)!; Assert.NotNull(run); Assert.Null(store.ClaimQueued(s)); Assert.Equal("first", s.History.Single()["content"]!.ToString());
        var saved = store.Get(s.Id)!; Assert.Single(saved.Runs); Assert.Equal(run.Id, store.Queue(s.Id).Items.Single(i => i.Id == first.Id).RunId);
        Assert.Null(store.ClaimQueued(saved)); run.State = RunState.Completed; store.Save(s, new("run.outcome", s.Id, run.Id, new { })); store.SettleQueue(s.Id, run);
        store.HoldQueue(s.Id, "Restart"); Assert.Null(store.ClaimQueued(s)); store.HoldQueue(s.Id, null);
        var next = store.ClaimQueued(s)!; Assert.NotEqual(run.Id, next.Id); Assert.Equal(next.Id, store.Queue(s.Id).Items.Single(i => i.Id == second.Id).RunId);
    }
    [Fact]
    public void QueueLimitsRemovalAndDecisionBarriers()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Conversation(store);
        Assert.Throws<UserFacingException>(() => store.Enqueue(s.Id, " ")); Assert.Throws<UserFacingException>(() => store.Enqueue(s.Id, new string('é', 17000)));
        for (var n = 0; n < 20; n++) store.Enqueue(s.Id, "item " + n);
        Assert.Throws<UserFacingException>(() => store.Enqueue(s.Id, "overflow"));
        s.Runs.Add(new() { State = RunState.AwaitingUser, Question = new() { Text = "Explicit answer?" } }); Assert.Null(store.ClaimQueued(s));
        Assert.Null(s.Runs.Last().Question!.Answer); store.RemoveQueued(s.Id, null); Assert.All(store.Queue(s.Id).Items, i => Assert.Equal("Removed", i.State));
    }
    [Fact]
    public void V3MigrationPreservesPermissionsAndCreatesEmptyQueue()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Conversation(store); s.Mode = PermissionMode.Edit; s.Workflow = WorkflowMode.Plan; store.Save(s, new("saved", s.Id, null, new { }));
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(dir.Path, "sessions.db")))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DROP TABLE worker_profiles; DROP TABLE workers; DROP TABLE context_snapshots; DROP TABLE prompt_queue; DROP TABLE queue_holds; DROP TABLE workflow_reports; DROP TABLE workflow_revisions; DROP TABLE steering; UPDATE schema_version SET version=3"; cmd.ExecuteNonQuery(); }
        var upgraded = new SessionStore(dir.Path); Assert.Empty(upgraded.Queue(s.Id).Items); Assert.Null(upgraded.Get(s.Id)!.ActiveContextId); Assert.Equal(PermissionMode.Edit, upgraded.Get(s.Id)!.Mode); Assert.Equal(WorkflowMode.Plan, upgraded.Get(s.Id)!.Workflow);
        Assert.NotEmpty(Directory.GetFiles(dir.Path, "sessions-v3-*.backup.db"));
    }
    [Fact]
    public void ActivityOmitsEnvironmentStdinAndSecretArguments()
    {
        var call = new ToolCall { Id = "c", Name = "process_start", Arguments = Json.Serialize(new { executable = "pwsh", arguments = new[] { "--token", "SUPERSECRET", "--password=hunter2" }, workingDirectory = ".", environment = new[] { new { name = "secret", value = "ENVSECRET" } } }) };
        var summary = Json.Serialize(Activity.Describe(new(), call)); Assert.DoesNotContain("SUPERSECRET", summary); Assert.DoesNotContain("hunter2", summary); Assert.DoesNotContain("ENVSECRET", summary);
        call.Name = "process_input"; call.Arguments = Json.Serialize(new { processId = "id", text = "STDINSECRET", closeStdin = false }); Assert.DoesNotContain("STDINSECRET", Json.Serialize(Activity.Describe(new(), call)));
    }
    [Fact]
    public void FailedQueueClaimRollsBackMessageRunAndReceipt()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Conversation(store); store.Enqueue(s.Id, "one");
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(dir.Path, "sessions.db")))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "CREATE TRIGGER fail_run BEFORE INSERT ON runs BEGIN SELECT RAISE(ABORT,'injected'); END"; cmd.ExecuteNonQuery(); }
        Assert.Throws<SqliteException>(() => store.ClaimQueued(s)); Assert.Empty(s.History); Assert.Empty(s.Runs);
        Assert.Equal("Pending", store.Queue(s.Id).Items.Single().State); Assert.Empty(store.Get(s.Id)!.Runs);
    }
    [Fact]
    public async Task FailedSnapshotCommitKeepsPreviousProjection()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Conversation(store); LongHistory(s);
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(dir.Path, "sessions.db")))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "CREATE TRIGGER fail_snapshot BEFORE INSERT ON context_snapshots BEGIN SELECT RAISE(ABORT,'injected'); END"; cmd.ExecuteNonQuery(); }
        var builder = new ContextBuilder(store, new ScriptedModel([Summary("Observed evidence")]), new CountingTools()) { Limit = 30000 };
        await Assert.ThrowsAsync<SqliteException>(() => builder.PrepareAsync(s, new(), _ => { }, default));
        Assert.Null(s.ActiveContextId); Assert.Empty(s.ContextSnapshots); Assert.Null(store.Get(s.Id)!.ActiveContextId);
    }
    [Fact]
    public void AbandonedQuestionsDoNotBecomeCurrentMandatoryContext()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Conversation(store);
        s.Runs.Add(new() { State = RunState.Cancelled, Question = new() { Text = "ABANDONED_QUESTION" } });
        s.Runs.Add(new() { State = RunState.AwaitingUser, Question = new() { Text = "CURRENT_QUESTION" } });
        var context = new ContextBuilder(store, new ScriptedModel(), new CountingTools()).Build(s);
        Assert.DoesNotContain("ABANDONED_QUESTION", context.Request.Instructions); Assert.Contains("CURRENT_QUESTION", context.Request.Instructions);
    }
}
