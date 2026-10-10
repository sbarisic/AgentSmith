using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using Microsoft.Data.Sqlite;

namespace AgentSmith.Storage;

public sealed partial class SessionStore
{
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Key, object? Value)[] args)
    {
        var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var arg in args) cmd.Parameters.AddWithValue(arg.Key, arg.Value ?? DBNull.Value);
        return cmd;
    }
    private static void QueueEvent(SqliteConnection db, SqliteTransaction tx, string conversationId, string type, object payload)
    {
        using var cmd = Command(db, tx, "INSERT INTO events(conversation_id,type,timestamp,payload) VALUES($id,$type,$at,$payload)", ("$id", conversationId), ("$type", type), ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$payload", Json.Serialize(payload))); cmd.ExecuteNonQuery();
    }
    public PromptQueue Queue(string conversationId)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var cmd = Command(db, tx, "SELECT payload FROM prompt_queue WHERE conversation_id=$id ORDER BY sequence", ("$id", conversationId));
        var items = new List<QueuedPrompt>(); using (var r = cmd.ExecuteReader()) while (r.Read()) items.Add(JsonSerializer.Deserialize<QueuedPrompt>(r.GetString(0), Json.Options)!);
        cmd.CommandText = "SELECT reason FROM queue_holds WHERE conversation_id=$id"; var reason = cmd.ExecuteScalar() as string;
        return new(items, reason);
    }
    public QueuedPrompt Enqueue(string conversationId, string text)
        => Enqueue(conversationId, UserPrompt.Text(text));
    public QueuedPrompt Enqueue(string conversationId, UserPrompt prompt)
    {
        prompt.Validate();
        var owned = Attachments(conversationId);
        if (prompt.Parts.Any(p => p.Kind == "image" && !owned.Any(a => a.Id == p.Value))) throw new UserFacingException("Image is not owned by this conversation.");
        using var db = Open(); using var tx = db.BeginTransaction();
        using var count = Command(db, tx, "SELECT COUNT(*) FROM prompt_queue WHERE conversation_id=$id AND state='Pending'", ("$id", conversationId));
        if (Convert.ToInt32(count.ExecuteScalar()) >= 20) throw new UserFacingException("The queue already contains 20 pending prompts.");
        count.CommandText = "SELECT COALESCE(MAX(sequence),0)+1 FROM prompt_queue WHERE conversation_id=$id";
        var item = new QueuedPrompt { Text = prompt.PlainText, Prompt = prompt, Sequence = Convert.ToInt64(count.ExecuteScalar()) };
        using var insert = Command(db, tx, "INSERT INTO prompt_queue VALUES($key,$id,$seq,'Pending',$payload)", ("$key", item.Id), ("$id", conversationId), ("$seq", item.Sequence), ("$payload", Json.Serialize(item)));
        insert.ExecuteNonQuery(); QueueEvent(db, tx, conversationId, "prompt.queued", new { item.Id, item.Sequence }); tx.Commit(); return item;
    }
    public void HoldQueue(string conversationId, string? reason)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var cmd = Command(db, tx, "INSERT INTO queue_holds VALUES($id,$reason) ON CONFLICT(conversation_id) DO UPDATE SET reason=$reason", ("$id", conversationId), ("$reason", reason));
        cmd.ExecuteNonQuery(); QueueEvent(db, tx, conversationId, reason is null ? "queue.continued" : "queue.held", new { reason }); tx.Commit();
    }
    public void RemoveQueued(string conversationId, string? id)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var cmd = Command(db, tx, "SELECT payload FROM prompt_queue WHERE conversation_id=$id AND state='Pending' AND ($key IS NULL OR id=$key)", ("$id", conversationId), ("$key", id));
        var items = new List<QueuedPrompt>(); using (var r = cmd.ExecuteReader()) while (r.Read()) items.Add(JsonSerializer.Deserialize<QueuedPrompt>(r.GetString(0), Json.Options)!);
        if (id is not null && items.Count == 0) throw new UserFacingException("No pending prompt with that ID.");
        foreach (var item in items) { item.State = "Removed"; using var update = Command(db, tx, "UPDATE prompt_queue SET state='Removed',payload=$payload WHERE id=$id", ("$payload", Json.Serialize(item)), ("$id", item.Id)); update.ExecuteNonQuery(); QueueEvent(db, tx, conversationId, "prompt.removed", new { item.Id }); }
        tx.Commit();
    }
    public Run? ClaimQueued(Session session)
    {
        lock (session)
        {
            if (session.PendingPlanPrompt is not null || Planning.Active(session)?.PendingReview is not null || session.Runs.LastOrDefault()?.State is RunState.Running or RunState.AwaitingUser or RunState.AwaitingPlan or RunState.Paused or RunState.RecoveryRequired) return null;
            using var db = Open(); using var tx = db.BeginTransaction();
            using var cmd = Command(db, tx, "SELECT reason FROM queue_holds WHERE conversation_id=$id", ("$id", session.Id));
            if (cmd.ExecuteScalar() is string) return null;
            cmd.CommandText = "SELECT payload FROM prompt_queue WHERE conversation_id=$id AND state='Pending' ORDER BY sequence LIMIT 1";
            if (cmd.ExecuteScalar() is not string payload) return null;
            var item = JsonSerializer.Deserialize<QueuedPrompt>(payload, Json.Options)!;
            var plan = Planning.Active(session); var run = new Run { PlanId = plan?.Id, PlanRevision = plan?.Current.Revision };
            item.State = "Dispatched"; item.RunId = run.Id;
            var message = item.Prompt?.Parts.Any(p => p.Kind == "image") == true ? item.Prompt.HistoryItem() : new JsonObject { ["role"] = "user", ["content"] = item.Text };
            session.MessageWorkflows[session.History.Count] = session.Workflow;
            session.History.Add(message); session.Runs.Add(run);
            try
            {
                using var update = Command(db, tx, "UPDATE prompt_queue SET state='Dispatched',payload=$payload WHERE id=$id", ("$payload", Json.Serialize(item)), ("$id", item.Id)); update.ExecuteNonQuery();
                SaveInTransaction(session, new("prompt.dispatched", session.Id, run.Id, new { item.Id, item.RunId }), db, tx); tx.Commit(); return run;
            }
            catch { session.MessageWorkflows.Remove(session.History.Count - 1); session.History.Remove(message); session.Runs.Remove(run); throw; }
        }
    }
    public void SettleQueue(string conversationId, Run run)
    {
        if (run.State is not (RunState.Completed or RunState.Cancelled or RunState.Failed)) return;
        using var db = Open(); using var tx = db.BeginTransaction();
        using var cmd = Command(db, tx, "SELECT payload FROM prompt_queue WHERE conversation_id=$id AND state='Dispatched' AND json_extract(payload,'$.runId')=$run", ("$id", conversationId), ("$run", run.Id));
        if (cmd.ExecuteScalar() is string payload)
        {
            var item = JsonSerializer.Deserialize<QueuedPrompt>(payload, Json.Options)!; item.State = "Completed";
            using var update = Command(db, tx, "UPDATE prompt_queue SET state='Completed',payload=$payload WHERE id=$id", ("$payload", Json.Serialize(item)), ("$id", item.Id)); update.ExecuteNonQuery();
            QueueEvent(db, tx, conversationId, "prompt.completed", new { item.Id, runId = run.Id, outcome = run.State.ToString() });
        }
        tx.Commit();
    }
}
