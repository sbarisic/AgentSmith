using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AgentSmith.Cli;
using AgentSmith.Core;
using AgentSmith.Runtime;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class ContinuousChatTests
{
    [Fact]
    public async Task EmptySavedPlanContinuationStaysOpenUntilExplicitExit()
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-pending-exit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = new Input(); using var app = new CliApp(root, true, true, input);
            var session = new Session { Workspace = root, PendingPlanPrompt = "saved continuation" };
            app.Store.Save(session, new("created", session.Id, null, new { }));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var chat = app.Chat(session, null, true, timeout.Token);
            input.Lines.Writer.TryWrite(""); input.Lines.Writer.TryWrite("leave pending");
            await Task.Delay(150); Assert.False(chat.IsCompleted);
            input.Lines.Writer.TryWrite("/exit"); Assert.Equal(3, await chat);
            Assert.Equal("saved continuation", app.Store.Get(session.Id)!.PendingPlanPrompt);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
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
    public async Task SteeringRedirectsCurrentRunWhileNormalBusyTextStaysQueued()
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-steering-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = new Input(); var model = new Model(); using var app = new CliApp(root, true, true, input) { ModelOverride = model };
            var s = new Session { Workspace = root }; app.Store.Save(s, new("created", s.Id, null, new { }));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var chat = app.Chat(s, "first", true, timeout.Token);
            await model.Requests.Reader.ReadAsync(timeout.Token);
            input.Lines.Writer.TryWrite("queued follow-up"); input.Lines.Writer.TryWrite("/steer Correct the active task");
            await Until(() => app.Store.Steering(s.Id).Count == 1);
            Assert.Single(app.Store.Queue(s.Id).Items); Assert.Single(app.Store.Get(s.Id)!.Runs);
            model.Replies.Writer.TryWrite([Message("old completion")]);
            var steered = await model.Requests.Reader.ReadAsync(timeout.Token);
            Assert.Contains("Correct the active task", Json.Serialize(steered.Input));
            Assert.DoesNotContain("queued follow-up", Json.Serialize(steered.Input));
            model.Replies.Writer.TryWrite([Message("revised completion")]);
            var queued = await model.Requests.Reader.ReadAsync(timeout.Token);
            Assert.Contains("queued follow-up", Json.Serialize(queued.Input));
            Assert.Equal(2, app.Store.Get(s.Id)!.Runs.Count);
            input.Lines.Writer.TryWrite("/cancel"); await Until(() => app.Store.Get(s.Id)!.Runs.Last().State == RunState.Cancelled);
            input.Lines.Writer.TryWrite("/exit"); await chat.WaitAsync(timeout.Token);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task UnifiedModeStatusIsSafeWhileBusyAndChangesStayBlocked()
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-busy-mode-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = new Input(); var model = new Model(); using var app = new CliApp(root, true, true, input) { ModelOverride = model };
            var s = new Session { Workspace = root }; app.Store.Save(s, new("created", s.Id, null, new { }));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var chat = app.Chat(s, "first", true, timeout.Token);
            await model.Requests.Reader.ReadAsync(timeout.Token);
            foreach (var command in new[] { "/mode", "/help all", "/worker-limits", "/worker-limits 8 2", "/worker-budget", "/worker-budget 200 45", "/mode plan", "/mode document", "/mode edit", "/mode plan Another task", "/plan Another task", "/plans", "/plan show", "/plan read missing" }) input.Lines.Writer.TryWrite(command);
            input.Lines.Writer.TryWrite("queue barrier"); await Until(() => app.Store.Queue(s.Id).Items.Count == 1);
            var saved = app.Store.Get(s.Id)!;
            Assert.Equal(WorkflowMode.Work, saved.Workflow); Assert.Equal(PermissionMode.Execute, saved.Mode);
            Assert.Equal(2, saved.WorkerPolicy.MaxConcurrent);
            Assert.Equal(100, saved.WorkerPolicy.MaxRequests);
            Assert.Equal(30, saved.WorkerPolicy.MaxMinutes);
            Assert.Single(saved.Runs); Assert.Empty(saved.Reports); Assert.Empty(saved.Plans);
            input.Lines.Writer.TryWrite("/exit"); await chat;
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task MemoryReadsWorkWhileBusyButUserWritesAreRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-busy-memory-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = new Input(); var model = new Model(); using var app = new CliApp(root, true, true, input) { ModelOverride = model };
            var s = new Session { Workspace = root }; app.Store.Save(s, new("created", s.Id, null, new { }));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var chat = app.Chat(s, "first", true, timeout.Token);
            await model.Requests.Reader.ReadAsync(timeout.Token);
            input.Lines.Writer.TryWrite("/memory add Should not be saved while busy"); input.Lines.Writer.TryWrite("/memory off"); input.Lines.Writer.TryWrite("/memory list");
            input.Lines.Writer.TryWrite("queued barrier"); await Until(() => app.Store.Queue(s.Id).Items.Count == 1);
            Assert.Empty(app.Store.ListMemories()); Assert.True(app.Store.MemoryEnabled);
            input.Lines.Writer.TryWrite("/exit"); await chat;
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task ContinueAfterFailedCompactionDoesNotRestartRunOrReleaseQueue()
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-paused-context-" + Guid.NewGuid().ToString("N"));
        try
        {
            var model = new Model(); using var app = new CliApp(root, true, true, new StringReader("")) { ModelOverride = model };
            var s = new Session { Workspace = root };
            s.History.Add(new() { ["role"] = "user", ["content"] = new string('x', 120000) });
            var run = new Run { State = RunState.Paused, Reason = "Compaction summary exceeded 8000 characters." }; s.Runs.Add(run);
            s.CompactionAttempts.Add(new() { BoundaryHash = ContextBuilder.Hash(s.History), State = "Failed" });
            app.Store.Save(s, new("created", s.Id, run.Id, new { }));
            app.Store.Enqueue(s.Id, "follow-up"); app.Store.HoldQueue(s.Id, "Paused");
            for (var n = 0; n < 2; n++) Assert.Same(run, await app.Run(s, null, null, default));
            Assert.False(model.Requests.Reader.TryRead(out _));
            Assert.Single(app.Store.Get(s.Id)!.Runs); Assert.Equal(RunState.Paused, app.Store.Get(s.Id)!.Runs[0].State);
            Assert.Equal("Paused", app.Store.Queue(s.Id).HoldReason); Assert.Equal("Pending", app.Store.Queue(s.Id).Items.Single().State);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
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
            try
            {
            await model.Requests.Reader.ReadAsync(timeout.Token); input.Lines.Writer.TryWrite("second"); input.Lines.Writer.TryWrite("/cancel");
            await Until(() => app.Store.Get(s.Id)!.Runs.Last().State == RunState.Cancelled);
            Assert.NotNull(app.Store.Queue(s.Id).HoldReason); Assert.False(model.Requests.Reader.TryRead(out _)); Assert.Equal("Pending", app.Store.Queue(s.Id).Items.Single().State);
            input.Lines.Writer.TryWrite("/queue continue"); await model.Requests.Reader.ReadAsync(timeout.Token); model.Replies.Writer.TryWrite([Message("done")]);
            await Until(() => app.Store.Queue(s.Id).Items.Single().State == "Completed"); input.Lines.Writer.TryWrite("/exit"); await chat;
            }
            finally
            {
                // An assertion failure must not leave the input pump accessing the database during cleanup.
                timeout.Cancel();
                try { await chat; } catch (OperationCanceledException) { }
            }
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
