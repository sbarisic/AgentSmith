using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

public sealed class ArtifactTests
{
    [Fact]
    public void RetainedLogsAreScopedDecodedSearchableAndBounded()
    {
        using var dir = new TestDirectory(); var ctx = ProcessTests.Context(dir.Path); Directory.CreateDirectory(ctx.ArtifactDirectory);
        var path = Path.Combine(ctx.ArtifactDirectory, "test.jsonl");
        File.WriteAllText(path, Json.Serialize(new { stream = "stdout", text = "cross-ch" }) + "\n" + Json.Serialize(new { stream = "stderr", text = "unrelated" }) + "\n" + Json.Serialize(new { stream = "stdout", text = "unk " + new string('x', 12000) }) + "\n");
        var artifact = ArtifactAccess.Register(ctx, path, "process-log");
        var search = ArtifactAccess.Execute(ctx, "artifact_search", new JsonObject { ["artifactId"] = artifact.Id, ["query"] = "CROSS-CHUNK" });
        Assert.Contains("cross-chunk", search.Content);
        var read = ArtifactAccess.Execute(ctx, "artifact_read", new JsonObject { ["artifactId"] = artifact.Id, ["cursor"] = 0, ["maxCharacters"] = 4000 });
        Assert.True(Encoding.UTF8.GetByteCount(read.Content) < 32768); Assert.True(JsonNode.Parse(read.Content)!["hasMore"]!.GetValue<bool>());
        var saved = ctx.Store.Get(ctx.Session.Id)!;
        Assert.Single(saved.Artifacts);
        Assert.Throws<UserFacingException>(() => ArtifactAccess.Register(ctx, Path.Combine(dir.Path, "credentials.dpapi"), "process-log"));
        Assert.Throws<UserFacingException>(() => ArtifactAccess.Execute(ctx, "artifact_read", new JsonObject { ["artifactId"] = "another-conversation", ["cursor"] = 0, ["maxCharacters"] = 1 }));
        File.Delete(path);
        Assert.Contains("missing", ArtifactAccess.Execute(ctx, "artifact_read", new JsonObject { ["artifactId"] = artifact.Id, ["cursor"] = 0, ["maxCharacters"] = 1 }).Content);
    }
    [Fact]
    public void ArtifactJunctionCannotEscapeConversation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); using var outside = new TestDirectory(); var ctx = ProcessTests.Context(dir.Path); Directory.CreateDirectory(ctx.ArtifactDirectory);
        File.WriteAllText(Path.Combine(outside.Path, "private.jsonl"), "private-token-seed");
        var link = Path.Combine(ctx.ArtifactDirectory, "link");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, outside.Path }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        process.WaitForExit(); Assert.Equal(0, process.ExitCode);
        try { Assert.Throws<UserFacingException>(() => ArtifactAccess.Register(ctx, Path.Combine(link, "private.jsonl"), "process-log")); }
        finally { Directory.Delete(link); }
    }
    [Fact]
    public void GrowingLogDoesNotExposePartialJsonRecord()
    {
        using var dir = new TestDirectory(); var ctx = ProcessTests.Context(dir.Path); Directory.CreateDirectory(ctx.ArtifactDirectory);
        var process = new ProcessRecord { State = "Running", LogPath = Path.Combine(ctx.ArtifactDirectory, "growing.jsonl") }; ctx.Session.Processes.Add(process);
        File.WriteAllText(process.LogPath, "{\"stream\":\"stdout\",\"text\":\"not yet"); ArtifactAccess.Backfill(ctx); var artifact = ctx.Session.Artifacts.Single();
        var args = new JsonObject { ["artifactId"] = artifact.Id, ["cursor"] = 0, ["maxCharacters"] = 4000 };
        Assert.DoesNotContain("not yet", ArtifactAccess.Execute(ctx, "artifact_read", args).Content);
        File.AppendAllText(process.LogPath, "\"}\n"); Assert.Contains("not yet", ArtifactAccess.Execute(ctx, "artifact_read", args).Content);
        File.AppendAllText(process.LogPath, "invalid\n"); Assert.Contains("corrupt", ArtifactAccess.Execute(ctx, "artifact_read", args).Content);
    }
}
