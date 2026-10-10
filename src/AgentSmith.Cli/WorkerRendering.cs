using System.Text.Json;
using AgentSmith.Core;

namespace AgentSmith.Cli;

public sealed partial class TerminalRenderer
{
    private volatile string runningStatus = "Running";
    public string RunningStatus => runningStatus;
    private readonly Dictionary<string, string> activeActivities = [];
    private readonly Dictionary<string, string> workerStates = [];

    private static bool RoutineTool(string name) => name is "file_read" or "workspace_list" or "text_search";
    private static string CallTiming(JsonElement data) => data.TryGetProperty("durationMilliseconds", out var ms)
        && ms.ValueKind == JsonValueKind.Number ? TerminalDuration.Format(TimeSpan.FromMilliseconds(ms.GetDouble()), "0.000") : "";

    private void StartActivity(JsonElement data)
    {
        var name = Text(data, "name"); var id = Text(data, "id");
        var label = ToolLabel(name);
        if (data.TryGetProperty("activity", out var activity) && activity.ValueKind == JsonValueKind.Object)
        {
            label = (Text(activity, "action") + " " + Text(activity, "target")).Trim();
            runningStatus = name == "agent_wait" ? "Waiting for " + Text(activity, "target")
                : name is "process_wait" or "process_poll" ? "Waiting for process " + Text(activity, "target")
                : Activity.Clean(label, 80);
            if (Text(activity, "options").Length > 0) label += " · " + Text(activity, "options");
        }
        else runningStatus = label;
        if (id.Length > 0) activeActivities[id] = label;
        // Fast reads get one result line. The composer still identifies an active read.
        // Spawn has its own authoritative agent.started block after the worker is saved.
        if (!Verbose && (name is "agent_spawn" or "agent_message" or "process_poll" || RoutineTool(name) && id.Length > 0)) return;
        MessageText("› " + label);
    }

    private bool RoutineResult(JsonElement data, JsonElement result)
    {
        var id = Text(data, "id");
        if (Verbose || !RoutineTool(Text(data, "name")) || !activeActivities.TryGetValue(id, out var label)) return false;
        var error = Text(result, "error");
        if (error.Length > 0) { StatusLine("Error", label, "status-error"); MessageText(Activity.Clean(error, 600), "warning"); return true; }
        var detail = result.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array ? $"{files.GetArrayLength()} files"
            : result.TryGetProperty("matches", out var matches) && matches.ValueKind == JsonValueKind.Array ? $"{matches.GetArrayLength()} matches"
            : Text(result, "totalLines").Length > 0 ? Text(result, "totalLines") + " total lines" : "";
        if (Flag(result, "truncated") || Flag(result, "limitReached")) detail += " · more available";
        var timing = CallTiming(data);
        MessageText("✓ " + label + (detail.Length > 0 ? " · " + detail : "") + (timing.Length > 0 ? " · call " + timing : ""));
        return true;
    }

    // Only the short badge gets a background; details remain white and wrap independently.
    private void StatusLine(string badge, string text, string style)
    {
        badge = Safe(badge); text = Safe(text).Replace('\n', ' ');
        var available = Math.Max(1, width - badge.Length - 9);
        var take = Math.Min(available, text.Length);
        if (take < text.Length)
        {
            var space = text.LastIndexOf(' ', take - 1, take);
            if (space > 0) take = space;
            if (take > 0 && char.IsHighSurrogate(text[take - 1])) take--;
        }
        Paint("  │", guideStyle); Paint(" ", "normal"); Paint(" " + badge + " ", style);
        Paint(" " + text[..take], "normal"); output.WriteLine();
        if (take < text.Length) Wrapped(text[take..].TrimStart(), "normal", "  │   ");
    }

    private static string WorkerLabel(JsonElement data) => Number(data, "number") > 0
        ? "Agent #" + Text(data, "number") : "Agent " + Activity.Clean(Text(data, "agentId"), 12);
    private void WorkerStarted(JsonElement data)
    {
        var label = WorkerLabel(data); workerStates[Text(data, "agentId")] = "Running";
        MessageText("› Spawn " + label + " · " + Activity.Clean(Text(data, "profile"), 80));
        var task = Text(data, "task");
        if (task.Length == 0) task = Text(data, "message"); // Older event payloads.
        if (task.Length > 0) MessageText("  " + Activity.Clean(task, Verbose ? 600 : 160));
        if (data.TryGetProperty("writePaths", out var paths) && paths.ValueKind == JsonValueKind.Array)
        {
            if (paths.GetArrayLength() == 0) MessageText("  Read-only assignment", "dim");
            else
            {
                MessageText("  Assigned files:", "dim");
                foreach (var path in paths.EnumerateArray().Take(Verbose ? 20 : 4)) MessageText("    " + Activity.Clean(path.GetString() ?? "", 200));
                var omitted = paths.GetArrayLength() - (Verbose ? 20 : 4);
                if (omitted > 0) MessageText($"    +{omitted} more assigned paths", "dim");
            }
        }
        MessageText("  Details: /agent " + (Number(data, "number") > 0 ? "#" + Text(data, "number") : Text(data, "agentId")), "dim");
    }

    private void WorkerStatus(JsonElement data, bool force = false)
    {
        var id = Text(data, "agentId"); var state = Text(data, "state");
        if (!force && !Verbose && workerStates.TryGetValue(id, out var old) && old == state) return;
        workerStates[id] = state;
        var duration = state is not ("Running" or "Prepared" or "Cancelling")
            && data.TryGetProperty("durationMilliseconds", out var ms) && ms.ValueKind == JsonValueKind.Number
            ? " · worker " + TerminalDuration.Format(TimeSpan.FromMilliseconds(ms.GetDouble())) : "";
        StatusLine(state.Length > 0 ? state : "Status", WorkerLabel(data) + duration,
            state switch { "Completed" => "status-success", "Failed" or "RecoveryRequired" => "status-error", "Running" or "Prepared" => "status-running", _ => "status-warning" });
        if (!FailureBlock(data, heading: false))
        {
            if (Text(data, "reason").Length > 0) MessageText("  Reason: " + Activity.Clean(Text(data, "reason"), 600), "warning");
            if (data.TryGetProperty("appliedCheckpointCount", out var edits) && edits.ValueKind == JsonValueKind.Number)
                MessageText($"  {edits.GetInt32()} applied file operation(s) recorded · /changes", "dim");
            else if (state is "Failed" or "RecoveryRequired") MessageText("  Saved edit count unavailable in this event; inspect /changes.", "dim");
        }
        if (state is not ("Running" or "Prepared" or "Cancelling")) MessageText("  Tests not run by this worker · /agent " + (Number(data, "number") > 0 ? "#" + Text(data, "number") : id), "dim");
    }

    private void WorkerResult(string name, JsonElement data)
    {
        if (name == "agent_message" && !Verbose && steeringPreviews.Contains(Text(data, "messageId"))) return;
        if (data.TryGetProperty("error", out _)) { MessageText(Activity.Clean(Text(data, "error"), 600), "status-error"); return; }
        if (data.TryGetProperty("agents", out var agents) && agents.ValueKind == JsonValueKind.Array)
        {
            foreach (var agent in agents.EnumerateArray()) WorkerStatus(agent);
            if (Text(data, "interruptedBy").Length > 0) MessageText("Wait ended: " + Text(data, "interruptedBy"), "dim");
            return;
        }
        if (data.TryGetProperty("agent", out var worker) && worker.ValueKind == JsonValueKind.Object)
        {
            WorkerStatus(worker, force: true);
            var reportText = Text(data, "report");
            if (TryJson(reportText, out var report))
            {
                MessageText("  Summary: " + Activity.Clean(Text(report, "summary"), Verbose ? 2000 : 600));
                foreach (var field in new[] { "limitations", "clarification" })
                    if (Text(report, field).Length > 0) MessageText("  " + (field == "limitations" ? "Limits: " : "Question: ") + Activity.Clean(Text(report, field), 600), "warning");
            }
            else MessageText(Text(worker, "state") is "Running" or "Prepared" or "Cancelling"
                ? "  Completion report is not available yet." : "  No structured completion report was saved.", "dim");
            if (data.TryGetProperty("checkpoints", out var checkpoints) && checkpoints.ValueKind == JsonValueKind.Array)
                MessageText($"  {checkpoints.GetArrayLength()} checkpoint(s) · /changes", "dim");
            if (data.TryGetProperty("artifacts", out var artifacts) && artifacts.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var artifact in artifacts.EnumerateArray().Take(4))
                {
                    var artifactId = artifact.ValueKind == JsonValueKind.String ? artifact.GetString() : Text(artifact, "id");
                    if (!string.IsNullOrEmpty(artifactId)) MessageText("  " + (index++ == 0 ? "Report" : "Transcript") + ": /artifact " + Activity.Clean(artifactId, 80), "dim");
                }
            }
        }
        else if (Text(data, "agentId").Length > 0) WorkerStatus(data, force: name == "agent_result");
        else if (Text(data, "note").Length > 0) MessageText(Activity.Clean(Text(data, "note"), 300), "dim");
    }
}
