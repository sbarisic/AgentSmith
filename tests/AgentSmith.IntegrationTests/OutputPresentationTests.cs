using System.Text.Json.Nodes;
using AgentSmith.Cli;
using AgentSmith.Core;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class OutputPresentationTests
{
    [Fact]
    public void SplitTerminalControlsNeverExecuteOrLeakParameters()
    {
        var filter = new TerminalControlFilter();
        Assert.Equal("before", filter.Append("before\u001b[32;"));
        Assert.Equal("green", filter.Append("1mgreen\u001b[0m\u001b]0;secret title"));
        Assert.Equal("", filter.Append("\u001b"));
        Assert.Equal("after", filter.Append("\\after"));
        Assert.Equal("plain", TerminalRenderer.Safe("\u009b31mplain\u009b0m\u001b]52;c;clipboard\a"));
        Assert.Equal("\nnext", filter.Append("\u001b[32;\nnext"));
    }

    [Fact]
    public void ProcessChunksKeepSeparateFiltersUntilTheLastRetainedPage()
    {
        using var output = new StringWriter(); var renderer = new TerminalRenderer(output, false);
        void Page(bool more, params object[] chunks) => renderer.Render("tool.completed", new { name = "process_poll", content = Json.Serialize(new
        { processId = "p", alias = "#15", state = "Exited", exitCode = 0, terminationConfirmed = true, hasMoreOutput = more, output = chunks }) });
        Page(true, new { cursor = 1, stream = "stdout", text = "\u001b[32;" }, new { cursor = 2, stream = "stderr", text = "warning\n" });
        Page(false, new { cursor = 3, stream = "stdout", text = "1mhello\u001b[0m\n" });
        var text = output.ToString();
        Assert.Contains("warning", text); Assert.Contains("hello", text); Assert.DoesNotContain("32;", text); Assert.DoesNotContain("1mhello", text);
        Assert.Contains("┌─ stdout", text); Assert.Contains("┌─ stderr", text); Assert.Contains("/artifact process:#15", text);
        Assert.DoesNotContain('\u001b', text);
    }

    [Fact]
    public void PathOnlyListingsShortenButDiagnosticSentencesDoNot()
    {
        using var output = new StringWriter(); var renderer = new TerminalRenderer(output, false);
        void Show(string id, string text) => renderer.Render("tool.completed", new { name = "process_poll", content = Json.Serialize(new
        { processId = id, alias = "#15", state = "Exited", exitCode = 0, terminationConfirmed = true, output = new[] { new { stream = "stdout", text } } }) });
        Show("p1", "FullName\n--------\nC:\\out\\publish\\one.dll\nC:\\out\\publish\\two.dll\n");
        var paths = output.ToString();
        Assert.Contains("Directory: C:/out/publish/", paths); Assert.Contains("one.dll", paths); Assert.DoesNotContain("FullName", paths);
        output.GetStringBuilder().Clear();
        Show("p2", "Failed to load C:\\out\\one.dll\nFailed to load C:\\out\\two.dll\n");
        Assert.Contains("Failed to load C:\\out\\one.dll", output.ToString()); Assert.DoesNotContain("Directory:", output.ToString());
    }

    [Fact]
    public void LogAliasSurvivesRestartAndCannotReadAnotherConversation()
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-log-alias-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SessionStore(root); var session = new Session { Workspace = root };
            var ctx = new ToolExecutionContext(session, new Run(), store, CancellationToken.None);
            Directory.CreateDirectory(ctx.ArtifactDirectory);
            var path = Path.Combine(ctx.ArtifactDirectory, "process.jsonl");
            File.WriteAllText(path, Json.Serialize(new { stream = "stdout", text = new string('a', 4100) + "needle" }) + "\n");
            session.Processes.Add(new() { Id = "owned", DisplayNumber = 15, LogPath = path, State = "Exited" });
            store.Save(session, new("created", session.Id, null, new { }));
            var id = ArtifactCommands.ResolveId(ctx, "process:#15");
            var restored = store.Get(session.Id)!; var resumed = ctx with { Session = restored };
            Assert.Equal(id, ArtifactCommands.ResolveId(resumed, "process:#15"));
            var first = JsonNode.Parse(ArtifactAccess.Execute(resumed, "artifact_read", new JsonObject { ["artifactId"] = id, ["cursor"] = 0, ["maxCharacters"] = 4000 }).Content)!;
            var next = JsonNode.Parse(ArtifactAccess.Execute(resumed, "artifact_read", new JsonObject { ["artifactId"] = id, ["cursor"] = first["cursor"]!.GetValue<int>(), ["maxCharacters"] = 4000 }).Content)!;
            Assert.Contains("needle", next["text"]!.ToString());
            Assert.Throws<UserFacingException>(() => ArtifactCommands.ResolveId(ctx with { Session = new Session() }, "process:owned"));
            Assert.Equal(id, ArtifactCommands.ResolveId(resumed, id));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
