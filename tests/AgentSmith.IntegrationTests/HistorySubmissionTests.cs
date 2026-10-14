using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AgentSmith.Cli;
using AgentSmith.Core;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class HistorySubmissionTests
{
    private sealed class Input : TextReader
    {
        internal readonly Channel<string?> Lines = Channel.CreateUnbounded<string?>();
        public override Task<string?> ReadLineAsync() => Lines.Reader.ReadAsync().AsTask();
    }
    private sealed class Model : IModelClient
    {
        internal readonly Channel<ModelRequest> Requests = Channel.CreateUnbounded<ModelRequest>();
        internal readonly Channel<JsonObject[]> Replies = Channel.CreateUnbounded<JsonObject[]>();
        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Requests.Writer.WriteAsync(request, ct);
            yield return new("completed", Output: await Replies.Reader.ReadAsync(ct));
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "smith-history-" + Guid.NewGuid().ToString("N"));
        internal readonly Input Reader = new();
        internal readonly Model Model = new();
        internal readonly CliApp App;
        internal readonly Session Session;
        internal ConsoleInput Composer => (ConsoleInput)typeof(CliApp).GetField("input", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(App)!;
        internal Fixture()
        {
            App = new(Root, true, true, Reader) { ModelOverride = Model };
            Session = new() { Workspace = Root };
            App.Store.Save(Session, new("created", Session.Id, null, new { }));
            App.PrepareResume(Session);
        }
        internal Task Busy(string text) => (Task)typeof(CliApp).GetMethod("BusyInput", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(App, [Session, text])!;
        public void Dispose()
        {
            App.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Root, true);
        }
    }
    private static JsonObject Done() => new() { ["role"] = "assistant", ["content"] = "done" };
    private static void Up(ConsoleInput input) => input.HandleKey(new('\0', ConsoleKey.UpArrow, false, false, false));
    private static async Task Until(Func<bool> condition, CancellationToken ct)
    { while (!condition()) await Task.Delay(20, ct); }

    [Fact]
    public async Task DirectPromptIsRecordedAtRunStartAndResumeDoesNotDuplicateIt()
    {
        using var f = new Fixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = f.App.Run(f.Session, "direct prompt", null, timeout.Token);
        try
        {
            await f.Model.Requests.Reader.ReadAsync(timeout.Token);
            Assert.Equal(1, f.Composer.HistoryCount);
            Up(f.Composer); Assert.Equal("direct prompt", f.Composer.Compose().PlainText);
            f.Model.Replies.Writer.TryWrite([Done()]); await run;
            f.Session.Runs.Last().State = RunState.Paused;
            var resumed = f.App.Run(f.Session, null, null, timeout.Token);
            await f.Model.Requests.Reader.ReadAsync(timeout.Token);
            Assert.Equal(1, f.Composer.HistoryCount);
            f.Model.Replies.Writer.TryWrite([Done()]); await resumed;
            Assert.Equal(1, f.Composer.HistoryCount);
        }
        finally { timeout.Cancel(); try { await run; } catch (OperationCanceledException) { } }
    }

    [Fact]
    public async Task RejectedDirectPromptIsNotRecordedAndDraftIsRestored()
    {
        using var f = new Fixture();
        f.Session.Runs.Add(new() { State = RunState.Paused });
        await Assert.ThrowsAsync<UserFacingException>(() => f.App.Run(f.Session, "rejected draft", null, default));
        Assert.Equal(0, f.Composer.HistoryCount);
        Assert.Equal("rejected draft", f.Composer.Compose().PlainText);
        Assert.False(f.Model.Requests.Reader.TryRead(out _));
    }

    [Fact]
    public async Task BusyPasteIsOneLiteralHistoryEntryAndFailedEnqueueRestoresIt()
    {
        using var f = new Fixture();
        await f.Busy("/paste"); Assert.False(f.Composer.EditingEnabled);
        await f.Busy("/exit"); await f.Busy("literal second line");
        Assert.Equal(0, f.Composer.HistoryCount); Assert.Empty(f.App.Store.Queue(f.Session.Id).Items);
        await f.Busy("/end");
        Assert.True(f.Composer.EditingEnabled); Assert.Equal(1, f.Composer.HistoryCount);
        Up(f.Composer); Assert.Equal("/exit\nliteral second line", f.Composer.Compose().PlainText);
        Assert.True(f.Composer.HandleKey(new('\r', ConsoleKey.Enter, false, false, false)).Ready);
        Assert.True(f.Composer.SubmittedLiteral); f.Composer.ClearSubmission();
        for (var i = 0; i < 19; i++) f.App.Store.Enqueue(f.Session.Id, "fill " + i);
        await f.Busy("/paste"); await f.Busy("/help rejected literal"); await f.Busy("/end");
        Assert.Equal(1, f.Composer.HistoryCount); Assert.Equal(20, f.App.Store.Queue(f.Session.Id).Items.Count);
        Assert.Equal("/help rejected literal", f.Composer.Compose().PlainText);
    }

    [Fact]
    public async Task QueuedPromptIsRecordedOnceAtEnqueueNotAgainAtDispatch()
    {
        using var f = new Fixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var chat = f.App.Chat(f.Session, "first", true, timeout.Token);
        try
        {
            await f.Model.Requests.Reader.ReadAsync(timeout.Token);
            f.Reader.Lines.Writer.TryWrite("second");
            await Until(() => f.Composer.HistoryCount == 2, timeout.Token);
            Assert.Single(f.App.Store.Queue(f.Session.Id).Items);
            f.Model.Replies.Writer.TryWrite([Done()]);
            await f.Model.Requests.Reader.ReadAsync(timeout.Token);
            Assert.Equal(2, f.Composer.HistoryCount);
            f.Reader.Lines.Writer.TryWrite("/exit"); await chat.WaitAsync(timeout.Token);
            Assert.Equal(2, f.Composer.HistoryCount);
        }
        finally { timeout.Cancel(); try { await chat; } catch (OperationCanceledException) { } }
    }

    [Theory]
    [InlineData("question:q")]
    [InlineData("plan-continuation:p")]
    [InlineData("paste")]
    public async Task NonComposerReadsSuppressHistoryAndSuggestionsAndRestoreEditing(string attentionKey)
    {
        using var f = new Fixture();
        f.Composer.AcceptPrompt(UserPrompt.Text("previous"), false);
        f.Composer.Restore("/ex");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var read = f.App.ReadInput(timeout.Token, attentionKey);
        try
        {
            Assert.False(f.Composer.EditingEnabled);
            f.Composer.Refresh(); Assert.Equal(0, f.Composer.SuggestionCount);
            Up(f.Composer); Assert.Equal("/ex", f.Composer.Draft);
            f.Reader.Lines.Writer.TryWrite("answer"); Assert.Equal("answer", await read);
            Assert.True(f.Composer.EditingEnabled); Assert.Equal(1, f.Composer.HistoryCount);
        }
        finally { timeout.Cancel(); try { await read; } catch (OperationCanceledException) { } }
    }

    [Fact]
    public async Task IdlePasteIsRecordedLiterallyRatherThanExecutedAsCommand()
    {
        using var f = new Fixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        f.Reader.Lines.Writer.TryWrite("/exit"); f.Reader.Lines.Writer.TryWrite("/end");
        var chat = f.App.Chat(f.Session, "/paste", false, timeout.Token);
        try
        {
            await f.Model.Requests.Reader.ReadAsync(timeout.Token);
            Assert.Equal(1, f.Composer.HistoryCount);
            Up(f.Composer); Assert.Equal("/exit", f.Composer.Compose().PlainText);
            Assert.Equal(0, f.Composer.SuggestionCount);
            f.Model.Replies.Writer.TryWrite([Done()]); Assert.Equal(0, await chat);
        }
        finally { timeout.Cancel(); try { await chat; } catch (OperationCanceledException) { } }
    }
}
