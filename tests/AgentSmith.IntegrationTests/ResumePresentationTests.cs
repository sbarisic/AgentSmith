using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSmith.Cli;
using AgentSmith.Core;
using AgentSmith.Storage;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class ResumePresentationTests
{
    [Theory]
    [InlineData(32)]
    [InlineData(160)]
    public void ResumeCardKeepsExcerptsNarrowAndNextActionFirst(int columns)
    {
        var run = new Run { State = RunState.Completed };
        var s = new Session { Runs = [run] };
        s.Reports.Add(new WorkflowReport { State = "Completed", Workflow = WorkflowMode.Document, Revisions = [new()
        {
            RunId = run.Id, Summary = string.Concat(Enumerable.Repeat("Reviewed README.md against source code. ", 40)),
            Coverage = new string('x', 500), Limitations = ["Tests were not rerun.\u001b[2J"]
        }] });
        using var output = new StringWriter();
        var renderer = new TerminalRenderer(output, false); renderer.Resize(columns);
        renderer.Render("conversation.resumed", new { history = Array.Empty<object>(), resume = ResumePresentation.Build(s, new([], null)) });
        var text = output.ToString();
        Assert.True(text.IndexOf("Ready for", StringComparison.Ordinal) < text.IndexOf("Summary", StringComparison.Ordinal));
        Assert.Contains("Summary (excerpt)", text);
        Assert.Contains("Coverage (excerpt)", text);
        Assert.Contains("Limitations (excerpts)", text);
        Assert.Contains("…", text);
        Assert.DoesNotContain('\u001b', text);
        var card = text[text.IndexOf("  ┌─ Saved state", StringComparison.Ordinal)..];
        Assert.All(card.Split('\n'), line => Assert.True(line.TrimEnd('\r').Length <= Math.Min(columns, 100), line));
        Assert.True(card.Split("Reviewed README.md").Length < 20);
    }

    [Fact]
    public void PersistedCompletionAppearsAfterOldProgressWithoutExecutingWork()
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-resume-summary-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SessionStore(root);
            var run = new Run { State = RunState.Completed, ExecutionMilliseconds = 430000 };
            var s = new Session { Runs = [run], History = [new JsonObject { ["role"] = "assistant", ["content"] = "I am updating README." }] };
            var report = new WorkflowReport { Workflow = WorkflowMode.Document, State = "Completed", Task = "Update documentation", Revisions = [new() { RunId = run.Id, Revision = 1, Summary = "Updated README from source. Checked 11 local links.", Coverage = "README.md", Limitations = ["Tests were not rerun."] }] };
            s.Reports.Add(report); s.ActiveReportId = report.Id;
            store.Save(s, new("test", s.Id, run.Id, new { }));
            store.Enqueue(s.Id, "Follow-up"); store.HoldQueue(s.Id, "Restart");
            var restored = new SessionStore(root).Get(s.Id)!;
            var state = Json.Serialize(restored); var queue = store.Queue(s.Id);
            using var output = new StringWriter();
            new TerminalRenderer(output, false).Render("conversation.resumed", new { history = restored.History, resume = ResumePresentation.Build(restored, queue) });
            var text = output.ToString();
            Assert.True(text.IndexOf("Saved state · Completed", StringComparison.Ordinal) > text.IndexOf("I am updating README.", StringComparison.Ordinal));
            Assert.Contains("Updated README from source", text);
            Assert.Contains("7m 10s", text);
            Assert.Contains("Tests were not rerun", text);
            Assert.Contains("Full report: /report " + report.Id, text);
            Assert.Contains("No continuation is required", text);
            Assert.Contains("held: Restart", text);
            Assert.Equal(state, Json.Serialize(restored));
            Assert.Null(store.Queue(s.Id).Items.Single().RunId);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(RunState.Running, "interrupted")]
    [InlineData(RunState.AwaitingUser, "saved question")]
    [InlineData(RunState.AwaitingPlan, "plan decision")]
    [InlineData(RunState.RecoveryRequired, "/recover")]
    [InlineData(RunState.Cancelled, "cancelled")]
    [InlineData(RunState.Failed, "failed")]
    public void IncompleteRunsNeverClaimCompletion(RunState state, string expected)
    {
        var s = new Session { Runs = [new() { State = state }] };
        var data = JsonSerializer.SerializeToElement(ResumePresentation.Build(s, new([], null)), Json.Options);
        Assert.Contains(expected, data.GetProperty("next").GetString());
        Assert.DoesNotContain("No continuation", data.GetProperty("next").GetString());
    }

    [Fact]
    public void OlderReportIsNotPresentedAsTheLatestRunAndLargeSummariesAreBounded()
    {
        var s = new Session { Runs = [new() { State = RunState.Completed }] };
        var report = new WorkflowReport { Workflow = WorkflowMode.Review, State = "Completed", Revisions = [new() { RunId = "older", Summary = new string('x', 8000) }] };
        s.Reports.Add(report); s.ActiveReportId = report.Id;
        var old = JsonSerializer.SerializeToElement(ResumePresentation.Build(s, new([], null)), Json.Options);
        Assert.Equal(JsonValueKind.Null, old.GetProperty("report").ValueKind);
        report.Current!.RunId = s.Runs[0].Id;
        var current = JsonSerializer.SerializeToElement(ResumePresentation.Build(s, new([], null)), Json.Options);
        Assert.True(current.GetProperty("report").GetProperty("summary").GetString()!.Length <= 1601);
    }
}
