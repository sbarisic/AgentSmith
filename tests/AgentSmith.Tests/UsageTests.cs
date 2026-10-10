using AgentSmith.Core;
using AgentSmith.Storage;
using Xunit;

namespace AgentSmith.Tests;

public sealed class UsageTests
{
    private static void Event(SessionStore store, Session session, string type, object payload) => store.Save(session, new(type, session.Id, session.Runs.LastOrDefault()?.Id, payload));
    private static string Tokens(long input, long output) => Json.Serialize(new { input_tokens = input, output_tokens = output,
        input_tokens_details = new { cached_tokens = input / 2 }, attribution = new { input_tokens = input, output_tokens = output } });
    private static void Request(SessionStore store, Session session, string? usage)
    { Event(store, session, "model.requested", new { }); Event(store, session, "model.completed", new { usage }); }
    [Fact]
    public void ReportUsesDurableEventsAcrossRunsAndWorkersWithoutDoubleCounting()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path);
        var parent = new Session { Model = "cloud", Runs = [new() { State = RunState.Completed, Usage = Tokens(999999, 999999) }] };
        Request(store, parent, Tokens(100, 10)); parent.Runs.Add(new() { State = RunState.Cancelled }); Request(store, parent, Tokens(200, 20));
        Event(store, parent, "context.compaction.completed", new { usage = new[] { Tokens(50, 5) } });
        var child = new Session { ParentConversationId = parent.Id, Model = "bonsai", Connection = new() { Provider = ModelProvider.Local } };
        store.CreateWorker(new() { ParentId = parent.Id, ParentRunId = parent.Runs[0].Id, CallId = "spawn", ChildId = child.Id,
            Profile = new() { Name = "bonsai-edit" }, RequestUsage = [Tokens(999999, 999999)] }, child);
        Request(store, child, Tokens(400, 40)); Request(store, child, Tokens(500, 50));
        Event(store, child, "context.compaction.completed", new { usage = new[] { Tokens(20, 2) } });
        Request(store, new Session { Model = "unrelated" }, Tokens(777777, 777777));
        var report = new SessionStore(dir.Path).Usage(parent.Id);
        Assert.Equal(350, report.CloudInputTokens); Assert.Equal(35, report.CloudOutputTokens);
        Assert.Equal(920, report.LocalInputTokens); Assert.Equal(92, report.LocalOutputTokens); Assert.Equal(1012, report.LocalWorkerTokens);
        Assert.Equal(2, report.Rows.Count); Assert.All(report.Rows, r => Assert.Equal(3, r.ReportedRequests));
        Assert.Equal(50, report.Rows[0].CompactionInputTokens); Assert.Equal(20, report.Rows[1].CompactionInputTokens);
        Assert.Equal("bonsai-edit", report.Rows[1].Profile); Assert.Equal(0, report.RequestsWithoutUsage); Assert.Equal(0, report.UnfinishedRequests);
        Assert.Throws<UserFacingException>(() => store.Usage(child.Id)); Assert.Throws<UserFacingException>(() => store.Usage("missing"));
    }
    [Fact]
    public void MissingMalformedAndInterruptedUsageIsVisibleInsteadOfInventingSavings()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var session = new Session();
        foreach (var json in new string?[] { null, "{}", "broken", Tokens(-1, 2), "{\"input_tokens\":\"10\",\"output_tokens\":20}", "{\"attribution\":{\"input_tokens\":99,\"output_tokens\":9}}" }) Request(store, session, json);
        Event(store, session, "model.requested", new { }); Event(store, session, "context.compaction.failed", new { reason = "test" });
        Event(store, session, "context.compaction.completed", new { usage = Array.Empty<string>() });
        Event(store, session, "context.compaction.retry", new { usageUnavailable = true });
        Event(store, session, "context.compaction.retry", new { usageUnavailable = false });
        var report = store.Usage(session.Id);
        Assert.Equal(8, report.RequestsWithoutUsage); Assert.Equal(1, report.UnfinishedRequests); Assert.Equal(1, report.FailedCompactions);
        Assert.Equal(0, report.CloudInputTokens); Assert.Equal(0, report.LocalWorkerTokens); Assert.Contains("not exact ChatGPT", report.Interpretation);
    }
    [Fact]
    public void LocalParentIsNotMisreportedAsWorkerOffloadAndZeroUsageIsValid()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path);
        var session = new Session { Connection = new() { Provider = ModelProvider.LmStudio } };
        Request(store, session, Tokens(0, 0)); Request(store, session, Tokens(120, 30));
        var report = store.Usage(session.Id);
        Assert.Equal(120, report.LocalInputTokens); Assert.Equal(30, report.LocalOutputTokens); Assert.Equal(0, report.LocalWorkerTokens);
        Assert.Equal(2, report.Rows[0].ReportedRequests); Assert.Equal(0, report.RequestsWithoutUsage);
    }
}
