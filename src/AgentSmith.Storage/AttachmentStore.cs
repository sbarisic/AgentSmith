using System.Text.Json;
using AgentSmith.Core;

namespace AgentSmith.Storage;

public sealed partial class SessionStore
{
    public IReadOnlyList<AttachmentRecord> Attachments(string conversationId)
    {
        using var db = Open();
        using var cmd = Command(db, null, "SELECT payload FROM attachments WHERE conversation_id=$id", ("$id", conversationId));
        using var reader = cmd.ExecuteReader(); var result = new List<AttachmentRecord>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<AttachmentRecord>(reader.GetString(0), Json.Options)!);
        return result;
    }
    public void SaveAttachment(AttachmentRecord item)
    {
        AttachmentFiles.ValidatePath(this, item);
        using var db = Open();
        using var cmd = Command(db, null, "INSERT INTO attachments VALUES($id,$conversation,$payload)", ("$id", item.Id), ("$conversation", item.ConversationId), ("$payload", Json.Serialize(item)));
        cmd.ExecuteNonQuery();
    }
}
