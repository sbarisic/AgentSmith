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
    public static ActivitySummary Describe(Session session, ToolCall call, IReadOnlyList<WorkerRecord>? workers = null)
    {
        JsonObject a;
        try { a = JsonNode.Parse(call.Prepared?.Arguments ?? call.Arguments)?.AsObject() ?? new(); }
        catch { return new(call.Id, Clean(call.Name), "", ""); }
        string S(string key) => a[key]?.ToString() ?? "";
        string AgentLabel(string id)
        {
            var worker = workers?.FirstOrDefault(w => w.Id == id || "#" + w.Number == id);
            return worker is not null ? "Agent #" + worker.Number : id.StartsWith('#') ? "Agent " + id : "Agent " + Clean(id, 12);
        }
        var processId = S("processId");
        var process = session.Processes.FirstOrDefault(p => p.Id == processId || p.Id == call.Prepared?.OperationId);
        var label = process is null ? processId : "#" + process.DisplayNumber;
        var target = S("path"); var options = "";
        var action = call.Name switch
        {
            "csharp_symbols" => "Find C# symbols", "csharp_load" => "Load C# project", "csharp_status" => "Inspect C# snapshot", "csharp_query" => "Navigate C#", "diagnostics_read" => "Read diagnostics",
            "audio_listen" => "Listen to audio", "image_read" => "View image", "workspace_map" => "Map workspace", "http_request" => "HTTP", "web_search" => "Search web", "web_read" => "Read web", "file_read" => "Read", "workspace_list" => "List", "text_search" => "Search", "file_create" => "Create",
            "file_apply_patch" => "Edit", "file_move" => "Move", "file_delete" => "Delete", "process_start" => "Run",
            "process_poll" => "Poll", "process_wait" => "Wait", "process_input" => "Input", "process_stop" => "Stop", "dotnet_build" => "Build",
            "dotnet_test" => "Test", "user_ask" => "Ask", "plan_update" => "Update plan",
            "plan_list" => "List directory plans", "plan_fetch" => "Read saved plan", "plan_read" => "Read active plan",
            "agent_spawn" => "Spawn", "agent_wait" => "Wait for", "agent_result" => "Collect",
            "agent_cancel" => "Cancel", "agent_message" => "Message", _ => call.Name.Replace('_', ' ')
        };
        switch (call.Name)
        {
            case "audio_listen": options = S("startSeconds") + "s · " + (S("durationSeconds") == "0" ? "default range" : S("durationSeconds") + "s"); break;
            case "csharp_symbols": options = S("query"); break;
            case "csharp_load": target = session.NavigationSnapshots.FirstOrDefault(s => s.ProcessId == call.Prepared?.OperationId)?.Project ?? S("project"); break;
            case "csharp_status": target = S("snapshotId"); break;
            case "csharp_query": options = S("operation") + " · " + S("line") + ":" + S("column"); break;
            case "diagnostics_read": target = S("artifactId"); options = S("filter"); break;
            case "image_read":
                if (Uri.TryCreate(target, UriKind.Absolute, out var imageUrl) && imageUrl.Scheme is "http" or "https")
                    target = imageUrl.GetLeftPart(UriPartial.Path);
                break;
            case "http_request": target = S("method") + " " + (Uri.TryCreate(S("url"), UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Path) : "[invalid URL]"); break;
            case "workspace_map": options = "depth " + S("depth"); break;
            case "web_search": target = S("query"); break;
            case "web_read": target = S("url"); break;
            case "memory_search": target = S("query"); break;
            case "plan_list": options = "page " + S("page"); break;
            case "plan_fetch": target = S("planId"); break;
            case "memory_save": target = S("title"); break;
            case "memory_read": case "memory_update": target = S("id"); break;
            case "agent_spawn": target = S("profile"); options = S("task"); break;
            case "agent_result": case "agent_cancel": target = AgentLabel(S("agentId")); break;
            case "agent_message": target = AgentLabel(S("agentId")); options = SteeringNotice.PreviewText(S("text"), 240); break;
            case "agent_wait":
                target = a["agentIds"] is JsonArray ids ? string.Join(", ", ids.Select(id => AgentLabel(id?.ToString() ?? ""))) : "workers";
                if (int.TryParse(S("waitMilliseconds"), out var wait))
                {
                    var seconds = Math.Clamp(wait, 0, 360000) / 1000;
                    options = "up to " + (seconds >= 60 ? $"{seconds / 60}m" + (seconds % 60 > 0 ? $" {seconds % 60}s" : "") : $"{seconds}s");
                }
                break;
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
            case "process_poll": case "process_wait": case "process_stop": target = label; break;
            case "dotnet_build": case "dotnet_test": target = S("project"); options = string.Join(" · ", new[] { S("configuration"), S("framework"), S("filter") }.Where(s => s.Length > 0)); break;
            case "artifact_read": case "artifact_search": target = S("artifactId"); options = S("query"); break;
            case "git_diff": options = "staged " + S("staged"); break;
            case "git_show": target = S("revision"); break;
            case "user_ask": target = S("question"); break;
        }
        return new(call.Id, Clean(action), Clean(target), Clean(options), process?.Id);
    }
}
