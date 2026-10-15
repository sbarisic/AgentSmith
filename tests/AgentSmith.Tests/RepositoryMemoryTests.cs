using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Storage;
using Xunit;

namespace AgentSmith.Tests;

public sealed class RepositoryMemoryTests
{
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "smith-repository-" + Guid.NewGuid().ToString("N"));
        public string Workspace => Path.Combine(Root, "repo");
        public string Catalog => Path.Combine(Root, "catalog");
        public string State => Path.Combine(Workspace, ".agentsmith", "memory", "state.json");
        public Fixture() => Directory.CreateDirectory(Workspace);
        public string Write(string path, string text)
        {
            var file = Path.Combine(Workspace, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
            return file;
        }
        public RepositoryMemoryStore Store(RepositoryScanLimits? limits = null, bool enabled = true) => new(Workspace, Catalog, enabled, limits);
        public RepositorySourceScanner Scanner(RepositoryScanLimits? limits = null) => new(Workspace, limits);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private static RepositoryMemoryProposal Proposal(RepositoryScan scan, string path = "README.md", string topic = "architecture", int revision = 0, string content = "The repository contains the documented application entry point.", bool user = false)
    {
        var source = Assert.Single(scan.Sources, s => s.Path == path);
        return new(topic, revision, "Application architecture", content, ["architecture"],
            [new(source.Path, source.Hash, source.StartLine, source.EndLine)], user);
    }

    [Fact]
    public void ScopedFilesAndDirectoriesInheritParentIgnoreRules()
    {
        using var f = new Fixture();
        f.Write("src/.gitignore", "hidden/\n*.private.cs\n");
        f.Write("src/hidden/App.cs", "class App {}\n");
        f.Write("src/deep/.ignore", "App.cs\n"); f.Write("src/deep/App.cs", "class App {}\n");
        f.Write("src/Other.private.cs", "class Other {}\n");
        Assert.Empty(f.Scanner().Scan("id", "src/hidden").Sources);
        Assert.Empty(f.Scanner().Scan("id", "src/deep/App.cs").Sources);
        Assert.Empty(f.Scanner().Scan("id", "src/Other.private.cs").Sources);
        Assert.DoesNotContain(f.Scanner().Scan("id", "src/deep").Sources, s => s.Path.EndsWith("App.cs"));
    }

    [Fact]
    public void GlobNegationAndAnchoredRulesAreScoped()
    {
        using var f = new Fixture();
        f.Write(".gitignore", "*.cs\n!Keep.cs\n/root.txt\n**/ignored.md\n");
        f.Write("Keep.cs", "class Keep {}\n"); f.Write("Drop.cs", "class Drop {}\n");
        f.Write("root.txt", "excluded\n"); f.Write("nested/root.txt", "included\n"); f.Write("nested/ignored.md", "excluded\n");
        var scan = f.Scanner().Scan("id");
        Assert.Contains(scan.Sources, s => s.Path == "Keep.cs"); Assert.Contains(scan.Sources, s => s.Path == "nested/root.txt");
        Assert.DoesNotContain(scan.Sources, s => s.Path is "Drop.cs" or "root.txt" or "nested/ignored.md");
    }

    [Fact]
    public void SensitiveDirectoriesGeneratedFilesAndBinaryContentAreExcluded()
    {
        using var f = new Fixture(); f.Write("README.md", "Application documentation\n");
        foreach (var path in new[] { ".env.json", "secrets/settings.json", ".ssh/config.txt", ".aws/settings.json", "credentials/data.cs", "bin/Build.cs", "vendor/Library.cs", ".agentsmith/memory/raw.md", "Generated.g.cs", "app.min.js" })
            f.Write(path, "excluded\n");
        f.Write("Bad.cs", "class Bad {}\0\n");
        var scan = f.Scanner().Scan("id"); Assert.Equal("README.md", Assert.Single(scan.Sources).Path);
        Assert.Contains("binary-or-sensitive-content", scan.Coverage.Reasons);
    }

    [Fact]
    public void ScanBoundsAreEnforcedAndIncompleteCoverageIsReported()
    {
        using var f = new Fixture(); f.Write("README.md", "one\ntwo\nthree\n"); f.Write("Other.md", "other\n");
        var entry = f.Scanner(new(MaxVisitedEntries: 1)).Scan("id");
        Assert.Equal(1, entry.Coverage.VisitedEntries); Assert.Empty(entry.Sources); Assert.Contains("entry-limit", entry.Coverage.Reasons);
        var files = f.Scanner(new(MaxFiles: 1)).Scan("id"); Assert.Single(files.Sources); Assert.False(files.Coverage.Complete);
        var evidence = f.Scanner(new(MaxEvidenceCharacters: 5, MaxLinesPerFile: 1)).Scan("id", "README.md");
        Assert.InRange(evidence.Coverage.EvidenceCharacters, 1, 5); Assert.Equal(1, Assert.Single(evidence.Sources).EndLine); Assert.False(evidence.Coverage.Complete);
        var bytes = f.Scanner(new(MaxBytes: 3)).Scan("id"); Assert.Empty(bytes.Sources); Assert.InRange(bytes.Coverage.InspectedBytes, 0L, 3L);
        var file = f.Scanner(new(MaxFileBytes: 3)).Scan("id"); Assert.Empty(file.Sources); Assert.Contains("file-byte-limit", file.Coverage.Reasons);
        Assert.Throws<UserFacingException>(() => f.Scanner(new(MaxFiles: 0)));
    }

    [Theory]
    [InlineData("[ab].cs\n")]
    [InlineData("\\!private.cs\n")]
    public void UnsupportedIgnoreSyntaxFailsClosed(string rule)
    {
        using var f = new Fixture(); f.Write(".gitignore", rule); f.Write("App.cs", "class App {}\n");
        var scan = f.Scanner().Scan("id"); Assert.Empty(scan.Sources); Assert.False(scan.Coverage.Complete); Assert.Contains("unsupported-ignore-rule", scan.Coverage.Reasons);
    }

    [Fact]
    public void OversizedAndOverBudgetIgnoreFilesFailClosed()
    {
        using var f = new Fixture(); f.Write(".gitignore", new string('x', 64_001)); f.Write("App.cs", "class App {}\n");
        var oversized = f.Scanner().Scan("id"); Assert.Empty(oversized.Sources); Assert.Contains("ignore-rule-limit", oversized.Coverage.Reasons);
        f.Write(".gitignore", "App.cs\n"); var budget = f.Scanner(new(MaxBytes: 2)).Scan("id");
        Assert.Empty(budget.Sources); Assert.Contains("ignore-byte-limit", budget.Coverage.Reasons); Assert.InRange(budget.Coverage.InspectedBytes, 0L, 2L);
    }

    [Fact]
    public void EscapesAndExplicitLinksAreRejectedAndEnumeratedLinksAreSkipped()
    {
        using var f = new Fixture(); f.Write("README.md", "Application documentation\n");
        Assert.Throws<UserFacingException>(() => f.Scanner().Scan("id", "../outside"));
        Assert.Throws<UserFacingException>(() => f.Scanner().Scan("id", f.Workspace));
        var outside = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "Outside.cs"), "class Outside {}\n");
        // Link checks below cannot run when the host lacks symbolic-link privileges.
        try { Directory.CreateSymbolicLink(Path.Combine(f.Workspace, "linked"), outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }
        Assert.Throws<UserFacingException>(() => f.Scanner().Scan("id", "linked"));
        var scan = f.Scanner().Scan("id"); Assert.DoesNotContain(scan.Sources, s => s.Path.StartsWith("linked/")); Assert.Contains("links-skipped", scan.Coverage.Reasons);
        Directory.CreateSymbolicLink(Path.Combine(f.Workspace, ".agentsmith"), outside); Assert.Throws<UserFacingException>(() => f.Store());
    }

    [Fact]
    public void DurableReceiptsAndIdenticalRefreshDoNotChurnRevisions()
    {
        using var f = new Fixture(); f.Write("README.md", "Application entry point documentation\n");
        var store = f.Store(); var scan = store.Scan(); var proposal = Proposal(scan);
        var first = store.Commit(scan.Id, [proposal], "create"); Assert.Equal(1, first.Created);
        var reopened = f.Store(); var replay = reopened.Commit(scan.Id, [proposal], "create"); Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(replay));
        Assert.Throws<UserFacingException>(() => reopened.Commit(scan.Id, [proposal with { Content = "Different fact." }], "create"));
        var entry = Assert.Single(reopened.List()); var nextScan = reopened.Scan();
        Assert.Equal(1, reopened.Commit(nextScan.Id, [Proposal(nextScan, revision: 1)], "refresh").Unchanged);
        var refreshed = reopened.Read(entry.Id)!; Assert.Equal(entry.Revision, refreshed.Revision); Assert.Equal(entry.UpdatedAt, refreshed.UpdatedAt); Assert.True(refreshed.VerifiedAt >= entry.VerifiedAt);
        Assert.Single(reopened.History(entry.Id)); Assert.Single(reopened.Search("architecture"));
        Assert.All(reopened.Status().LastScan!.Sources, source => Assert.Equal("", source.Text));
    }

    [Fact]
    public void RevisionConflictsAndUserAuthoredProtectionPreserveBodies()
    {
        using var f = new Fixture(); f.Write("README.md", "Application documentation\n");
        var first = f.Store(); var scan = first.Scan(); first.Commit(scan.Id, [Proposal(scan)], "create"); var second = f.Store(); var next = second.Scan();
        Assert.Equal(1, first.Commit(next.Id, [Proposal(next, revision: 1, content: "Updated architecture fact.")], "update").Updated);
        var conflict = second.Commit(next.Id, [Proposal(next, revision: 1, content: "Stale writer fact.")], "conflict");
        Assert.Equal(1, conflict.Conflicts); Assert.False(conflict.Complete); Assert.Equal(2, first.Read("architecture")!.Revision);
        first.Commit(next.Id, [Proposal(next, topic: "user-note", content: "User-maintained architecture fact.", user: true)], "user");
        var protect = second.Commit(next.Id, [Proposal(next, topic: "user-note", revision: 1, content: "Automatic replacement.")], "automatic");
        Assert.Equal(1, protect.Protected); Assert.Equal("User-maintained architecture fact.", first.Read("user-note")!.Content);
    }

    [Fact]
    public void ChangedSourceOrInvalidEvidenceRejectsTheBatchWithoutWrites()
    {
        using var f = new Fixture(); f.Write("README.md", "Application documentation\n"); var store = f.Store(); var scan = store.Scan(); var proposal = Proposal(scan); var baseline = File.ReadAllText(f.State);
        Assert.Throws<UserFacingException>(() => store.Commit(scan.Id, [proposal with { Sources = [proposal.Sources[0] with { EndLine = 999 }] }], "bad-range"));
        Assert.Throws<UserFacingException>(() => store.Commit(scan.Id, [proposal with { Sources = [proposal.Sources[0] with { Path = "../README.md" }] }], "escape"));
        Assert.Throws<UserFacingException>(() => store.Commit(scan.Id, [proposal, proposal], "duplicate"));
        f.Write("README.md", "Changed application documentation\n"); Assert.Throws<UserFacingException>(() => store.Commit(scan.Id, [proposal], "changed"));
        Assert.Equal(baseline, File.ReadAllText(f.State)); Assert.Empty(store.List());
    }

    [Fact]
    public void PartialScansNeverDeleteUninspectedMemoriesAndMissingFilesAreStale()
    {
        using var f = new Fixture(); f.Write("A.md", "First module\n"); var b = f.Write("B.md", "Second module\n");
        var store = f.Store(); var scan = store.Scan(); store.Commit(scan.Id, [Proposal(scan, "A.md", "module-a"), Proposal(scan, "B.md", "module-b")], "initial");
        f.Write("A.md", "Changed first module\n"); var partial = store.Scan("A.md"); store.Commit(partial.Id, [], "partial");
        Assert.Equal(2, store.List().Count); Assert.True(store.Read("module-a")!.Stale); Assert.False(store.Read("module-b")!.Stale);
        File.Delete(b); Assert.True(f.Store().Read("module-b")!.Stale); Assert.Equal(2, store.List().Count);
        var refreshed = store.Scan("A.md"); store.Commit(refreshed.Id, [Proposal(refreshed, "A.md", "module-a", 1)], "reverify");
        Assert.False(store.Read("module-a")!.Stale); Assert.Equal(1, store.Read("module-a")!.Revision);
    }

    [Fact]
    public void LocalBodiesAndMinimalCatalogKeepDifferentRootsIsolated()
    {
        using var f = new Fixture(); f.Write("README.md", "Application documentation\n"); var store = f.Store(); var scan = store.Scan(); store.Commit(scan.Id, [Proposal(scan)], "create");
        var otherRoot = Path.Combine(f.Root, "other"); Directory.CreateDirectory(otherRoot); File.WriteAllText(Path.Combine(otherRoot, "README.md"), "Another repository\n");
        var other = new RepositoryMemoryStore(otherRoot, f.Catalog); other.Scan(); Assert.Equal(2, RepositoryMemoryStore.Discover(f.Catalog).Count);
        Assert.NotEqual(store.Status().RepositoryId, other.Status().RepositoryId); Assert.Empty(other.List()); Assert.Single(RepositoryMemoryStore.OpenRegistered(f.Catalog, store.Status().RepositoryId).List());
        foreach (var file in Directory.GetFiles(f.Catalog, "*.repository.json"))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            Assert.Equal(new[] { "id", "registeredAt", "workspace" }, json.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());
            Assert.DoesNotContain("Application architecture", File.ReadAllText(file));
        }
        Assert.Throws<UserFacingException>(() => RepositoryMemoryStore.OpenRegistered(f.Catalog, "../bad"));
    }

    [Fact]
    public void CopiesCannotAliasOriginalAndMovesRequireExplicitRelink()
    {
        using var f = new Fixture(); f.Write("README.md", "Application documentation\n"); var store = f.Store(); var scan = store.Scan(); store.Commit(scan.Id, [Proposal(scan)], "create"); var id = store.Status().RepositoryId;
        var clone = Path.Combine(f.Root, "clone"); Directory.CreateDirectory(Path.Combine(clone, ".agentsmith", "memory")); File.Copy(f.State, Path.Combine(clone, ".agentsmith", "memory", "state.json"));
        Assert.Throws<UserFacingException>(() => new RepositoryMemoryStore(clone, f.Catalog).Status()); Assert.Throws<UserFacingException>(() => RepositoryMemoryStore.Relink(f.Catalog, id, clone));
        var moved = Path.Combine(f.Root, "moved"); Directory.Move(f.Workspace, moved);
        Assert.Throws<UserFacingException>(() => RepositoryMemoryStore.OpenRegistered(f.Catalog, id)); Assert.Throws<UserFacingException>(() => new RepositoryMemoryStore(moved, f.Catalog).Status());
        RepositoryMemoryStore.Relink(f.Catalog, id, moved); var reopened = RepositoryMemoryStore.OpenRegistered(f.Catalog, id);
        Assert.Equal(moved, reopened.Status().Workspace); Assert.Single(reopened.List()); Assert.False(reopened.List()[0].Stale);
    }

    [Fact]
    public void CancellationDisableAndWriterLeaseDoNotMutateState()
    {
        using var f = new Fixture(); f.Write("README.md", "Application documentation\n"); using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var store = f.Store();
        Assert.Throws<OperationCanceledException>(() => store.Scan(ct: cancelled.Token)); Assert.False(File.Exists(f.State));
        var disabled = f.Store(enabled: false); Assert.Empty(disabled.List()); Assert.Empty(disabled.Search("architecture")); Assert.Null(disabled.Read("architecture")); Assert.False(disabled.Status().Enabled); Assert.Throws<UserFacingException>(() => disabled.Scan());
        var scan = store.Scan(); var baseline = File.ReadAllText(f.State);
        Assert.Throws<OperationCanceledException>(() => store.Commit(scan.Id, [Proposal(scan)], "cancel", cancelled.Token));
        using (var lease = new FileStream(Path.Combine(f.Workspace, ".agentsmith", "memory", "write.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<UserFacingException>(() => f.Store().Commit(scan.Id, [Proposal(scan)], "locked"));
        Assert.Equal(baseline, File.ReadAllText(f.State));
    }

    [Fact]
    public void CorruptProvenanceIsRejectedWithoutOverwrite()
    {
        using var f = new Fixture(); f.Write("README.md", "Application documentation\n"); var store = f.Store(); var scan = store.Scan(); store.Commit(scan.Id, [Proposal(scan)], "create");
        var json = JsonNode.Parse(File.ReadAllText(f.State))!; json["entries"]![0]!["sources"]![0]!["path"] = "../outside.cs";
        File.WriteAllText(f.State, json.ToJsonString()); var baseline = File.ReadAllText(f.State);
        Assert.Throws<UserFacingException>(() => store.List()); Assert.Throws<UserFacingException>(() => store.Scan()); Assert.Equal(baseline, File.ReadAllText(f.State));
    }

    [Fact]
    public void CatalogFailureIsReportedWithoutLosingLocalBodies()
    {
        using var f = new Fixture(); f.Write("README.md", "Application documentation\n"); File.WriteAllText(f.Catalog, "not a directory");
        var store = f.Store(); var scan = store.Scan(); Assert.Contains("catalog-registration-failed", scan.Coverage.Reasons); Assert.False(scan.Coverage.Complete);
        var report = store.Commit(scan.Id, [Proposal(scan)], "create"); Assert.Equal(1, report.Created); Assert.False(report.Complete);
        Assert.Contains(report.Messages, message => message.Contains("catalog registration failed")); Assert.Single(f.Store().List());
    }
}
