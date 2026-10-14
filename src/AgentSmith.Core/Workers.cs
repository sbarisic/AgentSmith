namespace AgentSmith.Core;

public enum ModelProvider { ChatGpt, LmStudio, Local }
public sealed class WorkerPolicy
{
    // Keep the legacy total limit; default local admission to one to avoid contention.
    public int MaxConcurrent { get; set; } = 2;
    public int MaxLocalConcurrent { get; set; } = 1;
    public int MaxRequests { get; set; } = 100;
    public int MaxMinutes { get; set; } = 30;
    public void Validate()
    {
        if (MaxConcurrent is < 1 or > 16 || MaxLocalConcurrent is < 1 or > 16)
            throw new UserFacingException("Worker limits must each be 1..16. The local limit is also bounded by the total limit.");
        if (MaxRequests is < 1 or > 10000 || MaxMinutes is < 1 or > 1440)
            throw new UserFacingException("Worker budget requires 1..10000 requests and 1..1440 minutes.");
    }
}
public sealed class ModelConnection
{
    public ModelProvider Provider { get; set; }
    public string Endpoint { get; set; } = "http://127.0.0.1:1234";
    public int MaxOutputTokens { get; set; } = 8192;
    public int? LoadedContextTokens { get; set; }
    public ModelCapabilities Capabilities { get; set; } = new();
}
public sealed class WorkerProfile
{
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public string AccountId { get; set; } = "";
    public ModelConnection Connection { get; set; } = new();
    public PermissionMode Mode { get; set; } = PermissionMode.Inspect;
}
public sealed class WorkerRecord
{
    public string[] NavigationSnapshotIds { get; set; } = [];
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Number { get; set; }
    public string ParentId { get; set; } = "";
    public string ParentRunId { get; set; } = "";
    public string CallId { get; set; } = "";
    public string ChildId { get; set; } = "";
    public WorkerProfile Profile { get; set; } = new();
    public string Task { get; set; } = "";
    public string Purpose { get; set; } = "general";
    public double WaitingMilliseconds { get; set; }
    public string[] WritePaths { get; set; } = [];
    public string State { get; set; } = "Prepared";
    public bool ReservationsHeld { get; set; }
    public bool Collected { get; set; }
    public string? Report { get; set; }
    public string? Reason { get; set; }
    public FailureDetail? Failure { get; set; }
    public int? AppliedCheckpointCount { get; set; }
    public int? RequestsUsed { get; set; }
    public int? RequestLimit { get; set; }
    public double DurationMilliseconds { get; set; }
    public string? Usage { get; set; }
    public List<string> RequestUsage { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Active => State is "Prepared" or "Running" or "Cancelling";
}
public interface IWorkerStore
{
    IReadOnlyList<WorkerProfile> Profiles();
    void PutProfile(WorkerProfile profile);
    void RemoveProfile(string name);
    IReadOnlyList<WorkerRecord> Workers(string parentId);
    WorkerRecord CreateWorker(WorkerRecord worker, Session child);
    void SaveWorker(WorkerRecord worker);
}
public sealed class NoopLease : IDisposable
{
    public static readonly NoopLease Instance = new();
    public void Dispose() { }
}
