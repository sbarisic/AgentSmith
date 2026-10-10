using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Core;

namespace AgentSmith.Tools;

public sealed class ProcessManager : IDisposable
{
    public string OwnerId { get; } = Guid.NewGuid().ToString("N");
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Owned> children = new();
    private sealed class Owned
    {
        public required WindowsChild Child;
        public required ProcessRecord Record;
        public required string SessionId;
        public required Task Output;
        public required Task Error;
        public required Task Monitor;
        public required StreamWriter Log;
        public readonly object Gate = new();
        public long Bytes;
        public long Sequence;
        public readonly List<Chunk> Chunks = [];
        public readonly Stopwatch Timer = Stopwatch.StartNew();
    }
    private sealed record Chunk(long Cursor, string Stream, string Text);
    public static string ResolveExecutable(string value)
    {
        if (value.Length == 0 || value.Contains('\0')) throw new UserFacingException("Executable is required.");
        if (Path.IsPathRooted(value)) { if (File.Exists(value) && Path.GetExtension(value).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(value); }
        else if (!value.Contains('/') && !value.Contains('\\'))
        {
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Path.IsPathRooted(directory)) continue;
                var candidate = Path.Combine(directory.Trim('"'), value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value : value + ".exe");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }
        throw new UserFacingException("Executable not found. Use an installed executable or an absolute .exe path; use pwsh explicitly for scripts.");
    }
    public PreparedOperation Prepare(ToolExecutionContext ctx, ToolCall call, JsonObject args)
    {
        if (call.Name != "process_start")
        {
            args["processId"] = AgentSmith.Core.Activity.ProcessId(ctx.Session, args["processId"]!.GetValue<string>());
            Find(ctx, args["processId"]!.GetValue<string>());
            if (call.Name == "process_poll" && (args["cursor"]!.GetValue<long>() < 0 || args["waitMilliseconds"]!.GetValue<int>() is < 0 or > 10000)) throw new UserFacingException("Invalid output cursor or poll wait.");
            if (call.Name == "process_wait" && (args["cursor"]!.GetValue<long>() < 0 || args["waitMilliseconds"]!.GetValue<int>() is < 0 or > 60000 || args["until"]!.ToString() is not ("output" or "exit"))) throw new UserFacingException("Wait needs a valid cursor, until output|exit, and 0..60000 milliseconds.");
            if (call.Name == "process_input" && args["text"]!.GetValue<string>().Length > 32768) throw new UserFacingException("Input exceeds 32 KiB.");
            return new(args.ToJsonString());
        }
        if (children.Values.Count(p => p.SessionId == ctx.Session.Id && !p.Monitor.IsCompleted) >= 4) throw new UserFacingException("Four processes are already active in this conversation.");
        args["executable"] = ResolveExecutable(args["executable"]!.GetValue<string>());
        var cwd = WorkspaceTools.Resolve(ctx.Workspace, args["workingDirectory"]!.GetValue<string>());
        if (!Directory.Exists(cwd)) throw new UserFacingException("Working directory does not exist.");
        var timeout = args["timeoutSeconds"]!.GetValue<int>();
        if (timeout is < 0 or > 3600) throw new UserFacingException("Timeout must be 0 (default 600) to 3600 seconds.");
        if (args["arguments"]!.AsArray().Sum(a => a!.GetValue<string>().Length + 3) > 24000 || args["arguments"]!.AsArray().Any(a => a!.GetValue<string>().Contains('\0'))) throw new UserFacingException("Invalid or oversized process arguments.");
        foreach (var item in args["environment"]!.AsArray())
        {
            var name = item!["name"]!.GetValue<string>(); var value = item["value"]!.GetValue<string>();
            if (name.Length == 0 || name.Contains('=') || name.Contains('\0') || value.Contains('\0')) throw new UserFacingException("Invalid environment variable.");
        }
        var record = new ProcessRecord { DisplayNumber = ctx.Session.Processes.Select(p => p.DisplayNumber).DefaultIfEmpty().Max() + 1, OwnerId = OwnerId, RunId = ctx.Run.Id, Executable = args["executable"]!.GetValue<string>() };
        record.LogPath = Path.Combine(ctx.ArtifactDirectory, record.Id + ".jsonl");
        ctx.Session.Processes.Add(record);
        return new(args.ToJsonString(), record.Id);
    }
    public async Task<ToolOutput> ExecuteAsync(ToolExecutionContext ctx, ToolCall call)
    {
        var args = JsonNode.Parse(call.Prepared!.Arguments)!.AsObject();
        if (call.Name == "process_start")
        {
            var record = ctx.Session.Processes.Single(p => p.Id == call.Prepared.OperationId);
            Directory.CreateDirectory(ctx.ArtifactDirectory);
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in args["environment"]!.AsArray()) env[pair!["name"]!.GetValue<string>()] = pair["value"]!.GetValue<string>();
            ctx.Cancellation.ThrowIfCancellationRequested();
            var log = new StreamWriter(new FileStream(record.LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
            WindowsChild child;
            try { child = WindowsChild.Start(record.Executable, args["arguments"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray(), WorkspaceTools.Resolve(ctx.Workspace, args["workingDirectory"]!.GetValue<string>()), env); }
            catch { log.Dispose(); record.State = "Failed"; record.Reason = "Launch failed before execution."; record.TerminationConfirmed = true; ctx.Store.SaveProcess(ctx.Session.Id, record); throw; }
            var owned = new Owned { Child = child, Record = record, SessionId = ctx.Session.Id, Output = Task.CompletedTask, Error = Task.CompletedTask, Monitor = Task.CompletedTask,
                Log = log };
            if (!children.TryAdd(record.Id, owned)) throw new UserFacingException("Process identity already exists."); record.State = "Running";
            owned.Output = Task.Run(() => Drain(owned, child.Output, "stdout"));
            owned.Error = Task.Run(() => Drain(owned, child.Error, "stderr"));
            var seconds = args["timeoutSeconds"]!.GetValue<int>();
            owned.Monitor = Monitor(owned, seconds == 0 ? 600 : seconds, ctx);
            ctx.Save("process.started", new { record.Id, record.Executable });
            await Task.WhenAny(owned.Monitor, Task.Delay(100, ctx.Cancellation));
            return Snapshot(ctx, owned, 0);
        }
        var process = Find(ctx, args["processId"]!.GetValue<string>());
        if (call.Name == "process_input")
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            if (process.Monitor.IsCompleted) throw new UserFacingException("Process has exited.");
            await process.Child.Input.WriteAsync(args["text"]!.GetValue<string>()).WaitAsync(ctx.Cancellation);
            if (args["closeStdin"]!.GetValue<bool>()) process.Child.Input.Close();
            return new(Json.Serialize(new { processId = process.Record.Id, alias = "#" + process.Record.DisplayNumber, state = "Input delivered", inputDelivered = true }));
        }
        if (call.Name == "process_stop") { await Stop(process, "Stopped by user or agent."); return Snapshot(ctx, process, process.Sequence); }
        var cursor = args["cursor"]!.GetValue<long>();
        var wait = args["waitMilliseconds"]!.GetValue<int>();
        if (cursor > process.Sequence) throw new UserFacingException("Output cursor is beyond retained output.");
        var timer = Stopwatch.StartNew();
        using var waitStop = CancellationTokenSource.CreateLinkedTokenSource(ctx.Cancellation);
        async Task WaitForOutput()
        {
            while (!process.Monitor.IsCompleted && (cursor >= process.Sequence || call.Name == "process_wait" && args["until"]!.ToString() == "exit") && timer.ElapsedMilliseconds < wait)
                await Task.Delay(25, waitStop.Token);
        }
        var waiting = WaitForOutput();
        try { await SteeringWake.WaitOrSteeringAsync(ctx, waiting); }
        finally { waitStop.Cancel(); try { await waiting; } catch (OperationCanceledException) { } }
        ctx.Cancellation.ThrowIfCancellationRequested();
        var snapshot = Snapshot(ctx, process, cursor);
        if (!ctx.SteeringPending) return snapshot;
        var data = JsonNode.Parse(snapshot.Content)!; data["interruptedBy"] = "steering";
        return snapshot with { Content = data.ToJsonString() };
    }
    private static void Drain(Owned owned, StreamReader reader, string stream)
    {
        try
        {
            var buffer = new char[2048]; int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                var text = new string(buffer, 0, count);
                lock (owned.Gate)
                {
                    var chunk = new Chunk(owned.Sequence + 1, stream, text);
                    var serialized = Json.Serialize(chunk) + "\n";
                    var bytes = Encoding.UTF8.GetByteCount(serialized);
                    if (owned.Bytes + bytes > 20 * 1024 * 1024) { owned.Record.Truncated = true; continue; }
                    owned.Bytes += bytes;
                    owned.Sequence++;
                    owned.Chunks.Add(chunk);
                    owned.Log.Write(serialized);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { lock (owned.Gate) { owned.Record.Truncated = true; } }
    }
    private static async Task Monitor(Owned owned, int seconds, ToolExecutionContext ctx)
    {
        while (!owned.Child.Exited)
        {
            if (owned.Timer.Elapsed.TotalSeconds >= seconds) { owned.Record.Reason = "Process timeout."; owned.Child.Stop(); break; }
            await Task.Delay(25);
        }
        while (!owned.Child.Exited) await Task.Delay(25);
        // A parent that exited cannot leave descendants holding the output pipes open.
        owned.Child.Stop();
        var cleanup = Stopwatch.StartNew();
        while (!owned.Child.TreeExited && cleanup.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
        var treeExited = owned.Child.TreeExited;
        await Task.WhenAll(owned.Output, owned.Error);
        owned.Log.Dispose();
        lock (owned.Gate)
        {
            owned.Record.ExitCode = owned.Child.ExitCode;
            owned.Record.State = owned.Record.Reason is null ? "Exited" : "Stopped";
            owned.Record.TerminationConfirmed = treeExited;
            if (!treeExited) owned.Record.Reason = "Process tree termination could not be confirmed.";
            owned.Record.DurationMilliseconds = owned.Timer.Elapsed.TotalMilliseconds;
        }
        ctx.Store.SaveProcess(ctx.Session.Id, owned.Record);
    }
    private Owned Find(ToolExecutionContext ctx, string id) => children.TryGetValue(id, out var process) && process.SessionId == ctx.Session.Id ? process : throw new UserFacingException("Process is not owned by this conversation and CLI instance.");
    private static ToolOutput Snapshot(ToolExecutionContext ctx, Owned owned, long cursor)
    {
        object payload;
        lock (owned.Gate)
        {
            var chunks = new List<Chunk>(); var size = 0;
            foreach (var chunk in owned.Chunks.Where(c => c.Cursor > cursor))
            { var bytes = Encoding.UTF8.GetByteCount(Json.Serialize(chunk)); if (size + bytes > 28000) break; chunks.Add(chunk); size += bytes; }
            var nextCursor = chunks.Count > 0 ? chunks[^1].Cursor : cursor;
            payload = new { processId = owned.Record.Id, alias = "#" + owned.Record.DisplayNumber, state = owned.Record.State, exitCode = owned.Record.ExitCode, reason = owned.Record.Reason, durationMilliseconds = owned.Record.TerminationConfirmed ? owned.Record.DurationMilliseconds : owned.Timer.Elapsed.TotalMilliseconds, owned.Record.TerminationConfirmed, owned.Record.Truncated, logPath = owned.Record.LogPath, cursor = nextCursor, hasMoreOutput = nextCursor < owned.Sequence, output = chunks };
        }
        ctx.Save("process.observed", new { owned.Record.Id });
        return new(Json.Serialize(payload));
    }
    private static async Task Stop(Owned owned, string reason)
    {
        if (!owned.Monitor.IsCompleted) { owned.Record.Reason = reason; owned.Child.Stop(); }
        await owned.Monitor.WaitAsync(TimeSpan.FromSeconds(10));
    }
    public async Task StopAsync(Session session, string? runId = null)
    {
        List<Exception> failures = [];
        foreach (var owned in children.Values.Where(c => c.SessionId == session.Id && (runId is null || c.Record.RunId == runId)))
            try { await Stop(owned, "Stopped by cancellation, mode change, or CLI exit."); }
            catch (Exception ex) { failures.Add(ex); owned.Child.Dispose(); }
        if (failures.Count > 0) throw new AggregateException("Some process outcomes could not be saved or confirmed; their jobs were closed.", failures);
    }
    public void Recover(Session session)
    {
        foreach (var process in session.Processes.Where(p => p.OwnerId != OwnerId && p.State is "Running" or "Prepared"))
        { process.State = "Interrupted"; process.Reason = "Previous CLI instance ended; execution outcome is unverified."; process.TerminationConfirmed = false; }
    }
    public void Dispose()
    {
        List<Exception> failures = [];
        foreach (var owned in children.Values)
        {
            try { Stop(owned, "CLI exit.").GetAwaiter().GetResult(); }
            catch (Exception ex) { failures.Add(ex); }
            finally { try { owned.Log.Dispose(); } catch (IOException ex) { failures.Add(ex); } finally { owned.Child.Dispose(); } }
        }
        children.Clear();
        if (failures.Count > 0) throw new AggregateException("Process cleanup completed with unverified outcomes.", failures);
    }
}
