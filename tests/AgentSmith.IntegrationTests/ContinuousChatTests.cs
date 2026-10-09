using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AgentSmith.Cli;
using AgentSmith.Core;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class ContinuousChatTests
{
    private sealed class Input(bool synchronouslyBlocking = false) : TextReader
    {
        public readonly Channel<string?> Lines = Channel.CreateUnbounded<string?>();
        public override Task<string?> ReadLineAsync() => synchronouslyBlocking ? Task.FromResult(Lines.Reader.ReadAsync().AsTask().GetAwaiter().GetResult()) : Lines.Reader.ReadAsync().AsTask();
    }
    private sealed class Model : IModelClient
    {
        public readonly Channel<ModelRequest> Requests = Channel.CreateUnbounded<ModelRequest>();
        public readonly Channel<JsonObject[]> Replies = Channel.CreateUnbounded<JsonObject[]>();
        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
        { await Requests.Writer.WriteAsync(request, ct); var output = await Replies.Reader.ReadAsync(ct); yield return new("completed", Output: output); }
    }
    private static JsonObject Message(string text) => new() { ["role"] = "assistant", ["content"] = text };
    private static async Task Until(Func<bool> predicate)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); while (!predicate()) await Task.Delay(25, timeout.Token); }
    [Fact]
    public async Task FifoRunsStartOnCompletionAndInputDoesNotSteerActiveRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-queue-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = new Input(); var model = new Model(); using var app = new CliApp(root, true, true, input) { ModelOverride = model };
            var s = new Session { Workspace = root }; app.Store.Save(s, new("created", s.Id, null, new { }));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var chat = app.Chat(s, "first", true, timeout.Token);
            var first = await model.Requests.Reader.ReadAsync(timeout.Token);
            input.Lines.Writer.TryWrite("second"); input.Lines.Writer.TryWrite("third");
            await Until(() => app.Store.Queue(s.Id).Items.Count == 2);
            Assert.DoesNotContain(first.Input, i => i.ToJsonString().Contains("second"));
            model.Replies.Writer.TryWrite([Message("first done")]);
            var second = await model.Requests.Reader.ReadAsync(timeout.Token); Assert.Equal("second", second.Input.Last()["content"]!.ToString());
            model.Replies.Writer.TryWrite([Message("second done")]);
            var third = await model.Requests.Reader.ReadAsync(timeout.Token); Assert.Equal("third", third.Input.Last()["content"]!.ToString());
            model.Replies.Writer.TryWrite([Message("third done")]);
            await Until(() => app.Store.Queue(s.Id).Items.All(i => i.State == "Completed")); input.Lines.Writer.TryWrite("/exit");
            Assert.Equal(0, await chat); Assert.Equal(3, app.Store.Get(s.Id)!.Runs.Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task CancellationHoldsQueueUntilExplicitContinue()
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-cancel-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = new Input(); var model = new Model(); using var app = new CliApp(root, true, true, input) { ModelOverride = model };
            var s = new Session { Workspace = root }; app.Store.Save(s, new("created", s.Id, null, new { }));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var chat = app.Chat(s, "first", true, timeout.Token);
            await model.Requests.Reader.ReadAsync(timeout.Token); input.Lines.Writer.TryWrite("second"); input.Lines.Writer.TryWrite("/cancel");
            await Until(() => app.Store.Get(s.Id)!.Runs.Last().State == RunState.Cancelled);
            Assert.NotNull(app.Store.Queue(s.Id).HoldReason); Assert.False(model.Requests.Reader.TryRead(out _)); Assert.Equal("Pending", app.Store.Queue(s.Id).Items.Single().State);
            input.Lines.Writer.TryWrite("/queue continue"); await model.Requests.Reader.ReadAsync(timeout.Token); model.Replies.Writer.TryWrite([Message("done")]);
            await Until(() => app.Store.Queue(s.Id).Items.Single().State == "Completed"); input.Lines.Writer.TryWrite("/exit"); await chat;
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task QueuedTextNeverAnswersAQuestion()
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-question-queue-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = new Input(); var model = new Model(); using var app = new CliApp(root, true, true, input) { ModelOverride = model };
            var s = new Session { Workspace = root }; app.Store.Save(s, new("created", s.Id, null, new { }));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var chat = app.Chat(s, "first", true, timeout.Token);
            await model.Requests.Reader.ReadAsync(timeout.Token); input.Lines.Writer.TryWrite("queued follow-up"); await Until(() => app.Store.Queue(s.Id).Items.Count == 1);
            model.Replies.Writer.TryWrite([new() { ["type"] = "function_call", ["call_id"] = "q", ["name"] = "user_ask", ["arguments"] = Json.Serialize(new { question = "Choose?", choices = new[] { "one", "two" } }) }]);
            await Until(() => app.Store.Get(s.Id)!.Runs.Last().State == RunState.AwaitingUser);
            var question = app.Store.Get(s.Id)!.Runs.Last().Question!; Assert.Null(question.Answer); Assert.Equal("Pending", app.Store.Queue(s.Id).Items.Single().State);
            input.Lines.Writer.TryWrite("/answer " + question.Id + " one"); await model.Requests.Reader.ReadAsync(timeout.Token); model.Replies.Writer.TryWrite([Message("answered")]);
            await model.Requests.Reader.ReadAsync(timeout.Token); model.Replies.Writer.TryWrite([Message("follow-up done")]);
            await Until(() => app.Store.Queue(s.Id).Items.Single().State == "Completed"); input.Lines.Writer.TryWrite("/exit"); await chat;
            Assert.Equal("one", app.Store.Get(s.Id)!.Runs[0].Question!.Answer);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Fact]
    public void EscapePreservesUnsubmittedDraftAndNeverSubmitsIt()
    {
        var input = new ConsoleInput(false); input.Restore("next prompt");
        var escape = input.HandleKey(new('\u001b', ConsoleKey.Escape, false, false, false));
        Assert.True(escape.Cancel); Assert.False(escape.Ready); Assert.Equal("next prompt", input.Draft);
        var enter = input.HandleKey(new('\r', ConsoleKey.Enter, false, false, false)); Assert.Equal("next prompt", enter.Text);
    }
    [Fact]
    public async Task BlockingConsoleReaderCannotPreventQuestionTransition()
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-blocking-input-" + Guid.NewGuid().ToString("N"));
        var input = new Input(true);
        try
        {
            var model = new Model(); using var app = new CliApp(root, true, true, input) { ModelOverride = model };
            var s = new Session { Workspace = root }; app.Store.Save(s, new("created", s.Id, null, new { }));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var chat = Task.Run(() => app.Chat(s, "ask", true, timeout.Token));
            await model.Requests.Reader.ReadAsync(timeout.Token);
            model.Replies.Writer.TryWrite([new() { ["type"] = "function_call", ["call_id"] = "q", ["name"] = "user_ask", ["arguments"] = Json.Serialize(new { question = "Choose?", choices = new[] { "one", "two" } }) }]);
            await Until(() => app.Store.Queue(s.Id).HoldReason == "AwaitingUser");
            var question = app.Store.Get(s.Id)!.Runs.Last().Question!;
            input.Lines.Writer.TryWrite("one"); // Bare input is rejected after concurrent input begins.
            await Task.Delay(100); Assert.Null(app.Store.Get(s.Id)!.Runs.Last().Question!.Answer);
            input.Lines.Writer.TryWrite("/answer " + question.Id + " one");
            await model.Requests.Reader.ReadAsync(timeout.Token); model.Replies.Writer.TryWrite([Message("done")]);
            await Until(() => app.Store.Get(s.Id)!.Runs.Last().State == RunState.Completed); input.Lines.Writer.TryWrite("/exit"); await chat;
        }
        finally { input.Lines.Writer.TryWrite(null); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task ApprovedWorkflowStartsBeforeQueuedFollowUp()
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-review-queue-" + Guid.NewGuid().ToString("N"));
        var input = new Input();
        try
        {
            var model = new Model(); using var app = new CliApp(root, true, true, input) { ModelOverride = model };
            var plan = new WorkPlan { Revisions = [new() { Revision = 1, Goal = "Inspect notes", Steps = [new() { Id = "S1", Description = "Read notes", Verification = "Observe first line" }] }] };
            var s = new Session { Workspace = root, Workflow = WorkflowMode.Plan, ActivePlanId = plan.Id, Plans = [plan] }; app.Store.Save(s, new("created", s.Id, null, new { }));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var chat = app.Chat(s, "submit", true, timeout.Token);
            await model.Requests.Reader.ReadAsync(timeout.Token); input.Lines.Writer.TryWrite("queued follow-up"); await Until(() => app.Store.Queue(s.Id).Items.Count == 1);
            model.Replies.Writer.TryWrite([new() { ["type"] = "function_call", ["call_id"] = "review", ["name"] = "plan_submit", ["arguments"] = "{}" }]);
            await Until(() => app.Store.Queue(s.Id).HoldReason == "AwaitingPlan");
            var review = Planning.Active(app.Store.Get(s.Id)!)!.PendingReview!; Assert.Empty(plan.Approvals); Assert.False(model.Requests.Reader.TryRead(out _));
            input.Lines.Writer.TryWrite("/review " + review.Id + " implement edit");
            var implementation = await model.Requests.Reader.ReadAsync(timeout.Token);
            Assert.Contains("Implement approved plan", implementation.Input.Last()["content"]!.ToString());
            Assert.Single(app.Store.Queue(s.Id).Items); // The workflow prompt must not enter the user FIFO.
            model.Replies.Writer.TryWrite([Message("inspection done")]);
            var followUp = await model.Requests.Reader.ReadAsync(timeout.Token); Assert.Equal("queued follow-up", followUp.Input.Last()["content"]!.ToString());
            model.Replies.Writer.TryWrite([Message("follow-up done")]);
            await Until(() => app.Store.Queue(s.Id).Items.Single().State == "Completed"); input.Lines.Writer.TryWrite("/exit"); await chat;
            Assert.Equal(PermissionMode.Edit, app.Store.Get(s.Id)!.Mode); Assert.Single(Planning.Active(app.Store.Get(s.Id)!)!.Approvals);
        }
        finally { input.Lines.Writer.TryWrite(null); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
