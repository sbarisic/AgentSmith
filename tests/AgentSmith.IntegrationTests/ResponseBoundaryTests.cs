using AgentSmith.Cli;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class ResponseBoundaryTests
{
    [Fact]
    public void AutomaticCollectionShowsWorkerOutcomeWithoutClaimingSuccess()
    {
        using var output = new StringWriter(); var renderer = new TerminalRenderer(output, false, 100);
        renderer.Render("agent.collected", new { number = 9, state = "Cancelled", artifactId = "report-id", automatic = true });
        Assert.Contains("Collected Agent #9", output.ToString()); Assert.Contains("Cancelled", output.ToString());
        Assert.Contains("/artifact report-id", output.ToString()); Assert.DoesNotContain("Completed", output.ToString());
    }
    [Fact]
    public void CompletionFeedbackShowsActualReasonAndNextStep()
    {
        using var output = new StringWriter(); var renderer = new TerminalRenderer(output, false, 100);
        renderer.Render("run.completion.pending", new { code = "worker_results_pending", reason = "Agent #2 (Cancelled) needs collection.", next = "Call agent_result for the saved worker ID." });
        Assert.Contains("Agent #2 (Cancelled)", output.ToString()); Assert.Contains("Next: Call agent_result", output.ToString());
        renderer.Render("status", new { state = "Paused", reason = "Missing report", completionRequirement = new { reason = "Document report pending", next = "Call workflow_finish" } });
        Assert.Contains("Missing report", output.ToString()); Assert.Contains("Document report pending", output.ToString()); Assert.Contains("Call workflow_finish", output.ToString());
    }
    [Fact]
    public void AdjacentModelResponsesFlushMarkdownAndDoNotConcatenate()
    {
        using var output = new StringWriter(); var renderer = new TerminalRenderer(output, false, 100);
        renderer.Render("text.delta", new { text = "First **answer**." });
        renderer.Render("model.completed", new { });
        Assert.Contains("First answer.", output.ToString());
        renderer.Render("model.requested", new { });
        renderer.Render("text.delta", new { text = "Second **answer**." });
        renderer.Render("model.completed", new { });
        Assert.Contains("Second answer.", output.ToString());
        Assert.DoesNotContain("answer.Second", output.ToString());
        Assert.DoesNotContain("**", output.ToString());
    }
}
