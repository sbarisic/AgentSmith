using AgentSmith.Core;
using AgentSmith.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentSmith.Tests;

public sealed class ChatDefaultsTests
{
    private static Session Candidate(string model) => new() { Model = model, AccountId = "account-ref", Connection = new() { MaxOutputTokens = 16384 } };
    [Fact]
    public void MigrationPreservesConversationAndHeldQueue()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path);
        var s = Candidate("old"); s.Mode = PermissionMode.Inspect;
        store.Save(s, new("created", s.Id, null, new { })); store.Enqueue(s.Id, "later"); store.HoldQueue(s.Id, "restart");
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(dir.Path, "sessions.db")))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DROP TABLE chat_defaults; UPDATE schema_version SET version=9"; cmd.ExecuteNonQuery(); }
        var migrated = new SessionStore(dir.Path);
        Assert.Null(migrated.ChatDefault()); Assert.Equal("restart", migrated.Queue(s.Id).HoldReason);
        Assert.Equal(PermissionMode.Inspect, migrated.Get(s.Id)!.Mode); Assert.Null(migrated.ClaimQueued(s));
        Assert.NotEmpty(Directory.GetFiles(dir.Path, "sessions-v9-*.backup.db"));
    }
    [Fact]
    public void SelectionAndDefaultCommitTogetherOrNeitherChanges()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Candidate("old");
        store.Save(s, new("created", s.Id, null, new { })); store.SaveChatDefault(ChatModelSelection.From(s));
        using var db = new SqliteConnection("Data Source=" + Path.Combine(dir.Path, "sessions.db")); db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TRIGGER reject_selection BEFORE INSERT ON events WHEN NEW.type='model.changed' BEGIN SELECT RAISE(ABORT,'fixture'); END"; cmd.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => store.SelectChatModel(s, Candidate("next")));
        Assert.Equal("old", s.Model); Assert.Equal("old", store.Get(s.Id)!.Model); Assert.Equal("old", store.ChatDefault()!.Model);
        cmd.CommandText = "DROP TRIGGER reject_selection"; cmd.ExecuteNonQuery();
        store.SelectChatModel(s, Candidate("next"));
        var reopened = new SessionStore(dir.Path);
        Assert.Equal("next", s.Model); Assert.Equal("next", reopened.Get(s.Id)!.Model); Assert.Equal("next", reopened.ChatDefault()!.Model);
    }
    [Fact]
    public void UsageRemainsAttributedToEachSelectedModelIncludingSwitchBack()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = Candidate("old");
        void Request(int count)
        {
            store.Save(s, new("model.requested", s.Id, null, new { }));
            store.Save(s, new("model.completed", s.Id, null, new { usage = Json.Serialize(new { input_tokens = count, output_tokens = 1 }) }));
        }
        Request(100); store.SelectChatModel(s, Candidate("next")); Request(200);
        store.SelectChatModel(s, Candidate("old")); Request(300);
        var usage = store.Usage(s.Id);
        Assert.Equal(2, usage.Rows.Count); Assert.Equal(400, usage.Rows.Single(r => r.Model == "old").InputTokens);
        Assert.Equal(200, usage.Rows.Single(r => r.Model == "next").InputTokens); Assert.Equal(600, usage.CloudInputTokens);
    }
}
