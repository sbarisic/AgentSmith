using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSmith.Core;

namespace AgentSmith.Tools;

public sealed partial class AudioServiceHost
{
    private async Task CaptureControl(AudioMessage message, NamedPipeServerStream pipe, Func<AudioMessage, CancellationToken, Task> send, CancellationToken ct)
    {
        try
        {
            if (!Guid.TryParseExact(message.Id, "N", out _)) throw new IOException("Invalid capture request ID.");
            var id = message.Data?["id"]?.GetValue<string>();
            object result;
            switch (message.Type)
            {
                case "capture.devices": result = captures.Devices(); break;
                case "capture.list": result = captures.List(message.Data?["page"]?.GetValue<int>() ?? 1); break;
                case "capture.status": result = Compact(captures.Status(id)); break;
                case "capture.start":
                    lock (sync) { if (draining) throw new UserFacingException("Service is draining."); }
                    result = Compact(await captures.Start(AudioWire.Value<CaptureStart>(message.Data))); break;
                case "capture.stop": result = Compact(await captures.Stop(id)); break;
                case "capture.delete": await captures.Delete(id ?? ""); result = new { id, deleted = true }; break;
                case "capture.export":
                    using (await captures.AcquireExport(id ?? ""))
                    {
                        var manifest = captures.Status(id);
                        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
                        await send(new("capture.file", message.Id, new() { ["name"] = "manifest.json", ["length"] = manifestBytes.Length, ["hash"] = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant() }), ct);
                        for (var offset = 0; offset < manifestBytes.Length; offset += AudioWire.ChunkSize)
                            await send(new("capture.bytes", message.Id, Bytes: manifestBytes.AsSpan(offset, Math.Min(AudioWire.ChunkSize, manifestBytes.Length - offset)).ToArray()), ct);
                        foreach (var chunk in manifest.Chunks)
                        {
                            await using var file = File.OpenRead(captures.ChunkPath(manifest.Id, chunk.File));
                            if (file.Length != chunk.Bytes) throw new IOException("Recording chunk changed.");
                            await send(new("capture.file", message.Id, new() { ["name"] = chunk.File, ["length"] = chunk.Bytes, ["hash"] = chunk.Hash }), ct);
                            var buffer = new byte[AudioWire.ChunkSize]; int count;
                            while ((count = await file.ReadAsync(buffer, ct)) > 0) await send(new("capture.bytes", message.Id, Bytes: buffer.AsSpan(0, count).ToArray()), ct);
                        }
                        await send(new("capture.done", message.Id), ct);
                    }
                    return;
                default: throw new IOException("Unknown capture command.");
            }
            await send(new("capture.result", message.Id, new() { ["result"] = JsonSerializer.SerializeToNode(result) }), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UserFacingException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException or JsonException)
        { await send(new("error", message.Id, new() { ["message"] = ex.Message }), ct); }
    }
    private static CaptureManifest Compact(CaptureManifest manifest)
    { manifest.Chunks = []; manifest.Coverage = []; return manifest; }
}

public static class AudioCaptureControl
{
    public static async Task<JsonNode> Request(string directory, string action, JsonObject? data = null, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            using var pipe = await AudioConnection.Connect(AudioEndpoint.Resolve(directory), "control", timeout.Token);
            using var gate = new SemaphoreSlim(1);
            var id = Guid.NewGuid().ToString("N");
            await AudioWire.Write(pipe, new("capture." + action, id, data), gate, timeout.Token);
            var reply = await AudioWire.Read(pipe, timeout.Token);
            if (reply.Type != "capture.result" || reply.Id != id) throw AudioConnection.Failure(reply);
            return reply.Data?["result"]?.DeepClone() ?? throw new IOException("Missing capture result.");
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            throw new UserFacingException("Capture command could not be confirmed. Run smith service start if unavailable, then smith audio capture status or list. No retry was made. " + ex.Message);
        }
    }
    public static async Task<string> Export(string directory, string id, string destination, CancellationToken ct = default)
    {
        destination = Path.GetFullPath(destination);
        if (Path.Exists(destination)) throw new UserFacingException("Export destination must not already exist.");
        var parent = Path.GetDirectoryName(destination) ?? throw new UserFacingException("Invalid export directory.");
        if (!Directory.Exists(parent)) throw new UserFacingException("Export parent directory must exist.");
        for (var ancestor = new DirectoryInfo(parent); ancestor is not null; ancestor = ancestor.Parent) AudioCaptureStore.Check(ancestor.FullName);
        var staging = Path.Combine(parent, ".smith-export-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromHours(1));
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token); connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var pipe = await AudioConnection.Connect(AudioEndpoint.Resolve(directory), "control", connectTimeout.Token);
        using var gate = new SemaphoreSlim(1);
        var request = Guid.NewGuid().ToString("N");
        await AudioWire.Write(pipe, new("capture.export", request, new() { ["id"] = id }), gate, timeout.Token);
        FileStream? output = null; IncrementalHash? hash = null; long remaining = 0; string? expectedHash = null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Finish()
        {
            if (output is null) return;
            output.Flush(true); output.Dispose(); output = null;
            var actual = Convert.ToHexString(hash!.GetHashAndReset()).ToLowerInvariant(); hash.Dispose(); hash = null;
            if (remaining != 0 || actual != expectedHash) throw new IOException("Export length/hash mismatch. Partial files remain in " + staging);
        }
        try
        {
            while (true)
            {
                using var frameTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token); frameTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                var message = await AudioWire.Read(pipe, frameTimeout.Token);
                if (message.Id != request) throw new IOException("Unexpected export request ID.");
                if (message.Type == "error") throw AudioConnection.Failure(message);
                if (message.Type == "capture.done") { Finish(); break; }
                if (message.Type == "capture.file")
                {
                    Finish();
                    var name = message.Data?["name"]?.GetValue<string>() ?? "";
                    if (name != "manifest.json" && !AudioCaptureStore.ValidChunk(name) || !names.Add(name)) throw new IOException("Invalid export filename.");
                    remaining = message.Data?["length"]?.GetValue<long>() ?? -1;
                    if (remaining is < 0 or > 17 * 1024 * 1024) throw new IOException("Invalid export file length.");
                    expectedHash = message.Data?["hash"]?.GetValue<string>();
                    output = new(Path.Combine(staging, name), FileMode.CreateNew, FileAccess.Write, FileShare.None); hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                }
                else if (message.Type == "capture.bytes" && output is not null && message.Bytes is { Length: > 0 } bytes && bytes.Length <= AudioWire.ChunkSize && bytes.Length <= remaining)
                { await output.WriteAsync(bytes, timeout.Token); hash!.AppendData(bytes); remaining -= bytes.Length; }
                else throw new IOException("Invalid export sequence.");
            }
            var manifest = JsonSerializer.Deserialize<CaptureManifest>(await File.ReadAllTextAsync(Path.Combine(staging, "manifest.json"), ct)) ?? throw new IOException("Invalid export manifest.");
            if (manifest.Id != id || manifest.Chunks.Any(c => !names.Contains(c.File)) || names.Count != manifest.Chunks.Count + 1) throw new IOException("Incomplete export.");
            await File.WriteAllLinesAsync(Path.Combine(staging, "recording.m3u"), new[] { "#EXTM3U" }.Concat(manifest.Chunks.Select(c => c.File)), ct);
            Directory.Move(staging, destination); return destination;
        }
        finally { output?.Dispose(); hash?.Dispose(); }
    }
}
