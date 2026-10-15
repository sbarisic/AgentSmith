using System.Diagnostics;
using System.Text.Json;
using AgentSmith.Core;
using AgentSmith.Storage;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class RepositoryMemoryCommandTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "smith-repository-memory-cli-" + Guid.NewGuid().ToString("N"));
    private readonly string data;
    private readonly string current;
    private readonly string other;
    private readonly SessionStore store;

    public RepositoryMemoryCommandTests()
    {
        data = Path.Combine(root, "data"); current = Path.Combine(root, "current"); other = Path.Combine(root, "other");
        Directory.CreateDirectory(current); Directory.CreateDirectory(other);
        store = new SessionStore(data);
    }

    private RepositoryMemoryEntry[] Seed(string workspace, string prefix, int count = 1, bool userAuthored = false)
    {
        File.WriteAllText(Path.Combine(workspace, "README.md"), "# Architecture\nDevelopment facts are documented here.\n");
        var scan = store.ScanRepository(workspace);
        var source = Assert.Single(scan.Sources);
        var proposals = Enumerable.Range(1, count).Select(i => new RepositoryMemoryProposal(prefix + "-" + i.ToString("D2"), 0,
            prefix + " architecture " + i, prefix + "UniqueRepositoryFact " + i, [prefix],
            [new(source.Path, source.Hash, source.StartLine, source.EndLine)], userAuthored)).ToArray();
        Assert.Equal(count, store.CommitRepository(workspace, scan.Id, proposals, Guid.NewGuid().ToString("N")).Created);
        return new RepositoryMemoryStore(workspace, Path.Combine(data, "repositories")).List().ToArray();
    }

    // Processes are exercised only when the parent runs these tests; no model or credentials are needed.
    private async Task<(int Code, string Output, string Error)> Invoke(string[] args, string? workspace = null, string input = "")
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workspace ?? current, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        start.ArgumentList.Add(typeof(AgentSmith.Cli.Program).Assembly.Location);
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(data); start.ArgumentList.Add("--json");
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(input); process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); throw; }
        return (process.ExitCode, await output, await error);
    }

    private static JsonElement Payload((int Code, string Output, string Error) result, string type)
    {
        Assert.Equal(0, result.Code); Assert.Empty(result.Error);
        using var document = JsonDocument.Parse(Assert.Single(result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)));
        Assert.Equal(type, document.RootElement.GetProperty("type").GetString());
        return document.RootElement.GetProperty("payload").Clone();
    }

    [Fact]
    public async Task CurrentListSearchAndShowUseWorkingDirectoryWithProvenanceAndNoWrites()
    {
        var entry = Assert.Single(Seed(current, "current")); Seed(other, "other");
        var sourcePath = Path.Combine(current, "README.md"); var sourceBytes = File.ReadAllBytes(sourcePath);
        var statePath = Path.Combine(current, ".agentsmith", "memory", "state.json"); var stateBytes = File.ReadAllBytes(statePath);
        var catalog = Directory.GetFiles(Path.Combine(data, "repositories")).ToDictionary(p => p, File.ReadAllBytes);
        var list = Payload(await Invoke(["memory", "current", "list"]), "memory.repository.list");
        Assert.Equal("current", list.GetProperty("scope").GetString());
        Assert.Equal(current, list.GetProperty("workspace").GetString());
        Assert.Equal(entry.Id, Assert.Single(list.GetProperty("entries").EnumerateArray()).GetProperty("id").GetString());
        Assert.DoesNotContain("otherUniqueRepositoryFact", list.ToString());
        var search = Payload(await Invoke(["memory", "current", "search", "currentUniqueRepositoryFact"]), "memory.repository.search");
        Assert.Single(search.GetProperty("entries").EnumerateArray());
        var show = Payload(await Invoke(["memory", "current", "show", entry.TopicKey]), "memory.repository.entry");
        Assert.Equal(entry.Id, show.GetProperty("entry").GetProperty("id").GetString());
        Assert.False(show.GetProperty("entry").GetProperty("stale").GetBoolean());
        var source = Assert.Single(show.GetProperty("entry").GetProperty("sources").EnumerateArray());
        Assert.Equal("README.md", source.GetProperty("path").GetString());
        Assert.Equal(entry.Sources[0].Hash, source.GetProperty("hash").GetString());
        Assert.Contains("never instructions or current verification", show.GetProperty("message").GetString());
        Assert.Equal(sourceBytes, File.ReadAllBytes(sourcePath)); Assert.Equal(stateBytes, File.ReadAllBytes(statePath));
        Assert.Equal(catalog.Count, Directory.GetFiles(Path.Combine(data, "repositories")).Length);
        foreach (var pair in catalog) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
    }

    [Fact]
    public async Task RegisteredReadsAreExplicitAndNeverFallbackToGlobalOrAnotherRepository()
    {
        var first = Assert.Single(Seed(current, "current")); var second = Assert.Single(Seed(other, "other"));
        var registered = store.DiscoverRepositories().Single(r => r.Workspace == other);
        var added = Payload(await Invoke(["memory", "add", "GlobalUniqueLibraryFact"]), "memory.saved");
        var globalId = added.GetProperty("id").GetString()!;
        var legacy = Payload(await Invoke(["memory", "list"]), "memory.list");
        Assert.Equal(globalId, Assert.Single(legacy.GetProperty("entries").EnumerateArray()).GetProperty("id").GetString());
        Assert.DoesNotContain(first.Content, legacy.ToString()); Assert.DoesNotContain(second.Content, legacy.ToString());
        var explicitGlobal = Payload(await Invoke(["memory", "global", "show", globalId]), "memory.entry");
        Assert.Equal("GlobalUniqueLibraryFact", explicitGlobal.GetProperty("entry").GetProperty("content").GetString());
        var globalList = Payload(await Invoke(["memory", "global", "list"]), "memory.list");
        Assert.Equal(globalId, Assert.Single(globalList.GetProperty("entries").EnumerateArray()).GetProperty("id").GetString());
        var globalSearch = Payload(await Invoke(["memory", "global", "search", "GlobalUniqueLibraryFact"]), "memory.search");
        Assert.Equal(globalId, Assert.Single(globalSearch.GetProperty("entries").EnumerateArray()).GetProperty("id").GetString());
        var list = Payload(await Invoke(["memory", "repository", registered.Id, "list"]), "memory.repository.list");
        Assert.Equal("repository", list.GetProperty("scope").GetString());
        Assert.Equal(registered.Id, list.GetProperty("repositoryId").GetString());
        Assert.Equal(other, list.GetProperty("workspace").GetString());
        Assert.Equal(second.Id, Assert.Single(list.GetProperty("entries").EnumerateArray()).GetProperty("id").GetString());
        var search = Payload(await Invoke(["memory", "repository", registered.Id, "search", "otherUniqueRepositoryFact"]), "memory.repository.search");
        Assert.Single(search.GetProperty("entries").EnumerateArray());
        var show = Payload(await Invoke(["memory", "repository", registered.Id, "show", second.Id]), "memory.repository.entry");
        Assert.Equal(second.Content, show.GetProperty("entry").GetProperty("content").GetString());
        foreach (var id in new[] { second.Id, globalId })
        {
            var missing = await Invoke(["memory", "current", "show", id]);
            Assert.Equal(2, missing.Code); Assert.Contains("selected scope", missing.Output);
        }
        var noFallback = Payload(await Invoke(["memory", "current", "search", "otherUniqueRepositoryFact GlobalUniqueLibraryFact"]), "memory.repository.search");
        Assert.Empty(noFallback.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task CatalogDiscoveryDoesNotReadRepositoryBodies()
    {
        Seed(other, "other"); var registration = Assert.Single(store.DiscoverRepositories());
        var statePath = Path.Combine(other, ".agentsmith", "memory", "state.json");
        File.WriteAllText(statePath, "intentionally invalid repository body");
        var catalog = Payload(await Invoke(["memory", "repositories"]), "memory.repositories");
        var discovered = Assert.Single(catalog.GetProperty("repositories").EnumerateArray());
        Assert.Equal(registration.Id, discovered.GetProperty("id").GetString());
        Assert.Equal(other, discovered.GetProperty("workspace").GetString());
        Assert.DoesNotContain("otherUniqueRepositoryFact", catalog.ToString());
        Assert.Contains("metadata only", catalog.GetProperty("message").GetString());
        Assert.Equal("intentionally invalid repository body", File.ReadAllText(statePath));
        var opened = await Invoke(["memory", "repository", registration.Id, "list"]);
        Assert.Equal(2, opened.Code);
        Assert.DoesNotContain("otherUniqueRepositoryFact", opened.Output);
    }

    [Fact]
    public async Task RepositoryPaginationIsBoundedAndStable()
    {
        var entries = Seed(current, "topic", 25);
        var first = Payload(await Invoke(["memory", "current", "list", "1"]), "memory.repository.list");
        var second = Payload(await Invoke(["memory", "current", "list", "2"]), "memory.repository.list");
        Assert.Equal(20, first.GetProperty("pageSize").GetInt32()); Assert.True(first.GetProperty("hasMore").GetBoolean());
        Assert.False(second.GetProperty("hasMore").GetBoolean());
        Assert.Equal(entries.Take(20).Select(e => e.Id), first.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("id").GetString()));
        Assert.Equal(entries.Skip(20).Select(e => e.Id), second.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("id").GetString()));
        Assert.Contains("/memory current list 2", first.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ShowLabelsStaleSourcesAndProtectedUserAuthorship()
    {
        var entry = Assert.Single(Seed(current, "protected", userAuthored: true));
        File.WriteAllText(Path.Combine(current, "README.md"), "Source changed after memorization.\n");
        var shown = Payload(await Invoke(["memory", "current", "show", entry.Id]), "memory.repository.entry");
        Assert.True(shown.GetProperty("entry").GetProperty("stale").GetBoolean());
        Assert.True(shown.GetProperty("entry").GetProperty("userAuthored").GetBoolean());
        Assert.Contains("Freshness: stale", shown.GetProperty("message").GetString());
        Assert.Contains("protected from automatic updates", shown.GetProperty("message").GetString());
        Assert.True(shown.GetProperty("entry").TryGetProperty("verifiedAt", out _));
    }

    [Fact]
    public async Task DisabledScopedReadsAndDiscoveryFailBeforeOpeningBodies()
    {
        var entry = Assert.Single(Seed(current, "current")); var registration = Assert.Single(store.DiscoverRepositories());
        File.WriteAllText(Path.Combine(current, ".agentsmith", "memory", "state.json"), "invalid body");
        store.SetMemoryEnabled(false);
        foreach (var command in new[] {
            new[] { "memory", "current", "list" }, new[] { "memory", "current", "search", "current" },
            new[] { "memory", "current", "show", entry.Id }, new[] { "memory", "repositories" },
            new[] { "memory", "repository", registration.Id, "list" },
            new[] { "memory", "global", "list" }, new[] { "memory", "global", "search", "current" },
            new[] { "memory", "global", "show", entry.Id } })
        {
            var result = await Invoke(command);
            Assert.Equal(2, result.Code); Assert.Contains("disabled or unavailable", result.Output);
            Assert.DoesNotContain(entry.Content, result.Output);
        }
        Assert.False(Directory.Exists(Path.Combine(other, ".agentsmith")));
    }

    [Theory]
    [InlineData("current", "list", "0")]
    [InlineData("current", "list", "100001")]
    [InlineData("current", "list", "not-a-page")]
    [InlineData("current", "list", "1 extra")]
    [InlineData("current", "search", "")]
    [InlineData("current", "show", "")]
    [InlineData("current", "show", "two ids")]
    [InlineData("current", "add", "not permitted")]
    [InlineData("global", "add", "not permitted")]
    [InlineData("repositories", "extra", "")]
    public async Task InvalidScopedGrammarIsRejectedWithoutCreatingRepositoryState(string scope, string action, string argument)
    {
        var result = await Invoke(["memory", scope, action, argument]);
        Assert.Equal(2, result.Code);
        Assert.False(Directory.Exists(Path.Combine(current, ".agentsmith")));
        Assert.False(Directory.Exists(Path.Combine(data, "repositories")));
        Assert.Empty(store.ListMemories(1));
    }

    [Fact]
    public async Task UnknownIdsPathsAndMissingRegisteredStorageDoNotOpenAnotherScope()
    {
        Seed(other, "other"); var registration = Assert.Single(store.DiscoverRepositories());
        foreach (var target in new[] { other, "../other", new string('a', 64) })
        {
            var result = await Invoke(["memory", "repository", target, "list"]);
            Assert.Equal(2, result.Code); Assert.DoesNotContain("otherUniqueRepositoryFact", result.Output);
        }
        File.Delete(Path.Combine(other, ".agentsmith", "memory", "state.json"));
        var missing = await Invoke(["memory", "repository", registration.Id, "list"]);
        Assert.Equal(2, missing.Code); Assert.Contains("storage is missing", missing.Output);
        Assert.False(Directory.Exists(Path.Combine(current, ".agentsmith")));
        var emptyCurrent = Payload(await Invoke(["memory", "current", "list"]), "memory.repository.list");
        Assert.Empty(emptyCurrent.GetProperty("entries").EnumerateArray());
        Assert.False(Directory.Exists(Path.Combine(current, ".agentsmith")));
    }

    [Fact]
    public async Task SlashCurrentUsesSessionWorkspaceRatherThanProcessWorkingDirectory()
    {
        var entry = Assert.Single(Seed(other, "session"));
        var session = new Session { Workspace = other, Mode = PermissionMode.Inspect, Connection = new() { Provider = ModelProvider.Local } };
        store.Save(session, new("created", session.Id, null, new { }));
        var result = await Invoke(["resume", session.Id], input: "/memory current show " + entry.TopicKey + "\n/exit\n");
        Assert.Equal(0, result.Code); Assert.Empty(result.Error); Assert.Contains("sessionUniqueRepositoryFact", result.Output);
        using var document = JsonDocument.Parse(result.Output.Split('\n').Single(line => line.Contains("\"type\":\"memory.repository.entry\"", StringComparison.Ordinal)));
        Assert.Equal(other, document.RootElement.GetProperty("payload").GetProperty("workspace").GetString());
        Assert.Empty(store.Get(session.Id)!.History); Assert.Empty(store.Get(session.Id)!.Runs);
        Assert.False(Directory.Exists(Path.Combine(current, ".agentsmith")));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
