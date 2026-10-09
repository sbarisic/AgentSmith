namespace AgentSmith.Core;

public sealed class ContextSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? ParentId { get; set; }
    public int Through { get; set; }
    public string SourceHash { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Model { get; set; } = "";
    public int FormatVersion { get; set; } = 1;
    public int Before { get; set; }
    public int After { get; set; }
    public double DurationMilliseconds { get; set; }
    public List<string> Usage { get; set; } = [];
}
public sealed class CompactionAttempt
{
    public string BoundaryHash { get; set; } = "";
    public string State { get; set; } = "Started";
    public string? Reason { get; set; }
}
public sealed class QueuedPrompt
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public long Sequence { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset SubmittedAt { get; set; } = DateTimeOffset.UtcNow;
    public string State { get; set; } = "Pending";
    public string? RunId { get; set; }
}
public sealed record PromptQueue(IReadOnlyList<QueuedPrompt> Items, string? HoldReason);
