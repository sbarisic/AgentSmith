using AgentSmith.Cli;
using AgentSmith.Core;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class PlanCommandTests
{
    [Fact]
    public async Task DirectoryPlanCommandsDoNotAdoptApproveOrStartWork()
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-plan-catalog-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var app = new CliApp(Path.Combine(root, "state"), true, true);
            var source = new Session { Workspace = root, Workflow = WorkflowMode.Plan }; var run = new Run(); source.Runs.Add(run);
            var plan = Planning.Update(new(source, run, app.Store, default), 0,
                new() { Goal = "Shared scope", Steps = [new() { Id = "one", Description = "Inspect", Verification = "Read files" }] });
            Planning.Submit(source); app.Store.Save(source, new("plan.submitted", source.Id, run.Id, new { }));
            var reader = new Session { Workspace = root, Mode = PermissionMode.Inspect };
            app.Store.Save(reader, new("created", reader.Id, null, new { }));
            var original = Json.Serialize(app.Store.Get(reader.Id));
            foreach (var command in new[] { "/plans", "/plans 2", "/plans invalid", "/plan read " + plan.Id, "/plan read missing", "/plan read" })
                Assert.Equal(CliApp.CommandAction.Handled, await app.HandleCommand(command, reader));
            Assert.Equal(original, Json.Serialize(app.Store.Get(reader.Id)));
            Assert.Null(reader.ActivePlanId); Assert.Empty(reader.Runs); Assert.Empty(reader.Plans);
            Assert.NotNull(Planning.Active(app.Store.Get(source.Id)!)!.PendingReview);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public void DirectoryPlanRenderingShowsSourceAndReadOnlySemanticsWithoutTerminalControls()
    {
        using var output = new StringWriter(); var renderer = new TerminalRenderer(output, false);
        var row = new PlanListing("plan-1", "source-1", "workspace", true, null, "Draft", 1, "Move\u001b[31m player");
        renderer.Render("plan.list", new DirectoryPlans("workspace", 1, true, [row]));
        renderer.Render("plan.display", new { plan = new DirectoryPlan("plan-1", "source-1", "workspace", true, null,
            "Draft", new PlanRevision { Revision = 1, Goal = "Movement", Steps = [] }) });
        var text = output.ToString();
        Assert.Contains("source-1", text); Assert.Contains("origin inferred", text); Assert.Contains("/plans 2", text);
        Assert.Contains("/plan read <id>", text); Assert.Contains("does not activate or approve", text);
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
    }

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
