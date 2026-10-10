using System.Text.Json.Nodes;
using AgentSmith.Cli;
using AgentSmith.Core;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class PromptPresentationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnterKeepsImageVisibleWithOrWithoutText(bool imageOnly)
    {
        var image = new AttachmentRecord { Id = "image-one", Width = 1920, Height = 1080, Bytes = 2048, Path = "private-path" };
        var input = new ConsoleInput(false);
        if (!imageOnly) input.Restore("See what is missing?");
        input.AddImage(image);
        var submitted = input.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
        Assert.True(submitted.Ready); Assert.False(input.HasBlocks);
        var prompt = input.SubmittedPrompt!;
        Assert.Contains(prompt.Parts, p => p.Kind == "image" && p.Value == image.Id);
        var original = prompt.HistoryItem(); var saved = original.ToJsonString();
        var content = PromptPresentation.Content(original["content"], [image]);
        using var output = new StringWriter();
        new TerminalRenderer(output, false, 100).Render("user.submitted", new { text = submitted.Text, content });
        Assert.Contains("[Image 1 · 1920×1080 · 2 KiB]", output.ToString());
        Assert.Contains("Attachment: image-one", output.ToString());
        if (!imageOnly) Assert.Contains("See what is missing?", output.ToString());
        Assert.DoesNotContain("private-path", content.ToJsonString());
        Assert.Equal(saved, original.ToJsonString());
    }

    [Fact]
    public void OrderedImagesRenderOnSubmissionReplayAndQueueWithoutPayloadLeakage()
    {
        var parts = new JsonArray(
            new JsonObject { ["type"] = "input_text", ["text"] = "before" },
            new JsonObject { ["type"] = "input_image", ["attachment_id"] = "first", ["image_url"] = "data:image/png;base64,PRIVATE" },
            new JsonObject { ["type"] = "input_text", ["text"] = "between" },
            new JsonObject { ["type"] = "input_image", ["attachment_id"] = "second" });
        var content = PromptPresentation.Content(parts, []);
        Assert.DoesNotContain("base64", content.ToJsonString());
        using var live = new StringWriter();
        new TerminalRenderer(live, false, 100).Render("user.submitted", new { content });
        var rendered = live.ToString();
        Assert.True(rendered.IndexOf("before", StringComparison.Ordinal) < rendered.IndexOf("[Image 1]", StringComparison.Ordinal));
        Assert.True(rendered.IndexOf("[Image 1]", StringComparison.Ordinal) < rendered.IndexOf("between", StringComparison.Ordinal));
        Assert.True(rendered.IndexOf("between", StringComparison.Ordinal) < rendered.IndexOf("[Image 2]", StringComparison.Ordinal));
        using var replay = new StringWriter();
        new TerminalRenderer(replay, false, 100).Render("conversation.resumed", new { history = new[] { new { role = "user", content } } });
        Assert.Contains(rendered, replay.ToString());
        using var queued = new StringWriter();
        new TerminalRenderer(queued, false, 100).Render("prompt.queued", new { message = "Queued #1 · 2 image(s) attached", content });
        Assert.Contains("[Image 1]", queued.ToString()); Assert.Contains("[Image 2]", queued.ToString());
        Assert.DoesNotContain("between", queued.ToString());
    }

    [Fact]
    public void LegacyTextEventsStillRender()
    {
        using var output = new StringWriter();
        new TerminalRenderer(output, false, 100).Render("user.submitted", new { text = "Legacy message" });
        Assert.Contains("Legacy message", output.ToString());
    }
}
