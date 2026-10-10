using System.Text.Json.Nodes;

namespace AgentSmith.Core;

public static class ToolEffects
{
    public static bool IsHttpWrite(ToolCall call)
    {
        if (call.Name != "http_request") return false;
        try { return JsonNode.Parse(call.Prepared?.Arguments ?? call.Arguments)?["method"]?.ToString() is not ("GET" or "HEAD"); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { return true; }
    }
    public static bool HasEffects(ToolDefinition definition, ToolCall call) => definition.HasEffects || call.Prepared?.Effect == ToolEffect.Network || IsHttpWrite(call);
}
