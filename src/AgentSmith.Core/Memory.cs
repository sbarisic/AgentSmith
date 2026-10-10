using System.Text.RegularExpressions;

namespace AgentSmith.Core;

public sealed record MemorySource(string ConversationId, string? RunId, string Reference, string Attribution);
public sealed record MemoryEntry(string Id, int Revision, string Title, string Content, string[] Tags,
    string OriginWorkspace, MemorySource[] Sources, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool UserAuthored);
public sealed record MemoryWrite(string? Id, int ExpectedRevision, string Title, string Content, string[] Tags,
    string OriginWorkspace, MemorySource[] Sources, bool UserAuthored, string OperationId);
public interface IMemoryStore
{
    bool MemoryEnabled { get; }
    void SetMemoryEnabled(bool enabled);
    IReadOnlyList<MemoryEntry> ListMemories(int page = 1);
    IReadOnlyList<MemoryEntry> SearchMemories(string query, string workspace = "");
    MemoryEntry? ReadMemory(string id);
    MemoryEntry WriteMemory(MemoryWrite write, CancellationToken ct = default);
    MemoryEntry? MemoryReceipt(string operationId);
    void ForgetMemory(string id);
}
public static class MemoryPolicy
{
    public static bool HasSecret(string text) => Regex.IsMatch(text,
        @"(?i)(\bsk-[a-z0-9_-]{12,}|\b(?:ghp_|github_pat_)[a-z0-9_]{15,}|\beyJ[a-z0-9_-]{8,}\.[a-z0-9_-]+\.[a-z0-9_-]+|-----BEGIN [^-]*PRIVATE KEY-----|\bBearer\s+[a-z0-9._~+/=-]{8,}|\b(?:password|passwd|api[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret)\s*[""']?\s*[:=]\s*[""']?[^\s""',;}]{4,})");
    public static void Validate(string title, string content, string[] tags)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 120 || string.IsNullOrWhiteSpace(content) || content.Length > 2000 || tags.Length > 8 || tags.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 64))
            throw new UserFacingException("Memory requires a title of 1..120 characters, content of 1..2000 characters, and at most eight tags of 1..64 characters.");
        var text = title + "\n" + content + "\n" + string.Join('\n', tags);
        if (HasSecret(text)) throw new UserFacingException("Memory rejected: recognized credential material. The rejected content was not saved to the memory library.");
        if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) || text.Any(c => c is >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069'))
            throw new UserFacingException("Memory contains unsupported control characters.");
    }
    public static string Key(string text) => Regex.Replace(text.Trim(), @"\s+", " ").ToUpperInvariant();
    public static string Origin(string path) => string.IsNullOrEmpty(path) ? "" : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();
}
