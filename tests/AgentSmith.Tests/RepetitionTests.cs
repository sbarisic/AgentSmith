using AgentSmith.Core;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using Xunit;

namespace AgentSmith.Tests;

public sealed class RepetitionTests
{
    private const string Paragraph = "I confirmed the solution references a browser project, but the referenced project file is missing. I was unable to complete or verify the browser project and Visual Studio launch setup, so it is not ready to launch or test in a browser yet. ";

    [Theory]
    [InlineData(1)]
    [InlineData(73)]
    [InlineData(10000)]
    public void DetectsRunawayAnswerAcrossArbitraryStreamChunks(int size)
    {
        var text = string.Concat(Enumerable.Repeat(Paragraph, 30)); var guard = new ResponseRepetitionGuard();
        var detected = false;
        for (var offset = 0; offset < text.Length && !detected; offset += size)
            detected = guard.Append(text.Substring(offset, Math.Min(size, text.Length - offset)));
        Assert.True(detected);
    }

    [Fact]
    public void DoesNotRejectOrdinaryProseCodeOrLimitedQuotation()
    {
        Assert.False(new ResponseRepetitionGuard().Append(Paragraph + Paragraph, true));
        Assert.False(new ResponseRepetitionGuard().Append(string.Concat(Enumerable.Range(0, 300).Select(i => $"public int Property{i} {{ get; set; }}\n")), true));
        Assert.False(new ResponseRepetitionGuard().Append(new string('x', 40000), true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepetitiveResponsePausesWithoutAdmittingCallsAndCanExplicitlyResume(bool stream)
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var session = new Session { Workspace = dir.Path };
        var repeated = string.Concat(Enumerable.Repeat(Paragraph, 30)); var tools = new CountingTools();
        var completion = RuntimeTests.Done(RuntimeTests.Message(repeated), RuntimeTests.Call("unsafe", "workspace_list", "{\"path\":\".\"}"));
        var model = new ScriptedModel(stream ? [new ModelEvent("text", repeated), completion] : [completion]);
        var run = await new AgentRunner(store, model, tools).RunAsync(session, "inspect", null, _ => { }, default);
        Assert.Equal(RunState.Paused, run.State); Assert.Equal(PauseReason.NoProgress, run.PauseReason);
        Assert.Empty(run.Calls); Assert.Empty(tools.Calls); Assert.Single(store.Get(session.Id)!.History);
        var next = await new AgentRunner(store, new ScriptedModel([RuntimeTests.Done(RuntimeTests.Message("Completed inspection."))]), tools)
            .RunAsync(store.Get(session.Id)!, null, null, _ => { }, default);
        Assert.Equal(RunState.Completed, next.State);
    }

    [Fact]
    public async Task CompletionBarrierCannotGenerateEndlessNoToolAnswers()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var session = new Session { Workspace = dir.Path };
        var model = new ScriptedModel(Enumerable.Range(0, 4).Select(i => new[] { RuntimeTests.Done(RuntimeTests.Message("Still waiting " + i)) }).ToArray());
        var run = await new AgentRunner(store, model, new BlockedTools()).RunAsync(session, "wait", null, _ => { }, default);
        Assert.Equal(PauseReason.NoProgress, run.PauseReason); Assert.Equal(3, model.Requests.Count);
    }

    private sealed class BlockedTools : IToolDispatcher
    {
        public IReadOnlyList<ToolDefinition> Definitions => [];
        public string Instructions(string workspace) => "";
        public Task<ToolOutput> ExecuteAsync(string workspace, ToolCall call, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CanCompleteAsync(ToolExecutionContext context) => Task.FromResult(false);
    }
}
