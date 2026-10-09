using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

public sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AgentSmith-tests-" + Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(Path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Path, true);
    }
}
public sealed class ScriptedModel(params ModelEvent[][] steps) : IModelClient
{
    private int index;
    public List<ModelRequest> Requests { get; } = [];
    public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Add(request);
        foreach (var ev in steps[index++]) { yield return ev; await Task.Yield(); }
    }
}
public sealed class CountingTools : IWorkspaceTools
{
    private readonly WorkspaceTools actual = new();
    public List<string> Calls { get; } = [];
    public IReadOnlyList<ToolDefinition> Definitions => actual.Definitions;
    public string Instructions(string workspace) => actual.Instructions(workspace);
    public Task<ToolOutput> ExecuteAsync(string workspace, ToolCall call, CancellationToken ct)
    { Calls.Add(call.Id); return actual.ExecuteAsync(workspace, call, ct); }
}
public class RuntimeTests
{
    public static JsonObject Call(string id, string name, string arguments) => new() { ["type"] = "function_call", ["call_id"] = id, ["name"] = name, ["namespace"] = "agentsmith", ["arguments"] = arguments };
    public static ModelEvent Done(params JsonObject[] items) => new("completed", Output: items);
    public static JsonObject Message(string text = "Finished") => new() { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }) };
    private static Session New(TestDirectory dir) => new() { Workspace = dir.Path, AccountId = "test", Model = "test-model" };

    [Fact]
    public async Task SerialCallsAreCorrelatedAndNotRepeatedOnResume()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var tools = new CountingTools(); var session = New(dir);
        var model = new ScriptedModel([Done(Call("a", "workspace_list", "{\"path\":\".\"}"), Call("b", "workspace_list", "{\"path\":\".\"}"))]);
        var run = await new AgentRunner(store, model, tools) { MaxSteps = 1 }.RunAsync(session, "inspect", null, _ => { }, default);
        Assert.Equal(RunState.Paused, run.State); Assert.Equal(new[] { "a", "b" }, tools.Calls);
        var saved = new SessionStore(dir.Path).Get(session.Id)!;
        var continuation = new ScriptedModel([Done(Message())]);
        var next = await new AgentRunner(store, continuation, tools).RunAsync(saved, null, null, _ => { }, default);
        Assert.Equal(RunState.Completed, next.State); Assert.Equal(2, tools.Calls.Count);
        Assert.Equal(new[] { "a", "b" }, continuation.Requests[0].Input.Where(i => i["type"]?.ToString() == "function_call_output").Select(i => i["call_id"]!.ToString()));
    }
    [Fact]
    public async Task UnfinishedStreamExecutesNothing()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var tools = new CountingTools();
        var model = new ScriptedModel([new ModelEvent("text", "partial")]); var session = New(dir);
        var run = await new AgentRunner(store, model, tools).RunAsync(session, "inspect", null, _ => { }, default);
        Assert.Equal(RunState.Failed, run.State); Assert.Empty(tools.Calls); Assert.Single(session.History);
    }
    [Fact]
    public async Task QuestionPersistsAndBlankInputDoesNotAnswer()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var session = New(dir); var tools = new CountingTools();
        var model = new ScriptedModel([Done(Call("question", "user_ask", "{\"question\":\"Which file?\",\"choices\":[\"a\",\"b\"]}"))]);
        await new AgentRunner(store, model, tools).RunAsync(session, "help", null, _ => { }, default);
        var saved = store.Get(session.Id)!; Assert.Equal(RunState.AwaitingUser, saved.Runs.Last().State);
        var next = new ScriptedModel([Done(Message())]); var runner = new AgentRunner(store, next, tools);
        await runner.RunAsync(saved, null, " ", _ => { }, default); Assert.Empty(next.Requests); Assert.Null(saved.Runs.Last().Question!.Answer);
        await runner.RunAsync(saved, null, "b", _ => { }, default);
        Assert.Equal("b", store.Get(session.Id)!.Runs.Last().Question!.Answer); Assert.Single(tools.Calls);
        Assert.Single(saved.History, h => h["type"]?.ToString() == "function_call_output");
    }
    [Fact]
    public async Task UnknownStartedInvocationRequiresRecovery()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var session = New(dir);
        session.Runs.Add(new() { State = RunState.Running, Calls = [new() { Id = "a", Name = "workspace_list", State = CallState.Started }] });
        var tools = new CountingTools(); var model = new ScriptedModel();
        var run = await new AgentRunner(store, model, tools).RunAsync(session, null, null, _ => { }, default);
        Assert.Equal(RunState.RecoveryRequired, run.State); Assert.Empty(tools.Calls); Assert.Empty(model.Requests);
    }
    [Fact]
    public async Task CancellationDoesNotDispatch()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var session = New(dir); var model = new ScriptedModel();
        using var ct = new CancellationTokenSource(); ct.Cancel();
        var run = await new AgentRunner(store, model, new CountingTools()).RunAsync(session, "hello", null, _ => { }, ct.Token);
        Assert.Equal(RunState.Cancelled, run.State); Assert.Empty(model.Requests);
    }
    [Fact]
    public async Task ContextLimitPausesWithoutRequest()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var model = new ScriptedModel();
        var run = await new AgentRunner(store, model, new CountingTools()) { MaxContextCharacters = 10 }.RunAsync(New(dir), "hello", null, _ => { }, default);
        Assert.Equal(RunState.Paused, run.State); Assert.Empty(model.Requests);
    }
    [Fact]
    public void ConversationLocksConflictAndRelease()
    {
        using var dir = new TestDirectory(); var first = new SessionStore(dir.Path); var second = new SessionStore(dir.Path);
        using (var lease = first.Acquire("test")) Assert.Throws<UserFacingException>(() => second.Acquire("test"));
        using var success = second.Acquire("test");
    }
    [Fact]
    public async Task UnknownToolProducesErrorNotAnEffect()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var session = New(dir);
        var model = new ScriptedModel([Done(Call("a", "process_run", "{}"))], [Done(Message())]);
        var run = await new AgentRunner(store, model, new WorkspaceTools()).RunAsync(session, "test", null, _ => { }, default);
        Assert.Equal(RunState.Completed, run.State); Assert.Contains("rejected", run.Calls[0].Result);
    }
    [Fact]
    public async Task DuplicateCallsFailBeforeDispatch()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var tools = new CountingTools();
        var model = new ScriptedModel([Done(Call("a", "workspace_list", "{}"), Call("a", "workspace_list", "{}"))]);
        var run = await new AgentRunner(store, model, tools).RunAsync(New(dir), "test", null, _ => { }, default);
        Assert.Equal(RunState.Failed, run.State); Assert.Empty(tools.Calls);
    }
}
