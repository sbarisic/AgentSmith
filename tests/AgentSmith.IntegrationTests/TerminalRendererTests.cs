using AgentSmith.Cli;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class TerminalRendererTests
{
    [Fact]
    public void FragmentedAnswerStaysReadableAndMetadataBecomesASummary()
    {
        using var output = new StringWriter();
        var renderer = new TerminalRenderer(output, false);
        renderer.Render("run.started", new { });
        renderer.Render("model.requested", new { context = "PRIVATE-CONTEXT-MARKER" });
        renderer.Render("text.delta", new { text = "Hello" });
        renderer.Render("text.delta", new { text = " world\n\n- First" });
        renderer.Render("text.delta", new { text = " item" });
        renderer.Render("model.completed", new { usage = "{\"input_tokens\":1234,\"output_tokens\":56,\"attribution\":{\"internal\":true}}" });
        renderer.Render("run.outcome", new { state = "Completed" });
        var text = output.ToString();
        Assert.Contains("Hello world\n  │ \n  │ • First item", text.Replace("\r\n", "\n"));
        Assert.Contains("1,234 in / 56 out tokens", text);
        Assert.DoesNotContain("PRIVATE-CONTEXT-MARKER", text);
        Assert.DoesNotContain("attribution", text);
        Assert.False(text.Contains('\u001b'));
    }

    [Fact]
    public void ResumeShowsMessagesWithoutOpaqueProviderState()
    {
        using var output = new StringWriter();
        var renderer = new TerminalRenderer(output, false, 40);
        renderer.Render("conversation.resumed", new
        {
            model = "example-model-with-a-long-name",
            workspace = "C:/projects/a-long-project-name-with-more-characters",
            history = new object[]
            {
                new { role = "user", content = "Explain the project" },
                new { type = "reasoning", encrypted_content = "OPAQUE-PROVIDER-STATE" },
                new { role = "assistant", content = new[] { new { type = "output_text", text = "It is a CLI." } } }
            }
        });
        var text = output.ToString();
        Assert.Contains("Explain the project", text);
        Assert.Contains("It is a CLI.", text);
        Assert.DoesNotContain("OPAQUE-PROVIDER-STATE", text);
        Assert.Contains("Remaining usage unavailable", text);
        foreach (var line in text.Split('\n').Where(l => l.Contains('│'))) Assert.True(line.TrimEnd('\r').Length <= 40);
    }

    [Fact]
    public void QuestionsAreNotPrintedTwiceAndUntrustedControlsStayInert()
    {
        using var output = new StringWriter();
        var renderer = new TerminalRenderer(output, false);
        var question = new { id = "q1", text = "Choose \u001b[2J a file", choices = new[] { "one", "two" } };
        renderer.Render("question.pending", question);
        renderer.Render("question.pending", question);
        renderer.Prompt();
        var text = output.ToString();
        Assert.Equal(1, text.Split("Choose").Length - 1);
        Assert.False(text.Contains('\u001b'));
        Assert.Contains("you ›", text);
    }

    [Fact]
    public void EveryExecutionSegmentIsTimedWithoutUserWaitingTime()
    {
        using var output = new StringWriter();
        var clock = new TestClock();
        var renderer = new TerminalRenderer(output, false, timeProvider: clock);
        renderer.Render("run.started", new { });
        clock.Advance(TimeSpan.FromSeconds(3));
        renderer.Render("run.outcome", new { state = "AwaitingUser" });
        clock.Advance(TimeSpan.FromMinutes(5));
        renderer.Render("answer.submitted", new { text = "Arabic\n\nNote: For travel" });
        renderer.Render("run.started", new { });
        clock.Advance(TimeSpan.FromSeconds(2));
        renderer.Render("run.outcome", new { state = "Completed" });
        var text = output.ToString();
        Assert.Contains("Question ready  ·  Executed in 3.0s", text);
        Assert.Contains("✓ Done  ·  Executed in 2.0s", text);
        Assert.Contains("  ╭─ You", text);
        Assert.Contains("  │ Note: For travel", text);
        Assert.DoesNotContain("303.0s", text);
        Assert.False(text.Contains('\u001b'));
    }

    private sealed class TestClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }
}
