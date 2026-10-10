using System.Text.Json;
using AgentSmith.Core;

namespace AgentSmith.Cli;

public sealed partial class TerminalRenderer
{
    private bool ToolFailure(JsonElement result, JsonElement call)
    {
        if (FailureBlock(result, Text(call, "id"))) return true;
        if (!result.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(error.GetString())) return false;
        // Legacy/recovered results may not describe effects. Do not infer success or rollback.
        StatusLine("Error", activeActivities.GetValueOrDefault(Text(call, "id")) ?? ToolLabel(Text(call, "name")), "status-error");
        MessageText("  Reason: " + Activity.Clean(error.GetString()!, 800));
        if (Text(result, "checkpointId").Length > 0) MessageText("  Checkpoint: " + Activity.Clean(Text(result, "checkpointId"), 80) + " · /changes", "dim");
        if (Text(result, "processId").Length > 0) MessageText("  Process: " + Activity.Clean(Text(result, "processId"), 80) + " · /processes", "dim");
        if (Text(call, "id").Length > 0) MessageText("  Details: /details " + Activity.Clean(Text(call, "id"), 80), "dim");
        return true;
    }

    private bool FailureBlock(JsonElement data, string callId = "", bool heading = true)
    {
        if (!data.TryGetProperty("failure", out var failure) || failure.ValueKind != JsonValueKind.Object) return false;
        var operation = Text(failure, "operation");
        var target = Activity.Clean(Text(failure, "target"), 400);
        var category = Text(failure, "category");
        if (heading) StatusLine(category.Length > 0 ? category : "Error",
            (operation.Length > 0 ? ToolLabel(operation).Replace('_', ' ') : "Operation") + (target.Length > 0 ? " · " + target : ""),
            category == "Rejected" ? "status-warning" : "status-error");
        foreach (var (key, label) in new[] { ("reason", "Reason"), ("effect", "Effect"), ("next", "Next") })
            if (Text(failure, key).Length > 0) MessageText("  " + label + ": " + Activity.Clean(Text(failure, key), 800));
        var code = Activity.Clean(Text(failure, "code"), 80);
        if (code.Length > 0) MessageText("  " + code + (callId.Length > 0 ? " · /details " + Activity.Clean(callId, 80) : ""), "dim");
        return true;
    }
}
