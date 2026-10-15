using AgentSmith.Cli;
using AgentSmith.Core;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class MemorizeUndoTests
{
    [Theory]
    [InlineData("/memorize")]
    [InlineData("/mode memorize")]
    public async Task UndoRefusesAppliedCheckpointUntilExplicitWorkflowExit(string entryCommand)
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-memorize-undo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var app = new CliApp(Path.Combine(root, "data"), true, true);
            var session = new Session { Workspace = root, Workflow = WorkflowMode.Review, Mode = PermissionMode.Execute };
            var report = WorkflowPolicy.Begin(session, "Review fixture");
            report.Revisions.Add(new() { Revision = 1, Summary = "Fixture report", Coverage = "No source inspected" });
            app.Store.Save(session, new("created", session.Id, null, new { }));
            await app.HandleCommand($"/report export {report.Id} review.md", session);
            var checkpoint = Assert.Single(session.Mutations);
            Assert.Equal("Applied", checkpoint.State);
            var path = Path.Combine(root, "review.md");
            var before = File.ReadAllBytes(path);
            var mutationsBefore = Json.Serialize(session.Mutations);

            await app.HandleCommand("/workflow work", session);
            await app.HandleCommand(entryCommand, session);
            Assert.Equal(WorkflowMode.Memorize, session.Workflow);
            await app.HandleCommand("/undo " + checkpoint.Id, session);

            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(mutationsBefore, Json.Serialize(session.Mutations));
            Assert.Equal(mutationsBefore, Json.Serialize(app.Store.Get(session.Id)!.Mutations));
            Assert.Equal(WorkflowMode.Memorize, session.Workflow);
            Assert.Equal(PermissionMode.Execute, session.Mode);
            Assert.False(Directory.Exists(Path.Combine(root, ".agentsmith")));

            // The checkpoint is valid: explicit exit permits the very same undo.
            await app.HandleCommand("/workflow work", session);
            await app.HandleCommand("/undo " + checkpoint.Id, session);
            Assert.False(File.Exists(path));
            Assert.Equal(WorkflowMode.Work, session.Workflow);
            Assert.Equal(PermissionMode.Execute, session.Mode);
            Assert.Equal(2, session.Mutations.Count);
            Assert.Equal(checkpoint.Id, session.Mutations.Last().UndoOf);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
