using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentSmith.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

public enum RunState { Running, AwaitingUser, Paused, Completed, Failed, Cancelled, RecoveryRequired, AwaitingPlan }
public enum CallState { Pending, Started, Completed }
public enum PermissionMode { Inspect, Edit, Execute }
public enum WorkflowMode { Work, Plan }
public enum ToolEffect { ReadOnly, SessionState, Workspace, Process }
public sealed class Session
{
    public ModelConnection Connection { get; set; } = new();
    public List<WorkerProfile> EnabledWorkers { get; set; } = [];
    public string? ParentConversationId { get; set; }
    public string? WorkerId { get; set; }
    public string WorkerInstructions { get; set; } = "";
    public string? WorkerReport { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Workspace { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string Model { get; set; } = "";
    public PermissionMode Mode { get; set; } = PermissionMode.Execute;
    public WorkflowMode Workflow { get; set; }
    public List<ContextSnapshot> ContextSnapshots { get; set; } = [];
    public string? ActiveContextId { get; set; }
    public List<CompactionAttempt> CompactionAttempts { get; set; } = [];
    public string? ActivePlanId { get; set; }
    public string? PendingPlanPrompt { get; set; }
    public List<WorkPlan> Plans { get; set; } = [];
    public Dictionary<string, ToolReceipt> Receipts { get; set; } = [];
    public List<ArtifactRecord> Artifacts { get; set; } = [];
    public List<VerificationRecord> Verifications { get; set; } = [];
    public List<MutationRecord> Mutations { get; set; } = [];
    public List<ProcessRecord> Processes { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<JsonObject> History { get; set; } = [];
    public List<Run> Runs { get; set; } = [];
}
public sealed class Run
{
    public string? PlanId { get; set; }
    public int? PlanRevision { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public RunState State { get; set; }
    public string? Reason { get; set; }
    public int Steps { get; set; }
    public List<ToolCall> Calls { get; set; } = [];
    public Question? Question { get; set; }
    public string? ContextManifest { get; set; }
    public string? Usage { get; set; }
    public double ExecutionMilliseconds { get; set; }
}
public sealed class ToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
    public CallState State { get; set; }
    public string? Result { get; set; }
    public ActivitySummary? Activity { get; set; }
    public double DurationMilliseconds { get; set; }
    public PreparedOperation? Prepared { get; set; }
}
public sealed class Question
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CallId { get; set; } = "";
    public string Text { get; set; } = "";
    public string[] Choices { get; set; } = [];
    public string? Answer { get; set; }
}
public sealed record AgentEvent(string Type, string ConversationId, string? RunId, object Payload);
public sealed record ToolDefinition(string Name, string Description, JsonObject Parameters, PermissionMode Capability = PermissionMode.Inspect, bool HasEffects = false, ToolEffect Effect = ToolEffect.ReadOnly)
{
    public bool Enabled(Session session) => Capability <= session.Mode && (session.Workflow != WorkflowMode.Plan || (Effect is ToolEffect.ReadOnly or ToolEffect.SessionState) && Capability == PermissionMode.Inspect);
}
public sealed record PreparedOperation(string Arguments, string? OperationId = null);
public sealed record ToolExecutionContext(Session Session, Run Run, ISessionStore Store, CancellationToken Cancellation, Action<AgentEvent>? Emit = null)
{
    public string Workspace => Session.Workspace;
    public string ArtifactDirectory => Path.Combine(Store.DataDirectory, "artifacts", Session.Id);
    public void Save(string type, object payload)
    {
        var ev = new AgentEvent(type, Session.Id, Run.Id, payload); Store.Save(Session, ev); Emit?.Invoke(ev);
    }
}
public sealed class FileTransition
{
    public string Path { get; set; } = "";
    public byte[]? Before { get; set; }
    public byte[]? After { get; set; }
    public string? BeforeHash => Before is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Before)).ToLowerInvariant();
    public string? AfterHash => After is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(After)).ToLowerInvariant();
    public FileAttributes Attributes { get; set; } = FileAttributes.Normal;
    public DateTime LastWriteUtc { get; set; }
}
public sealed class MutationRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RunId { get; set; } = "";
    public string CallId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string State { get; set; } = "Prepared";
    public string? UndoOf { get; set; }
    public List<FileTransition> Files { get; set; } = [];
    public List<string> CreatedDirectories { get; set; } = [];
    public double DurationMilliseconds { get; set; }
}
public sealed class ProcessRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OwnerId { get; set; } = "";
    public string RunId { get; set; } = "";
    public int DisplayNumber { get; set; }
    public string Executable { get; set; } = "";
    public string State { get; set; } = "Prepared";
    public string? Reason { get; set; }
    public int? ExitCode { get; set; }
    public bool TerminationConfirmed { get; set; }
    public double DurationMilliseconds { get; set; }
    public string LogPath { get; set; } = "";
    public bool Truncated { get; set; }
}
public sealed record ToolOutput(string Content, Question? Question = null, bool PlanReview = false, bool FinishRun = false);
public sealed record ModelRequest(string Model, string Instructions, IReadOnlyList<JsonObject> Input, IReadOnlyList<ToolDefinition> Tools);
public sealed record ModelEvent(string Type, string? Text = null, IReadOnlyList<JsonObject>? Output = null, string? Usage = null);
public sealed record ContextResult(ModelRequest Request, string Manifest);

public interface IModelClient
{
    IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken);
}
public interface IToolDispatcher
{
    Task<IDisposable> AcquireToolLeaseAsync(ToolExecutionContext context, ToolCall call) => Task.FromResult<IDisposable>(NoopLease.Instance);
    string SessionInstructions(Session session) => session.WorkerInstructions;
    Task<bool> CanCompleteAsync(ToolExecutionContext context) => Task.FromResult(true);
    Task RunEndedAsync(ToolExecutionContext context) => Task.CompletedTask;
    IReadOnlyList<ToolDefinition> Definitions { get; }
    string Instructions(string workspace);
    Task<ToolOutput> ExecuteAsync(string workspace, ToolCall call, CancellationToken cancellationToken);
    IReadOnlyList<ToolDefinition> Available(PermissionMode mode) => Definitions.Where(t => t.Capability <= mode).ToArray();
    IReadOnlyList<ToolDefinition> Available(Session session) => Definitions.Where(t => t.Enabled(session)).ToArray();
    PreparedOperation Prepare(ToolExecutionContext context, ToolCall call) => new(call.Arguments);
    Task<ToolOutput> ExecuteAsync(ToolExecutionContext context, ToolCall call) => ExecuteAsync(context.Workspace, call, context.Cancellation);
    ToolOutput? Recover(ToolExecutionContext context, ToolCall call) => null;
    Task CancelProcessesAsync(ToolExecutionContext context) => Task.CompletedTask;
}
public interface IWorkspaceTools : IToolDispatcher { }
public interface ISessionStore
{
    string DataDirectory { get; }
    IDisposable Acquire(string conversationId);
    Session? Get(string conversationId);
    IReadOnlyList<Session> List();
    void Save(Session session, AgentEvent agentEvent);
    void SaveProcess(string conversationId, ProcessRecord process);
}
public sealed class UserFacingException(string message) : Exception(message);
