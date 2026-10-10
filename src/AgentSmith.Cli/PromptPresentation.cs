using System.Text.Json.Nodes;
using AgentSmith.Core;

namespace AgentSmith.Cli;

// Display metadata only. Never copy attachment paths or provider image payloads into events.
internal static class PromptPresentation
{
    internal static JsonArray Content(JsonNode? content, IReadOnlyList<AttachmentRecord> attachments)
    {
        if (content is not JsonArray parts)
            return new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = content?.ToString() ?? "" });
        var result = new JsonArray();
        foreach (var part in parts.OfType<JsonObject>())
        {
            if (part["type"]?.ToString() == "input_image")
            {
                var id = part["attachment_id"]?.ToString();
                var image = attachments.FirstOrDefault(a => a.Id == id);
                var display = new JsonObject { ["type"] = "input_image", ["attachment_id"] = id };
                if (image is not null) { display["width"] = image.Width; display["height"] = image.Height; display["bytes"] = image.Bytes; }
                result.Add(display);
            }
            else if (part["text"] is { } text)
                result.Add(new JsonObject { ["type"] = "input_text", ["text"] = text.ToString() });
        }
        return result;
    }
}
