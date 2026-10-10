using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using AgentSmith.Core;

namespace AgentSmith.Runtime;

public sealed class SessionModelClient(ISessionStore store, Session session, IModelClient inner, Action<string>? notice = null) : IModelClient
{
    private static string EndpointKey(string endpoint)
    { var uri = new Uri(endpoint); return uri.Scheme + "://" + (uri.IsLoopback ? "loopback" : uri.Host) + ":" + uri.Port; }
    public IModelClient Inner => inner;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> slots = new(StringComparer.OrdinalIgnoreCase);
    public Task<TokenMeasurement?> CountTokensAsync(ModelRequest request, CancellationToken ct) => inner.CountTokensAsync(AttachmentFiles.Resolve(store, session, request), ct);
    public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var gate = session.Connection.Provider == ModelProvider.ChatGpt ? null : slots.GetOrAdd(EndpointKey(session.Connection.Endpoint), _ => new(Math.Clamp(session.Connection.Capabilities.ConcurrentRequests, 1, 16)));
        var timer = Stopwatch.StartNew();
        if (gate is not null)
        {
            if (gate.CurrentCount == 0) notice?.Invoke("Waiting for local inference capacity · " + (session.WorkerId ?? "parent"));
            await gate.WaitAsync(ct);
        }
        session.ModelWaitMilliseconds += timer.Elapsed.TotalMilliseconds;
        try { await foreach (var e in inner.StreamAsync(AttachmentFiles.Resolve(store, session, request), ct)) yield return e; }
        finally { gate?.Release(); }
    }
}
