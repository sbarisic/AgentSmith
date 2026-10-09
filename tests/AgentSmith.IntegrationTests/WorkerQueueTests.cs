using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AgentSmith.Cli;
using AgentSmith.Core;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class WorkerQueueTests
{
    private sealed class Input : TextReader
    {
        public readonly Channel<string?> Lines = Channel.CreateUnbounded<string?>();
        public override Task<string?> ReadLineAsync() => Lines.Reader.ReadAsync().AsTask();
    }
    private sealed class Model : IModelClient
    {
        public readonly Channel<ModelRequest> Requests = Channel.CreateUnbounded<ModelRequest>();
        public readonly Channel<JsonObject[]> Replies = Channel.CreateUnbounded<JsonObject[]>();
        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest r, [EnumeratorCancellation] CancellationToken ct)
        { await Requests.Writer.WriteAsync(r, ct); yield return new("completed", Output: await Replies.Reader.ReadAsync(ct)); }
    }
    private static JsonObject Call(string id, string name, object arguments) => new() { ["type"] = "function_call", ["call_id"] = id, ["name"] = name, ["arguments"] = Json.Serialize(arguments) };
    private static JsonObject Message() => new() { ["role"] = "assistant", ["content"] = "Done" };
    private static async Task Until(Func<bool> predicate, CancellationToken ct) { while (!predicate()) await Task.Delay(10, ct); }
    [Fact]
    public async Task QueuedFollowUpWaitsForWorkerReportsAndUsesParentProvider()
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-worker-queue-" + Guid.NewGuid().ToString("N")); var input = new Input();
        try
        {
            var parent = new Model(); var child = new Model(); using var app = new CliApp(root, true, true, input) { ModelFactoryOverride = s => s.ParentConversationId is null ? parent : child };
            var session = new Session { Workspace = root, Model = "parent", EnabledWorkers = [new() { Name = "reader", Model = "local-worker", Connection = new() { Provider = ModelProvider.LmStudio } }] };
            app.Store.Save(session, new("created", session.Id, null, new { }));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var ct = timeout.Token;
            var chat = app.Chat(session, "delegate", true, ct);
            Assert.Equal("parent", (await parent.Requests.Reader.ReadAsync(ct)).Model);
            parent.Replies.Writer.TryWrite([Call("spawn1", "agent_spawn", new { profile = "reader", task = "read", writePaths = Array.Empty<string>() })]);
            Assert.Equal("local-worker", (await child.Requests.Reader.ReadAsync(ct)).Model);
            await parent.Requests.Reader.ReadAsync(ct); input.Lines.Writer.TryWrite("follow-up");
            await Until(() => app.Store.Queue(session.Id).Items.Count == 1, ct);
            parent.Replies.Writer.TryWrite([Message()]);
            await Task.Delay(100, ct); Assert.Single(app.Store.Get(session.Id)!.Runs); Assert.False(parent.Requests.Reader.TryRead(out _));
            child.Replies.Writer.TryWrite([Call("finish", "agent_finish", new { status = "completed", summary = "Read done", limitations = "Tests not run", evidence = Array.Empty<string>(), clarification = "" })]);
            await parent.Requests.Reader.ReadAsync(ct);
            var worker = app.Store.Workers(session.Id).Single();
            parent.Replies.Writer.TryWrite([Call("collect", "agent_result", new { agentId = worker.Id })]);
            await parent.Requests.Reader.ReadAsync(ct); parent.Replies.Writer.TryWrite([Message()]);
            var followUp = await parent.Requests.Reader.ReadAsync(ct); Assert.Equal("parent", followUp.Model); Assert.Equal("follow-up", followUp.Input.Last()["content"]!.ToString());
            parent.Replies.Writer.TryWrite([Message()]);
            await Until(() => app.Store.Queue(session.Id).Items.All(i => i.State == "Completed"), ct); input.Lines.Writer.TryWrite("/exit");
            Assert.Equal(0, await chat); Assert.True(app.Store.Workers(session.Id).Single().Collected);
        }
        finally { input.Lines.Writer.TryWrite(null); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
