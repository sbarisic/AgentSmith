using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentSmith.Core;

namespace AgentSmith.Storage;

public sealed partial class SessionStore : IRepositoryMemoryStore
{
    private string RepositoryCatalog => Path.Combine(DataDirectory, "repositories");
    private RepositoryMemoryStore Repository(string workspace) => new(workspace, RepositoryCatalog, MemoryEnabled);
    public RepositoryScan ScanRepository(string workspace, string path = ".", bool full = false, CancellationToken ct = default) => Repository(workspace).Scan(path, full, ct);
    public RepositoryCommitReport CommitRepository(string workspace, string scanId, IReadOnlyList<RepositoryMemoryProposal> proposals, string operationId, CancellationToken ct = default) => Repository(workspace).Commit(scanId, proposals, operationId, ct);
    public IReadOnlyList<RepositoryMemoryEntry> SearchRepositoryMemories(string workspace, string query) => MemoryEnabled ? Repository(workspace).Search(query) : [];
    public RepositoryMemoryEntry? ReadRepositoryMemory(string workspace, string id) => MemoryEnabled ? Repository(workspace).Read(id) : null;
    public RepositoryMemoryStatus RepositoryStatus(string workspace) => Repository(workspace).Status();
    public IReadOnlyList<RepositoryRegistration> DiscoverRepositories() => MemoryEnabled ? RepositoryMemoryStore.Discover(RepositoryCatalog) : [];
    public string ResolveRepository(string id) => RepositoryMemoryStore.OpenRegistered(RepositoryCatalog, id, MemoryEnabled).Status().Workspace;
}

/// <summary>Repository-local, atomic JSON storage. Historical facts are evidence, never authorization.</summary>
public sealed class RepositoryMemoryStore
{
    private readonly string workspace;
    private readonly string directory;
    private readonly string catalogDirectory;
    private readonly RepositoryScanLimits limits;
    public bool MemoryEnabled { get; }
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const int MaxStateBytes = 32_000_000;

    public RepositoryMemoryStore(string workspace, string catalogDirectory, bool enabled = true, RepositoryScanLimits? limits = null)
    {
        this.workspace = RepositoryPaths.Canonical(workspace);
        this.catalogDirectory = RepositoryPaths.Canonical(catalogDirectory);
        directory = RepositoryPaths.Inside(this.workspace, ".agentsmith/memory");
        RepositoryPaths.NoLinks(this.workspace); RepositoryPaths.NoLinks(this.catalogDirectory);
        if (!Directory.Exists(this.workspace)) throw new UserFacingException("Repository workspace does not exist.");
        MemoryEnabled = enabled; this.limits = limits ?? new();
        _ = new RepositorySourceScanner(this.workspace, this.limits);
    }

    public RepositoryScan Scan(string path = ".", bool full = false, CancellationToken ct = default)
    {
        EnsureEnabled(); ct.ThrowIfCancellationRequested();
        var initial = Load();
        var scan = new RepositorySourceScanner(workspace, limits).Scan(initial.Id, path, full, ct);
        using var lease = Lock();
        var state = Load();
        if (state.Id != initial.Id) throw new UserFacingException("Repository identity changed during scan.");
        if (!TryRegister(state.Id)) scan = scan with { Coverage = scan.Coverage with {
            Complete = false, Reasons = scan.Coverage.Reasons.Append("catalog-registration-failed").ToArray() } };
        var manifest = scan with { Sources = scan.Sources.Select(s => s with { Text = "" }).ToArray() };
        state.Scans.Add(manifest);
        if (state.Scans.Count > 16) state.Scans.RemoveAt(0);
        ct.ThrowIfCancellationRequested(); Save(state);
        return scan;
    }

    public RepositoryMemoryEntry? Read(string id)
    {
        if (!MemoryEnabled) return null;
        var entry = Load().Entries.FirstOrDefault(e => e.Id == id || e.TopicKey == NormalizeTopic(id));
        return entry is null ? null : Freshness(entry);
    }
    public IReadOnlyList<RepositoryMemoryEntry> List() => !MemoryEnabled ? [] : Load().Entries.Select(Freshness).OrderBy(e => e.TopicKey, StringComparer.Ordinal).ToArray();
    public IReadOnlyList<RepositoryMemoryEntry> Search(string query)
    {
        if (!MemoryEnabled) return [];
        var terms = Regex.Matches(query[..Math.Min(query.Length, 32_000)], @"[\p{L}\p{N}_-]{2,64}")
            .Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToArray();
        if (terms.Length == 0) return [];
        return List().Select(e => new { Entry = e, Score = terms.Sum(t =>
                (e.Title.Contains(t, StringComparison.OrdinalIgnoreCase) ? 3 : 0) +
                (e.TopicKey.Contains(t, StringComparison.OrdinalIgnoreCase) ? 2 : 0) +
                (e.Content.Contains(t, StringComparison.OrdinalIgnoreCase) ? 1 : 0) +
                (e.Tags.Any(tag => tag.Contains(t, StringComparison.OrdinalIgnoreCase)) ? 2 : 0)) })
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Entry.Stale)
            .ThenBy(x => x.Entry.TopicKey, StringComparer.Ordinal).Take(20).Select(x => x.Entry).ToArray();
    }
    public IReadOnlyList<RepositoryMemoryEntry> History(string id) => !MemoryEnabled ? [] : Load().History.Where(e => e.Id == id).OrderBy(e => e.Revision).ToArray();

    public RepositoryCommitReport Commit(string scanId, IReadOnlyList<RepositoryMemoryProposal> proposals,
        string operationId, CancellationToken ct = default)
    {
        EnsureEnabled();
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128 || operationId.Any(char.IsControl))
            throw new UserFacingException("A bounded, stable operation ID is required.");
        if (proposals.Count > 64) throw new UserFacingException("At most 64 memory proposals may be committed at once.");
        ct.ThrowIfCancellationRequested(); using var lease = Lock(); var state = Load();
        var fingerprint = RepositorySourceScanner.Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { scanId, proposals }, Options)));
        var receipt = state.Receipts.FirstOrDefault(r => r.Report.OperationId == operationId);
        if (receipt is not null)
        {
            if (receipt.Fingerprint != fingerprint) throw new UserFacingException("Operation ID was reused for different proposals.");
            return receipt.Report;
        }
        var scan = state.Scans.FirstOrDefault(s => s.Id == scanId)
            ?? throw new UserFacingException("Scan manifest is missing or expired. Scan again before committing.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var proposal in proposals)
        {
            if (proposal is null || proposal.TopicKey is null || proposal.Title is null || proposal.Content is null ||
                proposal.Tags is null || proposal.Tags.Any(t => t is null) || proposal.Sources is null || proposal.Sources.Any(s => s is null))
                throw new UserFacingException("Memory proposal fields cannot be null.");
            var key = NormalizeTopic(proposal.TopicKey);
            if (key.Length is < 1 or > 120 || !Regex.IsMatch(key, @"^[a-z0-9][a-z0-9._/-]*$")) throw new UserFacingException("Topic keys require 1..120 stable lowercase identifier characters.");
            if (!seen.Add(key)) throw new UserFacingException("Duplicate topic key in proposal batch.");
            MemoryPolicy.Validate(proposal.Title, proposal.Content, proposal.Tags);
            if (proposal.ExpectedRevision < 0 || proposal.Sources.Length is < 1 or > 8) throw new UserFacingException("Proposals require a revision and 1..8 scan evidence references.");
            foreach (var source in proposal.Sources)
            {
                var file = RepositoryPaths.Inside(workspace, source.Path);
                if (source.Path != Path.GetRelativePath(workspace, file).Replace('\\', '/')) throw new UserFacingException("Source references must use canonical workspace-relative paths.");
                var inspected = scan.Sources.FirstOrDefault(s => s.Path == source.Path && s.Hash == source.Hash);
                if (inspected is null || source.StartLine < inspected.StartLine || source.EndLine > inspected.EndLine || source.StartLine > source.EndLine)
                    throw new UserFacingException("Proposal source was not inspected within the scan evidence ranges.");
                if (!MatchesHash(file, source.Hash, scan.Limits.MaxFileBytes, ct))
                    throw new UserFacingException("Source changed or became inaccessible after scan. No proposals were committed.");
            }
        }
        int created = 0, updated = 0, unchanged = 0, protect = 0, conflicts = 0;
        var messages = new List<string>(); var now = DateTimeOffset.UtcNow;
        foreach (var proposal in proposals)
        {
            ct.ThrowIfCancellationRequested();
            var key = NormalizeTopic(proposal.TopicKey); var prior = state.Entries.FirstOrDefault(e => e.TopicKey == key);
            if (prior is not null && prior.UserAuthored && !proposal.UserAuthored) { protect++; messages.Add($"Protected user-authored topic: {key}"); continue; }
            if ((prior?.Revision ?? 0) != proposal.ExpectedRevision) { conflicts++; messages.Add($"Revision conflict: {key}"); continue; }
            var title = proposal.Title.Trim(); var content = proposal.Content.Trim();
            var tags = proposal.Tags.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var sources = proposal.Sources.Distinct().OrderBy(s => s.Path, StringComparer.Ordinal).ThenBy(s => s.StartLine).ToArray();
            bool same = prior is not null && prior.Title == title && prior.Content == content && prior.Tags.SequenceEqual(tags) && prior.UserAuthored == proposal.UserAuthored;
            var entry = new RepositoryMemoryEntry(prior?.Id ?? TopicId(key), key, same ? prior!.Revision : (prior?.Revision ?? 0) + 1,
                title, content, tags, sources, prior?.CreatedAt ?? now, same ? prior!.UpdatedAt : now, now, proposal.UserAuthored, false);
            if (prior is null)
            {
                if (state.Entries.Count >= 1000) throw new UserFacingException("Repository memory limit reached.");
                state.Entries.Add(entry); created++;
            }
            else { state.Entries[state.Entries.IndexOf(prior)] = entry; if (same) unchanged++; else updated++; }
            if (!same) state.History.Add(entry);
        }
        foreach (var entry in state.Entries.ToArray())
        {
            if (seen.Contains(entry.TopicKey)) continue;
            bool changed = entry.Sources.Any(s => scan.Sources.Any(inspected => inspected.Path == s.Path && inspected.Hash != s.Hash));
            if (changed && !entry.Stale) state.Entries[state.Entries.IndexOf(entry)] = entry with { Stale = true };
        }
        foreach (var source in proposals.SelectMany(p => p.Sources).Distinct())
            if (!MatchesHash(RepositoryPaths.Inside(workspace, source.Path), source.Hash, scan.Limits.MaxFileBytes, ct))
                throw new UserFacingException("Source changed during reconciliation. No proposals were committed.");
        bool registered = TryRegister(state.Id);
        if (!registered) messages.Add("Repository catalog registration failed; local memories remain available by workspace.");
        var report = new RepositoryCommitReport(operationId, scanId, now, created, updated, unchanged, protect,
            state.Entries.Count(e => e.Stale), conflicts, messages.ToArray(), scan.Coverage.Complete && conflicts == 0 && registered);
        state.Receipts.Add(new(fingerprint, report)); state.LastReport = report;
        ct.ThrowIfCancellationRequested(); Save(state);
        return report;
    }

    public RepositoryMemoryStatus Status()
    {
        if (!MemoryEnabled) return new(Identity(workspace), workspace, false, 0, 0, null, null);
        var state = Load(); var entries = state.Entries.Select(Freshness).ToArray();
        return new(state.Id, workspace, true, entries.Length, entries.Count(e => e.Stale), state.Scans.LastOrDefault(), state.LastReport);
    }
    public static IReadOnlyList<RepositoryRegistration> Discover(string catalogDirectory)
    {
        var directory = RepositoryPaths.Canonical(catalogDirectory); RepositoryPaths.NoLinks(directory);
        if (!Directory.Exists(directory)) return [];
        var result = new List<RepositoryRegistration>(); int visited = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.repository.json"))
        {
            if (++visited > 1000) break;
            RepositoryPaths.NoLinks(file);
            var registration = ReadJson<RepositoryRegistration>(file, 16_000);
            if (!ValidId(registration.Id) || string.IsNullOrWhiteSpace(registration.Workspace) || registration.Workspace != RepositoryPaths.Canonical(registration.Workspace) ||
                !Path.GetFileName(file).Equals(registration.Id + ".repository.json", StringComparison.Ordinal))
                throw new UserFacingException("Invalid repository catalog entry.");
            result.Add(registration);
        }
        return result.OrderBy(r => r.Workspace, StringComparer.Ordinal).ToArray();
    }
    public static RepositoryMemoryStore OpenRegistered(string catalogDirectory, string id, bool enabled = true)
    {
        if (!enabled) throw new UserFacingException("Memory is disabled.");
        var registration = FindRegistration(catalogDirectory, id);
        var store = new RepositoryMemoryStore(registration.Workspace, catalogDirectory, enabled);
        if (!File.Exists(store.StatePath)) throw new UserFacingException("Registered repository storage is missing; explicit relinking may be required.");
        if (store.Load().Id != id) throw new UserFacingException("Registered repository identity does not match local storage.");
        return store;
    }
    public static void Relink(string catalogDirectory, string id, string newWorkspace)
    {
        var store = new RepositoryMemoryStore(newWorkspace, catalogDirectory);
        using var lease = store.Lock();
        using var catalogLease = store.LockCatalog();
        var registration = FindRegistration(catalogDirectory, id);
        if (!registration.Workspace.Equals(store.workspace, RepositoryPaths.Comparison) && Directory.Exists(registration.Workspace))
            throw new UserFacingException("Original workspace still exists; clones require a separate repository identity.");
        var state = ReadJson<State>(store.StatePath, MaxStateBytes);
        Validate(state);
        // A prior attempt may have saved local state before the catalog write failed.
        if (state.Id != id || (!state.Workspace.Equals(registration.Workspace, RepositoryPaths.Comparison) &&
            !state.Workspace.Equals(store.workspace, RepositoryPaths.Comparison)))
            throw new UserFacingException("Relink target does not contain the original repository memory state.");
        state.Workspace = store.workspace; store.Save(state); store.RegisterLocked(id, relink: true);
    }

    private string StatePath => Path.Combine(directory, "state.json");
    private static string NormalizeTopic(string topic) => topic.Trim().ToLowerInvariant();
    private static string Identity(string root) => RepositorySourceScanner.Hash(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root));
    private static string TopicId(string key) => RepositorySourceScanner.Hash(Encoding.UTF8.GetBytes(key))[..32];
    private static bool ValidId(string? id) => id is not null && Regex.IsMatch(id, "^[a-f0-9]{64}$");
    private void EnsureEnabled() { if (!MemoryEnabled) throw new UserFacingException("Memory is disabled."); }
    private State Load()
    {
        RepositoryPaths.NoLinks(directory); RepositoryPaths.NoLinks(StatePath);
        if (!File.Exists(StatePath)) return new() { Id = Identity(workspace), Workspace = workspace };
        var state = ReadJson<State>(StatePath, MaxStateBytes); Validate(state);
        if (!state.Workspace.Equals(workspace, RepositoryPaths.Comparison))
            throw new UserFacingException("Repository memory was moved or copied. Explicit relinking is required; clones must retain separate identities.");
        return state;
    }
    private static void Validate(State state)
    {
        if (state.Version != 1 || !ValidId(state.Id) || string.IsNullOrWhiteSpace(state.Workspace) ||
            state.Workspace != RepositoryPaths.Canonical(state.Workspace) ||
            state.Entries is null || state.History is null || state.Scans is null || state.Receipts is null ||
            state.Entries.Count > 1000 || state.Scans.Count > 16 || state.History.Count > 16_000 || state.Receipts.Count > 4096 ||
            state.Entries.Any(e => e is null) || state.Entries.GroupBy(e => e.TopicKey).Any(g => g.Count() > 1))
            throw new UserFacingException("Repository memory state is corrupt or has an unsupported version.");
        if (state.Receipts.Any(r => r is null || !ValidId(r.Fingerprint) || r.Report is null ||
                string.IsNullOrWhiteSpace(r.Report.OperationId) || r.Report.OperationId.Length > 128 || r.Report.OperationId.Any(char.IsControl) ||
                r.Report.Messages is null || r.Report.Messages.Any(m => m is null)) ||
            state.Receipts.GroupBy(r => r.Report.OperationId).Any(g => g.Count() > 1))
            throw new UserFacingException("Repository memory receipt is corrupt.");

        foreach (var entry in state.Entries.Concat(state.History))
        {
            if (entry is null || entry.TopicKey is null || entry.TopicKey.Length is < 1 or > 120 ||
                !Regex.IsMatch(entry.TopicKey, @"^[a-z0-9][a-z0-9._/-]*$") || entry.Id != TopicId(entry.TopicKey) ||
                entry.Title is null || entry.Content is null || entry.Tags is null || entry.Tags.Any(t => t is null))
                throw new UserFacingException("Repository memory entry is corrupt.");
            MemoryPolicy.Validate(entry.Title, entry.Content, entry.Tags);
            if (entry.Revision < 1 || entry.Sources is null || entry.Sources.Length is < 1 or > 8 ||
                entry.Sources.Any(s => s is null || !ValidSource(state.Workspace, s.Path, s.Hash, s.StartLine, s.EndLine)))
                throw new UserFacingException("Repository memory provenance is corrupt.");
        }
        foreach (var scan in state.Scans)
        {
            if (scan is null || scan.RepositoryId != state.Id || scan.Limits is null || scan.Coverage is null ||
                scan.Coverage.Reasons is null || scan.Sources is null || scan.Sources.Length > 2000 ||
                scan.Limits.MaxFileBytes is < 1 or > 1_000_000 ||
                scan.Sources.Any(s => s is null || !ValidSource(state.Workspace, s.Path, s.Hash, s.StartLine, s.EndLine) || s.Text != ""))
                throw new UserFacingException("Repository scan manifest is corrupt.");
        }
    }
    private static bool ValidSource(string root, string? path, string? hash, int start, int end)
    {
        if (string.IsNullOrWhiteSpace(path) || !ValidId(hash) || start < 1 || end < start || end > 1000 || Path.IsPathRooted(path)) return false;
        try
        {
            var absolute = RepositoryPaths.Canonical(Path.Combine(root, path));
            return path == Path.GetRelativePath(root, absolute).Replace('\\', '/') &&
                !path.Split('/').Any(p => p is ".." or ".");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private FileStream Lock()
    {
        RepositoryPaths.NoLinks(directory); Directory.CreateDirectory(directory); RepositoryPaths.NoLinks(directory);
        var path = Path.Combine(directory, "write.lock"); RepositoryPaths.NoLinks(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new UserFacingException("Repository memory is being updated; retry after the current writer finishes."); }
    }
    private void Save(State state) { Validate(state); AtomicJson(StatePath, state, MaxStateBytes); }
    private static T ReadJson<T>(string path, int maxBytes)
    {
        try
        {
            var bytes = RepositorySourceScanner.ReadBounded(path, maxBytes, default);
            return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new UserFacingException("Empty repository memory data.");
        }
        catch (JsonException) { throw new UserFacingException("Repository memory data is corrupt; it was not overwritten."); }
    }
    private static void AtomicJson<T>(string path, T value, int maxBytes)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (data.Length > maxBytes) throw new UserFacingException("Repository memory storage limit exceeded; no changes were committed.");
        RepositoryPaths.NoLinks(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(data); stream.Flush(true); }
            RepositoryPaths.NoLinks(path); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private bool MatchesHash(string path, string hash, int maxBytes, CancellationToken ct)
    {
        try { return RepositorySourceScanner.Hash(RepositorySourceScanner.ReadBounded(path, maxBytes, ct)) == hash; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UserFacingException) { return false; }
    }
    private RepositoryMemoryEntry Freshness(RepositoryMemoryEntry entry)
    {
        var stale = entry.Stale || entry.Sources.Any(source =>
        {
            try { return !MatchesHash(RepositoryPaths.Inside(workspace, source.Path), source.Hash, 1_000_000, default); }
            catch (UserFacingException) { return true; }
        });
        return entry with { Stale = stale };
    }
    private bool TryRegister(string id)
    {
        try { Register(id); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UserFacingException) { return false; }
    }
    private void Register(string id, bool relink = false)
    {
        using var lease = LockCatalog();
        RegisterLocked(id, relink);
    }
    private FileStream LockCatalog()
    {
        RepositoryPaths.NoLinks(catalogDirectory); Directory.CreateDirectory(catalogDirectory); RepositoryPaths.NoLinks(catalogDirectory);
        var path = Path.Combine(catalogDirectory, "write.lock"); RepositoryPaths.NoLinks(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new UserFacingException("Repository catalog is being updated; retry after the current writer finishes."); }
    }
    private void RegisterLocked(string id, bool relink = false)
    {
        var path = Path.Combine(catalogDirectory, id + ".repository.json");
        var prior = File.Exists(path) ? ReadJson<RepositoryRegistration>(path, 16_000) : null;
        if (prior is not null && (prior.Id != id || string.IsNullOrWhiteSpace(prior.Workspace) ||
            (!relink && !prior.Workspace.Equals(workspace, RepositoryPaths.Comparison))))
            throw new UserFacingException("Repository catalog identity mismatch; explicit relinking is required.");
        AtomicJson(path, new RepositoryRegistration(id, workspace, prior?.RegisteredAt ?? DateTimeOffset.UtcNow), 16_000);
    }
    private static RepositoryRegistration FindRegistration(string catalogDirectory, string id)
    {
        if (!ValidId(id)) throw new UserFacingException("Invalid registered repository ID.");
        var root = RepositoryPaths.Canonical(catalogDirectory); RepositoryPaths.NoLinks(root);
        var path = Path.Combine(root, id + ".repository.json");
        if (!File.Exists(path)) throw new UserFacingException("Registered repository not found.");
        var registration = ReadJson<RepositoryRegistration>(path, 16_000);
        if (registration.Id != id || string.IsNullOrWhiteSpace(registration.Workspace) || registration.Workspace != RepositoryPaths.Canonical(registration.Workspace))
            throw new UserFacingException("Invalid repository registration.");
        return registration;
    }
    private sealed class State
    {
        public int Version { get; set; } = 1;
        public string Id { get; set; } = "";
        public string Workspace { get; set; } = "";
        public List<RepositoryMemoryEntry> Entries { get; set; } = [];
        public List<RepositoryMemoryEntry> History { get; set; } = [];
        public List<RepositoryScan> Scans { get; set; } = [];
        public List<Receipt> Receipts { get; set; } = [];
        public RepositoryCommitReport? LastReport { get; set; }
    }
    private sealed record Receipt(string Fingerprint, RepositoryCommitReport Report);
}
