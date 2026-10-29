using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using Xunit;

namespace AgentSmith.Tests;

public sealed class CompactionRecoveryTests
{
    private sealed class NoTools : IToolDispatcher
    {
        public IReadOnlyList<ToolDefinition> Definitions => [];
        public string Instructions(string workspace) => "";
        public Task<ToolOutput> ExecuteAsync(string workspace, ToolCall call, CancellationToken ct) => throw new InvalidOperationException("Saved tools must not replay.");
    }
    private sealed class Model(bool failCount = false, bool longSummary = false, bool failSummary = false) : IModelClient
    {
        private int counts;
        public int Summaries;
        public Task<TokenMeasurement?> CountTokensAsync(ModelRequest r, CancellationToken ct)
        {
            if (failCount && ++counts == 2) throw Failures.Reject("local_token_count_failed", "Tokenizer temporarily unavailable", "Retry");
            return Task.FromResult<TokenMeasurement?>(new(r.Instructions.Length + r.Input.Sum(i => i.ToJsonString().Length), "fixture count", true));
        }
        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest r, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield(); Summaries++;
            if (failSummary && Summaries == 2) throw new UserFacingException("Second slice failed");
            yield return new("completed", Output: [new() { ["role"] = "assistant", ["content"] = longSummary ? new string('s', 7900) : "Preserved the task and evidence" }],
                Usage: "{\"input_tokens\":100,\"output_tokens\":10}");
        }
    }
    private static Session Create(SessionStore store)
    {
        var s = new Session { Workspace = store.DataDirectory, Model = "test", Connection = new() { MaxOutputTokens = 8192, Capabilities = new() { ContextTokens = 32768 } } };
        s.History.Add(new() { ["role"] = "user", ["content"] = "Keep this active goal" });
        return s;
    }
    private static void Round(Session s, string id, int size)
    {
        s.History.Add(RuntimeTests.Call(id, "file_read", "{}"));
        s.History.Add(new() { ["type"] = "function_call_output", ["call_id"] = id, ["output"] = new string('z', size) });
    }
    [Fact]
    public async Task TransientCountingFailureDuringCompactionAllowsOrdinaryRetryWithoutToolReplay()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Create(store); Round(s, "large", 40000);
        var original = ContextBuilder.Hash(s.History); var model = new Model(failCount: true);
        var builder = new ContextBuilder(store, model, new NoTools());
        var error = await Assert.ThrowsAsync<ContextBudgetException>(() => builder.PrepareAsync(s, new(), _ => { }, default));
        Assert.Equal(PauseReason.Network, error.Reason); Assert.Equal("Retryable", Assert.Single(s.CompactionAttempts).State);
        var retry = new ContextBuilder(store, model, new NoTools());
        Assert.False(retry.RequiresExplicitRetry(s));
        await retry.PrepareAsync(s, new(), _ => { }, default);
        Assert.NotNull(s.ActiveContextId); Assert.Equal(original, ContextBuilder.Hash(s.History));
    }
    [Fact]
    public async Task LargerActualSummaryExtendsBoundaryAndRecordsEachRequestOnlyOnce()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Create(store);
        Round(s, "first", 24000); Round(s, "second", 11000);
        var original = ContextBuilder.Hash(s.History); var model = new Model(longSummary: true);
        await new ContextBuilder(store, model, new NoTools()).PrepareAsync(s, new(), _ => { }, default);
        Assert.Equal(s.History.Count, ContextBuilder.Active(s)!.Through);
        Assert.True(model.Summaries >= 2); Assert.Equal(original, ContextBuilder.Hash(s.History));
        var usage = Assert.Single(new SessionStore(dir.Path).Usage(s.Id).Rows);
        Assert.Equal(model.Summaries * 100, usage.CompactionInputTokens);
        Assert.Equal(model.Summaries * 10, usage.CompactionOutputTokens);
        Assert.Equal(model.Summaries, usage.ReportedRequests);
    }
    [Fact]
    public async Task FailedLaterSliceKeepsEarlierUsageDurablyWithoutActivatingPartialSummary()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Create(store);
        Round(s, "large", 90000); Round(s, "recent", 10); Round(s, "last", 10);
        var original = ContextBuilder.Hash(s.History);
        await Assert.ThrowsAsync<ContextBudgetException>(() => new ContextBuilder(store, new Model(failSummary: true), new NoTools()).PrepareAsync(s, new(), _ => { }, default, true));
        Assert.Null(s.ActiveContextId); Assert.Equal(original, ContextBuilder.Hash(s.History));
        var usage = Assert.Single(new SessionStore(dir.Path).Usage(s.Id).Rows);
        Assert.Equal(100, usage.CompactionInputTokens); Assert.Equal(10, usage.CompactionOutputTokens);
        Assert.Equal(1, usage.ReportedRequests); Assert.Equal(1, usage.FailedCompactions);
    }
}
