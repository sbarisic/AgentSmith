using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

public sealed class FailureTests
{
    [Fact]
    public void ErrorDetailsDoNotExposeExceptionBodiesOrPrivateArguments()
    {
        var call = new ToolCall { Name = "file_read", Arguments = Json.Serialize(new { path = "src/missing.cs", text = "PRIVATE_STDIN", environment = new { secret = "PRIVATE_ENV" } }) };
        var failure = Failures.Tool(new FileNotFoundException("PRIVATE_EXCEPTION"), call, beforeExecution: true);
        Assert.Equal("file_not_found", failure.Code);
        Assert.Equal("src/missing.cs", failure.Target);
        Assert.Equal("This operation was not started.", failure.Effect);
        Assert.DoesNotContain("PRIVATE", Json.Serialize(failure));
        var rejected = Failures.Tool(Failures.Reject("blocked", "token=PRIVATE_TOKEN\u001b blocked", "Check assignment"), call, true);
        Assert.DoesNotContain("PRIVATE_TOKEN", Json.Serialize(rejected));
        Assert.DoesNotContain('\u001b', rejected.Reason);
    }

    [Fact]
    public void UncertainAndStorageFailuresNeverPromiseRollback()
    {
        var uncertain = Failures.Uncertain(new() { Name = "file_move" });
        Assert.Equal("effect_uncertain", uncertain.Code);
        Assert.Contains("may have occurred", uncertain.Effect);
        Assert.Contains("/recover", uncertain.Next);
        var corrupt = Failures.Storage(11);
        Assert.Equal("storage_corrupt", corrupt.Code);
        Assert.Contains("WAL/SHM", corrupt.Next);
        Assert.Contains("may not have been saved", corrupt.Effect);
        Assert.Equal("storage_busy", Failures.Storage(5).Code);
    }

    [Fact]
    public async Task MissingFileAndUnknownToolHavePersistedCorrelatedFailures()
    {
        using var dir = new TestDirectory();
        var store = new SessionStore(Path.Combine(dir.Path, "data"));
        using var tools = new ToolRegistry();
        var session = new Session { Workspace = dir.Path, Model = "test" };
        var model = new ScriptedModel([RuntimeTests.Done(
            RuntimeTests.Call("missing", "file_read", Json.Serialize(new { path = "missing.cs", startLine = 1, lineCount = 10 })),
            RuntimeTests.Call("unknown", "invented_tool", "{}"))]);
        var events = new List<AgentEvent>();
        await new AgentRunner(store, model, tools) { MaxSteps = 1 }.RunAsync(session, "Inspect files", null, events.Add, default);
        var calls = store.Get(session.Id)!.Runs.Single().Calls;
        Assert.All(calls, c => Assert.Equal(CallState.Completed, c.State));
        Assert.Equal("file_not_found", JsonNode.Parse(calls[0].Result!)!["failure"]!["code"]!.ToString());
        Assert.Equal("tool_unavailable", JsonNode.Parse(calls[1].Result!)!["failure"]!["code"]!.ToString());
        Assert.Contains(events, e => e.Type == "tool.rejected");
        Assert.Empty(session.Mutations);
    }
}
