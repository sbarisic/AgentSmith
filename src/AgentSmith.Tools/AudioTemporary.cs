namespace AgentSmith.Tools;

/// <summary>Exclusive leases distinguish abandoned snapshots from another Smith process's live work.</summary>
internal sealed class AudioTemporary : IDisposable
{
    public string Directory { get; }
    private readonly FileStream lease;
    internal AudioTemporary(string dataDirectory, string folder = "jobs")
    {
        var root = Path.GetFullPath(Path.Combine(dataDirectory, "audio", folder));
        System.IO.Directory.CreateDirectory(root);
        if ((File.GetAttributes(Path.GetDirectoryName(root)!) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked temporary audio directory is unsupported.");
        foreach (var old in System.IO.Directory.EnumerateDirectories(root).Take(128))
        {
            try
            {
                if (!Guid.TryParseExact(Path.GetFileName(old), "N", out _) || (File.GetAttributes(old) & FileAttributes.ReparsePoint) != 0) continue;
                // A creator may have made the directory but not opened its lease yet.
                if (!File.Exists(Path.Combine(old, "lease")) || (File.GetAttributes(Path.Combine(old, "lease")) & FileAttributes.ReparsePoint) != 0) continue;
                using (var held = new FileStream(Path.Combine(old, "lease"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
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
        try { File.Delete(Path.Combine(Directory, "lease")); System.IO.Directory.Delete(Directory); }
        catch (IOException) { /* Another cleanup may hold or have removed the released lease. It can finish cleanup. */ }
    }
}
