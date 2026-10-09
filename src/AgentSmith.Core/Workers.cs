namespace AgentSmith.Core;

public enum ModelProvider { ChatGpt, LmStudio }
public sealed class ModelConnection
{
    public ModelProvider Provider { get; set; }
    public string Endpoint { get; set; } = "http://127.0.0.1:1234";
    public int MaxOutputTokens { get; set; } = 8192;
    public int? LoadedContextTokens { get; set; }
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
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Number { get; set; }
    public string ParentId { get; set; } = "";
    public string ParentRunId { get; set; } = "";
    public string CallId { get; set; } = "";
    public string ChildId { get; set; } = "";
    public WorkerProfile Profile { get; set; } = new();
    public string Task { get; set; } = "";
    public string[] WritePaths { get; set; } = [];
    public string State { get; set; } = "Prepared";
    public bool ReservationsHeld { get; set; }
    public bool Collected { get; set; }
    public string? Report { get; set; }
    public string? Reason { get; set; }
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
