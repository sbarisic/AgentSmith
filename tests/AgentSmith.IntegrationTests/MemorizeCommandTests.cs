using AgentSmith.Cli;
using AgentSmith.Core;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class MemorizeCommandTests
{
    [Theory]
    [InlineData("memorize")]
    [InlineData("MEMORIZE")]
    public void StartupParsesMemorizeAndUsesMemoryReportTarget(string value)
    {
        var session = new Session { Workflow = CliApp.ParseWorkflow(value, false) };
        Assert.Equal(WorkflowMode.Memorize, session.Workflow);
        Assert.Equal("memory", WorkflowPolicy.Begin(session, "Inspect this repository").TargetKind);
        Assert.Contains("Finalization stays in Memorize", WorkflowTools.Instructions(session));
        Assert.DoesNotContain("Finalization returns to Work", WorkflowTools.Instructions(session));
        Assert.Throws<UserFacingException>(() => CliApp.ParseWorkflow(value, true));
    }

    [Theory]
    [InlineData("", "memory", ".")]
    [InlineData("--full", "memory-full", ".")]
    [InlineData("--path src", "memory", "src")]
    [InlineData("--path \"source files\"", "memory", "source files")]
    public void TargetParsing(string args, string kind, string path) =>
        Assert.Equal((kind, path), CliApp.ParseMemorizeTarget(args));

    [Theory]
    [InlineData("--path ../outside")]
    [InlineData("--path /outside")]
    [InlineData("--path C:\\outside")]
    [InlineData("--path src --full")]
    [InlineData("arbitrary task")]
    public void InvalidTargetsAreRejected(string args) =>
        Assert.Throws<UserFacingException>(() => CliApp.ParseMemorizeTarget(args));

    [Theory]
    [InlineData("/memorize --full", "memory-full")]
    [InlineData("/mode memorize --path .", "memory")]
    public async Task EntryStatusAndExplicitExitPreserveSourceOnlyPolicy(string command, string kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-memorize-command-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var app = new CliApp(Path.Combine(root, "data"), true, true);
            var session = new Session { Workspace = root, Mode = PermissionMode.Execute };
            app.Store.Save(session, new("created", session.Id, null, new { }));
            await app.HandleCommand(command, session);
            Assert.Equal(WorkflowMode.Memorize, session.Workflow);
            var report = Assert.Single(session.Reports);
            Assert.Equal(kind, report.TargetKind);
            Assert.DoesNotContain(app.Agents.Available(session), t => t.Name is "file_create" or "process_start" or "dotnet_test" or "csharp_load");
            await app.HandleCommand("/memorize status", session);
            await app.HandleCommand("/mode memorize status", session);
            Assert.Single(session.Reports);
            Assert.Empty(session.Runs);
            Assert.False(Directory.Exists(Path.Combine(root, ".agentsmith")));
            await app.HandleCommand("/mode execute", session);
            await app.HandleCommand("/mode document", session);
            await app.HandleCommand("/document Write docs", session);
            Assert.Equal(WorkflowMode.Memorize, session.Workflow);
            await app.HandleCommand($"/report export {report.Id} report.md", session);
            Assert.False(File.Exists(Path.Combine(root, "report.md")));
            Assert.Empty(session.Mutations);
            await app.HandleCommand("/workflow work", session);
            Assert.Equal(WorkflowMode.Work, session.Workflow);
            Assert.Equal("Abandoned", report.State);
            Assert.Equal(PermissionMode.Execute, session.Mode);
            await app.HandleCommand("/mode memorize", session);
            Assert.Equal(WorkflowMode.Memorize, session.Workflow);
            await app.HandleCommand("/mode work", session);
            Assert.Equal(WorkflowMode.Work, session.Workflow);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
