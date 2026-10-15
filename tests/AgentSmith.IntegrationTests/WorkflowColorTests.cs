using AgentSmith.Cli;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class WorkflowColorTests
{
    [Fact]
    public void CompletionShowsResultingWorkflow()
    {
        using var output = new StringWriter();
        var renderer = new TerminalRenderer(output, false);
        renderer.Render("workflow.finished", new { id = "report", artifactId = "artifact", workflow = "Work", summary = "Partial coverage recorded." });
        Assert.Contains("Report saved", output.ToString());
        Assert.Contains("Workflow: Work", output.ToString());
        Assert.Contains("Partial coverage recorded.", output.ToString());
    }

    [Theory]
    [InlineData("Work", "92")]
    [InlineData("Plan", "96")]
    [InlineData("Document", "95")]
    [InlineData("Review", "93")]
    public void WorkflowColorsGuidesButLeavesBodyWhite(string workflow, string ansi)
    {
        using var output = new StringWriter();
        var renderer = new TerminalRenderer(output, true);
        renderer.Render("run.started", new { workflow });
        renderer.Render("text.delta", new { text = "Plain body\n" });
        renderer.Render("run.outcome", new { state = "Completed", workflow = "Work" });
        renderer.Prompt();
        var text = output.ToString();
        Assert.Contains($"\u001b[{ansi}m  ┌─", text);
        Assert.Contains($"\u001b[{ansi}m  │ ", text);
        Assert.Contains($"\u001b[{ansi}m  └─", text);
        Assert.Contains("\u001b[97mPlain body", text);
        Assert.Contains("\u001b[92m You", text);
    }

    [Fact]
    public void ResumeUsesRecordedMessageAndReportWorkflowsThenRestoresCurrentColor()
    {
        using var output = new StringWriter();
        var renderer = new TerminalRenderer(output, true);
        renderer.Render("conversation.resumed", new
        {
            workflow = "Work", history = new[]
            {
                new { role = "assistant", content = "Old untagged", _workflow = (string?)null },
                new { role = "assistant", content = "Documentation text", _workflow = (string?)"Document" }
            },
            resume = new { state = "Completed", workflow = "Work", next = "Ready", report = new { workflow = "Document", state = "Completed" } }
        });
        renderer.Prompt();
        var text = output.ToString();
        Assert.Contains("\u001b[90m AgentSmith", text);
        Assert.Contains("\u001b[95m AgentSmith", text);
        Assert.Contains("\u001b[95m Saved state", text);
        Assert.Contains("\u001b[92m You", text);
    }

    [Fact]
    public void NoColorKeepsTextAndGuidesWithoutEscapes()
    {
        using var output = new StringWriter();
        var renderer = new TerminalRenderer(output, false);
        renderer.Render("run.started", new { workflow = "Review" });
        renderer.Render("text.delta", new { text = "Body" });
        renderer.Render("run.outcome", new { state = "Completed" });
        Assert.DoesNotContain('\u001b', output.ToString());
        Assert.Contains("│ Body", output.ToString());
    }
}
