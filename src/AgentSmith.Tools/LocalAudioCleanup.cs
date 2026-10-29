using System.Text.Json;
using AgentSmith.Core;

namespace AgentSmith.Tools;

public static partial class LocalAudioInstallation
{
    // Lock files are outside generation directories and are never removed: replacing a
    // lock inode would allow two processes to believe they hold the same lease.
    internal static FileStream? Use(string directory)
    {
        var path = Path.GetFullPath(directory);
        if (!Guid.TryParseExact(Path.GetFileName(path), "N", out _) || Directory.GetParent(path)?.Name != "installations") return null;
        var lease = GenerationLease(path, false);
        if (Directory.Exists(path)) return lease;
        lease.Dispose();
        throw new IOException("Audio installation was retired. Retry using the current installation.");
    }

    private static FileStream GenerationLease(string directory, bool exclusive)
    {
        RecordingAnalysisStore.CheckPath(directory);
        var root = Directory.GetParent(directory)!.Parent!.FullName;
        var locks = Path.Combine(root, "generation-leases");
        RecordingAnalysisStore.CheckPath(locks); Directory.CreateDirectory(locks);
        var path = Path.Combine(locks, Path.GetFileName(directory) + ".lock");
        RecordingAnalysisStore.CheckPath(path);
        return new(path, FileMode.OpenOrCreate, exclusive ? FileAccess.ReadWrite : FileAccess.Read,
            exclusive ? FileShare.None : FileShare.Read);
    }

    /// <summary>Best-effort retirement; active, resumable, busy or uncertain generations stay intact.</summary>
    public static void Cleanup(string dataDirectory, Action<string>? progress = null)
    {
        var root = Path.GetFullPath(Path.Combine(dataDirectory, "audio"));
        if (!Directory.Exists(Path.Combine(root, "installations"))) return;
        try
        {
            RecordingAnalysisStore.CheckPath(Path.Combine(root, "setup.lock"));
            using var setup = new FileStream(Path.Combine(root, "setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            CleanupLocked(dataDirectory, progress);
        }
        catch (Exception ex) when (CleanupError(ex)) { progress?.Invoke("Audio cleanup deferred: " + ex.Message); }
    }

    private static bool CleanupError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or UserFacingException;

    private static void CleanupLocked(string dataDirectory, Action<string>? progress)
    {
        try
        {
            var root = Path.GetFullPath(Path.Combine(dataDirectory, "audio"));
            var pointer = Path.Combine(root, "current");
            RecordingAnalysisStore.CheckPath(pointer);
            var current = File.ReadAllText(pointer).Trim();
            if (!Guid.TryParseExact(current, "N", out _)) return;
            var installations = Path.Combine(root, "installations");
            RecordingAnalysisStore.CheckPath(installations);
            var active = Path.Combine(installations, current);
            RecordingAnalysisStore.CheckPath(active);
            if (!File.Exists(Path.Combine(active, "ready.json"))) return;
            var ready = RecordingAnalysisStore.ReadJson<JsonElement>(Path.Combine(active, "ready.json"));
            if (ready.ValueKind != JsonValueKind.Object || !ready.TryGetProperty("fingerprint", out var fingerprint) ||
                fingerprint.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(fingerprint.GetString())) return;
            foreach (var directory in Directory.EnumerateDirectories(installations))
            {
                var id = Path.GetFileName(directory);
                if (id.Equals(current, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(id, "N", out _)) continue;
                try
                {
                    // Acquire before reading jobs: a creator holds a shared lease until
                    // its durable reference is saved. Queued and warm helpers also lease.
                    using var lease = GenerationLease(directory, true);
                    if (NeededByAnalysis(root, id)) continue;
                    CheckTree(directory);
                    Directory.Delete(directory, true);
                    progress?.Invoke("Removed unused audio installation " + id);
                }
                catch (Exception ex) when (CleanupError(ex)) { progress?.Invoke("Kept audio installation " + id + ": " + ex.Message); }
            }
        }
        catch (Exception ex) when (CleanupError(ex)) { progress?.Invoke("Audio cleanup deferred: " + ex.Message); }
    }

    private static bool NeededByAnalysis(string root, string installation)
    {
        var analyses = Path.Combine(root, "analyses");
        RecordingAnalysisStore.CheckPath(analyses);
        if (!Directory.Exists(analyses)) return false;
        foreach (var directory in Directory.EnumerateDirectories(analyses))
        {
            RecordingAnalysisStore.CheckPath(directory);
            var path = Path.Combine(directory, "job.json");
            if (!File.Exists(path)) continue; // Deleted job tombstone, or creator holding a lease.
            var job = RecordingAnalysisStore.ReadJson<RecordingAnalysisJob>(path);
            if (job.Version != 1 || job.Id != Path.GetFileName(directory) || !Guid.TryParseExact(job.Installation, "N", out _))
                throw new IOException("Unknown analysis metadata; preserving installations.");
            // CompletedWithGaps can still retry failed/uncertain components.
            if (job.State != "Completed" && job.Installation.Equals(installation, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void CheckTree(string directory)
    {
        RecordingAnalysisStore.CheckPath(directory);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(directory));
        while (pending.TryPop(out var parent))
        {
            // Enumeration supplies attributes without reopening every package file.
            foreach (var entry in parent.EnumerateFileSystemInfos())
            {
                var attributes = entry.Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked installation paths are unsupported.");
                if (entry is DirectoryInfo child) pending.Push(child);
            }
        }
    }
}
