using AgentSmith.Cli;
using AgentSmith.Core;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class ActivityRenderingTests
{
    [Fact]
    public void EmptyPollsAreHiddenButCompletionAndNewOutputRemain()
    {
        var output = new StringWriter(); var renderer = new TerminalRenderer(output, false);
        void Poll(string state, object[] chunks) => renderer.Render("tool.completed", new { name = "process_poll", content = Json.Serialize(new { processId = "long-guid", alias = "#1", state, durationMilliseconds = 1234, output = chunks, exitCode = state == "Exited" ? 0 : (int?)null, terminationConfirmed = state == "Exited" }) });
        Poll("Running", []); var first = output.ToString(); Poll("Running", []); Assert.Equal(first, output.ToString());
        Poll("Running", [new { stream = "stdout", text = "hel" }]); Poll("Exited", [new { stream = "stdout", text = "lo\nsecond\n" }]);
        var text = output.ToString(); Assert.Contains("hello", text); Assert.Contains("second", text); Assert.Contains("exit 0", text); Assert.DoesNotContain("long-guid", text); Assert.Equal(1, text.Split("stdout").Length - 1);
    }
    [Fact]
    public void BoundedOutputPreservesHeadTailAndControlsAreInert()
    {
        var text = string.Join('\n', Enumerable.Range(0, 200).Select(i => "line " + i + " " + new string('é', 100))) + "\u001b[2J";
        var preview = TerminalRenderer.BoundedPreview(text); Assert.Contains("line 0", preview); Assert.Contains("line 199", preview); Assert.Contains("omitted", preview); Assert.DoesNotContain('\u001b', preview); Assert.True(System.Text.Encoding.UTF8.GetByteCount(preview) <= 4096);
    }
    [Fact]
    public void ArgumentsAndTaskTitlesUseReadableEvents()
    {
        var output = new StringWriter(); var renderer = new TerminalRenderer(output, false);
        renderer.Render("tool.started", new { name = "file_read", activity = new ActivitySummary("id", "Read", "src/Program.cs", "lines 40–120") });
        renderer.Render("plan.updated", new { revision = 1, steps = new[] { new { id = "S1", status = "In progress", description = "Inspect entry point" } } });
        Assert.Contains("Read src/Program.cs", output.ToString()); Assert.Contains("Inspect entry point", output.ToString());
    }
}
