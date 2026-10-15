namespace AgentSmith.Core;

/// <summary>Hard bounds for source inventory and model-visible evidence. No execution is involved.</summary>
public sealed record RepositoryScanLimits(int MaxFiles = 200, long MaxBytes = 2_000_000,
    int MaxFileBytes = 128_000, int MaxEvidenceCharacters = 64_000, int MaxLinesPerFile = 120,
    int MaxVisitedEntries = 10_000);
public sealed record RepositorySource(string Path, string Hash, int StartLine, int EndLine, string Text);
public sealed record RepositoryScanCoverage(int VisitedEntries, int InspectedFiles, long InspectedBytes,
    int EvidenceCharacters, int SkippedEntries, bool Complete, string[] Reasons);
public sealed record RepositoryScan(string Id, string RepositoryId, string Path, bool Full,
    DateTimeOffset StartedAt, RepositorySource[] Sources, RepositoryScanCoverage Coverage, RepositoryScanLimits Limits);
public sealed record RepositoryMemorySource(string Path, string Hash, int StartLine, int EndLine);
public sealed record RepositoryMemoryEntry(string Id, string TopicKey, int Revision, string Title, string Content,
    string[] Tags, RepositoryMemorySource[] Sources, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset VerifiedAt, bool UserAuthored, bool Stale);
public sealed record RepositoryMemoryProposal(string TopicKey, int ExpectedRevision, string Title, string Content,
    string[] Tags, RepositoryMemorySource[] Sources, bool UserAuthored = false);
public sealed record RepositoryCommitReport(string OperationId, string ScanId, DateTimeOffset At,
    int Created, int Updated, int Unchanged, int Protected, int Stale, int Conflicts,
    string[] Messages, bool Complete);
/// <summary>Minimal discovery metadata only; memory bodies remain in the registered workspace.</summary>
public sealed record RepositoryRegistration(string Id, string Workspace, DateTimeOffset RegisteredAt);
public sealed record RepositoryMemoryStatus(string RepositoryId, string Workspace, bool Enabled,
    int Memories, int Stale, RepositoryScan? LastScan, RepositoryCommitReport? LastReport);

public interface IRepositoryMemoryStore
{
    RepositoryScan ScanRepository(string workspace, string path = ".", bool full = false, CancellationToken ct = default);
    RepositoryCommitReport CommitRepository(string workspace, string scanId, IReadOnlyList<RepositoryMemoryProposal> proposals, string operationId, CancellationToken ct = default);
    IReadOnlyList<RepositoryMemoryEntry> SearchRepositoryMemories(string workspace, string query);
    RepositoryMemoryEntry? ReadRepositoryMemory(string workspace, string id);
    RepositoryMemoryStatus RepositoryStatus(string workspace);
    IReadOnlyList<RepositoryRegistration> DiscoverRepositories();
    string ResolveRepository(string id);
}
