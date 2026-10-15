using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Core;

namespace AgentSmith.Tools;

/// <summary>Process-wide, serialized, job-contained inference host. No audio buffers survive requests.</summary>
internal static class LocalAudioService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object Sync = new();
    private static WindowsChild? child;
    private static string? generation;
    private static int owners;
    private static int errorBytes;
    private static Task? errors;
    private static bool active;
    internal static TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly Timer Idle = new(_ => Expire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    internal static readonly IReadOnlyDictionary<string, string> OfflineEnvironment = new Dictionary<string, string>
    {
        ["HF_HUB_OFFLINE"] = "1", ["TRANSFORMERS_OFFLINE"] = "1", ["HF_HUB_DISABLE_TELEMETRY"] = "1",
        ["OPENAI_API_KEY"] = "", ["SMITH_AUDIO_MODEL"] = "", ["PYTHONNOUSERSITE"] = "1"
    };
    internal static Func<string, WindowsChild> Launch = directory => WindowsChild.Start(Path.Combine(directory, "env", "Scripts", "python.exe"),
        ["-I", "-u", Path.Combine(directory, "helper", "audio_helper.py"), "--server"], directory, OfflineEnvironment);
    internal static string State { get { lock (Sync) return child is null ? "unloaded" : active ? "processing" : "warm (unloads after five idle minutes)"; } }
    internal static long PeakCommittedBytes
    {
        get
        {
            lock (Sync)
            {
                if (child is null || child.Exited) return 0;
                return child.PeakJobMemoryBytes;
            }
        }
    }
    internal static void Acquire() { lock (Sync) owners++; }
    internal static void Release() { lock (Sync) { if (--owners == 0 && !active) Stop(); } }
    private static void Expire()
    {
        lock (Sync)
        {
            try { if (!active) Stop(); }
            catch (IOException) { /* Stop still disposes the containment job in its finally block. */ }
        }
    }
    private static void Stop()
    {
        Idle.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (child is { } process)
        {
            try { process.Stop(); process.WaitForExit(5000); }
            finally { process.Dispose(); child = null; generation = null; }
        }
    }
    internal static async Task<JsonObject> Infer(string directory, JsonObject request, Action<string> progress, CancellationToken cancellation)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "helper", "downloads.json")));
        if (manifest?["helperProtocol"]?.GetValue<int>() != 2)
            return await LocalAudioProcess.Infer(directory, request, progress, cancellation);
        await Gate.WaitAsync(cancellation);
        WindowsChild current;
        try
        {
            lock (Sync)
            {
                Idle.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                if (child is not null && (generation != directory || child.Exited)) Stop();
                if (child is null)
                {
                    child = Launch(directory);
                    generation = directory;
                    var process = child;
                    errors = Task.Run(async () =>
                    {
                        try
                        {
                            var buffer = new char[4096]; int count;
                            while ((count = await process.Error.ReadAsync(buffer)) > 0)
                                if (Interlocked.Add(ref errorBytes, count) > LocalAudioProcess.OutputLimit) { process.Stop(); break; }
                        }
                        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                    });
                }
                active = true; current = child; Interlocked.Exchange(ref errorBytes, 0);
            }
            using var cancel = cancellation.Register(() => { try { current.Stop(); } catch (IOException) { } });
            var id = Guid.NewGuid().ToString("N");
            var input = (JsonObject)request.DeepClone(); input["protocol"] = 2; input["requestId"] = id;
            var serialized = input.ToJsonString();
            if (Encoding.UTF8.GetByteCount(serialized) > 16384) throw new UserFacingException("Audio request exceeds 16 KiB.");
            await current.Input.WriteLineAsync(serialized.AsMemory(), cancellation);
            var result = await Task.Run(async () =>
            {
                var line = new StringBuilder(); var buffer = new char[1]; var total = 0; JsonObject? result = null;
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var count = await current.Output.ReadAsync(buffer.AsMemory(), cancellation);
                    if (count == 0) throw new UserFacingException("Local audio helper exited before completion.");
                    if (++total > LocalAudioProcess.OutputLimit) throw new UserFacingException("Local audio helper output exceeded 2 MiB.");
                    if (buffer[0] != '\n') { line.Append(buffer[0]); continue; }
                    var message = JsonNode.Parse(line.ToString()); line.Clear();
                    if (message?["protocol"]?.GetValue<int>() != 2 || message["requestId"]?.ToString() != id) throw new UserFacingException("Invalid audio response identity or protocol.");
                    switch (message["type"]?.ToString())
                    {
                        case "progress": progress(message["stage"]?.ToString() ?? "processing"); break;
                        case "component": break; // Full validated component evidence arrives in the final result.
                        case "result":
                            if (result is not null) throw new UserFacingException("Duplicate audio result.");
                            result = message["result"]!.DeepClone().AsObject(); break;
                        case "complete": return result ?? throw new UserFacingException("Audio completed without a result.");
                        case "error": throw new UserFacingException("Local audio helper failed: " + message["error"]);
                        default: throw new UserFacingException("Invalid audio response message.");
                    }
                }
            }, CancellationToken.None);
            cancellation.ThrowIfCancellationRequested();
            LocalAudioProcess.Validate(result, request["operation"]!.ToString(), request["components"]?.AsArray().Select(n => n!.ToString()).ToArray());
            return result;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or NullReferenceException or FormatException)
        { lock (Sync) Stop(); throw new UserFacingException("Local audio helper returned malformed evidence; no result was retained."); }
        catch { lock (Sync) Stop(); cancellation.ThrowIfCancellationRequested(); throw; }
        finally
        {
            lock (Sync)
            {
                active = false;
                if (owners == 0) Stop();
                else if (child is not null) Idle.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
            }
            Gate.Release();
        }
    }
}
