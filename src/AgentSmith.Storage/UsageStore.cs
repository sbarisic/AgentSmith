using System.Text.Json;
using AgentSmith.Core;

namespace AgentSmith.Storage;

public sealed partial class SessionStore
{
    public UsageReport Usage(string conversationId)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT json_extract(snapshot,'$.parentConversationId') FROM conversations WHERE id=$id";
        command.Parameters.AddWithValue("$id", conversationId);
        var parent = command.ExecuteScalar();
        if (parent is null) throw new UserFacingException("Conversation not found.");
        if (parent is string root && root.Length > 0) throw new UserFacingException("View usage for the parent conversation: " + root);
        command.CommandText = """
            SELECT c.id, COALESCE(json_extract(c.snapshot,'$.connection.provider'),0),
                   json_extract(c.snapshot,'$.model'), json_extract(w.payload,'$.profile.name'),
                   json_extract(w.payload,'$.purpose'), json_extract(w.payload,'$.state'), json_extract(w.payload,'$.waitingMilliseconds')
            FROM conversations c LEFT JOIN workers w ON w.child_id=c.id AND w.parent_id=$id
            WHERE c.id=$id OR w.parent_id=$id ORDER BY CASE WHEN c.id=$id THEN 0 ELSE 1 END,w.rowid
            """;
        var rows = new List<UsageRow>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var provider = reader.GetInt32(1);
                rows.Add(new() { ConversationId = reader.GetString(0), Role = reader.GetString(0) == conversationId ? "Parent" : "Worker",
                    Provider = Enum.IsDefined(typeof(ModelProvider), provider) ? ((ModelProvider)provider).ToString() : "Unknown",
                    Model = reader.IsDBNull(2) ? "Unknown" : reader.GetString(2), Profile = reader.IsDBNull(3) ? null : reader.GetString(3), Purpose = reader.IsDBNull(4) ? null : reader.GetString(4), Outcome = reader.IsDBNull(5) ? null : reader.GetString(5), WaitingMilliseconds = reader.IsDBNull(6) ? 0 : reader.GetDouble(6) });
            }
        foreach (var row in rows)
        {
            using var events = db.CreateCommand(); events.Transaction = tx;
            events.CommandText = "SELECT type,json_extract(payload,'$.usage'),json_extract(payload,'$.usageUnavailable') FROM events WHERE conversation_id=$id AND type IN ('model.requested','model.completed','context.compaction.completed','context.compaction.failed','context.compaction.retry') ORDER BY sequence";
            events.Parameters.AddWithValue("$id", row.ConversationId);
            using var reader = events.ExecuteReader(); var requested = 0; var completed = 0;
            while (reader.Read())
            {
                var type = reader.GetString(0); var usage = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (type == "model.requested") { requested++; continue; }
                if (type == "context.compaction.failed") { row.FailedCompactions++; continue; }
                if (type == "context.compaction.retry") { if (!reader.IsDBNull(2) && reader.GetInt64(2) == 1) row.RequestsWithoutUsage++; continue; }
                if (type == "model.completed") { completed++; Add(row, usage, false); continue; }
                try
                {
                    using var document = JsonDocument.Parse(usage ?? "null");
                    if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0) { row.RequestsWithoutUsage++; continue; }
                    foreach (var item in document.RootElement.EnumerateArray()) Add(row, item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText(), true);
                }
                catch (JsonException) { row.RequestsWithoutUsage++; }
            }
            row.UnfinishedRequests = Math.Max(0, requested - completed);
        }
        tx.Commit(); return new(conversationId, rows);
    }

    private static void Add(UsageRow row, string? json, bool compaction)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? "null"); var usage = document.RootElement;
            if (usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("input_tokens", out var input) && input.TryGetInt64(out var i) && i >= 0 &&
                usage.TryGetProperty("output_tokens", out var output) && output.TryGetInt64(out var o) && o >= 0)
            {
                // Only top-level totals: attribution, cached and reasoning details are subsets.
                var nextInput = checked(row.InputTokens + i); var nextOutput = checked(row.OutputTokens + o); _ = checked(nextInput + nextOutput);
                row.InputTokens = nextInput; row.OutputTokens = nextOutput; row.ReportedRequests++;
                if (usage.TryGetProperty("input_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object && details.TryGetProperty("cached_tokens", out var cached) && cached.TryGetInt64(out var cache) && cache >= 0 && cache <= i)
                { row.CachedInputTokens += cache; row.KnownUncachedInputTokens += i - cache; row.RequestsWithCacheDetails++; }
                if (compaction) { row.CompactionInputTokens += i; row.CompactionOutputTokens += o; }
                return;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or OverflowException) { }
        row.RequestsWithoutUsage++;
    }
}
