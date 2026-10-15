namespace AgentSmith.Tools;

/// <summary>Exclusive leases distinguish abandoned snapshots from another Smith process's live work.</summary>
internal sealed class AudioTemporary : IDisposable
{
    public string Directory { get; }
    private readonly FileStream lease;
    internal AudioTemporary(string dataDirectory)
    {
        var root = Path.GetFullPath(Path.Combine(dataDirectory, "audio", "jobs"));
        System.IO.Directory.CreateDirectory(root);
        foreach (var old in System.IO.Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(old), "N", out _) || (File.GetAttributes(old) & FileAttributes.ReparsePoint) != 0) continue;
            try
            {
                using (var held = new FileStream(Path.Combine(old, "lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    foreach (var file in System.IO.Directory.EnumerateFiles(old).Where(f => Path.GetFileName(f) != "lease")) File.Delete(file);
                File.Delete(Path.Combine(old, "lease")); System.IO.Directory.Delete(old);
            }
            catch (IOException) { /* A live owner or concurrent cleanup retains its lease. */ }
            catch (UnauthorizedAccessException) { /* Never broaden access for cleanup. */ }
        }
        Directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        lease = new FileStream(Path.Combine(Directory, "lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    }
    public void Dispose()
    {
        try { foreach (var file in System.IO.Directory.EnumerateFiles(Directory).Where(f => Path.GetFileName(f) != "lease")) File.Delete(file); }
        finally { lease.Dispose(); }
        File.Delete(Path.Combine(Directory, "lease")); System.IO.Directory.Delete(Directory);
    }
}
