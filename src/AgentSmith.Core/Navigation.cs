namespace AgentSmith.Core;

public sealed class NavigationSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OwnerLifetime { get; set; } = "";
    public string Project { get; set; } = "";
    public string Configuration { get; set; } = "Debug";
    public string Framework { get; set; } = "";
    public string State { get; set; } = "Loading";
    public string ProcessId { get; set; } = "";
    public string BundlePath { get; set; } = "";
    public string? ArtifactId { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
