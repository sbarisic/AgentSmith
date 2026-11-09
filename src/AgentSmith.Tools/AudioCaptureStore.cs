using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using AgentSmith.Core;

namespace AgentSmith.Tools;

public sealed record CaptureChunk(string File, long StartFrame, long Frames, long Bytes, string Hash);
public sealed record CaptureCoverage(long StartFrame, long Frames, string Kind, string TrackId = "");
public sealed record CaptureAnchor(long Frame, long Qpc);
public sealed record CaptureTimingObservation(long ExpectedPosition, long Position, long Qpc, long ExpectedFrame, long TimestampFrame, bool DeviceDiscontinuity);
public sealed class CaptureTimingDiagnostics
{
    public long Packets { get; set; }
    public long DeviceDiscontinuities { get; set; }
    public long PositionRegressions { get; set; }
    public long PositionUnitMismatches { get; set; }
    public long TimestampFallbacks { get; set; }
    public long TimestampErrors { get; set; }
    public long OverlapFramesDiscarded { get; set; }
    public List<CaptureTimingObservation> Observations { get; set; } = [];
}
public sealed class CaptureManifest
{
    public int Version { get; set; } = 1;
    public string TrackId { get; set; } = "";
    public long StartQpc { get; set; }
    public double TimelineSeconds { get; set; }
    public double Level { get; set; }
    public bool Muted { get; set; }
    public List<CaptureAnchor> Anchors { get; set; } = [];
    // Optional additive metadata: older manifests have no packet-level diagnostics.
    public CaptureTimingDiagnostics? TimingDiagnostics { get; set; }
    public List<CaptureManifest> Tracks { get; set; } = [];
    public IEnumerable<CaptureChunk> AllChunks() => Version == 2 ? Tracks.SelectMany(t => t.Chunks) : Chunks;
    public string Id { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string Owner { get; set; } = "";
    public string? Conversation { get; set; }
    public CaptureDevice Device { get; set; } = new("", "", false);
    public string State { get; set; } = "Starting";
    public DateTimeOffset Started { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Ended { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public int BitsPerSample { get; set; }
    public int BlockAlign { get; set; }
    public string Format { get; set; } = "";
    public bool? FloatingPoint { get; set; }
    public long Frames { get; set; }
    public long Bytes { get; set; }
    public long BudgetBytes { get; set; }
    public long RetainedBytes { get; set; }
    public bool BudgetWarning { get; set; }
    public string? Error { get; set; }
    public List<CaptureChunk> Chunks { get; set; } = [];
    public List<CaptureCoverage> Coverage { get; set; } = [];
    public double DurationSeconds => TimelineSeconds > 0 ? TimelineSeconds : SampleRate > 0 ? (double)Frames / SampleRate : 0;
}

internal sealed class AudioCaptureStore
{
    internal string Root { get; }
    internal AudioCaptureStore(string dataDirectory)
    {
        var audio = Path.Combine(dataDirectory, "audio");
        Directory.CreateDirectory(audio); Check(audio);
        Root = Path.Combine(audio, "recordings"); Directory.CreateDirectory(Root); Check(Root);
        if (OperatingSystem.IsWindows())
        {
            using var user = WindowsIdentity.GetCurrent();
            var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(user.User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(Root).SetAccessControl(security);
        }
    }
    internal static void Check(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked recording paths are unsupported.");
    }
    internal string DirectoryFor(string id, bool create = false)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new UserFacingException("Invalid recording ID.");
        Check(Root);
        var path = Path.Combine(Root, id);
        if (create) Directory.CreateDirectory(path);
        Check(path); return path;
    }
    internal FileStream Lease(string id)
    {
        var path = Path.Combine(DirectoryFor(id), "recording.lock");
        if (File.Exists(path)) Check(path);
        return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    internal CaptureManifest Read(string id)
    {
        var path = Path.Combine(DirectoryFor(id), "manifest.json"); Check(path);
        var info = new FileInfo(path);
        if (info.Length > 16 * 1024 * 1024) throw new IOException("Recording manifest is too large.");
        var m = JsonSerializer.Deserialize<CaptureManifest>(File.ReadAllText(path)) ?? throw new IOException("Invalid recording manifest.");
        if (m.Version is not (1 or 2) || m.Id != id || m.AllChunks().Any(c => !ValidChunk(c.File)) ||
            m.Version == 2 && (m.Tracks.Count != 2 || m.Tracks.Select(t => t.TrackId).Order().SequenceEqual(new[] { "call", "mic" }) == false)) throw new IOException("Unsupported recording manifest.");
        return m;
    }
    internal static bool ValidChunk(string name)
    {
        if (name.StartsWith("call-", StringComparison.Ordinal)) name = name[5..];
        else if (name.StartsWith("mic-", StringComparison.Ordinal)) name = name[4..];
        return name.Length == 16 && name.StartsWith("chunk-", StringComparison.Ordinal) && name.EndsWith(".wav", StringComparison.Ordinal) && name.AsSpan(6, 6).ToString().All(char.IsAsciiDigit);
    }
    internal void Save(CaptureManifest manifest)
    {
        var directory = DirectoryFor(manifest.Id);
        var name = manifest.TrackId is "call" or "mic" ? manifest.TrackId + "-manifest" : "manifest";
        var path = Path.Combine(directory, name + ".json");
        if (File.Exists(path)) Check(path);
        var temporary = Path.Combine(directory, name + ".new");
        if (File.Exists(temporary)) Check(temporary);
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, manifest); file.Flush(true); }
        File.Move(temporary, path, true);
    }
    internal string[] Ids() => Directory.EnumerateDirectories(Root).Select(Path.GetFileName).OfType<string>().Where(x => Guid.TryParseExact(x, "N", out _)).OrderDescending().ToArray();
    internal long UsedBytes()
    {
        long bytes = 0;
        foreach (var id in Ids())
        foreach (var file in Directory.EnumerateFiles(DirectoryFor(id))) { Check(file); bytes = checked(bytes + new FileInfo(file).Length); }
        return bytes;
    }
    internal static string Hash(string path) { Check(path); using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
    internal void Recover()
    {
        foreach (var id in Ids())
        {
            using var lease = Lease(id);
            if (!File.Exists(Path.Combine(DirectoryFor(id), "manifest.json"))) continue;
            var manifest = Read(id);
            if (manifest.State is not ("Starting" or "Recording" or "Finalizing")) continue;
            if (manifest.Version == 2)
            {
                for (var index = 0; index < manifest.Tracks.Count; index++)
                {
                    var track = manifest.Tracks[index];
                    var checkpoint = Path.Combine(DirectoryFor(id), track.TrackId + "-manifest.json");
                    if (File.Exists(checkpoint)) { Check(checkpoint); track = JsonSerializer.Deserialize<CaptureManifest>(File.ReadAllText(checkpoint))!; }
                    RecoverTrack(track); manifest.Tracks[index] = track;
                }
                manifest.State = "Interrupted"; manifest.Error = "Recovered call; missing intervals and trailing coverage are unknown.";
                manifest.Ended = DateTimeOffset.UtcNow; manifest.Bytes = manifest.Tracks.Sum(t => t.Bytes);
                manifest.TimelineSeconds = manifest.Tracks.Max(t => t.DurationSeconds); Save(manifest); continue;
            }
            RecoverTrack(manifest);
        }
    }
    private void RecoverTrack(CaptureManifest manifest)
    {
            var id = manifest.Id;
            manifest.Chunks.Clear(); manifest.Frames = 0; manifest.Bytes = 0;
            foreach (var path in Directory.EnumerateFiles(DirectoryFor(id), (manifest.TrackId.Length == 0 ? "" : manifest.TrackId + "-") + "chunk-*.wav").Order())
            {
                Check(path);
                if (!ValidChunk(Path.GetFileName(path))) throw new IOException("Unexpected recording chunk name.");
                var frames = RepairWave(path, manifest.BlockAlign);
                manifest.Chunks.Add(new(Path.GetFileName(path), manifest.Frames, frames, new FileInfo(path).Length, Hash(path)));
                manifest.Frames += frames; manifest.Bytes += new FileInfo(path).Length;
            }
            manifest.Coverage.RemoveAll(c => c.StartFrame >= manifest.Frames);
            manifest.Anchors.RemoveAll(a => a.Frame > manifest.Frames);
            manifest.Coverage = manifest.Coverage.Select(c => c with { Frames = Math.Min(c.Frames, manifest.Frames - c.StartFrame) }).ToList();
            manifest.TimelineSeconds = Math.Max(manifest.TimelineSeconds, manifest.SampleRate > 0 ? manifest.Frames / (double)manifest.SampleRate : 0);
            manifest.State = "Interrupted"; manifest.Ended = DateTimeOffset.UtcNow;
            manifest.Error = "Service stopped before finalization; recovered complete frames. Uncheckpointed coverage and the trailing interval are unknown.";
            Save(manifest);
    }
    private static long RepairWave(string path, int blockAlign)
    {
        if (blockAlign <= 0) throw new IOException("Missing recording format.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var reader = new BinaryReader(file, System.Text.Encoding.ASCII, true);
        using var writer = new BinaryWriter(file, System.Text.Encoding.ASCII, true);
        if (file.Length < 12 || reader.ReadUInt32() != 0x46464952) throw new IOException("Invalid recording WAV header.");
        file.Position = 8; if (reader.ReadUInt32() != 0x45564157) throw new IOException("Invalid recording WAV header.");
        long? factPosition = null;
        while (file.Position + 8 <= file.Length)
        {
            var kind = reader.ReadUInt32(); var lengthAt = file.Position; var size = reader.ReadUInt32();
            if (kind == 0x61746164)
            {
                var bytes = (file.Length - file.Position) / blockAlign * blockAlign;
                file.SetLength(file.Position + bytes); file.Position = lengthAt; writer.Write((uint)bytes);
                if (factPosition is { } fact) { file.Position = fact; writer.Write((uint)(bytes / blockAlign)); }
                file.Position = 4; writer.Write((uint)(file.Length - 8)); writer.Flush(); file.Flush(true); return bytes / blockAlign;
            }
            if (kind == 0x74636166 && size >= 4) factPosition = file.Position;
            file.Position = checked(file.Position + size + (size & 1));
        }
        throw new IOException("Recording WAV has no data header.");
    }
}
