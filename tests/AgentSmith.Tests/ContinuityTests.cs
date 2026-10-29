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
        Assert.All(model.Requests, r => Assert.False(r.NativeWebSearch));
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
    public async Task OversizedProtocolGroupUsesEvidenceSlicesAndKeepsHistoryAndRecentGroupsIntact()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Conversation(store);
        s.History.Add(User("Keep this goal"));
        s.History.Add(new() { ["type"] = "reasoning", ["encrypted_content"] = "opaque" });
        s.History.Add(RuntimeTests.Call("big", "file_read", "{}"));
        s.History.Add(new() { ["type"] = "function_call_output", ["call_id"] = "big", ["output"] = new string('x', 90000) + "FINAL_EVIDENCE" });
        s.History.Add(RuntimeTests.Call("recent", "file_read", "{}"));
        s.History.Add(new() { ["type"] = "function_call_output", ["call_id"] = "recent", ["output"] = "recent evidence" });
        s.History.Add(RuntimeTests.Message("latest response"));
        var original = ContextBuilder.Hash(s.History);
        var model = new ScriptedModel(Enumerable.Range(0, 8).Select(_ => new[] { Summary("Preserved goal, evidence, and unfinished work.") }).ToArray());
        var builder = new ContextBuilder(store, model, new CountingTools());
        var context = await builder.PrepareAsync(s, new(), _ => { }, default, true);
        Assert.InRange(model.Requests.Count, 3, 8);
        Assert.All(model.Requests, r => { Assert.Empty(r.Tools); Assert.True(builder.Size(r.Instructions, r.Input, r.Tools) <= 48000); });
        Assert.Contains(model.Requests, r => r.Input[0]["content"]!.ToString().Contains("FINAL_EVIDENCE"));
        var snapshot = ContextBuilder.Active(store.Get(s.Id)!)!;
        Assert.Equal(4, snapshot.Through); Assert.Equal(2, snapshot.FormatVersion);
        Assert.Equal(model.Requests.Count, snapshot.EvidenceSlices.Count);
        Assert.Equal(0, snapshot.EvidenceSlices[0].Offset);
        for (var i = 1; i < snapshot.EvidenceSlices.Count; i++)
            Assert.Equal(snapshot.EvidenceSlices[i - 1].Offset + snapshot.EvidenceSlices[i - 1].Length, snapshot.EvidenceSlices[i].Offset);
        Assert.Single(snapshot.EvidenceSlices.Select(p => p.Hash).Distinct());
        Assert.Equal(original, ContextBuilder.Hash(store.Get(s.Id)!.History));
        Assert.Contains(context.Request.Input, i => i["call_id"]?.ToString() == "recent" && i["type"]?.ToString() == "function_call_output");
    }

    [Fact]
    public async Task InterruptedEvidenceSliceDoesNotActivatePartialSnapshot()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Conversation(store);
        s.History.Add(User("goal")); s.History.Add(RuntimeTests.Message(new string('x', 90000)));
        for (var i = 0; i < 2; i++) { s.History.Add(User("next " + i)); s.History.Add(RuntimeTests.Message("recent")); }
        var original = ContextBuilder.Hash(s.History);
        var model = new ScriptedModel([Summary("First slice")], [new("text", "interrupted")]);
        await Assert.ThrowsAsync<ContextBudgetException>(() => new ContextBuilder(store, model, new CountingTools()).PrepareAsync(s, new(), _ => { }, default, true));
        Assert.Null(store.Get(s.Id)!.ActiveContextId); Assert.Equal(original, ContextBuilder.Hash(s.History)); Assert.Equal(2, model.Requests.Count);
    }

    [Fact]
    public async Task EvidenceSlicesShrinkWhenProviderCountExceedsEstimate()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Conversation(store);
        s.Connection.Capabilities.ContextTokens = 200000;
        s.History.Add(User("goal")); s.History.Add(RuntimeTests.Message(new string('x', 80000)));
        for (var i = 0; i < 2; i++) { s.History.Add(User("next " + i)); s.History.Add(RuntimeTests.Message("recent")); }
        var model = new ExactCountingModel();
        await new ContextBuilder(store, model, new CountingTools()).PrepareAsync(s, new(), _ => { }, default, true);
        Assert.NotNull(s.ActiveContextId); Assert.InRange(model.Requests.Count, 3, 8);
        Assert.All(model.Requests, r => Assert.True(ExactCountingModel.Count(r) <= 32000));
    }

    private sealed class ExactCountingModel : IModelClient
    {
        public List<ModelRequest> Requests { get; } = [];
        public static int Count(ModelRequest r) => r.Instructions.Length + r.Input.Sum(i => i.ToJsonString().Length);
        public Task<TokenMeasurement?> CountTokensAsync(ModelRequest r, CancellationToken ct) => Task.FromResult<TokenMeasurement?>(new(Count(r), "test count", true));
        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest r, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Requests.Add(r); await Task.Yield(); yield return Summary("Observed evidence; unfinished task."); }
    }

    [Fact]
    public async Task OneLargeCompletedToolGroupCanCompactWithoutLosingTaskOrSourceHistory()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Conversation(store);
        s.Connection.Capabilities.ContextTokens = 32768; s.Connection.MaxOutputTokens = 8192;
        s.History.Add(User("Keep this active task verbatim"));
        s.History.Add(RuntimeTests.Call("only", "file_read", "{}"));
        s.History.Add(new() { ["type"] = "function_call_output", ["call_id"] = "only", ["output"] = new string('x', 24000) });
        var original = ContextBuilder.Hash(s.History); var model = new ExactCountingModel();
        var builder = new ContextBuilder(store, model, new CountingTools());
        var run = new Run { PromptHistoryIndex = 0, ContextManifest = builder.Build(s).Manifest };
        var observedCount = false;
        var context = await builder.PrepareAsync(s, run, e =>
        {
            if (e.Type != "context.compaction.started") return;
            var manifest = JsonNode.Parse(run.ContextManifest!)!;
            Assert.True(manifest["measurement"]!["exact"]!.GetValue<bool>());
            Assert.Equal("test count", manifest["measurement"]!["method"]!.ToString());
            observedCount = true;
        }, default);
        Assert.True(observedCount);
        Assert.NotNull(s.ActiveContextId); Assert.Equal(3, ContextBuilder.Active(s)!.Through);
        Assert.Equal(original, ContextBuilder.Hash(store.Get(s.Id)!.History));
        Assert.Contains(context.Request.Input, i => i["content"]?.ToString() == "Keep this active task verbatim");
        Assert.DoesNotContain(context.Request.Input, i => i["type"]?.ToString() is "function_call" or "function_call_output");
        Assert.True(ExactCountingModel.Count(context.Request) < 20643);
    }

    private sealed class PaddedTools : IToolDispatcher
    {
        public IReadOnlyList<ToolDefinition> Definitions => [];
        public string Instructions(string workspace) => new string('i', 12000);
        public Task<ToolOutput> ExecuteAsync(string workspace, ToolCall call, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task FixedOverheadAbovePreferredTargetDoesNotRejectUsableShortConversationSummary()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Conversation(store);
        s.Connection.Capabilities.ContextTokens = 32768; s.Connection.MaxOutputTokens = 8192;
        s.History.Add(User("Preserve the task"));
        for (var i = 0; i < 2; i++)
        {
            s.History.Add(RuntimeTests.Call("read" + i, "file_read", "{}"));
            s.History.Add(new() { ["type"] = "function_call_output", ["call_id"] = "read" + i, ["output"] = new string('x', 5000) });
        }
        var builder = new ContextBuilder(store, new ExactCountingModel(), new PaddedTools());
        var context = await builder.PrepareAsync(s, new(), _ => { }, default);
        Assert.NotNull(s.ActiveContextId);
        Assert.InRange(ExactCountingModel.Count(context.Request), 13763, 20642);
        Assert.Equal(5, s.History.Count);
        Assert.Contains(context.Request.Input, i => i["content"]?.ToString() == "Preserve the task");
    }

    [Fact]
    public async Task ExactCountBelowTriggerDoesNotCompactDespiteLargeByteEstimate()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Conversation(store);
        s.Connection.Capabilities.ContextTokens = 32768; s.Connection.MaxOutputTokens = 8192;
        s.History.Add(User("small task"));
        var model = new SmallCountingModel();
        var builder = new ContextBuilder(store, model, new CountingTools());
        var context = await builder.PrepareAsync(s, new(), _ => { }, default);
        Assert.Null(s.ActiveContextId); Assert.Empty(s.CompactionAttempts);
        Assert.Contains("server count", context.Manifest);
    }

    private sealed class SmallCountingModel : IModelClient
    {
        public Task<TokenMeasurement?> CountTokensAsync(ModelRequest request, CancellationToken ct) => Task.FromResult<TokenMeasurement?>(new(9500, "server count", true));
        public IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken ct) => throw new InvalidOperationException("No compaction should run.");
    }

    private sealed class UnavailableCountingModel : IModelClient
    {
        public Task<TokenMeasurement?> CountTokensAsync(ModelRequest request, CancellationToken ct) => throw Failures.Reject("local_token_count_failed", "Tokenizer HTTP 503", "Restore the server.");
        public IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken ct) => throw new InvalidOperationException("Do not start compaction on a guessed count.");
    }

    [Fact]
    public async Task TokenizerOutagePausesBeforeEstimateDrivenCompactionAndRetainsHistory()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Conversation(store); LongHistory(s);
        s.Connection.Capabilities.ContextTokens = 32768; s.Connection.MaxOutputTokens = 8192;
        var original = ContextBuilder.Hash(s.History);
        var builder = new ContextBuilder(store, new UnavailableCountingModel(), new CountingTools());
        var error = await Assert.ThrowsAsync<ContextBudgetException>(() => builder.PrepareAsync(s, new(), _ => { }, default));
        Assert.Equal(PauseReason.Network, error.Reason); Assert.Contains("HTTP 503", error.Message);
        Assert.Empty(s.CompactionAttempts); Assert.Equal(original, ContextBuilder.Hash(s.History));
    }

    private sealed class FailedSummaryModel : IModelClient
    {
        public IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken ct) => throw new UserFacingException("Local response exhausted its output budget.");
    }

    [Fact]
    public async Task SummaryFailurePreservesTheActualProviderReason()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Conversation(store); LongHistory(s);
        var builder = new ContextBuilder(store, new FailedSummaryModel(), new CountingTools()) { Limit = 30000 };
        var error = await Assert.ThrowsAsync<ContextBudgetException>(() => builder.PrepareAsync(s, new(), _ => { }, default));
        Assert.Equal("Local response exhausted its output budget.", error.Message);
        Assert.Equal(error.Message, Assert.Single(s.CompactionAttempts).Reason);
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
