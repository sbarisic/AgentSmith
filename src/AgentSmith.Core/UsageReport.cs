namespace AgentSmith.Core;

public sealed class UsageRow
{
    public string? Purpose { get; set; }
    public string? Outcome { get; set; }
    public double WaitingMilliseconds { get; set; }
    public long CachedInputTokens { get; set; }
    public long KnownUncachedInputTokens { get; set; }
    public int RequestsWithCacheDetails { get; set; }
    public string ConversationId { get; init; } = "";
    public string Role { get; init; } = "";
    public string? Profile { get; init; }
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens => InputTokens + OutputTokens;
    public long CompactionInputTokens { get; set; }
    public long CompactionOutputTokens { get; set; }
    public int ReportedRequests { get; set; }
    public int RequestsWithoutUsage { get; set; }
    public int UnfinishedRequests { get; set; }
    public int FailedCompactions { get; set; }
    public bool Local => Provider is "Local" or "LmStudio";
}

public sealed record UsageReport(string ConversationId, IReadOnlyList<UsageRow> Rows)
{
    public long CloudInputTokens => Rows.Where(r => r.Provider == "ChatGpt").Sum(r => r.InputTokens);
    public long CloudOutputTokens => Rows.Where(r => r.Provider == "ChatGpt").Sum(r => r.OutputTokens);
    public long LocalInputTokens => Rows.Where(r => r.Local).Sum(r => r.InputTokens);
    public long LocalOutputTokens => Rows.Where(r => r.Local).Sum(r => r.OutputTokens);
    public long LocalWorkerInputTokens => Rows.Where(r => r.Local && r.Role == "Worker").Sum(r => r.InputTokens);
    public long LocalWorkerOutputTokens => Rows.Where(r => r.Local && r.Role == "Worker").Sum(r => r.OutputTokens);
    public long LocalWorkerTokens => LocalWorkerInputTokens + LocalWorkerOutputTokens;
    public int RequestsWithoutUsage => Rows.Sum(r => r.RequestsWithoutUsage);
    public int UnfinishedRequests => Rows.Sum(r => r.UnfinishedRequests);
    public int FailedCompactions => Rows.Sum(r => r.FailedCompactions);
    public string Interpretation => "Local worker tokens are measured offloaded work, not exact ChatGPT tokens or money saved. Tokenizers, prompts, and parent coordination differ. Input includes cached tokens where reported. Totals include reported completed requests (even from failed/cancelled runs) and committed compaction summaries; incomplete requests and failed compaction attempts may have unreported usage.";
}
