using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AgentSmith.Core;

namespace AgentSmith.Tools;

/// <summary>One user/data-directory host. It owns computation and disposable uploads, never conversation state.</summary>
public sealed partial class AudioServiceHost
{
    private readonly AudioEndpoint endpoint;
    private readonly LocalAudioHost helper = new();
    private readonly AudioCaptureManager captures;
    internal AudioCaptureManager CaptureManager => captures;
    private Task? captureShutdown;
    private readonly object sync = new();
    private readonly HashSet<Job> jobs = [];
    private readonly HashSet<int> clientProcesses = [];
    private readonly ConcurrentDictionary<int, Peer> peers = new();
    private readonly Channel<bool> ready = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly LinkedList<Job> queue = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool draining;
    private bool stopRequested;
    private readonly ConcurrentDictionary<string, Task> watchers = new();
    private Job? active;
    private int nextPeer;
    private string? generation;
    internal Func<string, JsonObject, Action<string>, CancellationToken, Task<JsonObject>> Infer;
    internal Func<AudioSubmission, string>? ValidateInstallation;
    internal TimeSpan UploadTimeout = TimeSpan.FromSeconds(30);

    public AudioServiceHost(string dataDirectory)
    {
        endpoint = AudioEndpoint.Resolve(dataDirectory, true);
        Infer = helper.Infer;
        captures = new(endpoint.Directory);
    }
    public AudioServiceStatus Status()
    {
        lock (sync) return new(true, endpoint.Identity, endpoint.Directory, Environment.ProcessId, AudioWire.Build, AudioWire.Version,
            draining, helper.State, helper.ProcessId, helper.PeakCommittedBytes, active is null ? 0 : 1,
            jobs.Count - (active is null ? 0 : 1), clientProcesses.Count, generation,
            LocalAudioInstallation.Status(endpoint.Directory), draining ? "Audio service is draining." : "Audio service is available.", captures.Active);
    }
    public async Task Run(CancellationToken ct = default)
    {
        var root = SafeDirectory("service");
        var lockPath = Path.Combine(root, "host.lock");
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked service lock is unsupported.");
        using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // First-instance protection prevents replacing an endpoint while an old service still owns it.
        using var initial = AudioServiceWindows.CreatePipe(endpoint.Pipe, true);
        captures.Recover();
        using var cancellation = ct.Register(() => Stop(true));
        using (var cleanup = new AudioTemporary(endpoint.Directory, "service-jobs")) { }
        var handlers = new ConcurrentDictionary<int, Task>();
        async Task Accept()
        {
            NamedPipeServerStream pending = initial;
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    await pending.WaitForConnectionAsync(lifetime.Token);
                    var pipe = pending;
                    // Keep a listener alive before handing off the previous instance.
                    pending = AudioServiceWindows.CreatePipe(endpoint.Pipe, false);
                    if (peers.Count >= 48) { pipe.Dispose(); continue; }
                    var id = Interlocked.Increment(ref nextPeer);
                    var peer = new Peer(this, pipe, id);
                    peers[id] = peer;
                    var handler = Task.Run(peer.Run); handlers[id] = handler;
                    _ = handler.ContinueWith(t => { handlers.TryRemove(id, out _); }, TaskScheduler.Default);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            finally { pending.Dispose(); }
        }
        var accept = Accept(); var worker = Dispatch();
        try
        {
            var completed = await Task.WhenAny(stopped.Task, accept, worker);
            if (completed != stopped.Task) await completed;
        }
        finally
        {
            Stop(true); lifetime.Cancel(); ready.Writer.TryComplete();
            foreach (var peer in peers.Values) peer.Close();
            try { await accept; } catch (OperationCanceledException) { }
            await Task.WhenAll(handlers.Values);
            await worker;
            Job[] remaining; lock (sync) remaining = jobs.ToArray();
            foreach (var job in remaining) Finish(job);
            await Task.WhenAll(watchers.Values);
            await captures.Shutdown(true); captures.Dispose();
            helper.Dispose();
        }
    }
    private string SafeDirectory(string name)
    {
        var audio = Path.Combine(endpoint.Directory, "audio");
        Directory.CreateDirectory(audio);
        if ((File.GetAttributes(audio) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked audio directories are unsupported.");
        var directory = Path.Combine(audio, name); Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked service directories are unsupported.");
        return directory;
    }
    private void Stop(bool force)
    {
        lock (sync)
        {
            draining = true; stopRequested = true;
            if (force) foreach (var job in jobs) job.Cancel.Cancel();
            captureShutdown ??= Task.Run(async () =>
            {
                await captures.Shutdown(force);
                lock (sync) { if (jobs.Count == 0) stopped.TrySetResult(); }
            });
            if (force) _ = captures.Shutdown(true);
            if (jobs.Count == 0 && captureShutdown.IsCompleted) stopped.TrySetResult();
        }
    }
    private string Installation(AudioSubmission submission)
    {
        if (ValidateInstallation is not null) return ValidateInstallation(submission);
        if (!Guid.TryParseExact(submission.Generation, "N", out _)) throw new IOException("Invalid audio generation.");
        var root = SafeDirectory("installations");
        var directory = Path.Combine(root, submission.Generation);
        using (var handle = AudioServiceWindows.OpenDirectory(directory))
        {
            var resolved = new StringBuilder(32768);
            var size = AudioServiceWindows.GetFinalPathNameByHandle(handle, resolved, resolved.Capacity, 0);
            if (size == 0 || size >= resolved.Capacity || !resolved.ToString().Replace(@"\\?\", "").Equals(directory, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Linked installation generation is unsupported: " + directory + " resolves to " + resolved + " (result " + size + ").");
        }
        var helperDirectory = Path.Combine(directory, "helper");
        var fingerprint = LocalAudioInstallation.Fingerprint(helperDirectory);
        var readyRecord = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "ready.json")));
        if (fingerprint != submission.Fingerprint || readyRecord?["fingerprint"]?.ToString() != fingerprint)
            throw new UserFacingException("Audio installation generation changed or is incompatible. Run smith audio setup and request analysis again.");
        var protocol = JsonNode.Parse(File.ReadAllText(Path.Combine(helperDirectory, "downloads.json")))?["helperProtocol"]?.GetValue<int>() ?? 1;
        if (protocol is not (1 or 2)) throw new UserFacingException("Unsupported helper generation. Run smith audio setup.");
        return directory;
    }
    private static void Validate(AudioSubmission s)
    {
        if (s.Format is not (".wav" or ".mp3") || s.Length is < 12 or > AudioTool.MaxFileBytes ||
            (s.Hash is null || s.Hash.Length != 64) || !s.Hash!.All(Uri.IsHexDigit) || s.StartSeconds < 0 || s.DurationSeconds is < 0 or > 120 ||
            !AudioTool.IsAudio(s.Operation) || (s.Language is null || !AudioTool.ValidLanguage(s.Language)) ||
            s.Components is null || s.Components.Length is < 1 or > 3 || s.Components.Distinct().Count() != s.Components.Length ||
            s.Components.Any(c => !AudioReuse.Required(s.Operation, true).Contains(c)) ||
            s.DeadlineTicks <= Environment.TickCount64 || s.DeadlineTicks - Environment.TickCount64 > 300_000)
            throw new IOException("Invalid audio submission, components, or deadline.");
    }
    private async Task Dispatch()
    {
        try
        {
            await foreach (var signal in ready.Reader.ReadAllAsync(lifetime.Token))
            while (true)
            {
                Job job;
                lock (sync)
                {
                    if (queue.First is null) break;
                    job = queue.First.Value; queue.RemoveFirst(); job.Node = null; active = job; generation = job.Submission.Generation;
                }
                try
                {
                    job.Cancel.Token.ThrowIfCancellationRequested();
                    var queueSeconds = job.ReadyAt.Elapsed.TotalSeconds;
                    var request = new JsonObject
                    {
                        ["protocol"] = 1, ["path"] = job.Path, ["models"] = Path.Combine(job.Installation, "models"),
                        ["operation"] = job.Submission.Operation, ["startSeconds"] = job.Submission.StartSeconds,
                        ["durationSeconds"] = job.Submission.DurationSeconds, ["language"] = job.Submission.Language,
                        ["components"] = new JsonArray(job.Submission.Components.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray())
                    };
                    await job.Peer.Send(new("progress", job.Id, new() { ["message"] = "processing shared audio job" }), job.Cancel.Token);
                    var timer = Stopwatch.StartNew();
                    var result = await Infer(job.Installation, request, message =>
                        job.Peer.Send(new("progress", job.Id, new() { ["message"] = message }), job.Cancel.Token).GetAwaiter().GetResult(), job.Cancel.Token);
                    job.Cancel.Token.ThrowIfCancellationRequested();
                    LocalAudioProcess.Validate(result, job.Submission.Operation, job.Submission.Components);
                    result["service"] = new JsonObject
                    {
                        ["protocol"] = AudioWire.Version, ["build"] = AudioWire.Build, ["processId"] = Environment.ProcessId,
                        ["helperProcessId"] = helper.ProcessId, ["generation"] = job.Submission.Generation,
                        ["fingerprint"] = job.Submission.Fingerprint, ["queueSeconds"] = queueSeconds,
                        ["uploadSeconds"] = job.UploadSeconds, ["inferenceSeconds"] = timer.Elapsed.TotalSeconds
                    };
                    var bytes = Encoding.UTF8.GetBytes(result.ToJsonString());
                    if (bytes.Length > LocalAudioProcess.OutputLimit) throw new IOException("Audio result exceeded 2 MiB.");
                    for (var offset = 0; offset < bytes.Length; offset += AudioWire.ChunkSize)
                        await job.Peer.Send(new("result", job.Id, Bytes: bytes[offset..Math.Min(bytes.Length, offset + AudioWire.ChunkSize)]), job.Cancel.Token);
                    await job.Peer.Send(new("complete", job.Id), job.Cancel.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or UserFacingException or JsonException or InvalidOperationException)
                { await job.Peer.Error(job.Id, ex is OperationCanceledException ? "Audio request cancelled, disconnected, or expired. No automatic retry was made." : ex.Message); }
                finally { Finish(job); }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    private void Finish(Job job, bool queuedOnly = false)
    {
        lock (sync)
        {
            if (queuedOnly && active == job || !jobs.Remove(job)) return;
            if (job.Node is not null) { queue.Remove(job.Node); job.Node = null; }
            job.Peer.Jobs.TryRemove(job.Id, out _);
            if (active == job) active = null;
            if (stopRequested && jobs.Count == 0 && captureShutdown?.IsCompleted == true) stopped.TrySetResult();
        }
        job.Dispose();
        // Queue positions are computed from actual ready jobs rather than uploads.
        foreach (var waiting in peers.Values.SelectMany(p => p.Jobs.Values).Where(j => j.Ready && j != active).OrderBy(j => j.Order).Select((j, i) => (j, i)))
            _ = waiting.j.Peer.Progress(waiting.j.Id, "waiting for shared audio slot; queue position " + (waiting.i + 1));
    }
    private long nextOrder;
    private sealed class Job : IDisposable
    {
        internal readonly string Id;
        internal readonly Peer Peer;
        internal readonly AudioSubmission Submission;
        internal readonly string Installation;
        internal readonly FileStream? InstallationLease;
        internal readonly AudioTemporary Temporary;
        internal readonly string Path;
        internal readonly CancellationTokenSource Cancel;
        internal readonly SemaphoreSlim UploadGate = new(1, 1);
        internal FileStream? Upload;
        internal long Length;
        internal readonly Stopwatch UploadClock = Stopwatch.StartNew();
        internal Stopwatch ReadyAt = new();
        internal double UploadSeconds;
        internal bool Ready;
        internal long Order;
        internal LinkedListNode<Job>? Node;
        internal Job(Peer peer, string id, AudioSubmission submission, string installation)
        {
            Peer = peer; Id = id; Submission = submission; Installation = installation;
            Temporary = new(peer.Host.endpoint.Directory, "service-jobs");
            Path = System.IO.Path.Combine(Temporary.Directory, "input" + submission.Format);
            Upload = new(Path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
            Cancel = CancellationTokenSource.CreateLinkedTokenSource(peer.Closed.Token);
            Cancel.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, submission.DeadlineTicks - Environment.TickCount64)));
            try { InstallationLease = LocalAudioInstallation.Use(installation); }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            try
            {
                Upload?.Dispose(); Upload = null;
                try { Temporary.Dispose(); } catch (IOException) { /* A later leased cleanup retries. */ }
                Cancel.Cancel();
            }
            finally { InstallationLease?.Dispose(); }
        }
    }
    private sealed class Peer(AudioServiceHost host, NamedPipeServerStream pipe, int number)
    {
        internal AudioServiceHost Host => host;
        internal readonly ConcurrentDictionary<string, Job> Jobs = new();
        internal readonly CancellationTokenSource Closed = new();
        private readonly SemaphoreSlim write = new(1, 1);
        private readonly HashSet<string> ids = [];
        private int? process;
        private bool inference;
        internal void Close() { Closed.Cancel(); pipe.Dispose(); }
        internal async Task Send(AudioMessage message, CancellationToken ct)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, Closed.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            try { await AudioWire.Write(pipe, message, write, limit.Token); }
            catch { Close(); throw; }
        }
        internal async Task Error(string id, string message)
        {
            try { await Send(new("error", id, new() { ["message"] = message[..Math.Min(message.Length, 2000)] }), Closed.Token); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        internal async Task Progress(string id, string message)
        {
            try { await Send(new("progress", id, new() { ["message"] = message }), Closed.Token); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        internal async Task Run()
        {
            try
            {
                using var handshake = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var helloMessage = await AudioWire.Read(pipe, handshake.Token);
                var hello = AudioWire.Value<AudioHello>(helloMessage.Data);
                var pid = AudioServiceWindows.VerifyPeer(pipe, true);
                if (helloMessage.Type != "hello" || hello.Protocol != AudioWire.Version || hello.Build != AudioWire.Build ||
                    hello.Identity != host.endpoint.Identity || hello.ProcessId != pid || hello.Role is not ("client" or "control"))
                    throw new IOException("Incompatible audio service handshake. Stop the old service and start the current build.");
                inference = hello.Role == "client";
                if (!inference) Closed.CancelAfter(TimeSpan.FromSeconds(5));
                lock (host.sync)
                {
                    if (inference && (host.clientProcesses.Count >= 32 || !host.clientProcesses.Add(pid)))
                        throw new IOException("Audio service client limit reached or this process already has a connection.");
                    if (inference) process = pid;
                }
                await Send(new("hello", Data: AudioWire.Data(host.Status())), Closed.Token);
                while (!Closed.IsCancellationRequested)
                {
                    var message = await AudioWire.Read(pipe, Closed.Token);
                    if (!inference)
                    {
                        if (message.Type.StartsWith("capture.", StringComparison.Ordinal))
                        {
                            // The five-second pre-command idle limit must not truncate a large export.
                            Closed.CancelAfter(message.Type == "capture.export" ? TimeSpan.FromHours(1) : TimeSpan.FromSeconds(15));
                            await host.CaptureControl(message, pipe, Send, Closed.Token); return;
                        }
                        if (message.Type == "stop") { lock (host.sync) host.draining = true; }
                        else if (message.Type != "status") throw new IOException("Control connection only accepts status or stop.");
                        await Send(new("status", message.Id, AudioWire.Data(host.Status())), Closed.Token);
                        if (message.Type == "stop") host.Stop(message.Data?["force"]?.GetValue<bool>() == true);
                        return;
                    }
                    if (!Guid.TryParseExact(message.Id, "N", out _)) throw new IOException("Invalid request ID.");
                    if (message.Type == "submit")
                    {
                        if (ids.Count >= 4096) throw new IOException("Connection request limit reached; open a new connection for future requests.");
                        if (!ids.Add(message.Id)) throw new IOException("Duplicate request ID.");
                        try
                        {
                            var submission = AudioWire.Value<AudioSubmission>(message.Data); Validate(submission);
                            var installation = host.Installation(submission);
                            Job job;
                            lock (host.sync)
                            {
                                if (host.draining) throw new IOException("Audio service is draining; new requests are not accepted.");
                                if (host.jobs.Count - (host.active is null ? 0 : 1) >= 32 || Jobs.Values.Count(j => j != host.active) >= 4)
                                    throw new IOException("Audio service queue is full. Request analysis again later.");
                                job = new(this, message.Id, submission, installation);
                                Jobs[job.Id] = job; host.jobs.Add(job);
                            }
                            var watcher = Watch(job); host.watchers[job.Id] = watcher;
                            _ = watcher.ContinueWith(t => { host.watchers.TryRemove(job.Id, out _); }, TaskScheduler.Default);
                            await Send(new("accepted", job.Id), Closed.Token);
                        }
                        catch (Exception ex) when (ex is IOException or UserFacingException or JsonException or ArgumentException)
                        { await Error(message.Id, ex.Message); }
                        continue;
                    }
                    if (!Jobs.TryGetValue(message.Id, out var existing))
                    {
                        // Late upload/cancel frames can follow timeout or a completed request on this connection.
                        if (ids.Contains(message.Id)) continue;
                        throw new IOException("Unknown request owned by this connection.");
                    }
                    if (message.Type == "cancel") { existing.Cancel.Cancel(); continue; }
                    await existing.UploadGate.WaitAsync(Closed.Token);
                    try
                    {
                        if (existing.Ready) throw new IOException("Request upload already completed.");
                        existing.Cancel.Token.ThrowIfCancellationRequested();
                        if (message.Type == "upload")
                        {
                            var bytes = message.Bytes ?? throw new IOException("Missing upload bytes.");
                            if (bytes.Length is < 1 or > AudioWire.ChunkSize || existing.Length + bytes.Length > existing.Submission.Length)
                                throw new IOException("Invalid upload length.");
                            await existing.Upload!.WriteAsync(bytes, existing.Cancel.Token); existing.Length += bytes.Length;
                        }
                        else if (message.Type == "uploaded")
                        {
                            await existing.Upload!.DisposeAsync(); existing.Upload = null;
                            if (existing.Length != existing.Submission.Length) throw new IOException("Incomplete audio upload.");
                            using var input = File.OpenRead(existing.Path);
                            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, existing.Cancel.Token));
                            if (!hash.Equals(existing.Submission.Hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Audio upload hash mismatch.");
                            existing.UploadSeconds = existing.UploadClock.Elapsed.TotalSeconds;
                            existing.ReadyAt.Start();
                            lock (host.sync)
                            {
                                existing.Ready = true; existing.Order = ++host.nextOrder;
                                if (host.active is not null || host.jobs.Any(j => j.Ready && j != existing))
                                    _ = Progress(existing.Id, "waiting for shared audio slot");
                                existing.Node = host.queue.AddLast(existing);
                                host.ready.Writer.TryWrite(true);
                            }
                        }
                        else throw new IOException("Unexpected audio request message.");
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException)
                    {
                        await Error(existing.Id, ex.Message); existing.Cancel.Cancel();
                    }
                    finally { existing.UploadGate.Release(); }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or ObjectDisposedException)
            { await Error("", ex.Message); }
            finally
            {
                Close();
                lock (host.sync) { if (process is { } pid) host.clientProcesses.Remove(pid); }
                host.peers.TryRemove(number, out _);
            }
        }
        private async Task Watch(Job job)
        {
            try
            {
                // Upload expiry is separate from the complete request deadline.
                await Task.Delay(host.UploadTimeout, job.Cancel.Token);
                await job.UploadGate.WaitAsync();
                try { if (!job.Ready) job.Cancel.Cancel(); } finally { job.UploadGate.Release(); }
                await Task.Delay(Timeout.Infinite, job.Cancel.Token);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { return; }
            await job.UploadGate.WaitAsync();
            try
            {
                // Dispatch owns ready-job completion and cleanup, including cancellation.
                bool finish; lock (host.sync) finish = host.jobs.Contains(job) && host.active != job;
                if (finish) { await Error(job.Id, "Audio request cancelled, disconnected, or expired."); host.Finish(job, true); }
            }
            finally { job.UploadGate.Release(); }
        }
    }
}
