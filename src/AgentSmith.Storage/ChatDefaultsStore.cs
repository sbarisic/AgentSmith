using System.Text.Json;
using AgentSmith.Core;
using Microsoft.Data.Sqlite;

namespace AgentSmith.Storage;

public sealed partial class SessionStore
{
    public ChatModelSelection? ChatDefault()
    {
        using var db = Open(); using var cmd = Command(db, null, "SELECT payload FROM chat_defaults WHERE id=1");
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<ChatModelSelection>(json, Json.Options) : null;
    }
    public void SaveChatDefault(ChatModelSelection selection)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        PutChatDefault(db, tx, selection); tx.Commit();
    }
    private static void PutChatDefault(SqliteConnection db, SqliteTransaction tx, ChatModelSelection selection)
    {
        if (string.IsNullOrWhiteSpace(selection.Model) || selection.Model.Length > 2048 || selection.Model.Any(char.IsControl)
            || !Enum.IsDefined(selection.Provider) || selection.MaxOutputTokens is < 256 or > 32768)
            throw new UserFacingException("Invalid default model configuration.");
        using var cmd = Command(db, tx, "INSERT INTO chat_defaults VALUES(1,$payload) ON CONFLICT(id) DO UPDATE SET payload=$payload", ("$payload", Json.Serialize(selection)));
        cmd.ExecuteNonQuery();
    }
    public void SelectChatModel(Session session, Session validated)
    {
        var copy = JsonSerializer.Deserialize<Session>(Json.Serialize(session), Json.Options)!;
        var oldModel = session.Model;
        if (session.Connection.Provider != validated.Connection.Provider || session.AccountId != validated.AccountId)
            throw new UserFacingException("Changing provider or account requires a new conversation.");
        copy.Model = validated.Model; copy.Connection = validated.Connection;
        if (oldModel != copy.Model) copy.ContextPolicy.TokensPerByte = new ContextPolicy().TokensPerByte;
        using var db = Open(); using var tx = db.BeginTransaction();
        PutChatDefault(db, tx, ChatModelSelection.From(copy));
        SaveInTransaction(copy, new("model.changed", copy.Id, null, new { oldModel, model = copy.Model, provider = copy.Connection.Provider.ToString() }), db, tx);
        tx.Commit();
        session.Model = copy.Model; session.Connection = copy.Connection; session.UpdatedAt = copy.UpdatedAt;
        session.ContextPolicy.TokensPerByte = copy.ContextPolicy.TokensPerByte;
    }
}
