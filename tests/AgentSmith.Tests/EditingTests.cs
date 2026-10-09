using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentSmith.Tests;

public sealed class EditingTests
{
    private static ToolExecutionContext Context(TestDirectory dir, PermissionMode mode = PermissionMode.Execute)
    {
        var store = new SessionStore(Path.Combine(dir.Path, "state")); var session = new Session { Workspace = dir.Path, Mode = mode }; var run = new Run(); session.Runs.Add(run);
        store.Save(session, new("created", session.Id, run.Id, new { })); return new(session, run, store, default);
    }
    private static async Task<ToolCall> Call(ToolRegistry registry, ToolExecutionContext ctx, string name, object args)
    {
        var call = new ToolCall { Id = Guid.NewGuid().ToString("N"), Name = name, Arguments = Json.Serialize(args) };
        call.Prepared = registry.Prepare(ctx, call); ctx.Save("prepared", new { call.Id });
        call.Result = (await registry.ExecuteAsync(ctx, call)).Content; return call;
    }
    [Fact]
    public async Task FileLifecycleUndoPreservesBytesAndUnrelatedWork()
    {
        using var dir = new TestDirectory(); using var registry = new ToolRegistry(); var ctx = Context(dir);
        var original = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes("one\r\ntwo\r\n")).ToArray();
        File.WriteAllBytes(Path.Combine(dir.Path, "a.txt"), original); File.WriteAllText(Path.Combine(dir.Path, "other.txt"), "user edit");
        var read = await Call(registry, ctx, "file_read", new { path = "a.txt", startLine = 2, lineCount = 1 });
        Assert.Equal(FileOperations.Hash(original), JsonNode.Parse(read.Result!)!["hash"]!.GetValue<string>());
        var patch = await Call(registry, ctx, "file_apply_patch", new { path = "a.txt", expectedHash = FileOperations.Hash(original), patch = "@@ -1,2 +1,2 @@\n one\n-two\n+three\n" });
        Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes("one\r\nthree\r\n")), File.ReadAllBytes(Path.Combine(dir.Path, "a.txt")));
        FileOperations.Undo(ctx, patch.Prepared!.OperationId!);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(dir.Path, "a.txt")));
        var move = await Call(registry, ctx, "file_move", new { source = "a.txt", destination = "new/child/b.txt", expectedHash = FileOperations.Hash(original) });
        Assert.False(File.Exists(Path.Combine(dir.Path, "a.txt")));
        FileOperations.Undo(ctx, move.Prepared!.OperationId!);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "new")));
        var delete = await Call(registry, ctx, "file_delete", new { path = "a.txt", expectedHash = FileOperations.Hash(original) });
        FileOperations.Undo(ctx, delete.Prepared!.OperationId!);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(dir.Path, "a.txt")));
        Assert.Equal("user edit", File.ReadAllText(Path.Combine(dir.Path, "other.txt")));
    }
    [Fact]
    public async Task StalePatchCollisionsAndUndoConflictsRejectWithoutOverwrite()
    {
        using var dir = new TestDirectory(); using var registry = new ToolRegistry(); var ctx = Context(dir);
        var call = await Call(registry, ctx, "file_create", new { path = "new.txt", content = "original\n" });
        await Assert.ThrowsAsync<UserFacingException>(() => Call(registry, ctx, "file_create", new { path = "new.txt", content = "overwrite" }));
        await Assert.ThrowsAsync<UserFacingException>(() => Call(registry, ctx, "file_apply_patch", new { path = "new.txt", expectedHash = "stale", patch = "@@ -1 +1 @@\n-original\n+changed\n" }));
        File.WriteAllText(Path.Combine(dir.Path, "new.txt"), "user changed it");
        Assert.Throws<UserFacingException>(() => FileOperations.Undo(ctx, call.Prepared!.OperationId!));
        Assert.Equal("user changed it", File.ReadAllText(Path.Combine(dir.Path, "new.txt")));
    }
    [Theory]
    [InlineData("mismatched context")]
    [InlineData("wrong header")]
    [InlineData("move collision")]
    [InlineData("oversized")]
    [InlineData("binary")]
    [InlineData("encoding")]
    public async Task InvalidMutationsLeaveFilesUntouched(string scenario)
    {
        using var dir = new TestDirectory(); using var registry = new ToolRegistry(); var ctx = Context(dir);
        var bytes = Encoding.UTF8.GetBytes("original\n"); var path = Path.Combine(dir.Path, "a.txt");
        if (scenario == "binary") bytes = [0, 1, 2];
        if (scenario == "encoding") bytes = [0xff, 0xfe, 65, 0];
        File.WriteAllBytes(path, bytes); File.WriteAllText(Path.Combine(dir.Path, "b.txt"), "unrelated");
        await Assert.ThrowsAsync<UserFacingException>(async () =>
        {
            if (scenario == "move collision") await Call(registry, ctx, "file_move", new { source = "a.txt", destination = "b.txt", expectedHash = FileOperations.Hash(bytes) });
            else if (scenario == "oversized") await Call(registry, ctx, "file_create", new { path = "large.txt", content = new string('x', FileOperations.MaxBytes + 1) });
            else await Call(registry, ctx, "file_apply_patch", new { path = "a.txt", expectedHash = FileOperations.Hash(bytes), patch = scenario == "wrong header" ? "--- a/b.txt\n+++ b/b.txt\n@@ -1 +1 @@\n-original\n+new\n" : "@@ -1 +1 @@\n-mismatch\n+new\n" });
        });
        Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Equal("unrelated", File.ReadAllText(Path.Combine(dir.Path, "b.txt")));
    }
    [Theory]
    [InlineData("a\nb", "@@ -1,2 +1 @@\n a\n-b\n\\ No newline at end of file\n", "a\n")]
    [InlineData("a\n", "@@ -1 +1 @@\n-a\n+b\n\\ No newline at end of file\n", "b")]
    [InlineData("", "@@ -0,0 +1 @@\n+new\n", "new\n")]
    public void PatchesRespectEndOfFileMarkers(string before, string patch, string after) => Assert.Equal(after, Encoding.UTF8.GetString(FileOperations.Patch(Encoding.UTF8.GetBytes(before), patch)));
    [Fact]
    public async Task ModeBoundaryAndPreparedFileRecovery()
    {
        using var dir = new TestDirectory(); using var registry = new ToolRegistry(); var ctx = Context(dir, PermissionMode.Inspect);
        Assert.Equal(PermissionMode.Execute, new Session().Mode);
        Assert.DoesNotContain(registry.Available(PermissionMode.Inspect), t => t.Effect is ToolEffect.Workspace or ToolEffect.Process);
        await Assert.ThrowsAsync<UserFacingException>(() => Call(registry, ctx, "file_create", new { path = "blocked.txt", content = "no" }));
        ctx.Session.Mode = PermissionMode.Edit;
        var call = new ToolCall { Name = "file_create", Arguments = Json.Serialize(new { path = "a.txt", content = "after" }) };
        call.Prepared = registry.Prepare(ctx, call); ctx.Save("intent", new { });
        File.WriteAllText(Path.Combine(dir.Path, "a.txt"), "after"); // crash after effect, before result persistence
        Assert.Contains("Applied", registry.Recover(ctx, call)!.Content);
        var another = new ToolCall { Name = "file_create", Arguments = Json.Serialize(new { path = "b.txt", content = "b" }) };
        another.Prepared = registry.Prepare(ctx, another);
        Assert.Contains("not replayed", registry.Recover(ctx, another)!.Content);
        Assert.False(File.Exists(Path.Combine(dir.Path, "b.txt")));
        await Assert.ThrowsAsync<UserFacingException>(() => Call(registry, ctx, "file_create", new { path = "../outside.txt", content = "no" }));
    }
    [Fact]
    public void MigrationBacksUpV1AndDefaultsLegacySessionsToInspect()
    {
        using var dir = new TestDirectory();
        var dbPath = Path.Combine(dir.Path, "sessions.db");
        using (var db = new SqliteConnection("Data Source=" + dbPath))
        {
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TABLE schema_version(version INTEGER); INSERT INTO schema_version VALUES(1); CREATE TABLE conversations(id TEXT PRIMARY KEY,snapshot TEXT NOT NULL,updated TEXT NOT NULL); INSERT INTO conversations VALUES('legacy','{\"id\":\"legacy\"}','now');";
            cmd.ExecuteNonQuery();
        }
        var store = new SessionStore(dir.Path);
        Assert.Equal(PermissionMode.Inspect, store.Get("legacy")!.Mode);
        Assert.Single(Directory.GetFiles(dir.Path, "sessions-v1-*.backup.db"));
        var again = new SessionStore(dir.Path); Assert.Equal(PermissionMode.Inspect, again.Get("legacy")!.Mode);
    }
}
