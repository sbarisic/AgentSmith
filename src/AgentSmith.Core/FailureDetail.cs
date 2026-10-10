using System.Text.Json;

namespace AgentSmith.Core;

// Additive event/result metadata. Keep the existing error string for older clients.
public sealed record FailureDetail(string Code, string Category, string Operation, string Target,
    string Reason, string Next, string Effect)
{
    public FailureDetail Sanitized() => new(Activity.Clean(Code, 80), Activity.Clean(Category, 40),
        Activity.Clean(Operation, 80), Activity.Clean(Target, 400), Activity.Clean(Reason, 800),
        Activity.Clean(Next, 600), Activity.Clean(Effect, 400));
}

public static class Failures
{
    // SQLite primary result codes. Do not copy exception text, SQL, or connection strings.
    public static FailureDetail Storage(int code)
    {
        var (id, reason, next) = code switch
        {
            11 or 26 => ("storage_corrupt", "The local session database is damaged or is not a valid SQLite database.",
                "Stop further work. Preserve the database and its WAL/SHM files together before repair or restore; do not delete them or use /recover abandon as a repair."),
            5 or 6 => ("storage_busy", "The local session database is busy or locked.", "Check for another Smith instance using this data directory. Inspect saved state before retrying."),
            13 => ("storage_full", "The disk containing the session database is full.", "Free space without deleting session data, then inspect the saved run before continuing."),
            8 or 14 => ("storage_unavailable", "The local session database cannot be opened for the required access.", "Check the data directory and file permissions, then inspect saved state before continuing."),
            _ => ("storage_failed", "The local session database operation failed.", "Preserve session data and inspect storage availability before continuing.")
        };
        return new(id, "Storage error", "Session storage", "", reason, next,
            "The latest state may not have been saved. Tool effects may already exist; do not assume they were rolled back.");
    }

    public static UserFacingException Reject(string code, string reason, string next, string target = "")
    {
        var detail = new FailureDetail(code, "Rejected", "", target, reason, next, "This operation was not started.").Sanitized();
        return new(detail.Reason, detail);
    }

    public static FailureDetail Tool(Exception exception, ToolCall call, bool beforeExecution)
    {
        var detail = (exception as UserFacingException)?.Detail;
        var target = detail?.Target ?? "";
        // Never include arbitrary argument JSON, command arguments, environments, or stdin.
        if (target.Length == 0)
        {
            try
            {
                using var args = JsonDocument.Parse(call.Arguments);
                foreach (var field in new[] { "path", "source", "agentId", "processId", "artifactId", "project" })
                    if (args.RootElement.ValueKind == JsonValueKind.Object && args.RootElement.TryGetProperty(field, out var value)
                        && value.ValueKind == JsonValueKind.String) { target = value.GetString() ?? ""; break; }
            }
            catch (JsonException) { }
        }
        var (code, reason, next) = exception switch
        {
            FileNotFoundException => ("file_not_found", "The file does not exist.", "List the parent directory and use an existing file path. If creation was delegated, inspect the worker result first."),
            DirectoryNotFoundException => ("path_not_found", "The directory or a parent directory does not exist.", "List the nearest existing parent and check the requested path."),
            UnauthorizedAccessException => ("access_denied", "Access was denied by the operating system.", "Check the file permissions and whether the path is a file or directory."),
            IOException => ("io_failed", "The file operation could not complete.", "Check the path, file locks, and storage availability before retrying."),
            JsonException or ArgumentException or FormatException or OverflowException => ("invalid_arguments", "Arguments do not match this tool's expected format or limits.", "Correct the arguments using the tool schema before retrying."),
            UserFacingException user => ("tool_rejected", user.Message, "Correct the reported condition before retrying. Use /tools to inspect available tools."),
            _ => ("operation_failed", "The operation failed; the cause is not available.", "Inspect the saved run and its evidence before retrying.")
        };
        return new FailureDetail(detail?.Code ?? code, detail?.Category ?? (beforeExecution ? "Rejected" : "Error"),
            call.Name, target, detail?.Reason ?? reason, detail?.Next ?? next,
            beforeExecution ? "This operation was not started." : "This read-only call made no workspace changes. Earlier edits are retained.").Sanitized();
    }

    public static FailureDetail Uncertain(ToolCall call) => new FailureDetail("effect_uncertain", "Recovery required", call.Name, "",
        "The operation started, but its outcome could not be confirmed.",
        "Use /recover to inspect the recorded operation before deciding how to continue.",
        "Changes may have occurred. This operation will not be replayed automatically.").Sanitized();

    public static FailureDetail Worker(WorkerRecord worker, PauseReason pause, int requests)
    {
        var (code, reason, next) = pause switch
        {
            PauseReason.RequestLimit => ("worker_request_limit", $"Worker request limit reached ({requests}/{worker.RequestLimit}).",
                "Collect the saved result, inspect its edits, then assign a smaller follow-up task if needed."),
            PauseReason.TimeLimit => ("worker_time_limit", "Worker time limit reached.", "Collect the saved result and assign a smaller follow-up task if needed."),
            PauseReason.ContextLimit => ("worker_context_limit", "Worker context could not fit within its budget.", "Collect the result and narrow the task or shared evidence."),
            PauseReason.CompactionFailed => ("worker_compaction_failed", "Worker context compaction failed.", "Inspect the result before assigning a smaller task."),
            PauseReason.Network => ("worker_network_failed", "Worker model request failed due to a connection problem.", "Check the provider connection and collect the result before creating a new assignment."),
            PauseReason.NoProgress => ("worker_no_progress", "Worker stopped after repeated rounds without progress.", "Inspect the result and revise the task or its evidence."),
            _ => ("worker_failed", worker.Reason ?? "Worker ended without a completion report.", "Inspect the saved result before assigning further work.")
        };
        if (worker.State == "RecoveryRequired")
            return new("worker_recovery_required", "Recovery required", "Worker", $"Agent #{worker.Number}",
                "Some worker file effects are uncertain.", $"Inspect /agent #{worker.Number}, /changes, and /recover before continuing.",
                "Recorded edits are retained; uncertain operations still require reconciliation.");
        return new FailureDetail(code, "Worker failed", "Worker", $"Agent #{worker.Number}", reason,
            next + $" Details: /agent #{worker.Number} · /changes.",
            (worker.AppliedCheckpointCount is { } count ? $"{count} applied file operation(s) recorded; edits were not rolled back. " : "Saved edit count is unavailable; inspect /changes. ") +
            (worker.Report is null ? "No completion report was saved." : "A completion report was saved.")).Sanitized();
    }
}
