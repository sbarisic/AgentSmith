using System.Text.Json;
using AgentSmith.Core;

namespace AgentSmith.Cli;

public sealed partial class TerminalRenderer
{
    private readonly HashSet<string> steeringPreviews = [];
    private readonly Queue<string> steeringPreviewOrder = new();

    private void SteeringActivity(JsonElement data, bool listing = false)
    {
        var preview = Text(data, "preview");
        var id = Activity.Clean(Text(data, "id"), 80);
        var target = Activity.Clean(Text(data, "target"), 80);
        var stage = Text(data, "message");
        if (stage.Length == 0) stage = Text(data, "state");
        if (target.Length == 0) target = "AgentSmith / worker"; // Older records lack a recipient label.
        if (preview.Length == 0)
        {
            MessageText("› Steering · " + (stage.Length > 0 ? Activity.Clean(stage, 300) : "Update"));
            return;
        }
        var seen = id.Length > 0 && steeringPreviews.Contains(id);
        if (listing || Verbose || !seen)
        {
            var sender = Text(data, "sender") switch { "user" => "You", "parent" => "Parent agent", _ => "Sender not recorded" };
            MessageText("› " + (target == "AgentSmith" ? "Steer " : "Message ") + target + " · " + sender);
            MessageText("  “" + SteeringNotice.PreviewText(TerminalControlFilter.Strip(preview), listing || Verbose ? 1000 : 400) + "”");
            MessageText("  " + Activity.Clean(stage, 80) + " · " + (listing ? id : Activity.Clean(id, 12)) + (listing ? "" : " · /steering"), "dim");
        }
        else MessageText("› Steering · " + target + " · " + Activity.Clean(stage, 80) + " · " + Activity.Clean(id, 12), "dim");
        if (!seen && id.Length > 0)
        {
            steeringPreviews.Add(id); steeringPreviewOrder.Enqueue(id);
            if (steeringPreviewOrder.Count > 512) steeringPreviews.Remove(steeringPreviewOrder.Dequeue());
        }
    }
}
