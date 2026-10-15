using AgentSmith.Cli;
using AgentSmith.Core;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class MemorizeTransitionTests
{
    [Theory]
    [InlineData("/memorize", "plan")]
    [InlineData("/mode memorize", "plan")]
    [InlineData("/memorize", "report")]
    [InlineData("/mode memorize", "report")]
    [InlineData("/memorize", "process")]
    [InlineData("/mode memorize", "process")]
    public async Task EntryRefusesUnsettledWorkflowAndProcessState(string command, string barrier)
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-memorize-barrier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var app = new CliApp(Path.Combine(root, "data"), true, true);
            var session = new Session { Workspace = root, Mode = PermissionMode.Execute };
            if (barrier == "plan") session.Workflow = WorkflowMode.Plan;
            if (barrier == "report")
            {
                session.Workflow = WorkflowMode.Review;
                WorkflowPolicy.Begin(session, "Existing unfinished review");
            }
            if (barrier == "process")
                session.Processes.Add(new ProcessRecord { State = "Interrupted", TerminationConfirmed = false });
            var originalWorkflow = session.Workflow;
            var originalReportCount = session.Reports.Count;
            app.Store.Save(session, new("created", session.Id, null, new { }));

            await app.HandleCommand(command, session);

            Assert.Equal(originalWorkflow, session.Workflow);
            Assert.Equal(originalWorkflow, app.Store.Get(session.Id)!.Workflow);
            Assert.Equal(originalReportCount, session.Reports.Count);
            Assert.Empty(session.Runs);
            Assert.Empty(session.Mutations);
            Assert.False(Directory.Exists(Path.Combine(root, ".agentsmith")));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("/memorize", PermissionMode.Inspect)]
    [InlineData("/memorize", PermissionMode.Edit)]
    [InlineData("/mode memorize", PermissionMode.Inspect)]
    [InlineData("/mode memorize", PermissionMode.Edit)]
    public async Task EntryAndExitDoNotRaisePermissionCeiling(string command, PermissionMode permission)
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-memorize-ceiling-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var app = new CliApp(Path.Combine(root, "data"), true, true);
            var session = new Session { Workspace = root, Mode = permission };
            app.Store.Save(session, new("created", session.Id, null, new { }));
            await app.HandleCommand(command, session);
            Assert.Equal(WorkflowMode.Memorize, session.Workflow);
            Assert.Equal(permission, session.Mode);
            Assert.DoesNotContain(app.Agents.Available(session), t => t.Name is "file_create" or "process_start" or "dotnet_test" or "csharp_load");
            await app.HandleCommand("/mode work", session);
            Assert.Equal(WorkflowMode.Work, session.Workflow);
            Assert.Equal(permission, session.Mode);
            Assert.Equal(permission, app.Store.Get(session.Id)!.Mode);
            Assert.Empty(session.Runs);
            Assert.Empty(session.Mutations);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
