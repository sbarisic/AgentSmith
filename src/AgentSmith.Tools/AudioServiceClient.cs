using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AgentSmith.Core;

namespace AgentSmith.Tools;

/// <summary>Read-only service management and explicit process lifecycle; no model or account initialization.</summary>
public static class AudioServiceControl
{
    public static async Task<AudioServiceStatus> Status(string dataDirectory, CancellationToken ct = default)
    {
        var endpoint = AudioEndpoint.Resolve(dataDirectory);
        try { return await Exchange(endpoint, false, false, ct); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or UserFacingException)
        {
            ct.ThrowIfCancellationRequested();
            return new(false, endpoint.Identity, endpoint.Directory, null, AudioWire.Build, AudioWire.Version, false,
                "unavailable", null, 0, 0, 0, 0, null, LocalAudioInstallation.Status(dataDirectory),
                "Audio service unavailable. Run smith service start with the same --data-dir. " + (ex is UserFacingException ? ex.Message : ""));
        }
    }
    public static async Task<AudioServiceStatus> Start(string dataDirectory, string executable, IReadOnlyList<string> prefix, CancellationToken ct = default)
    {
        var endpoint = AudioEndpoint.Resolve(dataDirectory, true);
        var status = await Status(dataDirectory, ct);
        if (status.Available) return status;
        AudioServiceWindows.Launch(executable, prefix.Concat(new[] { "service", "run", "--data-dir", endpoint.Directory, "--json" }).ToArray());
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            status = await Status(dataDirectory, ct);
            if (status.Available) return status;
            await Task.Delay(100, ct);
        }
        throw new UserFacingException("Audio service did not become ready. Check for an incompatible running build; run smith service run in a separate terminal for diagnostics.");
    }
    public static async Task<AudioServiceStatus> Stop(string dataDirectory, bool force, CancellationToken ct = default)
    {
        var endpoint = AudioEndpoint.Resolve(dataDirectory);
        var status = await Exchange(endpoint, true, force, ct);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            var current = await Status(dataDirectory, ct);
            if (!current.Available) return current with { Message = "Audio service stopped." };
            status = current; await Task.Delay(100, ct);
        }
        return status with { Message = "Audio service is draining accepted work; inspect smith service status." };
    }
    private static async Task<AudioServiceStatus> Exchange(AudioEndpoint endpoint, bool stop, bool force, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var pipe = await AudioConnection.Connect(endpoint, "control", timeout.Token);
        using var gate = new SemaphoreSlim(1);
        await AudioWire.Write(pipe, new(stop ? "stop" : "status", Data: new() { ["force"] = force }), gate, timeout.Token);
        var result = await AudioWire.Read(pipe, timeout.Token);
        if (result.Type != "status") throw AudioConnection.Failure(result);
        return AudioWire.Value<AudioServiceStatus>(result.Data);
    }
}

internal sealed class AudioConnection : IDisposable
{
    private readonly NamedPipeClientStream pipe;
    private readonly SemaphoreSlim write = new(1, 1);
    private readonly CancellationTokenSource closed = new();
    private readonly ConcurrentDictionary<string, Channel<AudioMessage>> pending = new();
    private readonly Task reader;
    internal bool IsClosed => closed.IsCancellationRequested;
    private AudioConnection(NamedPipeClientStream pipe) { this.pipe = pipe; reader = Read(); }
    internal static async Task<AudioConnection> Open(AudioEndpoint endpoint, CancellationToken ct) => new(await Connect(endpoint, "client", ct));
    internal static async Task<NamedPipeClientStream> Connect(AudioEndpoint endpoint, string role, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", endpoint.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(ct);
            var pid = AudioServiceWindows.VerifyPeer(pipe, false);
            using var gate = new SemaphoreSlim(1);
            await AudioWire.Write(pipe, new("hello", Data: AudioWire.Data(new AudioHello(AudioWire.Version, AudioWire.Build, endpoint.Identity, role, Environment.ProcessId))), gate, ct);
            var reply = await AudioWire.Read(pipe, ct);
            if (reply.Type != "hello") throw Failure(reply);
            var status = AudioWire.Value<AudioServiceStatus>(reply.Data);
            if (status.Identity != endpoint.Identity || status.Build != AudioWire.Build || status.Protocol != AudioWire.Version || status.ProcessId != pid)
                throw new IOException("Audio service identity mismatch.");
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }
    internal static UserFacingException Failure(AudioMessage message) =>
        new(message.Data?["message"]?.ToString() ?? "Audio service connection failed. No automatic retry was made.");
    private async Task Read()
    {
        Exception? failure = null;
        try
        {
            while (!closed.IsCancellationRequested)
            {
                var message = await AudioWire.Read(pipe, closed.Token);
                if (message.Id == "" && message.Type == "error") throw Failure(message);
                if (pending.TryGetValue(message.Id, out var channel) && !channel.Writer.TryWrite(message))
                    throw new IOException("Audio service sent excessive pending output.");
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or UserFacingException or ObjectDisposedException)
        { failure = ex; }
        finally
        {
            closed.Cancel(); pipe.Dispose();
            foreach (var request in pending.Values) request.Writer.TryComplete(failure ?? new IOException("Audio service disconnected."));
        }
    }
    internal async Task<JsonObject> Run(AudioSubmission submission, string path, Action<string> progress, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var messages = Channel.CreateBounded<AudioMessage>(128);
        if (!pending.TryAdd(id, messages)) throw new IOException("Duplicate local request.");
        if (IsClosed) { pending.TryRemove(id, out _); throw new IOException("Audio service disconnected."); }
        try
        {
            await AudioWire.Write(pipe, new("submit", id, AudioWire.Data(submission)), write, ct);
            var reply = await messages.Reader.ReadAsync(ct);
            if (reply.Type != "accepted") throw Failure(reply);
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, AudioWire.ChunkSize, FileOptions.Asynchronous))
            {
                var buffer = new byte[AudioWire.ChunkSize];
                int count;
                while ((count = await source.ReadAsync(buffer, ct)) != 0)
                    await AudioWire.Write(pipe, new("upload", id, Bytes: buffer[..count]), write, ct);
            }
            await AudioWire.Write(pipe, new("uploaded", id), write, ct);
            using var output = new MemoryStream();
            while (true)
            {
                reply = await messages.Reader.ReadAsync(ct);
                switch (reply.Type)
                {
                    case "progress": progress(reply.Data?["message"]?.ToString() ?? "processing"); break;
                    case "result":
                        var bytes = reply.Bytes ?? throw new IOException("Missing result bytes.");
                        if (output.Length + bytes.Length > LocalAudioProcess.OutputLimit) throw new IOException("Audio result exceeds 2 MiB.");
                        output.Write(bytes); break;
                    case "complete":
                        var result = JsonNode.Parse(output.ToArray())?.AsObject() ?? throw new IOException("Missing audio result.");
                        LocalAudioProcess.Validate(result, submission.Operation, submission.Components);
                        return result;
                    case "error": throw Failure(reply);
                    default: throw new IOException("Unexpected audio service response.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A cancelled channel read can unwind and unregister an earlier token callback before it runs.
            // Send cancellation explicitly before completing the caller's cancelled task.
            await Cancel(id);
            throw;
        }
        catch (Exception ex) when (ex is IOException or ChannelClosedException or JsonException)
        { throw new UserFacingException("Audio service request was interrupted. No automatic retry was made. " + ex.Message); }
        finally { pending.TryRemove(id, out _); }
    }
    private async Task Cancel(string id)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await AudioWire.Write(pipe, new("cancel", id), write, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { Dispose(); }
    }
    public void Dispose() { closed.Cancel(); pipe.Dispose(); }
}

internal sealed class SharedAudioClient(AudioEndpoint endpoint) : IDisposable
{
    private static readonly object PoolLock = new();
    private static readonly Dictionary<string, SharedAudioClient> Pool = [];
    private int owners;
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private AudioConnection? connection;
    internal static SharedAudioClient Acquire(string directory)
    {
        var endpoint = AudioEndpoint.Resolve(directory);
        lock (PoolLock)
        {
            if (!Pool.TryGetValue(endpoint.Identity, out var client)) Pool[endpoint.Identity] = client = new(endpoint);
            client.owners++; return client;
        }
    }
    public void Dispose()
    {
        lock (PoolLock)
            if (--owners == 0) { Pool.Remove(endpoint.Identity); connection?.Dispose(); }
    }
    internal async Task<JsonObject> Run(string installation, JsonObject request, Action<string> progress, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        AudioConnection current;
        await connectionGate.WaitAsync(ct);
        try
        {
            if (connection is null || connection.IsClosed)
            {
                connection?.Dispose();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(2));
                try { connection = await AudioConnection.Open(endpoint, timeout.Token); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new UserFacingException("Audio service unavailable. Run smith service start --data-dir " +
                        WindowsChild.Quote(endpoint.Directory) + ". No private helper was started.");
                }
            }
            current = connection;
        }
        finally { connectionGate.Release(); }
        var connectionSeconds = started.Elapsed.TotalSeconds;
        var path = request["path"]!.ToString();
        var hash = request["sourceHash"]?.ToString();
        if (hash is null) { using var input = File.OpenRead(path); hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input, ct)); }
        var components = request["components"]?.AsArray().Select(c => c!.ToString()).ToArray() ?? AudioReuse.Required(request["operation"]!.ToString(), true);
        var submission = new AudioSubmission(Path.GetFileName(installation), LocalAudioInstallation.Fingerprint(Path.Combine(installation, "helper")),
            Path.GetExtension(path).ToLowerInvariant(), new FileInfo(path).Length, hash, request["operation"]!.ToString(),
            request["startSeconds"]!.GetValue<int>(), request["durationSeconds"]!.GetValue<int>(), request["language"]!.ToString(),
            components, request["deadlineTicks"]?.GetValue<long>() ?? Environment.TickCount64 + 300_000);
        var result = await current.Run(submission, path, progress, ct);
        if (result["service"] is JsonObject service) service["connectionSeconds"] = connectionSeconds;
        return result;
    }
}
