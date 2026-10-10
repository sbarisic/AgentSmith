using AgentSmith.Cli;
using AgentSmith.Core;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class FailureRenderingTests
{
    [Fact]
    public void RejectionHasOperationTargetReasonEffectAndNextStepWithoutColor()
    {
        using var output = new StringWriter(); var renderer = new TerminalRenderer(output, false, 100);
        var failure = Failures.Tool(new FileNotFoundException(), new() { Name = "file_read", Arguments = "{\"path\":\"src/missing.cs\"}" }, true);
        renderer.Render("tool.rejected", new { id = "call-1", result = Json.Serialize(new { error = failure.Reason, failure }) });
        var text = output.ToString();
        Assert.Contains("Reading file", text); Assert.Contains("src/missing.cs", text);
        Assert.Contains("Reason:", text); Assert.Contains("Effect:", text); Assert.Contains("Next:", text);
        Assert.Contains("/details call-1", text); Assert.DoesNotContain('\u001b', text);
    }

    [Fact]
    public void FailedWorkerShowsBudgetAndRetainedEditsRatherThanEmptyOutput()
    {
        using var output = new StringWriter(); var renderer = new TerminalRenderer(output, false, 120);
        var worker = new WorkerRecord { Number = 8, State = "Failed", RequestLimit = 20, AppliedCheckpointCount = 3 };
        renderer.Render("agent.finished", new { agentId = "worker", number = 8, state = "Failed", durationMilliseconds = 170000,
            failure = Failures.Worker(worker, PauseReason.RequestLimit, 20) });
        var text = output.ToString();
        Assert.Contains("Agent #8", text); Assert.Contains("20/20", text);
        Assert.Contains("3 applied file operation(s)", text); Assert.Contains("not rolled back", text);
        Assert.Contains("/agent #8", text); Assert.Contains("/changes", text);
    }

    [Fact]
    public void LegacyErrorWithCheckpointDoesNotDisappearBehindOperationRendering()
    {
        using var output = new StringWriter(); var renderer = new TerminalRenderer(output, false);
        renderer.Render("tool.completed", new { id = "old-call", name = "file_create", content = Json.Serialize(new { error = "Operation was not replayed.", checkpointId = "checkpoint" }) });
        Assert.Contains("Operation was not replayed.", output.ToString());
        Assert.Contains("checkpoint", output.ToString());
        Assert.DoesNotContain("no workspace changes", output.ToString());
    }
}
