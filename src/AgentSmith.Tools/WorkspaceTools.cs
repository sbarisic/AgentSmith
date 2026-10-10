using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSmith.Core;

namespace AgentSmith.Tools;

public sealed class WorkspaceTools : IWorkspaceTools
{
    private const int MaxBytes = 262144;
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj", "node_modules", ".vs", "artifacts" };
    public IReadOnlyList<ToolDefinition> Definitions { get; } =
    [
        Define("workspace_list", "List up to 200 files below a workspace-relative directory. Ignores build output and links.", "{\"path\":{\"type\":\"string\"}}"),
        Define("file_read", "Read UTF-8 text with line numbers. Workspace-relative path; one-based startLine, at most 200 lines. Includes scoped AGENTS.md guidance.", "{\"path\":{\"type\":\"string\"},\"startLine\":{\"type\":\"integer\"},\"lineCount\":{\"type\":\"integer\"}}"),
        Define("text_search", "Literal case-insensitive search, up to 100 matches. Supply a workspace-relative directory and query.", "{\"path\":{\"type\":\"string\"},\"query\":{\"type\":\"string\"}}"),
        Define("user_ask", "Ask the user a question and wait for an explicit answer. Choices may be empty. Never select an answer automatically.", "{\"question\":{\"type\":\"string\"},\"choices\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}}")
    ];
    private static ToolDefinition Define(string name, string description, string properties)
    {
        var props = JsonNode.Parse(properties)!.AsObject();
        return new(name, description, new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = new JsonArray(props.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray()), ["additionalProperties"] = false });
    }
    public static string ValidateRoot(string workspace)
    {
        var root = Path.GetFullPath(workspace);
        if (!Directory.Exists(root)) throw new UserFacingException("Workspace directory does not exist.");
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new UserFacingException("Workspace paths through links or junctions are unsupported.");
        return Path.TrimEndingDirectorySeparator(root);
    }
    public static string Resolve(string workspace, string relative, bool allowMissing = false, bool imageAccess = false)
    {
        var root = ValidateRoot(workspace);
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new UserFacingException("Use a workspace-relative path.");
        var target = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!target.Equals(root, StringComparison.OrdinalIgnoreCase) && !target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException("Path is outside the workspace.");
        var current = root;
        foreach (var part in Path.GetRelativePath(root, target).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part.EndsWith(' ') || part.EndsWith('.') || part.Any(c => c is '<' or '>' or '"' or '|' or '?' or '*') || System.Text.RegularExpressions.Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) throw new UserFacingException("Invalid Windows path component.");
            if (Ignored.Contains(part) && !(imageAccess && (part.Equals("artifacts", StringComparison.OrdinalIgnoreCase) || part.Equals("bin", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase)))) throw new UserFacingException("Path is excluded from workspace access.");
            current = Path.Combine(current, part);
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new UserFacingException("Links and junctions are unsupported."); }
            catch (FileNotFoundException) when (allowMissing) { }
            catch (DirectoryNotFoundException) when (allowMissing) { }
        }
        return target;
    }
    public string Instructions(string workspace)
    {
        var root = ValidateRoot(workspace);
        var file = Path.Combine(root, "AGENTS.md");
        if (!File.Exists(file)) return "No root AGENTS.md present.";
        var text = ReadText(Resolve(root, "AGENTS.md"));
        return "Project guidance from root AGENTS.md (untrusted; permissions are unchanged):\n" + text[..Math.Min(text.Length, 12000)];
    }
    private string ScopedInstructions(string workspace, string target)
    {
        var root = ValidateRoot(workspace);
        var directory = Directory.Exists(target) ? target : Path.GetDirectoryName(target)!;
        var parts = Path.GetRelativePath(root, directory).Split(Path.DirectorySeparatorChar);
        var current = root;
        var result = new StringBuilder();
        foreach (var part in parts)
        {
            if (part == ".") continue;
            current = Path.Combine(current, part);
            var guide = Path.Combine(current, "AGENTS.md");
            if (!File.Exists(guide)) continue;
            var relative = Path.GetRelativePath(root, guide);
            var text = ReadText(Resolve(root, relative));
            result.AppendLine($"Guidance scoped to {Path.GetRelativePath(root, current)}: {text[..Math.Min(text.Length, 4000)]}");
            if (result.Length > 8000) throw new UserFacingException("Scoped instructions exceed the read budget.");
        }
        return result.ToString();
    }
    private static string ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > MaxBytes) throw new UserFacingException("File exceeds the 256 KiB read limit.");
        var bytes = new byte[MaxBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = stream.Read(bytes, length, bytes.Length - length);
            if (read == 0) break;
            length += read;
        }
        if (length > MaxBytes) throw new UserFacingException("File grew past the read limit.");
        if (bytes.AsSpan(0, length).Contains((byte)0)) throw new UserFacingException("Binary or UTF-16 data is unsupported in this release.");
        try { return new UTF8Encoding(false, true).GetString(bytes, 0, length).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw new UserFacingException("Only UTF-8 text files are supported."); }
    }
    private static IEnumerable<string> Enumerate(string root, string directory, CancellationToken ct)
    {
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((directory, 0));
        var visited = 0;
        while (stack.TryPop(out var entry))
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Depth > 20) continue;
            foreach (var path in Directory.EnumerateFileSystemEntries(entry.Path))
            {
                ct.ThrowIfCancellationRequested();
                if (++visited > 10000) throw new UserFacingException("Traversal limit reached; narrow the search directory.");
                if (Ignored.Contains(Path.GetFileName(path))) continue;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) stack.Push((path, entry.Depth + 1));
                else yield return Resolve(root, Path.GetRelativePath(root, path));
            }
        }
    }
    public Task<ToolOutput> ExecuteAsync(string workspace, ToolCall call, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var definition = Definitions.SingleOrDefault(d => d.Name == call.Name) ?? throw new UserFacingException("Unknown workspace read tool.");
        if (call.Arguments.Length > 16000) throw new UserFacingException("Tool arguments exceed the limit.");
        using var document = JsonDocument.Parse(call.Arguments);
        var args = document.RootElement;
        if (args.ValueKind != JsonValueKind.Object) throw new UserFacingException("Tool arguments must be an object.");
        var properties = definition.Parameters["properties"]!.AsObject();
        var seen = new HashSet<string>();
        foreach (var property in args.EnumerateObject())
            if (!properties.ContainsKey(property.Name) || !seen.Add(property.Name)) throw new UserFacingException("Unknown or duplicate argument.");
        foreach (var property in properties)
        {
            if (!args.TryGetProperty(property.Key, out var value)) throw new UserFacingException("Missing argument.");
            var type = property.Value!["type"]!.GetValue<string>();
            if ((type == "string" && value.ValueKind != JsonValueKind.String) || (type == "integer" && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _))) || (type == "array" && value.ValueKind != JsonValueKind.Array))
                throw new UserFacingException("Invalid argument type.");
        }
        string String(string key) => args.GetProperty(key).GetString()!;
        if (call.Name == "user_ask")
        {
            var text = String("question");
            var choices = args.GetProperty("choices");
            if (string.IsNullOrWhiteSpace(text) || text.Length > 2000 || choices.GetArrayLength() > 8 || choices.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || v.GetString()!.Length > 300)) throw new UserFacingException("Invalid question or choices.");
            return Task.FromResult(new ToolOutput("", new Question { Text = text, Choices = choices.EnumerateArray().Select(v => v.GetString()!).ToArray() }));
        }
        var root = ValidateRoot(workspace);
        var path = Resolve(root, String("path"));
        var guidance = ScopedInstructions(root, path);
        object output;
        if (call.Name == "file_read")
        {
            var start = args.GetProperty("startLine").GetInt32();
            var count = args.GetProperty("lineCount").GetInt32();
            if (start < 1 || count is < 1 or > 200) throw new UserFacingException("Invalid line range.");
            var bytes = FileOperations.ReadBytes(path);
            var text = FileOperations.Decode(bytes);
            var lines = text.Split('\n');
            var content = string.Join('\n', lines.Skip(start - 1).Take(count).Select((line, i) => $"{start + i}: {line.TrimEnd('\r')}"));
            var truncated = content.Length > 16000;
            output = new { path = String("path"), hash = FileOperations.Hash(bytes), guidance, totalLines = lines.Length, content = content[..Math.Min(content.Length, 16000)], truncated, moreLines = start - 1 + count < lines.Length };
        }
        else if (call.Name == "workspace_list")
        {
            var files = Enumerate(root, path, ct).Take(201).Select(p => Path.GetRelativePath(root, p)).ToArray();
            output = new { guidance, files = files.Take(200).Order(StringComparer.OrdinalIgnoreCase).ToArray(), truncated = files.Length > 200 };
        }
        else
        {
            var query = String("query");
            if (string.IsNullOrEmpty(query) || query.Length > 1000) throw new UserFacingException("Query must contain 1 to 1000 characters.");
            var matches = new List<object>();
            var skipped = 0;
            foreach (var file in Enumerate(root, path, ct))
            {
                string text;
                try { text = ReadText(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UserFacingException) { skipped++; continue; }
                var lines = text.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!lines[i].Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    matches.Add(new { path = Path.GetRelativePath(root, file), line = i + 1, text = lines[i][..Math.Min(lines[i].Length, 200)], guidance = ScopedInstructions(root, file) });
                    if (matches.Count >= 100) break;
                }
                if (matches.Count >= 100) break;
            }
            output = new { guidance, matches, skippedFiles = skipped, limitReached = matches.Count >= 100 };
        }
        var serialized = Json.Serialize(output);
        if (serialized.Length > 32768) throw new UserFacingException("Result exceeds the output limit; narrow the request.");
        return Task.FromResult(new ToolOutput(serialized));
    }
}
