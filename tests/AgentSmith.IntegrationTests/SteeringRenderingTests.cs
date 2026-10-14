using AgentSmith.Cli;
using AgentSmith.Core;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class SteeringRenderingTests
{
    [Fact]
    public void AudioAnalysisUsesContentBlockAndRetainedArtifact()
    {
        using var output = new StringWriter(); var renderer = new TerminalRenderer(output, false);
        var call = new ToolCall { Id = "listen", Name = "audio_listen", Arguments = Json.Serialize(new { path = "tone.wav", question = "What sound?", startSeconds = 2, durationSeconds = 5 }) };
        renderer.Render("tool.started", new { id = call.Id, name = call.Name, activity = Activity.Describe(new(), call) });
        renderer.Render("tool.completed", new { id = call.Id, name = call.Name, content = Json.Serialize(new {
            kind = "audio-analysis", path = "tone.wav", model = "audio-model", analysis = "Tone\u001b[31m", startSeconds = 2,
            durationSeconds = 5, partial = true, truncated = false, artifactId = "audio-artifact" }) });
        var text = output.ToString();
        Assert.Contains("Listen to audio", text); Assert.Contains("Audio analysis", text);
        Assert.Contains("Selected clip only", text); Assert.Contains("/artifact audio-artifact", text);
        Assert.DoesNotContain('\u001b', text);
    }
    [Fact]
    public void WorkerActionsUseAliasesAndHumanLabels()
    {
        var session = new Session();
        var workers = new[] { new WorkerRecord { Id = "opaque-worker-id", Number = 11 } };
        var wait = Activity.Describe(session, new() { Name = "agent_wait", Arguments = Json.Serialize(new { agentIds = new[] { workers[0].Id }, waitMilliseconds = 360000 }) }, workers);
        var message = Activity.Describe(session, new() { Name = "agent_message", Arguments = Json.Serialize(new { agentId = workers[0].Id, text = "Only fix\n the browser host." }) }, workers);
        Assert.Equal("Wait for", wait.Action); Assert.Equal("Agent #11", wait.Target); Assert.Equal("up to 6m", wait.Options);
        Assert.Equal("Message", message.Action); Assert.Equal("Agent #11", message.Target); Assert.Equal("Only fix the browser host.", message.Options);
        using var output = new StringWriter(); var renderer = new TerminalRenderer(output, false);
        renderer.Render("agent.started", new { agentId = workers[0].Id, number = 11, profile = "bonsai-edit", task = "Fix the browser host", writePaths = new[] { "src/Host.cs" } });
        renderer.Render("tool.started", new { id = "wait-call", name = "agent_wait", activity = wait });
        Assert.Contains("› Spawn Agent #11", output.ToString()); Assert.Contains("› Wait for Agent #11", output.ToString());
        Assert.DoesNotContain("opaque-worker-id", output.ToString()); Assert.DoesNotContain("agent_spawn", output.ToString());
    }

    [Fact]
    public void SteeringTextAppearsOnceWithSenderAndRecipientThenDeliveryUpdates()
    {
        using var output = new StringWriter(); var renderer = new TerminalRenderer(output, false);
        var message = new SteeringMessage { Id = "message-123", RootId = "parent", ConversationId = "child", Text = "Inspect the browser project first.", Sender = "parent" };
        renderer.Render("tool.started", new { id = "call", name = "agent_message", activity = new ActivitySummary("call", "Message", "Agent #11", "Inspect the browser project first.") });
        renderer.Render("steering.saved", SteeringNotice.Create(message, "Steering saved", 11));
        renderer.Render("tool.completed", new { id = "call", name = "agent_message", content = Json.Serialize(new { messageId = message.Id, note = "Saved for delivery, not confirmation of compliance." }) });
        renderer.Render("steering.incorporated", SteeringNotice.Create(message, "Added to next request", 11));
        renderer.Render("steering.sent", SteeringNotice.Create(message, "Sent to model", 11));
        var text = output.ToString();
        Assert.Contains("› Message Agent #11 · Parent agent", text);
        Assert.Equal(1, text.Split(message.Text).Length - 1);
        Assert.Contains("Steering saved", text); Assert.Contains("Added to next request", text); Assert.Contains("Sent to model", text);
        Assert.DoesNotContain("confirmation of compliance", text); Assert.Contains("/steering", text);
    }

    [Fact]
    public void ListingRestoresPreviewsAndKeepsFullIdsForRemoval()
    {
        using var output = new StringWriter(); var renderer = new TerminalRenderer(output, false);
        var message = new SteeringMessage { Id = "01234567890123456789012345678901", RootId = "parent", ConversationId = "parent", Text = "Focus on input handling." };
        var notice = SteeringNotice.Create(message, "Pending");
        renderer.Render("steering.saved", notice);
        renderer.Render("steering.status", new { items = new[] { notice }, message = "Pending messages stay with their original run." });
        var text = output.ToString(); Assert.Equal(2, text.Split(message.Text).Length - 1);
        Assert.Contains("› Steer AgentSmith · You", text); Assert.Contains(message.Id, text);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PreviewsAreBoundedRedactedAndDoNotChangeSavedText(bool verbose)
    {
        var message = new SteeringMessage { RootId = "root", ConversationId = "root", Text = "token=PRIVATE_SECRET\n" + new string('x', 2000) + "END_MARKER" };
        var notice = SteeringNotice.Create(message, "Steering saved");
        Assert.DoesNotContain("PRIVATE_SECRET", Json.Serialize(notice)); Assert.True(notice.Preview.Length <= 1001);
        Assert.Contains("PRIVATE_SECRET", message.Text);
        using var output = new StringWriter(); var renderer = new TerminalRenderer(output, false, 60) { Verbose = verbose };
        renderer.Render("steering.saved", notice);
        Assert.DoesNotContain("END_MARKER", output.ToString()); Assert.DoesNotContain("PRIVATE_SECRET", output.ToString());
    }

    [Fact]
    public void OldEventsStillShowTheirDeliveryMessage()
    {
        using var output = new StringWriter(); var renderer = new TerminalRenderer(output, false);
        renderer.Render("steering.sent", new { id = "old", message = "Sent to model · old" });
        Assert.Contains("Sent to model · old", output.ToString());
    }
}
