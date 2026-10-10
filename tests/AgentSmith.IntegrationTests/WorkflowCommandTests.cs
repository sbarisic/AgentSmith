using AgentSmith.Cli;
using AgentSmith.Core;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class WorkflowCommandTests
{
    [Theory]
    [InlineData(null, true, WorkflowMode.Plan)]
    [InlineData("document", false, WorkflowMode.Document)]
    [InlineData("REVIEW", false, WorkflowMode.Review)]
    public void WorkflowOptionSupportsPlanAlias(string? value, bool plan, WorkflowMode expected) => Assert.Equal(expected, CliApp.ParseWorkflow(value, plan));
    [Fact]
    public void ConflictingOptionsCannotSilentlyChangeWorkflow()
    {
        Assert.Throws<UserFacingException>(() => CliApp.ParseWorkflow("review", true));
        Assert.Throws<UserFacingException>(() => CliApp.ParseWorkflow("2", false));
    }
    [Fact]
    public async Task ExplicitExportInReviewInspectCreatesCheckpointWithoutGrantingPermissions()
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-workflow-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var app = new CliApp(Path.Combine(root, "data"), true, true);
            var s = new Session { Workspace = root, Workflow = WorkflowMode.Review, Mode = PermissionMode.Inspect };
            var report = WorkflowPolicy.Begin(s, "Review"); report.Revisions.Add(new() { Revision = 1, Summary = "Found a defect", Coverage = "source.cs", Findings = [new() { Id = "R1", Category = "defect", Title = "Bounds", Severity = "high", Explanation = "Index exceeds length", Path = "source.cs", Line = 2 }] });
            app.Store.Save(s, new("created", s.Id, null, new { }));
            await app.HandleCommand($"/report export {report.Id} review.md", s);
            Assert.Equal(WorkflowMode.Review, s.Workflow); Assert.Equal(PermissionMode.Inspect, s.Mode); Assert.Single(s.Mutations);
            var text = File.ReadAllText(Path.Combine(root, "review.md")); Assert.Contains("Bounds", text);
            await app.HandleCommand($"/report export {report.Id} review.md", s); Assert.Single(s.Mutations); Assert.Equal(text, File.ReadAllText(Path.Combine(root, "review.md")));
            await app.HandleCommand($"/report export {report.Id} ../escape.md", s); Assert.Single(s.Mutations);
            await app.HandleCommand($"/report export {report.Id} .git/escape.md", s); Assert.Single(s.Mutations);
            await app.HandleCommand("/workflow work", s); Assert.Equal("Abandoned", report.State); Assert.Equal(WorkflowMode.Work, s.Workflow);
            s.Mode = PermissionMode.Edit; await app.HandleCommand("/undo " + s.Mutations.Single().Id, s); Assert.False(File.Exists(Path.Combine(root, "review.md")));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task TransitionStopsProcessesAndCannotBypassPendingPlan()
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-workflow-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var app = new CliApp(Path.Combine(root, "data"), true, true);
            var s = new Session { Workspace = root }; app.Store.Save(s, new("created", s.Id, null, new { }));
            var ctx = app.Context(s);
            var start = new ToolCall { Id = "process", Name = "process_start", Arguments = Json.Serialize(new { executable = "pwsh", arguments = new[] { "-NoProfile", "-Command", "Start-Sleep 30" }, workingDirectory = ".", environment = Array.Empty<object>(), timeoutSeconds = 60 }) };
            start.Prepared = app.Registry.Prepare(ctx, start); await app.Registry.ExecuteAsync(ctx, start);
            await app.HandleCommand("/review --path .", s);
            Assert.Equal(WorkflowMode.Review, s.Workflow); Assert.All(s.Processes, p => Assert.True(p.TerminationConfirmed));
            await app.HandleCommand("/mode execute", s); Assert.DoesNotContain(app.Agents.Available(s), t => t.Name == "file_create");
            await app.HandleCommand("/workflow work", s); s.Workflow = WorkflowMode.Plan;
            await app.HandleCommand("/document Update docs", s); Assert.Equal(WorkflowMode.Plan, s.Workflow);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
