using AgentSmith.Cli;
using AgentSmith.Core;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class UsageCommandTests
{
    [Fact]
    public async Task UsageIsLocalReadOnlyAndPreservesPendingDecisionsAndQueues()
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-usage-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var app = new CliApp(root, true, true, new StringReader(""));
            var session = new Session { Runs = [new() { State = RunState.AwaitingUser, Question = new() { Text = "Choose" } }] };
            app.Store.Save(session, new("created", session.Id, null, new { }));
            app.Store.Enqueue(session.Id, "later"); app.Store.HoldQueue(session.Id, "AwaitingUser");
            await app.HandleCommand("/usage", session);
            Assert.Equal(RunState.AwaitingUser, app.Store.Get(session.Id)!.Runs[0].State); Assert.Null(session.Runs[0].Question!.Answer);
            Assert.Equal("AwaitingUser", app.Store.Queue(session.Id).HoldReason); Assert.Equal("Pending", app.Store.Queue(session.Id).Items[0].State);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void RenderingSeparatesWorkerOffloadFromCloudTotalsAndExplainsUnknownUsage()
    {
        var output = new StringWriter(); var renderer = new TerminalRenderer(output, false);
        renderer.Render("usage", new UsageReport("test", [new() { Role = "Parent", Provider = "ChatGpt", Model = "cloud", InputTokens = 100, OutputTokens = 10 },
            new() { Role = "Worker", Provider = "Local", Profile = "bonsai-edit", Model = "bonsai", InputTokens = 200, OutputTokens = 20, RequestsWithoutUsage = 1 }]));
        var text = output.ToString(); Assert.Contains("100 in / 10 out", text); Assert.Contains("220 tokens handled locally", text);
        Assert.Contains("bonsai-edit", text); Assert.Contains("Usage unavailable: 1", text); Assert.Contains("not exact ChatGPT", text);
    }
}
