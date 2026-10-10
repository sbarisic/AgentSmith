using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using Microsoft.Data.Sqlite;

namespace AgentSmith.Storage;

public sealed partial class SessionStore
{
    private static List<SteeringMessage> ReadSteering(SqliteConnection db, SqliteTransaction? tx, string filter, params (string, object?)[] args)
    {
        using var cmd = Command(db, tx, "SELECT payload FROM steering WHERE " + filter + " ORDER BY sequence", args);
        using var reader = cmd.ExecuteReader(); var result = new List<SteeringMessage>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<SteeringMessage>(reader.GetString(0), Json.Options)!);
        return result;
    }
    private static void UpdateSteering(SqliteConnection db, SqliteTransaction tx, SteeringMessage item)
    {
        using var cmd = Command(db, tx, "UPDATE steering SET state=$state,payload=$payload WHERE id=$id", ("$state", item.State), ("$payload", Json.Serialize(item)), ("$id", item.Id));
        cmd.ExecuteNonQuery();
    }
    private static string Operation(string rootId, string runId, string callId) => Json.Serialize(new[] { rootId, runId, callId });
    public IReadOnlyList<SteeringMessage> Steering(string rootId)
    { using var db = Open(); return ReadSteering(db, null, "root_id=$root", ("$root", rootId)); }
    public bool HasPendingSteering(string conversationId, string runId)
    {
        using var db = Open(); using var cmd = Command(db, null, "SELECT EXISTS(SELECT 1 FROM steering WHERE conversation_id=$id AND run_id=$run AND state='Pending')", ("$id", conversationId), ("$run", runId));
        return Convert.ToInt32(cmd.ExecuteScalar()) != 0;
    }
    public SteeringMessage? SteeringReceipt(string rootId, string sourceRunId, string sourceCallId)
    { using var db = Open(); return ReadSteering(db, null, "operation_key=$key", ("$key", Operation(rootId, sourceRunId, sourceCallId))).SingleOrDefault(); }
    public SteeringMessage SendSteering(string rootId, string targetId, string text, string sender = "user", string? sourceRunId = null, string? sourceCallId = null)
    {
        SteeringMessage.ValidateText(text);
        if (sender is not ("user" or "parent") || sender == "parent" && (sourceRunId is null || sourceCallId is null)) throw new UserFacingException("Invalid steering provenance.");
        using var db = Open(); using var tx = db.BeginTransaction();
        var operation = sourceRunId is not null && sourceCallId is not null ? Operation(rootId, sourceRunId, sourceCallId) : null;
        if (operation is not null && ReadSteering(db, tx, "operation_key=$key", ("$key", operation)).SingleOrDefault() is { } existing) return existing;
        using var target = Command(db, tx, "SELECT snapshot FROM conversations WHERE id=$id", ("$id", targetId));
        var session = target.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<Session>(payload, Json.Options)! : throw new UserFacingException("Steering target is unavailable.");
        if ((session.ParentConversationId ?? session.Id) != rootId || sender == "parent" && session.ParentConversationId != rootId)
            throw new UserFacingException("Steering target is outside this task tree.");
        var run = session.Runs.LastOrDefault();
        if (run is null || run.State is RunState.Completed or RunState.Cancelled or RunState.Failed)
            throw new UserFacingException("No unfinished target run. Steering does not start or relaunch work.");
        if (run.State is RunState.AwaitingUser or RunState.AwaitingPlan || Planning.Active(session)?.PendingReview is not null || run.Question is { Answer: null })
            throw new UserFacingException("Resolve the pending question or plan review explicitly; steering cannot answer or approve it.");
        if (session.WorkerReport is not null || run.Calls.Any(c => session.Receipts.TryGetValue(c.Id, out var receipt) && receipt.FinishRun))
            throw new UserFacingException("Target finalization is already committed; steering cannot reopen it.");
        if (session.ParentConversationId is not null)
        {
            using var worker = Command(db, tx, "SELECT payload FROM workers WHERE child_id=$id AND parent_id=$root", ("$id", targetId), ("$root", rootId));
            if (worker.ExecuteScalar() is not string w || !JsonSerializer.Deserialize<WorkerRecord>(w, Json.Options)!.Active || run.State != RunState.Running)
                throw new UserFacingException("Worker is not active. Steering never restarts a worker.");
        }
        using var count = Command(db, tx, "SELECT COUNT(*) FROM steering WHERE conversation_id=$id AND state='Pending'", ("$id", targetId));
        if (Convert.ToInt32(count.ExecuteScalar()) >= 20) throw new UserFacingException("Target already has 20 pending steering messages.");
        using var sequence = Command(db, tx, "SELECT COALESCE(MAX(sequence),0)+1 FROM steering");
        var item = new SteeringMessage { RootId = rootId, ConversationId = targetId, RunId = run.Id, Text = text, Sender = sender, SourceRunId = sourceRunId, SourceCallId = sourceCallId, Sequence = Convert.ToInt64(sequence.ExecuteScalar()) };
        using var insert = Command(db, tx, "INSERT INTO steering VALUES($id,$root,$conversation,$run,$sequence,'Pending',$operation,$payload)", ("$id", item.Id), ("$root", rootId), ("$conversation", targetId), ("$run", run.Id), ("$sequence", item.Sequence), ("$operation", operation), ("$payload", Json.Serialize(item)));
        insert.ExecuteNonQuery(); QueueEvent(db, tx, rootId, "steering.saved", new { item.Id, targetId, runId = run.Id, sender }); tx.Commit();
        SteeringWake.Notify(this, run.Id); return item;
    }
    public void RemoveSteering(string rootId, string id)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        var item = ReadSteering(db, tx, "root_id=$root AND id=$id", ("$root", rootId), ("$id", id)).SingleOrDefault();
        if (item is null || item.State != "Pending") throw new UserFacingException("Only a pending steering message in this task tree can be removed.");
        item.State = "Removed"; UpdateSteering(db, tx, item); QueueEvent(db, tx, rootId, "steering.removed", new { id }); tx.Commit();
        SteeringWake.Notify(this, item.RunId);
    }
    public IReadOnlyList<SteeringMessage> IncorporateSteering(Session session, Run run)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        var items = ReadSteering(db, tx, "conversation_id=$id AND run_id=$run AND state='Pending'", ("$id", session.Id), ("$run", run.Id));
        if (items.Count == 0 || run.State != RunState.Running || run.Question is { Answer: null } || Planning.Active(session)?.PendingReview is not null || run.Calls.Any(c => c.State == CallState.Started)) return [];
        var clone = JsonSerializer.Deserialize<Session>(Json.Serialize(session), Json.Options)!;
        var clonedRun = clone.Runs.Single(r => r.Id == run.Id);
        foreach (var call in clonedRun.Calls.Where(c => c.State == CallState.Pending))
        {
            if (clone.Mutations.SingleOrDefault(m => m.Id == call.Prepared?.OperationId) is { State: "Prepared" } mutation) mutation.State = "NotApplied";
            if (clone.Processes.SingleOrDefault(p => p.Id == call.Prepared?.OperationId) is { State: "Prepared" } process)
            { process.State = "NotStarted"; process.Reason = "Skipped due to steering before launch."; process.TerminationConfirmed = true; }
            call.State = CallState.Completed;
            call.Result = Json.Serialize(new { skipped = true, reason = "Skipped due to steering; no tool was executed." });
            clone.History.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = call.Id, ["output"] = call.Result });
        }
        foreach (var item in items)
        {
            item.State = "Incorporated"; item.IncorporatedAt = DateTimeOffset.UtcNow; item.HistoryIndex = clone.History.Count;
            clonedRun.SteeringHistoryIndices.Add(clone.History.Count);
            clone.MessageWorkflows[clone.History.Count] = clone.Workflow;
            var label = item.Sender == "user" ? "Direct user steering" : "Direction from the parent agent (not a direct user instruction or authorization)";
            var guidance = item.Sender == "user"
                ? "This is the user's newest instruction for the current run. Follow it instead of conflicting earlier user requests. An ordinary original task is not an approved plan."
                : "Reconsider the delegated task using this newer parent direction, within the existing assignment.";
            clone.History.Add(new JsonObject { ["role"] = "user", ["content"] = $"{label} [{item.Id}]:\n{item.Text}\n\n{guidance} Runtime permissions and reserved paths remain fixed. If an explicitly approved plan exists, changes outside that approved scope still require review. Steering itself does not answer a pending question or approve a plan." });
            UpdateSteering(db, tx, item);
        }
        SaveInTransaction(clone, new("steering.incorporated", clone.Id, run.Id, new { ids = items.Select(i => i.Id) }), db, tx); tx.Commit();
        session.History = clone.History; session.MessageWorkflows = clone.MessageWorkflows;
        run.SteeringHistoryIndices = clonedRun.SteeringHistoryIndices;
        foreach (var mutation in session.Mutations)
            if (clone.Mutations.Single(m => m.Id == mutation.Id).State == "NotApplied") mutation.State = "NotApplied";
        foreach (var process in session.Processes)
            if (clone.Processes.Single(p => p.Id == process.Id) is { State: "NotStarted" } savedProcess)
            { process.State = savedProcess.State; process.Reason = savedProcess.Reason; process.TerminationConfirmed = savedProcess.TerminationConfirmed; }
        foreach (var call in run.Calls)
        { var saved = clonedRun.Calls.Single(c => c.Id == call.Id); call.State = saved.State; call.Result = saved.Result; }
        SteeringWake.Notify(this, run.Id); return items;
    }
    public IReadOnlyList<SteeringMessage> MarkSteeringSent(Session session, Run run)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        var items = ReadSteering(db, tx, "conversation_id=$id AND run_id=$run AND state='Incorporated'", ("$id", session.Id), ("$run", run.Id));
        foreach (var item in items) { item.State = "Sent"; item.SentAt = DateTimeOffset.UtcNow; UpdateSteering(db, tx, item); }
        if (items.Count > 0) QueueEvent(db, tx, session.Id, "steering.sent", new { ids = items.Select(i => i.Id), runId = run.Id });
        tx.Commit(); return items;
    }
    public bool TryCompleteSteeredRun(Session session, Run run)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        if (ReadSteering(db, tx, "conversation_id=$id AND run_id=$run AND state='Pending'", ("$id", session.Id), ("$run", run.Id)).Count > 0) return false;
        var previous = run.State; run.State = RunState.Completed;
        try { SaveInTransaction(session, new("run.completion.committed", session.Id, run.Id, new { state = "Completed" }), db, tx); tx.Commit(); return true; }
        catch { run.State = previous; throw; }
    }
    public bool TryStartSteeredTool(Session session, Run run, ToolCall call)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        if (ReadSteering(db, tx, "conversation_id=$id AND run_id=$run AND state='Pending'", ("$id", session.Id), ("$run", run.Id)).Count > 0) return false;
        SaveInTransaction(session, new("tool.started", session.Id, run.Id, new { call.Id, call.Name, call.Activity }), db, tx); tx.Commit(); return true;
    }
}
