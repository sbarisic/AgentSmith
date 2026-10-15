using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Storage;
using Xunit;

namespace AgentSmith.Tests;

public sealed class RepositoryCatalogRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "smith-catalog-" + Guid.NewGuid().ToString("N"));
    private string Workspace => Path.Combine(root, "repo");
    private string Catalog => Path.Combine(root, "catalog");
    private static string State(string workspace) => Path.Combine(workspace, ".agentsmith", "memory", "state.json");

    public RepositoryCatalogRecoveryTests()
    {
        Directory.CreateDirectory(Workspace);
        File.WriteAllText(Path.Combine(Workspace, "README.md"), "Application documentation\n");
    }

    [Fact]
    public void CatalogContentionReportsIncompleteScanAndLaterRegistrationRecovers()
    {
        Directory.CreateDirectory(Catalog);
        var store = new RepositoryMemoryStore(Workspace, Catalog);
        using (var lease = new FileStream(Path.Combine(Catalog, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var scan = store.Scan();
            Assert.False(scan.Coverage.Complete);
            Assert.Contains("catalog-registration-failed", scan.Coverage.Reasons);
            Assert.Empty(RepositoryMemoryStore.Discover(Catalog));
            Assert.NotNull(store.Status().LastScan);
        }
        store.Scan();
        Assert.Single(RepositoryMemoryStore.Discover(Catalog));
    }

    [Fact]
    public void InterruptedRelinkCanRetryAndSuccessfulRelinkIsIdempotent()
    {
        var store = new RepositoryMemoryStore(Workspace, Catalog);
        var id = store.Scan().RepositoryId;
        var moved = Path.Combine(root, "moved");
        Directory.Move(Workspace, moved);
        // Simulate interruption after local save but before catalog replacement.
        var state = JsonNode.Parse(File.ReadAllText(State(moved)))!;
        state["workspace"] = moved;
        File.WriteAllText(State(moved), state.ToJsonString());
        RepositoryMemoryStore.Relink(Catalog, id, moved);
        RepositoryMemoryStore.Relink(Catalog, id, moved);
        Assert.Equal(moved, RepositoryMemoryStore.OpenRegistered(Catalog, id).Status().Workspace);
        Assert.Equal(moved, Assert.Single(RepositoryMemoryStore.Discover(Catalog)).Workspace);
    }

    [Fact]
    public void RelinkCatalogContentionDoesNotChangeLocalStateAndCanRetry()
    {
        var store = new RepositoryMemoryStore(Workspace, Catalog);
        var id = store.Scan().RepositoryId;
        var moved = Path.Combine(root, "moved");
        Directory.Move(Workspace, moved);
        var before = File.ReadAllBytes(State(moved));
        using (var lease = new FileStream(Path.Combine(Catalog, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<UserFacingException>(() => RepositoryMemoryStore.Relink(Catalog, id, moved));
            Assert.Equal(before, File.ReadAllBytes(State(moved)));
        }
        RepositoryMemoryStore.Relink(Catalog, id, moved);
        Assert.Equal(moved, RepositoryMemoryStore.OpenRegistered(Catalog, id).Status().Workspace);
    }

    public void Dispose() => Directory.Delete(root, true);
}
