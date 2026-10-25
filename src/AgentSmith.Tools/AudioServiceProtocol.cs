using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSmith.Core;

namespace AgentSmith.Tools;

internal sealed record AudioMessage(string Type, string Id = "", JsonObject? Data = null, byte[]? Bytes = null);
internal sealed record AudioHello(int Protocol, string Build, string Identity, string Role, int ProcessId);
internal sealed record AudioSubmission(string Generation, string Fingerprint, string Format, long Length, string Hash,
    string Operation, int StartSeconds, int DurationSeconds, string Language, string[] Components, long DeadlineTicks);

public sealed record AudioServiceStatus(bool Available, string Identity, string DataDirectory, int? ProcessId,
    string Build, int Protocol, bool Draining, string Helper, int? HelperProcessId, long HelperPeakCommittedBytes,
    int ActiveJobs, int WaitingJobs, int Clients, string? Generation, AudioReadiness Engines, string Message, CaptureSummary? Capture = null);

internal static class AudioWire
{
    internal const int Version = 3;
    internal const int FrameLimit = 64 * 1024;
    internal const int ChunkSize = 32 * 1024;
    internal static string Build => typeof(AudioWire).Assembly.ManifestModule.ModuleVersionId.ToString("N");
    internal static JsonObject Data<T>(T value) => JsonSerializer.SerializeToNode(value)!.AsObject();
    internal static T Value<T>(JsonObject? value) where T : class => value?.Deserialize<T>() ?? throw new IOException("Missing service message.");

    internal static async Task Write(Stream stream, AudioMessage message, SemaphoreSlim gate, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length > FrameLimit) throw new IOException("Audio service frame exceeds 64 KiB.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await gate.WaitAsync(ct);
        try
        {
            using var frameTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await stream.WriteAsync(header, frameTimeout.Token); await stream.WriteAsync(bytes, frameTimeout.Token); await stream.FlushAsync(frameTimeout.Token); }
            catch { stream.Dispose(); throw; }
        }
        finally { gate.Release(); }
    }

    internal static async Task<AudioMessage> Read(Stream stream, CancellationToken ct)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 2 or > FrameLimit) throw new IOException("Invalid audio service frame length.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, ct);
        var message = JsonSerializer.Deserialize<AudioMessage>(bytes) ?? throw new IOException("Invalid service message.");
        if (string.IsNullOrEmpty(message.Type) || message.Type.Length > 32 || message.Id is null || message.Id.Length > 64) throw new IOException("Invalid message identity.");
        return message;
    }
}
