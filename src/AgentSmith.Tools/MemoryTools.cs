using System.Text.Json.Nodes;
using AgentSmith.Core;

namespace AgentSmith.Tools;

public static class MemoryTools
{
    public static string Operation(ToolExecutionContext ctx, ToolCall call) => ctx.Session.Id + ":" + (ctx.Session.Runs.FirstOrDefault(r => r.Calls.Contains(call))?.Id ?? ctx.Run.Id) + ":" + call.Id;
    public static bool IsRead(string name) => name is "memory_read" or "memory_search";
    public static bool Enabled(IMemoryStore? store)
    {
        try { return store?.MemoryEnabled == true; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }
    public static void Check(ToolExecutionContext ctx, string name)
    {
        if (!Enabled(ctx.Store as IMemoryStore)) throw new UserFacingException("Memory is disabled or unavailable.");
        if (!IsRead(name) && ctx.Session.ParentConversationId is not null) throw new UserFacingException("Workers can read memories but cannot save them. Report proposed learnings to the parent.");
    }
    public static MemoryWrite PrepareWrite(ToolExecutionContext ctx, ToolCall call, JsonNode args)
    {
        Check(ctx, call.Name);
        var store = (IMemoryStore)ctx.Store;
        var id = call.Name == "memory_update" ? args["id"]!.GetValue<string>() : null;
        var prior = id is null ? null : store.ReadMemory(id) ?? throw new UserFacingException("Memory not found.");
        var revision = id is null ? 0 : args["expectedRevision"]!.GetValue<int>();
        if (prior is not null && (prior.UserAuthored || prior.Revision != revision)) throw new UserFacingException("Memory revision changed or is user-authored; read the current entry. User corrections cannot be overwritten automatically.");
        var title = prior?.Title ?? args["title"]!.GetValue<string>();
        var content = args["content"]!.GetValue<string>(); var tags = args["tags"]!.AsArray().Select(t => t!.GetValue<string>()).ToArray();
        MemoryPolicy.Validate(title, content, tags);
        var evidence = args["evidence"]!.AsArray().Select(t => t!.GetValue<string>()).Distinct().ToArray();
        if (evidence.Length is < 1 or > 8) throw new UserFacingException("Supply 1..8 evidence references: user:<history index> or tool:<completed call ID> from this conversation, including collected agent_result calls.");
        var sources = new List<MemorySource>();
        foreach (var reference in evidence)
        {
            if (reference.StartsWith("user:", StringComparison.Ordinal) && int.TryParse(reference[5..], out var index) && index >= 0 && index < ctx.Session.History.Count && ctx.Session.History[index]["role"]?.ToString() == "user")
                sources.Add(new(ctx.Session.Id, ctx.Run.Id, reference, "User statement"));
            else if (reference.StartsWith("tool:", StringComparison.Ordinal) && ctx.Session.Runs.SelectMany(r => r.Calls).FirstOrDefault(c => c.Id == reference[5..] && c.State == CallState.Completed && c.Result is not null && !c.Name.StartsWith("memory_", StringComparison.Ordinal)) is { } source)
                sources.Add(new(ctx.Session.Id, ctx.Session.Runs.First(r => r.Calls.Contains(source)).Id, reference, source.Name == "agent_result" ? "Agent conclusion" : "Observed evidence"));
            else throw new UserFacingException("Memory evidence must reference an existing user message or completed non-memory tool call in this conversation. Collect worker evidence with agent_result first.");
        }
        return new(id, revision, title, content, tags, prior?.OriginWorkspace ?? ctx.Workspace, sources.ToArray(), false, Operation(ctx, call));
    }
    public static ToolOutput Execute(ToolExecutionContext ctx, ToolCall call)
    {
        var store = (IMemoryStore)ctx.Store; var args = JsonNode.Parse(call.Prepared!.Arguments)!;
        try
        {
            Check(ctx, call.Name);
            if (call.Name == "memory_read") return new(Json.Serialize(store.ReadMemory(args["id"]!.GetValue<string>()) ?? throw new UserFacingException("Memory not found.")));
            if (call.Name == "memory_search") return new(Json.Serialize(store.SearchMemories(args["query"]!.GetValue<string>(), ctx.Workspace).Select(Preview)));
            var entry = store.MemoryReceipt(Operation(ctx, call)) ?? store.WriteMemory(PrepareWrite(ctx, call, args), ctx.Cancellation);
            // Library commit already contains its audit event and receipt. This event presents it in the conversation.
            ctx.Save("memory.saved", new { entry.Id, entry.Title, entry.Revision, message = "Remembered: " + entry.Title });
            return new(Json.Serialize(entry));
        }
        catch (UserFacingException ex) { return new(Json.Serialize(new { error = ex.Message })); }
        catch (Exception ex) when (IsRead(call.Name) && ex is not (OutOfMemoryException or OperationCanceledException))
        { ctx.Emit?.Invoke(new("memory.warning", ctx.Session.Id, ctx.Run.Id, new { message = "Memory read unavailable; continuing without that evidence." })); return new(Json.Serialize(new { error = "Memory read unavailable." })); }
    }
    public static object Preview(MemoryEntry entry) => new { entry.Id, entry.Revision, entry.Title, excerpt = entry.Content[..Math.Min(300, entry.Content.Length)], entry.OriginWorkspace, entry.UpdatedAt, entry.UserAuthored };
}
