using System.Collections.Concurrent;
using System.Text;

namespace AgentSmith.Core;

public sealed class SteeringMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RootId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string RunId { get; set; } = "";
    public long Sequence { get; set; }
    public string Text { get; set; } = "";
    public string Sender { get; set; } = "user";
    public string? SourceRunId { get; set; }
    public string? SourceCallId { get; set; }
    public string State { get; set; } = "Pending";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? IncorporatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public int? HistoryIndex { get; set; }
    public static void ValidateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > 32768)
            throw new UserFacingException("Steering requires nonempty text of at most 32 KiB UTF-8.");
    }
}

// Display metadata only; the durable inbox remains the source of the full message.
public sealed record SteeringNotice(string Id, string TargetId, string ConversationId, string RunId, string Target, string Sender,
    string State, string Preview, string Message)
{
    public static string PreviewText(string text, int limit = 1000) => Activity.Clean(
        System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim(), limit);

    public static SteeringNotice Create(SteeringMessage item, string stage, int? workerNumber = null) => new(
        item.Id, item.ConversationId, item.ConversationId, item.RunId,
        item.ConversationId == item.RootId ? "AgentSmith" : workerNumber is > 0 ? $"Agent #{workerNumber}" : "Worker " + Activity.Clean(item.ConversationId, 12),
        item.Sender, item.State, PreviewText(item.Text), stage);
}

public interface ISteeringStore
{
    IReadOnlyList<SteeringMessage> Steering(string rootId);
    bool HasPendingSteering(string conversationId, string runId);
    SteeringMessage SendSteering(string rootId, string targetId, string text, string sender = "user", string? sourceRunId = null, string? sourceCallId = null);
    SteeringMessage? SteeringReceipt(string rootId, string sourceRunId, string sourceCallId);
    void RemoveSteering(string rootId, string id);
    IReadOnlyList<SteeringMessage> IncorporateSteering(Session session, Run run);
    IReadOnlyList<SteeringMessage> MarkSteeringSent(Session session, Run run);
    bool TryCompleteSteeredRun(Session session, Run run);
    bool TryStartSteeredTool(Session session, Run run, ToolCall call);
}

// Signals accelerate same-process delivery. A bounded recheck also observes other store instances.
public static class SteeringWake
{
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> Signals = new(StringComparer.OrdinalIgnoreCase);
    private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Key(ISessionStore store, string runId) => Path.GetFullPath(store.DataDirectory) + "|" + runId;
    public static void Notify(ISessionStore store, string runId)
    { if (Signals.TryRemove(Key(store, runId), out var signal)) signal.TrySetResult(); }
    public static async Task WaitAsync(ToolExecutionContext ctx, CancellationToken ct)
    {
        if (ctx.Store is not ISteeringStore inbox) { await Task.Delay(Timeout.Infinite, ct); return; }
        var key = Key(ctx.Store, ctx.Run.Id);
        try
        {
            while (true)
            {
                var signal = Signals.GetOrAdd(key, _ => New());
                if (inbox.HasPendingSteering(ctx.Session.Id, ctx.Run.Id)) return;
                await Task.WhenAny(signal.Task, Task.Delay(250, ct));
                ct.ThrowIfCancellationRequested();
            }
        }
        finally { Signals.TryRemove(key, out _); }
    }
    public static async Task WaitOrSteeringAsync(ToolExecutionContext ctx, Task work)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.Cancellation);
        var steering = WaitAsync(ctx, stop.Token);
        try { await Task.WhenAny(work, steering).WaitAsync(ctx.Cancellation); if (work.IsCompleted) await work; }
        finally { stop.Cancel(); try { await steering; } catch (OperationCanceledException) { } }
    }
}
