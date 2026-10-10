using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Microsoft.Data.Sqlite;
using Xunit;
using static AgentSmith.Tests.RuntimeTests;

namespace AgentSmith.Tests;

public sealed class SteeringTests
{
    private sealed class Model(Func<int, ModelRequest, ModelEvent[]> reply) : IModelClient
    {
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
        { Requests.Add(request); foreach (var ev in reply(Requests.Count, request)) { ct.ThrowIfCancellationRequested(); yield return ev; await Task.Yield(); } }
    }
    private static Session Session(TestDirectory dir) => new() { Workspace = dir.Path, Runs = [new() { State = RunState.Running }] };
    private static void Save(SessionStore store, Session s) => store.Save(s, new("test", s.Id, s.Runs.LastOrDefault()?.Id, new { }));

    [Fact]
    public async Task InferenceSteeringSkipsProposedCallsAndStaysInSameRun()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); using var tools = new ToolRegistry();
        var s = new Session { Workspace = dir.Path }; var events = new List<string>();
        var model = new Model((round, _) =>
        {
            if (round == 1)
            {
                store.SendSteering(s.Id, s.Id, "Only inspect; do not create files.");
                return [Done(Call("edit", "file_create", "{\"path\":\"never.txt\",\"content\":\"bad\"}"), Call("read", "workspace_list", "{\"path\":\".\"}"))];
            }
            return [Done(Message("Understood"))];
        });
        var run = await new AgentRunner(store, model, tools).RunAsync(s, "task", null, e => events.Add(e.Type), default);
        Assert.Equal(RunState.Completed, run.State); Assert.Equal(2, run.BatchRequests); Assert.Single(s.Runs);
        Assert.False(File.Exists(Path.Combine(dir.Path, "never.txt")));
        Assert.All(run.Calls, c => Assert.Contains("Skipped due to steering", c.Result));
        Assert.Contains("Only inspect", Json.Serialize(model.Requests[1].Input));
        var group = model.Requests[1].Input;
        var steeringIndex = group.ToList().FindIndex(i => i["content"]?.ToString().Contains("Direct user steering", StringComparison.Ordinal) == true);
        Assert.True(steeringIndex > group.ToList().FindLastIndex(i => i["type"]?.ToString() == "function_call_output"));
        Assert.Equal("Sent", Assert.Single(store.Steering(s.Id)).State);
        Assert.Contains("steering.incorporated", events); Assert.Contains("steering.sent", events);
    }
    [Theory]
    [InlineData("tool.completed", true)]
    [InlineData("tool.started", true)]
    [InlineData("tool.prepared", false)]
    public async Task BoundarySteeringKeepsFinishedEditsButSkipsUnstartedMutations(string boundary, bool firstApplied)
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); using var tools = new ToolRegistry();
        File.WriteAllText(Path.Combine(dir.Path, "unrelated.txt"), "preserve");
        var s = new Session { Workspace = dir.Path }; var sent = false;
        var model = new ScriptedModel([Done(Call("one", "file_create", "{\"path\":\"one.txt\",\"content\":\"one\"}"), Call("two", "file_create", "{\"path\":\"two.txt\",\"content\":\"two\"}"))], [Done(Message())]);
        var run = await new AgentRunner(store, model, tools).RunAsync(s, "create", null, e =>
        { if (!sent && e.Type == boundary) { sent = true; store.SendSteering(s.Id, s.Id, "Stop creating files."); } }, default);
        Assert.Equal(RunState.Completed, run.State); Assert.Equal(firstApplied, File.Exists(Path.Combine(dir.Path, "one.txt")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "two.txt"))); Assert.Equal("preserve", File.ReadAllText(Path.Combine(dir.Path, "unrelated.txt")));
        Assert.DoesNotContain(s.Mutations, m => m.State == "Prepared");
    }
    [Fact]
    public async Task PreparedProcessSkippedBySteeringNeverLaunchesOrBecomesInterrupted()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); using var tools = new ToolRegistry();
        var s = new Session { Workspace = dir.Path };
        var args = Json.Serialize(new { executable = "pwsh", arguments = new[] { "-NoProfile", "-Command", "Set-Content must-not-exist.txt launched" }, workingDirectory = ".", environment = Array.Empty<object>(), timeoutSeconds = 30 });
        var model = new ScriptedModel([Done(Call("launch", "process_start", args))], [Done(Message())]);
        await new AgentRunner(store, model, tools).RunAsync(s, "task", null, e =>
        { if (e.Type == "tool.prepared") store.SendSteering(s.Id, s.Id, "Do not launch."); }, default);
        Assert.False(File.Exists(Path.Combine(dir.Path, "must-not-exist.txt")));
        Assert.Equal("NotStarted", Assert.Single(s.Processes).State);
        var saved = store.Get(s.Id)!; tools.Processes.Recover(saved);
        Assert.Equal("NotStarted", Assert.Single(saved.Processes).State);
        Assert.Null(saved.Processes[0].ExitCode);
    }
    [Fact]
    public async Task SteeringAcceptedDuringQuestionCallDoesNotAnswerQuestion()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = new Session { Workspace = dir.Path };
        using var tools = new ToolRegistry();
        var model = new ScriptedModel([Done(Call("ask", "user_ask", "{\"question\":\"Which option?\",\"choices\":[\"A\",\"B\"]}"))]);
        var run = await new AgentRunner(store, model, tools).RunAsync(s, "task", null, e =>
        { if (e.Type == "tool.started") store.SendSteering(s.Id, s.Id, "A"); }, default);
        Assert.Equal(RunState.AwaitingUser, run.State); Assert.Null(run.Question!.Answer);
        Assert.Equal("Pending", Assert.Single(store.Steering(s.Id)).State);
        Assert.Empty(store.IncorporateSteering(s, run));
    }
    [Fact]
    public void InboxOrderingLimitsRemovalAndTerminalTransitionAreAtomic()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Session(dir); Save(store, s); var run = s.Runs[0];
        for (var i = 0; i < 20; i++) store.SendSteering(s.Id, s.Id, "message " + i);
        Assert.Throws<UserFacingException>(() => store.SendSteering(s.Id, s.Id, "full"));
        Assert.Throws<UserFacingException>(() => store.SendSteering(s.Id, s.Id, " "));
        Assert.Throws<UserFacingException>(() => store.SendSteering(s.Id, s.Id, new string('é', 16385)));
        var first = store.Steering(s.Id)[0]; store.RemoveSteering(s.Id, first.Id);
        Assert.False(store.TryCompleteSteeredRun(s, run));
        var items = store.IncorporateSteering(s, run); Assert.Equal(19, items.Count);
        Assert.Equal("message 1", items[0].Text); Assert.Empty(store.IncorporateSteering(s, run));
        Assert.Throws<UserFacingException>(() => store.RemoveSteering(s.Id, items[0].Id));
        Assert.True(store.TryCompleteSteeredRun(s, run));
        Assert.Throws<UserFacingException>(() => store.SendSteering(s.Id, s.Id, "too late"));
        var reopened = new SessionStore(dir.Path); Assert.Equal(19, reopened.Get(s.Id)!.History.Count);
    }
    [Fact]
    public async Task CompletionRaceReconsidersInsteadOfLosingAcceptedMessage()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); using var tools = new ToolRegistry();
        var s = new Session { Workspace = dir.Path }; var once = false;
        var model = new ScriptedModel([Done(Message("old answer"))], [Done(Message("new answer"))]);
        var run = await new AgentRunner(store, model, tools).RunAsync(s, "task", null, e =>
        { if (e.Type == "model.completed" && !once) { once = true; store.SendSteering(s.Id, s.Id, "Please reconsider."); } }, default);
        Assert.Equal(RunState.Completed, run.State); Assert.Equal(2, model.Requests.Count);
    }
    [Fact]
    public void RestartHoldsPendingMessagesAndDoesNotInjectIntoAnotherRun()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Session(dir); Save(store, s);
        store.SendSteering(s.Id, s.Id, "pending"); s.Runs[0].State = RunState.Paused; Save(store, s);
        var reopened = new SessionStore(dir.Path); var saved = reopened.Get(s.Id)!;
        Assert.Empty(reopened.IncorporateSteering(saved, saved.Runs[0])); Assert.Equal("Pending", Assert.Single(reopened.Steering(s.Id)).State);
        AgentRunner.Abandon(saved, reopened); saved.Runs.Add(new() { State = RunState.Running }); Save(reopened, saved);
        Assert.Empty(reopened.IncorporateSteering(saved, saved.Runs.Last())); Assert.Empty(saved.History);
    }
    [Theory]
    [InlineData(RunState.AwaitingUser)]
    [InlineData(RunState.AwaitingPlan)]
    [InlineData(RunState.Completed)]
    [InlineData(RunState.Cancelled)]
    [InlineData(RunState.Failed)]
    public void DecisionsAndTerminalRunsRejectSteering(RunState state)
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Session(dir); s.Runs[0].State = state; Save(store, s);
        Assert.Throws<UserFacingException>(() => store.SendSteering(s.Id, s.Id, "yes")); Assert.Empty(store.Steering(s.Id));
    }
    [Fact]
    public void TransactionFailureLeavesHistoryAndInboxUnchanged()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Session(dir); Save(store, s); store.SendSteering(s.Id, s.Id, "keep me");
        using var db = new SqliteConnection("Data Source=" + Path.Combine(dir.Path, "sessions.db")); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = "CREATE TRIGGER fail_steering BEFORE INSERT ON events WHEN NEW.type='steering.incorporated' BEGIN SELECT RAISE(ABORT,'injected'); END;"; cmd.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => store.IncorporateSteering(s, s.Runs[0]));
        Assert.Empty(s.History); Assert.Empty(store.Get(s.Id)!.History); Assert.Equal("Pending", Assert.Single(store.Steering(s.Id)).State);
        cmd.CommandText = "DROP TRIGGER fail_steering"; cmd.ExecuteNonQuery();
        Assert.Single(store.IncorporateSteering(s, s.Runs[0])); Assert.Single(s.History);
    }
    [Fact]
    public void MigrationPreservesSavedRunAndCreatesConsistentBackup()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Session(dir); s.Mode = PermissionMode.Inspect; Save(store, s);
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(dir.Path, "sessions.db")))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DROP TABLE steering; UPDATE schema_version SET version=8"; cmd.ExecuteNonQuery(); }
        var migrated = new SessionStore(dir.Path); Assert.Empty(migrated.Steering(s.Id)); Assert.Equal(PermissionMode.Inspect, migrated.Get(s.Id)!.Mode);
        Assert.Equal(s.Runs[0].Id, migrated.Get(s.Id)!.Runs[0].Id); Assert.NotEmpty(Directory.GetFiles(dir.Path, "sessions-v8-*.backup.db"));
    }
    [Fact]
    public void CompactionProjectionPreservesOriginalTaskAndSteeringVerbatim()
    {
        var s = new Session { History = [new JsonObject { ["role"] = "user", ["content"] = "Original scope" }, Message("past"), new JsonObject { ["role"] = "user", ["content"] = "New correction" }, Message("recent")], Runs = [new() { PromptHistoryIndex = 0, SteeringHistoryIndices = [2] }] };
        var snapshot = new ContextSnapshot { Through = 3, SourceHash = ContextBuilder.Hash(s.History.Take(3)), Summary = "Older work" };
        var input = ContextBuilder.Project(s, snapshot);
        Assert.Single(input, i => i["content"]?.ToString() == "Original scope");
        Assert.Single(input, i => i["content"]?.ToString() == "New correction");
        Assert.Equal(4, s.History.Count);
    }
    [Fact]
    public async Task RequestLimitKeepsIncorporatedSteeringForExplicitResumeWithoutDuplication()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = new Session { Workspace = dir.Path }; var tools = new CountingTools();
        var first = new Model((_, _) => { store.SendSteering(s.Id, s.Id, "Resume with this correction"); return [Done(Message())]; });
        var paused = await new AgentRunner(store, first, tools) { MaxSteps = 1 }.RunAsync(s, "task", null, _ => { }, default);
        Assert.Equal(RunState.Paused, paused.State); Assert.Equal("Incorporated", Assert.Single(store.Steering(s.Id)).State);
        var saved = new SessionStore(dir.Path).Get(s.Id)!; var next = new ScriptedModel([Done(Message())]);
        await new AgentRunner(store, next, tools).RunAsync(saved, null, null, _ => { }, default);
        Assert.Single(saved.History, h => h["content"]?.ToString().Contains("Direct user steering", StringComparison.Ordinal) == true);
        Assert.Equal("Sent", Assert.Single(store.Steering(s.Id)).State);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SteeringDuringCompactionIsRetainedAndRebuildsBeforeInference(bool failSummary)
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = new Session { Workspace = dir.Path };
        for (var i = 0; i < 6; i++)
        { s.History.Add(new JsonObject { ["role"] = "user", ["content"] = new string('x', 5000) }); s.History.Add(Message("Old response")); }
        var queued = false;
        var model = new Model((_, request) =>
        {
            if (request.Tools.Count == 0)
            {
                if (!queued) { queued = true; store.SendSteering(s.Id, s.Id, "Preserve the existing input controls."); }
                return failSummary ? [new("text", "interrupted summary")] : [Done(Message("Historical evidence summary, original source retained."))];
            }
            return [Done(Message("Done"))];
        });
        var run = await new AgentRunner(store, model, new CountingTools()) { MaxContextCharacters = 30000 }.RunAsync(s, "current task", null, _ => { }, default);
        Assert.True(queued);
        if (failSummary) { Assert.Equal(RunState.Paused, run.State); Assert.Equal("Pending", Assert.Single(store.Steering(s.Id)).State); }
        else
        {
            Assert.Equal(RunState.Completed, run.State);
            Assert.Contains("Preserve the existing input controls", Json.Serialize(model.Requests.Last().Input));
            Assert.Equal("Sent", Assert.Single(store.Steering(s.Id)).State);
        }
    }
    [Fact]
    public async Task ConcurrentSubmissionAndCompletionNeverLoseAnAcceptedMessage()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path);
        for (var i = 0; i < 12; i++)
        {
            var s = Session(dir); Save(store, s);
            var send = Task.Run(() => { try { return store.SendSteering(s.Id, s.Id, "race"); } catch (UserFacingException) { return null; } });
            var close = Task.Run(() => store.TryCompleteSteeredRun(s, s.Runs[0]));
            await Task.WhenAll(send, close);
            var sent = await send; var closed = await close;
            if (sent is not null) { Assert.False(closed); Assert.True(store.HasPendingSteering(s.Id, s.Runs[0].Id)); }
            else { Assert.True(closed); Assert.Empty(store.Steering(s.Id)); }
        }
    }
    [Fact]
    public async Task ProcessWaitWakesWithoutStoppingProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); using var tools = new ToolRegistry(); var ctx = ProcessTests.Context(dir.Path);
        var process = await ProcessTests.Invoke(tools, ctx, "process_start", new { executable = "pwsh", arguments = new[] { "-NoProfile", "-Command", "Start-Sleep 60" }, workingDirectory = ".", environment = Array.Empty<object>(), timeoutSeconds = 90 });
        var wait = ProcessTests.Invoke(tools, ctx, "process_wait", new { processId = process["processId"]!.ToString(), cursor = 0, waitMilliseconds = 60000, until = "exit" });
        ((ISteeringStore)ctx.Store).SendSteering(ctx.Session.Id, ctx.Session.Id, "Stop waiting; inspect first.");
        var result = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("steering", result["interruptedBy"]!.ToString()); Assert.Equal("Running", result["state"]!.ToString());
        await ProcessTests.Invoke(tools, ctx, "process_stop", new { processId = process["processId"]!.ToString() });
    }
}
