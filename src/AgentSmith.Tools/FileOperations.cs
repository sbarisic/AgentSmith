using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentSmith.Core;

namespace AgentSmith.Tools;

public static class FileOperations
{
    public const int MaxBytes = 256 * 1024;
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string Decode(byte[] bytes)
    {
        if (bytes.Contains((byte)0)) throw new UserFacingException("Binary and UTF-16 files are unsupported.");
        try { var text = new UTF8Encoding(false, true).GetString(bytes); return text.StartsWith('\uFEFF') ? text[1..] : text; }
        catch (DecoderFallbackException) { throw new UserFacingException("Only UTF-8 files are supported."); }
    }
    public static byte[] ReadBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxBytes) throw new UserFacingException("File exceeds the 256 KiB limit.");
        using var buffer = new MemoryStream();
        var bytes = new byte[8192]; int n;
        while ((n = stream.Read(bytes)) > 0) { if (buffer.Length + n > MaxBytes) throw new UserFacingException("File exceeds the limit."); buffer.Write(bytes, 0, n); }
        var result = buffer.ToArray(); Decode(result); return result;
    }
    private static byte[] Encode(string text, bool bom = false)
    {
        var bytes = new UTF8Encoding(false, true).GetBytes(text);
        if (bom) bytes = [0xef, 0xbb, 0xbf, .. bytes];
        if (bytes.Length > MaxBytes) throw new UserFacingException("Result exceeds the 256 KiB file limit.");
        Decode(bytes); return bytes;
    }
    public static MutationRecord Prepare(ToolExecutionContext ctx, ToolCall call, JsonObject args, bool userExport = false)
    {
        string S(string key) => args[key]!.GetValue<string>();
        if (!userExport) WorkflowPolicy.CheckFiles(ctx.Session, call.Name == "file_move" ? [S("source"), S("destination")] : [S("path")]);
        var record = new MutationRecord { RunId = ctx.Run.Id, CallId = call.Id, Kind = call.Name };
        var relative = call.Name == "file_move" ? S("source") : S("path");
        var path = WorkspaceTools.Resolve(ctx.Workspace, relative, true);
        if (path == ctx.Workspace || Directory.Exists(path)) throw new UserFacingException("A file path is required.");
        byte[]? before = null;
        if (call.Name == "file_create") { if (File.Exists(path)) throw Failures.Reject("destination_exists", "Destination already exists; it will not be overwritten.", "Choose a new path, or read the existing file and prepare an explicit edit.", relative); }
        else
        {
            before = ReadBytes(path);
            if (!Hash(before).Equals(S("expectedHash"), StringComparison.OrdinalIgnoreCase)) throw Failures.Reject("stale_file_hash", "File changed since it was read.", "Read it again and prepare the edit using its current content and hash.", relative);
        }
        var file = new FileTransition { Path = relative, Before = before, Attributes = before is null ? FileAttributes.Normal : File.GetAttributes(path), LastWriteUtc = before is null ? DateTime.UtcNow : File.GetLastWriteTimeUtc(path) };
        file.After = call.Name switch
        {
            "file_create" => Encode(S("content")),
            "file_apply_patch" => Patch(before!, S("patch")),
            _ => null
        };
        record.Files.Add(file);
        if (call.Name == "file_apply_patch")
        {
            var headers = S("patch").Replace("\r\n", "\n").Split('\n');
            if (headers[0].StartsWith("--- "))
                foreach (var header in headers.Take(2))
                {
                    var name = header[4..].Split('\t')[0].Replace('\\', '/');
                    if (name.StartsWith("a/") || name.StartsWith("b/")) name = name[2..];
                    if (!WorkspaceTools.Resolve(ctx.Workspace, name).Equals(path, StringComparison.OrdinalIgnoreCase)) throw new UserFacingException("Patch header does not identify the requested file.");
                }
        }
        if (call.Name == "file_move")
        {
            var destination = WorkspaceTools.Resolve(ctx.Workspace, S("destination"), true);
            if (File.Exists(destination) || Directory.Exists(destination)) throw Failures.Reject("destination_exists", "Destination already exists; it will not be overwritten.", "Choose a new destination path.", S("destination"));
            record.Files.Add(new() { Path = S("destination"), After = before, Attributes = file.Attributes, LastWriteUtc = file.LastWriteUtc });
        }
        ctx.Session.Mutations.Add(record); return record;
    }
    public static byte[] Patch(byte[] original, string patch)
    {
        var text = Decode(original);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (text.Replace("\r\n", "").Contains('\r') || newline == "\r\n" && text.Replace("\r\n", "").Contains('\n')) throw new UserFacingException("Mixed line endings cannot be patched; normalize explicitly first.");
        var source = text.Replace("\r\n", "\n").Split('\n').ToList();
        var finalNewline = text.EndsWith('\n');
        if (finalNewline || text.Length == 0) source.RemoveAt(source.Count - 1);
        var lines = patch.Replace("\r\n", "\n").Split('\n');
        var output = new List<string>(); var endings = new List<bool>(); var position = 0; var index = 0; var hunks = 0;
        if (lines.Length >= 2 && lines[0].StartsWith("--- ") && lines[1].StartsWith("+++ ")) index = 2;
        while (index < lines.Length && lines[index].Length > 0)
        {
            var match = Regex.Match(lines[index++], @"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(?:.*)$", RegexOptions.CultureInvariant);
            if (!match.Success) throw new UserFacingException("Expected a single-file unified patch with @@ hunks.");
            var oldCount = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1;
            var newCount = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 1;
            var start = int.Parse(match.Groups[1].Value) - (oldCount == 0 ? 0 : 1);
            if (start < position || start > source.Count) throw new UserFacingException("Patch hunks overlap or are out of range.");
            while (position < start) { output.Add(source[position]); endings.Add(position < source.Count - 1 || finalNewline); position++; }
            var newStart = int.Parse(match.Groups[3].Value) - (newCount == 0 ? 0 : 1);
            if (newStart != output.Count) throw new UserFacingException("Patch new-line position is inconsistent.");
            var oldSeen = 0; var newSeen = 0; char last = '\0';
            while (index < lines.Length && !lines[index].StartsWith("@@ ") && !(index == lines.Length - 1 && lines[index].Length == 0))
            {
                var line = lines[index++];
                if (line == "\\ No newline at end of file")
                {
                    if (last == '\0') throw new UserFacingException("Invalid no-newline marker.");
                    if (last is '+' or ' ') endings[^1] = false;
                    continue;
                }
                if (line.Length == 0 || line[0] is not (' ' or '-' or '+')) throw new UserFacingException("Invalid unified patch line.");
                last = line[0]; var value = line[1..];
                if (last is ' ' or '-')
                { if (position >= source.Count || source[position++] != value) throw Failures.Reject("patch_context_mismatch", "Patch context does not match the current file.", "Read the file again and use complete, exact lines including whitespace in the patch."); oldSeen++; }
                if (last is ' ' or '+') { output.Add(value); endings.Add(last == '+' || position < source.Count || finalNewline); newSeen++; }
            }
            if (oldSeen != oldCount || newSeen != newCount) throw new UserFacingException("Patch hunk counts do not match.");
            hunks++;
        }
        if (hunks == 0 || lines.Skip(index).Any(l => l.Length > 0)) throw new UserFacingException("Invalid or empty patch.");
        while (position < source.Count) { output.Add(source[position]); endings.Add(position < source.Count - 1 || finalNewline); position++; }
        if (endings.Take(Math.Max(0, endings.Count - 1)).Any(e => !e)) throw new UserFacingException("No-newline marker is only valid at the end of the file.");
        return Encode(string.Join(newline, output) + (endings.Count > 0 && endings[^1] ? newline : ""), original.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
    }
    public static bool Matches(ToolExecutionContext ctx, FileTransition file, byte[]? expected)
    {
        var path = WorkspaceTools.Resolve(ctx.Workspace, file.Path, true);
        if (expected is null) return !File.Exists(path) && !Directory.Exists(path);
        return File.Exists(path) && Hash(ReadBytes(path)) == Hash(expected);
    }
    public static ToolOutput Apply(ToolExecutionContext ctx, MutationRecord record, bool userExport = false)
    {
        ctx.Cancellation.ThrowIfCancellationRequested();
        if (!userExport) WorkflowPolicy.CheckFiles(ctx.Session, record.Files.Select(f => f.Path));
        if (!record.Files.All(f => Matches(ctx, f, f.Before))) throw new UserFacingException("Checkpoint conflict: a file changed before execution.");
        var timer = Stopwatch.StartNew();
        foreach (var file in record.Files.Where(f => f.After is not null))
        {
            var path = WorkspaceTools.Resolve(ctx.Workspace, file.Path, true);
            var missing = new Stack<string>(); var dir = Path.GetDirectoryName(path)!;
            while (!Directory.Exists(dir)) { missing.Push(dir); dir = Path.GetDirectoryName(dir)!; }
            while (missing.TryPop(out var next))
            {
                WorkspaceTools.Resolve(ctx.Workspace, Path.GetRelativePath(ctx.Workspace, next), true);
                if (OperatingSystem.IsWindows())
                {
                    if (!CreateDirectoryW(next, IntPtr.Zero)) { if (Marshal.GetLastWin32Error() == 183) continue; throw new IOException("Cannot create parent directory."); }
                }
                else Directory.CreateDirectory(next);
                record.CreatedDirectories.Add(Path.GetRelativePath(ctx.Workspace, next));
                ctx.Save("mutation.directory_created", new { record.Id });
            }
        }
        // No cancellation inside the effect/result boundary: reconcile a failed operation instead of retrying it.
        if (record.Kind is "file_move" or "undo_move")
        {
            var from = record.Files.Single(f => f.After is null); var to = record.Files.Single(f => f.Before is null);
            if (!userExport) WorkflowPolicy.CheckFiles(ctx.Session, record.Files.Select(f => f.Path));
            if (!record.Files.All(f => Matches(ctx, f, f.Before))) throw new UserFacingException("Move conflict.");
            File.Move(WorkspaceTools.Resolve(ctx.Workspace, from.Path), WorkspaceTools.Resolve(ctx.Workspace, to.Path, true));
        }
        else foreach (var file in record.Files)
        {
            var path = WorkspaceTools.Resolve(ctx.Workspace, file.Path, true);
            if (!Matches(ctx, file, file.Before)) throw new UserFacingException("File changed before replacement.");
            if (file.After is null) File.Delete(path);
            else
            {
                var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".smith-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(file.After); stream.Flush(true); }
                    if (!Matches(ctx, file, file.Before)) throw new UserFacingException("File changed before replacement.");
                    if (file.Before is null) File.Move(temporary, path);
                    else File.Replace(temporary, path, null);
                    File.SetAttributes(path, file.Attributes);
                    if (record.UndoOf is not null) File.SetLastWriteTimeUtc(path, file.LastWriteUtc);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        record.DurationMilliseconds = timer.Elapsed.TotalMilliseconds;
        record.State = "Applied";
        ctx.Save("mutation.applied", new { record.Id, record.DurationMilliseconds });
        return Result(record);
    }
    public static ToolOutput Result(MutationRecord record) => new(Json.Serialize(new { checkpointId = record.Id, state = record.State, paths = record.Files.Select(f => f.Path), durationMilliseconds = record.DurationMilliseconds, diff = Diff(record) }));
    public static string Diff(MutationRecord record)
    {
        var text = new StringBuilder();
        foreach (var file in record.Files)
        {
            text.AppendLine("--- " + file.Path).AppendLine("+++ " + file.Path);
            var before = file.Before is null ? [] : Decode(file.Before).Split('\n');
            var after = file.After is null ? [] : Decode(file.After).Split('\n');
            var prefix = 0; while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
            var suffix = 0; while (suffix < before.Length - prefix && suffix < after.Length - prefix && before[^(suffix + 1)] == after[^(suffix + 1)]) suffix++;
            text.AppendLine($"@@ -{prefix + 1},{before.Length - prefix - suffix} +{prefix + 1},{after.Length - prefix - suffix} @@");
            foreach (var line in before.Skip(prefix).Take(before.Length - prefix - suffix)) { if (text.Length > 12000) break; text.Append('-').AppendLine(line); }
            foreach (var line in after.Skip(prefix).Take(after.Length - prefix - suffix)) { if (text.Length > 12000) break; text.Append('+').AppendLine(line); }
        }
        return text.Length <= 12000 ? text.ToString() : text.ToString(0, 12000) + "\n[diff truncated]";
    }
    public static ToolOutput Undo(ToolExecutionContext ctx, string id)
    {
        if (ctx.Session.Mode < PermissionMode.Edit) throw new UserFacingException("Undo requires Edit or Execute mode.");
        var original = ctx.Session.Mutations.SingleOrDefault(m => m.Id == id && m.State == "Applied") ?? throw new UserFacingException("Applied checkpoint not found.");
        WorkflowPolicy.CheckFiles(ctx.Session, original.Files.Select(f => f.Path));
        if (ctx.Session.Mutations.Any(m => m.UndoOf == id && m.State is "Applied" or "Prepared")) throw new UserFacingException("Checkpoint already has an undo operation.");
        if (!original.Files.All(f => Matches(ctx, f, f.After))) throw new UserFacingException("Undo conflict: newer file changes are preserved.");
        var reverse = new MutationRecord { RunId = ctx.Run.Id, CallId = "undo-" + Guid.NewGuid().ToString("N"), Kind = original.Kind is "file_move" or "undo_move" ? "undo_move" : "undo", UndoOf = id,
            Files = original.Files.Select(f => new FileTransition { Path = f.Path, Before = f.After, After = f.Before, Attributes = f.Attributes, LastWriteUtc = f.LastWriteUtc }).ToList() };
        ctx.Session.Mutations.Add(reverse); ctx.Save("mutation.prepared", new { reverse.Id });
        var result = Apply(ctx, reverse);
        foreach (var relative in original.CreatedDirectories.AsEnumerable().Reverse())
        {
            var path = WorkspaceTools.Resolve(ctx.Workspace, relative, true);
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path, false);
        }
        return result;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectoryW(string path, IntPtr security);
}
