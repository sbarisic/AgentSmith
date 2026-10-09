using AgentSmith.Cli;
using AgentSmith.Core;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class PlanCommandTests
{
    [Fact]
    public async Task EnteringPlanStopsOwnedProcessesAndModeCannotBypassIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "smith-plan-command-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var app = new CliApp(Path.Combine(root, "state"), true, true);
            var session = new Session { Workspace = root }; var run = new Run { State = RunState.Completed }; session.Runs.Add(run);
            app.Store.Save(session, new("created", session.Id, run.Id, new { }));
            var call = new ToolCall { Id = "start", Name = "process_start", Arguments = Json.Serialize(new { executable = "pwsh", arguments = new[] { "-NoProfile", "-Command", "Start-Sleep 60" }, workingDirectory = ".", environment = Array.Empty<object>(), timeoutSeconds = 60 }) };
            call.Prepared = app.Registry.Prepare(app.Context(session), call); app.Context(session).Save("intent", new { }); await app.Registry.ExecuteAsync(app.Context(session), call);
            await app.HandleCommand("/plan new investigate", session);
            Assert.Equal(WorkflowMode.Plan, session.Workflow); Assert.True(session.Processes.Single().TerminationConfirmed);
            await app.HandleCommand("/mode execute", session);
            Assert.DoesNotContain(app.Registry.Available(session), t => t.Name == "file_create" || t.Name == "process_start");
            await app.HandleCommand("/undo nonexistent", session); Assert.Empty(session.Mutations);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
