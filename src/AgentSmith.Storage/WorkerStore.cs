using System.Text.Json;
using AgentSmith.Core;

namespace AgentSmith.Storage;

public sealed partial class SessionStore
{
    public IReadOnlyList<WorkerProfile> Profiles()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload FROM worker_profiles ORDER BY name";
        using var reader = cmd.ExecuteReader(); var result = new List<WorkerProfile>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<WorkerProfile>(reader.GetString(0), Json.Options)!);
        return result;
    }
    public void PutProfile(WorkerProfile profile)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(profile.Name, "^[a-zA-Z][a-zA-Z0-9-]{0,47}$") || string.IsNullOrWhiteSpace(profile.Model) || profile.Mode is not (PermissionMode.Inspect or PermissionMode.Edit)) throw new UserFacingException("Worker profiles require a simple name, a model, and Inspect or Edit permission.");
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO worker_profiles VALUES($name,$payload) ON CONFLICT(name) DO UPDATE SET payload=excluded.payload";
        cmd.Parameters.AddWithValue("$name", profile.Name); cmd.Parameters.AddWithValue("$payload", Json.Serialize(profile)); cmd.ExecuteNonQuery();
    }
    public void RemoveProfile(string name)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM worker_profiles WHERE name=$name";
        cmd.Parameters.AddWithValue("$name", name); if (cmd.ExecuteNonQuery() == 0) throw new UserFacingException("Worker profile not found.");
    }
    public IReadOnlyList<WorkerRecord> Workers(string parentId)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload FROM workers WHERE parent_id=$parent ORDER BY rowid"; cmd.Parameters.AddWithValue("$parent", parentId);
        using var reader = cmd.ExecuteReader(); var result = new List<WorkerRecord>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<WorkerRecord>(reader.GetString(0), Json.Options)!);
        return result;
    }
    public WorkerRecord CreateWorker(WorkerRecord worker, Session child)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT payload FROM workers WHERE parent_id=$parent AND run_id=$run AND call_id=$call";
        cmd.Parameters.AddWithValue("$parent", worker.ParentId); cmd.Parameters.AddWithValue("$run", worker.ParentRunId); cmd.Parameters.AddWithValue("$call", worker.CallId);
        if (cmd.ExecuteScalar() is string saved) return JsonSerializer.Deserialize<WorkerRecord>(saved, Json.Options)!;
        cmd.CommandText = "INSERT INTO conversations VALUES($child,$session,$time); INSERT INTO workers VALUES($id,$parent,$run,$call,$child,$payload)";
        cmd.Parameters.AddWithValue("$child", child.Id); cmd.Parameters.AddWithValue("$session", Json.Serialize(child)); cmd.Parameters.AddWithValue("$time", child.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$id", worker.Id); cmd.Parameters.AddWithValue("$payload", Json.Serialize(worker)); cmd.ExecuteNonQuery();
        QueueEvent(db, tx, worker.ParentId, "agent.created", new { worker.Id, worker.ChildId, worker.ParentRunId, worker.CallId, worker.Profile, worker.WritePaths });
        tx.Commit(); return worker;
    }
    public void SaveWorker(WorkerRecord worker)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "UPDATE workers SET payload=$payload WHERE id=$id AND parent_id=$parent";
        cmd.Parameters.AddWithValue("$payload", Json.Serialize(worker)); cmd.Parameters.AddWithValue("$id", worker.Id); cmd.Parameters.AddWithValue("$parent", worker.ParentId);
        if (cmd.ExecuteNonQuery() != 1) throw new UserFacingException("Worker record not found.");
        QueueEvent(db, tx, worker.ParentId, "agent.state", new { worker.Id, worker.ChildId, worker.State, worker.Collected, worker.ReservationsHeld, worker.DurationMilliseconds, worker.Usage }); tx.Commit();
    }
}
