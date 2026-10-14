using System.Text.Json.Nodes;
using AgentSmith.Core;

namespace AgentSmith.Cli;

/// <summary>Available prompts in one conversation, never a global history store.</summary>
internal sealed class ComposerHistory
{
    internal sealed record Entry(UserPrompt Prompt, bool Literal);
    private readonly List<Entry> entries = [];
    private string? conversation;
    internal int Count => entries.Count;
    internal Entry this[int index] => new(Clone(entries[index].Prompt), entries[index].Literal);
    internal static UserPrompt Clone(UserPrompt prompt) => new() { Parts = [.. prompt.Parts] };

    internal bool SetConversation(Session session)
    {
        if (conversation == session.Id) return false;
        conversation = session.Id; entries.Clear();
        foreach (var item in session.History.Where(i => i["role"]?.ToString() == "user"))
        {
            var prompt = new UserPrompt();
            if (item["content"] is JsonValue value) prompt = UserPrompt.Text(value.ToString());
            else if (item["content"] is JsonArray parts)
                foreach (var part in parts.OfType<JsonObject>())
                {
                    if (part["type"]?.ToString() == "input_text" && part["text"] is not null)
                        prompt.Parts.Add(new("text", part["text"]!.ToString()));
                    else if (part["type"]?.ToString() == "input_image" && part["attachment_id"] is not null)
                        prompt.Parts.Add(new("image", part["attachment_id"]!.ToString()));
                }
            Add(prompt, prompt.Parts.Any(p => p.Kind == "image") || prompt.PlainText.Contains('\n') || prompt.PlainText.TrimStart().StartsWith('/'));
        }
        return true;
    }
    internal void Add(UserPrompt prompt, bool literal)
    {
        literal |= prompt.Parts.Any(p => p.Kind == "image");
        if (!literal && prompt.PlainText.TrimStart().StartsWith('/')) return;
        try { prompt.Validate(); } catch (UserFacingException) { return; }
        entries.Add(new(Clone(prompt), literal));
        if (entries.Count > 100) entries.RemoveAt(0);
    }
}
