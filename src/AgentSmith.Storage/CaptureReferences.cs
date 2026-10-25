namespace AgentSmith.Storage;

public sealed partial class SessionStore
{
    public void QueueCaptureSelection(string conversationId, string captureId)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS pending_capture_selection(conversation_id TEXT PRIMARY KEY, capture_id TEXT NOT NULL); INSERT INTO pending_capture_selection VALUES($conversation,$capture) ON CONFLICT(conversation_id) DO UPDATE SET capture_id=excluded.capture_id;";
        command.Parameters.AddWithValue("$conversation", conversationId); command.Parameters.AddWithValue("$capture", captureId); command.ExecuteNonQuery();
    }
    public string? PendingCaptureSelection(string conversationId)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS pending_capture_selection(conversation_id TEXT PRIMARY KEY, capture_id TEXT NOT NULL); SELECT capture_id FROM pending_capture_selection WHERE conversation_id=$conversation;";
        command.Parameters.AddWithValue("$conversation", conversationId); return command.ExecuteScalar() as string;
    }
    public void ClearCaptureSelection(string conversationId, string captureId)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM pending_capture_selection WHERE conversation_id=$conversation AND capture_id=$capture";
        command.Parameters.AddWithValue("$conversation", conversationId); command.Parameters.AddWithValue("$capture", captureId); command.ExecuteNonQuery();
    }
    public void RecordCapture(string conversationId, string captureId)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS capture_references(conversation_id TEXT NOT NULL, capture_id TEXT NOT NULL, PRIMARY KEY(conversation_id,capture_id));
            INSERT OR IGNORE INTO capture_references(conversation_id,capture_id) VALUES($conversation,$capture);
            """;
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$capture", captureId);
        command.ExecuteNonQuery();
    }
}
