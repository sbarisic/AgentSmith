using System.Text.Json;
using AgentSmith.Core;
using AgentSmith.Storage;
using Xunit;

namespace AgentSmith.Tests;

public sealed class RepositoryMemoryRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "smith-memory-recovery-" + Guid.NewGuid().ToString("N"));
    private string Workspace => Path.Combine(root, "repo");
    private string Catalog => Path.Combine(root, "catalog");
    private string State => Path.Combine(Workspace, ".agentsmith", "memory", "state.json");
    private RepositoryMemoryStore Open() => new(Workspace, Catalog);

    public RepositoryMemoryRecoveryTests()
    {
        Directory.CreateDirectory(Workspace);
        File.WriteAllText(Path.Combine(Workspace, "README.md"), "The application starts in Program.cs.\n");
    }

    private static RepositoryMemoryProposal Proposal(RepositoryScan scan, int revision = 0, string content = "The documented entry point is Program.cs.")
    {
        var source = Assert.Single(scan.Sources);
        return new("entry-point", revision, "Application entry point", content, ["architecture"],
            [new(source.Path, source.Hash, source.StartLine, source.EndLine)]);
    }

    [Fact]
    public void DurableReceiptSurvivesScanExpiryButNewOperationRequiresFreshScan()
    {
        var store = Open();
        var sourceBytes = File.ReadAllBytes(Path.Combine(Workspace, "README.md"));
        var scan = store.Scan();
        var proposal = Proposal(scan);
        var report = store.Commit(scan.Id, [proposal], "initial");
        for (var i = 0; i < 16; i++) Open().Scan();
        var before = File.ReadAllBytes(State);
        var reopened = Open();
        var replay = reopened.Commit(scan.Id, [proposal], "initial");
        Assert.Equal(JsonSerializer.Serialize(report), JsonSerializer.Serialize(replay));
        Assert.Equal(before, File.ReadAllBytes(State));
        Assert.Throws<UserFacingException>(() => reopened.Commit(scan.Id, [proposal], "new-operation"));
        Assert.Equal(before, File.ReadAllBytes(State));
        var entry = Assert.Single(reopened.List());
        Assert.Equal(1, entry.Revision);
        Assert.Single(reopened.History(entry.Id));
        Assert.Equal(sourceBytes, File.ReadAllBytes(Path.Combine(Workspace, "README.md")));
    }

    [Fact]
    public void RefusedWriterCanRetryAfterLeaseReleaseAndConflictReceiptRemainsIdempotent()
    {
        var store = Open();
        var scan = store.Scan();
        store.Commit(scan.Id, [Proposal(scan)], "initial");
        var update = Proposal(scan, 1, "Program.cs is the documented application startup file.");
        var before = File.ReadAllBytes(State);
        using (var lease = new FileStream(Path.Combine(Workspace, ".agentsmith", "memory", "write.lock"),
                   FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<UserFacingException>(() => Open().Commit(scan.Id, [update], "retry-update"));
            Assert.Equal(before, File.ReadAllBytes(State));
        }
        Assert.Equal(1, Open().Commit(scan.Id, [update], "retry-update").Updated);
        var conflict = Open().Commit(scan.Id, [update with { Content = "A competing stale fact." }], "conflict");
        Assert.Equal(1, conflict.Conflicts);
        Assert.False(conflict.Complete);
        var after = File.ReadAllBytes(State);
        var replay = Open().Commit(scan.Id, [update with { Content = "A competing stale fact." }], "conflict");
        Assert.Equal(JsonSerializer.Serialize(conflict), JsonSerializer.Serialize(replay));
        Assert.Equal(after, File.ReadAllBytes(State));
        var entry = Assert.Single(Open().List());
        Assert.Equal(update.Content, entry.Content);
        Assert.Equal(2, entry.Revision);
        Assert.Equal(2, Open().History(entry.Id).Count);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
