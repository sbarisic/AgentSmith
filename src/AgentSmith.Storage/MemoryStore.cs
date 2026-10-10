using System.Text.Json;
using System.Text.RegularExpressions;
using AgentSmith.Core;
using Microsoft.Data.Sqlite;

namespace AgentSmith.Storage;

public sealed partial class SessionStore
{
    private static SqliteCommand MemoryCommand(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Key, object Value)[] args)
    {
        var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value);
        return cmd;
    }
    private static MemoryEntry? Entry(object? value) => value is string json ? JsonSerializer.Deserialize<MemoryEntry>(json, Json.Options) : null;
    public bool MemoryEnabled
    {
        get { using var db = Open(); using var cmd = MemoryCommand(db, null, "SELECT enabled FROM memory_settings WHERE id=1"); return Convert.ToInt32(cmd.ExecuteScalar()) == 1; }
    }
    private static void MemoryEvent(SqliteConnection db, SqliteTransaction tx, string type, object payload)
    {
        using var cmd = MemoryCommand(db, tx, "INSERT INTO memory_events(type,timestamp,payload) VALUES($type,$time,$payload)", ("$type", type), ("$time", DateTimeOffset.UtcNow.ToString("O")), ("$payload", Json.Serialize(payload))); cmd.ExecuteNonQuery();
    }
    public void SetMemoryEnabled(bool enabled)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var cmd = MemoryCommand(db, tx, "UPDATE memory_settings SET enabled=$enabled WHERE id=1", ("$enabled", enabled ? 1 : 0)); cmd.ExecuteNonQuery();
        MemoryEvent(db, tx, "memory.setting", new { enabled }); tx.Commit();
    }
    private static IReadOnlyList<MemoryEntry> ReadEntries(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader(); var result = new List<MemoryEntry>();
        while (reader.Read()) result.Add(Entry(reader.GetString(0))!); return result;
    }
    public IReadOnlyList<MemoryEntry> ListMemories(int page = 1)
    {
        if (page is < 1 or > 100000) throw new UserFacingException("Memory page must be 1..100000.");
        using var db = Open(); using var cmd = MemoryCommand(db, null, "SELECT payload FROM memories ORDER BY updated DESC,id LIMIT 10 OFFSET $offset", ("$offset", (page - 1) * 10)); return ReadEntries(cmd);
    }
    public IReadOnlyList<MemoryEntry> SearchMemories(string query, string workspace = "")
    {
        // Never accept an FTS expression from the caller. OR quoted tokens supports task prose.
        var tokens = Regex.Matches(query[..Math.Min(query.Length, 32000)], @"[\p{L}\p{N}_]{2,64}").Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToArray();
        if (tokens.Length == 0) return [];
        using var db = Open(); using var cmd = MemoryCommand(db, null,
            "SELECT m.payload FROM memory_search f JOIN memories m ON m.id=f.id WHERE memory_search MATCH $query ORDER BY bm25(memory_search,0,3,1,2),CASE WHEN m.origin=$origin THEN 0 ELSE 1 END,m.updated DESC,m.id LIMIT 10",
            ("$query", string.Join(" OR ", tokens.Select(t => "\"" + t + "\""))), ("$origin", MemoryPolicy.Origin(workspace)));
        return ReadEntries(cmd);
    }
    public MemoryEntry? ReadMemory(string id)
    {
        using var db = Open(); using var cmd = MemoryCommand(db, null, "SELECT payload FROM memories WHERE id=$id", ("$id", id)); return Entry(cmd.ExecuteScalar());
    }
    public MemoryEntry? MemoryReceipt(string operationId)
    {
        using var db = Open(); using var cmd = MemoryCommand(db, null, "SELECT payload FROM memory_receipts WHERE operation_id=$id", ("$id", operationId)); return Entry(cmd.ExecuteScalar());
    }
    public MemoryEntry WriteMemory(MemoryWrite write, CancellationToken ct = default)
    {
        MemoryPolicy.Validate(write.Title, write.Content, write.Tags);
        if (write.Sources.Length is < 1 or > 8 || write.Sources.Any(s => s.Reference.Length > 256 || s.ConversationId.Length > 128 || s.RunId?.Length > 128) || write.OperationId.Length > 512) throw new UserFacingException("Memory requires 1..8 bounded source references.");
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var tx = db.BeginTransaction();
        using var receipt = MemoryCommand(db, tx, "SELECT payload FROM memory_receipts WHERE operation_id=$id", ("$id", write.OperationId));
        if (Entry(receipt.ExecuteScalar()) is { } saved) return saved;
        if (!write.UserAuthored)
        {
            using var enabled = MemoryCommand(db, tx, "SELECT enabled FROM memory_settings WHERE id=1");
            if (Convert.ToInt32(enabled.ExecuteScalar()) != 1) throw new UserFacingException("Memory is disabled.");
        }
        using var find = write.Id is null
            ? MemoryCommand(db, tx, "SELECT payload FROM memories WHERE title_key=$title AND origin=$origin", ("$title", MemoryPolicy.Key(write.Title)), ("$origin", MemoryPolicy.Origin(write.OriginWorkspace)))
            : MemoryCommand(db, tx, "SELECT payload FROM memories WHERE id=$id", ("$id", write.Id));
        var prior = Entry(find.ExecuteScalar());
        if (write.Id is not null && prior is null) throw new UserFacingException("Memory not found.");
        var duplicate = write.Id is null && prior is not null;
        if (duplicate && MemoryPolicy.Key(prior!.Content) != MemoryPolicy.Key(write.Content)) throw new UserFacingException("A memory with this title and origin already exists. Read it and update its revision explicitly.");
        if (write.Id is not null && prior is not null && (prior.Revision != write.ExpectedRevision || prior.UserAuthored && !write.UserAuthored)) throw new UserFacingException("Memory revision changed or is user-authored. Read the current entry; automatic updates cannot overwrite user corrections.");
        if (duplicate && write.UserAuthored && !prior!.UserAuthored) duplicate = false;
        var now = DateTimeOffset.UtcNow;
        var entry = duplicate ? prior! : new MemoryEntry(prior?.Id ?? Guid.NewGuid().ToString("N"), (prior?.Revision ?? 0) + 1,
            write.Title.Trim(), write.Content.Trim(), write.Tags, prior?.OriginWorkspace ?? write.OriginWorkspace,
            write.Sources, prior?.CreatedAt ?? now, now, write.UserAuthored);
        var payload = Json.Serialize(entry);
        if (!duplicate)
        {
            using var cmd = MemoryCommand(db, tx, """
                INSERT INTO memories VALUES($id,$title,$origin,$updated,$payload) ON CONFLICT(id) DO UPDATE SET title_key=$title,updated=$updated,payload=$payload;
                INSERT INTO memory_revisions VALUES($id,$revision,$payload);
                DELETE FROM memory_search WHERE id=$id;
                INSERT INTO memory_search(id,title,content,tags) VALUES($id,$textTitle,$content,$tags);
                """, ("$id", entry.Id), ("$title", MemoryPolicy.Key(entry.Title)), ("$origin", MemoryPolicy.Origin(entry.OriginWorkspace)), ("$updated", now.ToString("O")), ("$payload", payload), ("$revision", entry.Revision), ("$textTitle", entry.Title), ("$content", entry.Content), ("$tags", string.Join(' ', entry.Tags)));
            cmd.ExecuteNonQuery();
        }
        using var record = MemoryCommand(db, tx, "INSERT INTO memory_receipts VALUES($id,$payload)", ("$id", write.OperationId), ("$payload", payload)); record.ExecuteNonQuery();
        MemoryEvent(db, tx, "memory.saved", new { entry.Id, entry.Revision, entry.Title, duplicate });
        ct.ThrowIfCancellationRequested(); tx.Commit(); return entry;
    }
    public void ForgetMemory(string id)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var cmd = MemoryCommand(db, tx, "DELETE FROM memories WHERE id=$id; DELETE FROM memory_search WHERE id=$id", ("$id", id)); cmd.ExecuteNonQuery();
        MemoryEvent(db, tx, "memory.forgotten", new { id }); tx.Commit();
    }
}
