using System.Security.Cryptography;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using AgentSmith.Core;

namespace AgentSmith.Tools;

public sealed record RecordingAnalysisOptions(bool Sounds = false, string Language = "auto", bool NoSummary = false,
    bool AllowInterrupted = false, int MaxMinutes = 30, int MaxRequests = 100, double MaxStorageGiB = 10, string? Question = null);
public sealed class RecordingAnalysisJob
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceKind { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public string Installation { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string DecoderHash { get; set; } = "";
    public string State { get; set; } = "Paused";
    public string Stage { get; set; } = "Preparation";
    public string? Error { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    public RecordingAnalysisOptions Options { get; set; } = new();
    public long Frames { get; set; }
    public bool Prepared { get; set; }
    public List<CaptureCoverage> Coverage { get; set; } = [];
    public List<RecordingAnalysisChunk> Chunks { get; set; } = [];
    public string Reconciliation { get; set; } = "titanet-cosine-v1;threshold=.75;margin=.10";
    public string? SummaryModel { get; set; }
    public double ExecutionSeconds { get; set; }
    public int SummaryRequests { get; set; }
    public Dictionary<string, double> Timings { get; set; } = [];
    public double DurationSeconds => Frames / 16000d;
    public double ProcessedSeconds => Chunks.Where(c => c.Components.GetValueOrDefault("transcription") == "Succeeded").Sum(c =>
    {
        long missing = 0, end = c.CoreStart;
        foreach (var gap in Coverage.Where(g => g.Kind != "InferredSilence" && g.Frames > 0).OrderBy(g => g.StartFrame))
        {
            var first = Math.Max(c.CoreStart, gap.StartFrame); var last = Math.Min(c.CoreEnd, gap.StartFrame + gap.Frames);
            if (last > first) { missing += Math.Max(0, last - Math.Max(end, first)); end = Math.Max(end, last); }
        }
        return c.CoreEnd - c.CoreStart - missing;
    }) / 16000d;
}
public sealed class RecordingAnalysisChunk
{
    public int Index { get; set; }
    public long Start { get; set; }
    public long End { get; set; }
    public long CoreStart { get; set; }
    public long CoreEnd { get; set; }
    public string Hash { get; set; } = "";
    public Dictionary<string, string> Components { get; set; } = [];
    public double Seconds { get; set; }
}

/// <summary>Client-owned durable jobs. The service never opens this store.</summary>
public sealed class RecordingAnalysisStore
{
    public string Root { get; }
    public RecordingAnalysisStore(string dataDirectory)
    {
        Root = Path.Combine(Path.GetFullPath(dataDirectory), "audio", "analyses");
        CheckPath(Path.GetDirectoryName(Root)!);
        Directory.CreateDirectory(Root); CheckPath(Root);
        if (OperatingSystem.IsWindows())
        {
            using var user = WindowsIdentity.GetCurrent(); var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(user.User!, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(Root).SetAccessControl(security);
        }
    }
    public static void CheckPath(string path)
    {
        for (FileSystemInfo? entry = new FileInfo(Path.GetFullPath(path)); entry is not null; entry = entry is FileInfo f ? f.Directory : ((DirectoryInfo)entry).Parent)
            if (Path.Exists(entry.FullName) && (File.GetAttributes(entry.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new UserFacingException("Linked analysis paths are unsupported.");
    }
    public string DirectoryFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new UserFacingException("Invalid analysis ID.");
        var path = Path.Combine(Root, id); CheckPath(path); return path;
    }
    public string FileFor(string id, string name)
    {
        if (Path.GetFileName(name) != name) throw new IOException("Invalid analysis filename.");
        var path = Path.Combine(DirectoryFor(id), name); CheckPath(path); return path;
    }
    public FileStream Lease(string id) => new(FileFor(id, "job.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    public RecordingAnalysisJob Read(string id)
    {
        var job = ReadJson<RecordingAnalysisJob>(FileFor(id, "job.json"));
        if (job.Version != 1 || job.Id != id) throw new UserFacingException("Unsupported analysis record.");
        if (job.State == "Running")
        {
            try { using var lease = Lease(id); job.State = "Interrupted"; job.Error = "The owning client stopped. Resume explicitly; uncertain submissions require --retry-uncertain."; }
            catch (IOException) { }
        }
        return job;
    }
    public IEnumerable<RecordingAnalysisJob> List() => Directory.EnumerateDirectories(Root)
        .Select(Path.GetFileName).OfType<string>().Where(x => Guid.TryParseExact(x, "N", out _) && File.Exists(FileFor(x, "job.json"))).Select(Read).OrderByDescending(j => j.Created);
    public void Save(RecordingAnalysisJob job) { job.Updated = DateTimeOffset.UtcNow; WriteJson(FileFor(job.Id, "job.json"), job); }
    public static T ReadJson<T>(string path)
    {
        CheckPath(path);
        using var file = File.OpenRead(path);
        if (file.Length > 16 * 1024 * 1024) throw new IOException("Analysis metadata exceeds 16 MiB.");
        return JsonSerializer.Deserialize<T>(file) ?? throw new IOException("Invalid analysis metadata.");
    }
    public static void WriteJson<T>(string path, T value)
    {
        CheckPath(path); CheckPath(path + ".new");
        using (var file = new FileStream(path + ".new", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, value); file.Flush(true); }
        File.Move(path + ".new", path, true);
    }
    public static string Hash(string path)
    { CheckPath(path); using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
    internal static void CommitFile(string temporary, string destination)
    {
        CheckPath(temporary); CheckPath(destination);
        using (var file = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) file.Flush(true);
        File.Move(temporary, destination, true);
    }
    public void CheckBudget(double gib, long additional = 0)
    {
        var used = UsedBytes();
        if (used + additional > gib * 1073741824d) throw new UserFacingException("Analysis storage budget reached; resume with a larger --max-storage-gib or explicitly delete old analyses.");
        if (new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace - additional < 1073741824L)
            throw new UserFacingException("Analysis paused to preserve the 1 GiB free-disk reserve.");
    }
    private static IEnumerable<string> CheckedEntries(string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            CheckPath(path); yield return path;
            if (Directory.Exists(path)) foreach (var child in CheckedEntries(path)) yield return child;
        }
    }
    internal long UsedBytes() => CheckedEntries(Root).Where(File.Exists).Sum(path => new FileInfo(path).Length);
    internal sealed class Reservation(string path, FileStream lease, long capacity) : IDisposable
    {
        internal long Capacity => capacity;
        public void Dispose() { lease.Dispose(); try { File.Delete(path); } catch (FileNotFoundException) { } }
    }
    internal async Task<Reservation> Reserve(long? requested, double gib, CancellationToken ct)
    {
        FileStream gate;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(Root, "quota.lock"); CheckPath(path);
            try { gate = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
            catch (IOException) { await Task.Delay(100, ct); }
        }
        using (gate)
        {
            long reserved = 0;
            foreach (var path in Directory.EnumerateFiles(Root, "reserve-*.json"))
            {
                CheckPath(path);
                try
                {
                    using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    File.Delete(path); // No live lease: recover the abandoned reservation only.
                }
                catch (IOException)
                {
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    reserved = checked(reserved + JsonSerializer.Deserialize<long>(file));
                }
            }
            var available = Math.Min((long)(gib * 1073741824d) - UsedBytes() - reserved - 65536,
                new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace - 1073741824L - reserved - 65536);
            var amount = requested ?? available;
            if (amount <= 0 || amount > available) throw new UserFacingException("Analysis storage is reserved or exhausted. Resume after other preparation finishes, increase --max-storage-gib, or explicitly delete old analysis.");
            var reservationPath = Path.Combine(Root, "reserve-" + Guid.NewGuid().ToString("N") + ".json");
            var lease = new FileStream(reservationPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            JsonSerializer.Serialize(lease, amount); lease.Flush(true); return new(reservationPath, lease, amount);
        }
    }
    public void Delete(string id)
    {
        using (Lease(id))
        {
            var directory = DirectoryFor(id);
            foreach (var path in CheckedEntries(directory)) CheckPath(path);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                if (Path.GetFileName(path) != "job.lock") { if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path); }
        }
        // Keep the lease inode to avoid a delete/reopen race. Tombstones contain no evidence.
    }
}
