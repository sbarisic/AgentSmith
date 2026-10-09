using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentSmith.Core;

public sealed record ActivitySummary(string CallId, string Action, string Target, string Options, string? ProcessId = null);

public static partial class Activity
{
    [GeneratedRegex(@"(?i)(bearer\s+\S+|sk-[\w-]{12,}|(?:password|token|secret|api[_-]?key)\s*[=:]\s*[^\s,;]+)")]
    private static partial Regex Secrets();
    public static string Clean(string value, int limit = 240)
    {
        value = Secrets().Replace(value, "[redacted]");
        value = string.Concat(value.Where(c => !char.IsControl(c) && c is not '\u202e' and not '\u202d'));
        return value.Length <= limit ? value : value[..limit] + "…";
    }
    public static string ProcessId(Session session, string id) => id.StartsWith('#')
        ? session.Processes.SingleOrDefault(p => "#" + p.DisplayNumber == id)?.Id ?? throw new UserFacingException("Unknown process alias.") : id;
    public static ActivitySummary Describe(Session session, ToolCall call)
    {
        JsonObject a;
        try { a = JsonNode.Parse(call.Prepared?.Arguments ?? call.Arguments)?.AsObject() ?? new(); }
        catch { return new(call.Id, Clean(call.Name), "", ""); }
        string S(string key) => a[key]?.ToString() ?? "";
        var processId = S("processId");
        var process = session.Processes.FirstOrDefault(p => p.Id == processId || p.Id == call.Prepared?.OperationId);
        var label = process is null ? processId : "#" + process.DisplayNumber;
        var target = S("path"); var options = "";
        var action = call.Name switch
        {
            "file_read" => "Read", "workspace_list" => "List", "text_search" => "Search", "file_create" => "Create",
            "file_apply_patch" => "Edit", "file_move" => "Move", "file_delete" => "Delete", "process_start" => "Run",
            "process_poll" => "Poll", "process_input" => "Input", "process_stop" => "Stop", "dotnet_build" => "Build",
            "dotnet_test" => "Test", "user_ask" => "Ask", "plan_update" => "Update plan", _ => call.Name.Replace('_', ' ')
        };
        switch (call.Name)
        {
            case "agent_spawn": target = S("profile"); options = S("task"); break;
            case "agent_result": case "agent_cancel": target = S("agentId"); break;
            case "agent_wait": target = S("agentIds"); options = "wait " + S("waitMilliseconds") + "ms"; break;
            case "agent_finish": target = S("status"); options = S("summary"); break;
            case "file_read": options = $"line {S("startLine")} · {S("lineCount")} lines"; break;
            case "workspace_list": options = "limit 200"; break;
            case "text_search": target = '"' + S("query") + '"'; options = S("path"); break;
            case "file_move": target = S("source") + " → " + S("destination"); break;
            case "process_start":
                target = Path.GetFileName(S("executable"));
                if (a["arguments"] is JsonArray args)
                {
                    var hideNext = false;
                    foreach (var arg in args)
                    {
                        var value = arg?.ToString() ?? "";
                        target += " " + (hideNext ? "[redacted]" : Clean(value, 100));
                        hideNext = Regex.IsMatch(value, @"(?i)^[-/]*(password|token|secret|api[-_]?key|authorization)$");
                    }
                }
                options = "cwd " + S("workingDirectory") + (label.Length > 0 ? " · process " + label : ""); break;
            case "process_input": target = label; options = $"{System.Text.Encoding.UTF8.GetByteCount(S("text"))} bytes · EOF {S("closeStdin")}"; break;
            case "process_poll": case "process_stop": target = label; break;
            case "dotnet_build": case "dotnet_test": target = S("project"); options = string.Join(" · ", new[] { S("configuration"), S("framework"), S("filter") }.Where(s => s.Length > 0)); break;
            case "artifact_read": case "artifact_search": target = S("artifactId"); options = S("query"); break;
            case "git_diff": options = "staged " + S("staged"); break;
            case "git_show": target = S("revision"); break;
            case "user_ask": target = S("question"); break;
        }
        return new(call.Id, Clean(action), Clean(target), Clean(options), process?.Id);
    }
}
