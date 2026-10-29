using System.Text.Json.Nodes;
using AgentSmith.Cli;
using AgentSmith.Core;
using AgentSmith.Runtime;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class CompactionRecoveryTests
{
    private sealed class Model : IModelClient
    {
        public int Summaries;
        public int Inferences;
        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); await Task.Yield();
            if (request.Tools.Count == 0) Summaries++; else Inferences++;
            yield return new("completed", Output: [new() { ["role"] = "assistant", ["content"] = "Preserved the original task and observations." }]);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyNetworkPauseCanRecoverThroughContinueOrCompact(bool compactFirst)
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-network-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var model = new Model(); using var app = new CliApp(root, true, true, new StringReader("")) { ModelOverride = model };
            var session = new Session { Workspace = root, Mode = PermissionMode.Inspect };
            session.History.Add(new() { ["role"] = "user", ["content"] = "Inspect the workspace" });
            session.History.Add(new() { ["role"] = "assistant", ["content"] = new string('x', 120000) });
            session.History.Add(new() { ["role"] = "user", ["content"] = "Finish the original task" });
            var run = new Run { State = RunState.Paused, PauseReason = PauseReason.Network, PromptHistoryIndex = 2, Reason = "Tokenizer temporarily unavailable" };
            session.Runs.Add(run);
            session.CompactionAttempts.Add(new() { BoundaryHash = ContextBuilder.Hash(session.History), State = "Failed" });
            app.Store.Save(session, new("created", session.Id, run.Id, new { }));
            if (compactFirst)
            {
                await app.HandleCommand("/compact", session);
                Assert.NotNull(session.ActiveContextId);
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var resumed = await app.Run(session, null, null, timeout.Token);
            Assert.Same(run, resumed); Assert.Equal(RunState.Completed, resumed.State);
            Assert.True(model.Summaries > 0); Assert.Equal(1, model.Inferences);
            Assert.Single(app.Store.Get(session.Id)!.Runs);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
