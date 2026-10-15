using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentSmith.Core;

namespace AgentSmith.Storage;

/// <summary>Bounded, source-only inventory. Ignore rules are conservative: excluded directories are never traversed.</summary>
public sealed class RepositorySourceScanner
{
    private readonly string workspace;
    private readonly RepositoryScanLimits limits;
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".agentsmith", "bin", "obj", "node_modules", "vendor", "packages",
        "dist", "build", "out", "target", "coverage", ".next", ".cache", ".vs", ".idea", "__pycache__", ".venv", "venv"
    };
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".md", ".txt", ".rst", ".fs", ".fsproj",
        ".vb", ".vbproj", ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".config", ".editorconfig",
        ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs", ".html", ".css", ".scss", ".razor", ".vue", ".svelte",
        ".py", ".go", ".mod", ".rs", ".c", ".h", ".cpp", ".hpp", ".java", ".kt", ".swift", ".rb", ".php",
        ".sh", ".ps1", ".bat", ".cmd", ".sql", ".graphql", ".proto", ".dockerfile", ".gitignore", ".ignore"
    };
    public RepositorySourceScanner(string workspace, RepositoryScanLimits? limits = null)
    {
        this.workspace = RepositoryPaths.Canonical(workspace);
        RepositoryPaths.NoLinks(this.workspace);
        if (!Directory.Exists(this.workspace)) throw new UserFacingException("Repository workspace does not exist.");
        this.limits = limits ?? new();
        if (this.limits.MaxFiles is < 1 or > 2000 || this.limits.MaxBytes is < 1 or > 20_000_000 ||
            this.limits.MaxFileBytes is < 1 or > 1_000_000 || this.limits.MaxEvidenceCharacters is < 1 or > 256_000 ||
            this.limits.MaxLinesPerFile is < 1 or > 1000 || this.limits.MaxVisitedEntries is < 1 or > 100_000)
            throw new UserFacingException("Repository scan limits are outside supported bounds.");
    }

    public RepositoryScan Scan(string repositoryId, string path = ".", bool full = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var target = RepositoryPaths.Inside(workspace, path);
        RepositoryPaths.NoLinks(target);
        if (!File.Exists(target) && !Directory.Exists(target)) throw new UserFacingException("Scan path does not exist.");
        var sources = new List<RepositorySource>(); var reasons = new HashSet<string>(StringComparer.Ordinal);
        var rules = new List<IgnoreRule>(); int visited = 0, skipped = 0, characters = 0; long bytes = 0;
        var started = DateTimeOffset.UtcNow;
        bool stop = false;
        void Reason(string reason) => reasons.Add(reason);
        void LoadRules(string directory)
        {
            foreach (var name in new[] { ".gitignore", ".ignore" })
            {
                var file = System.IO.Path.Combine(directory, name);
                if (!File.Exists(file)) continue;
                try
                {
                    RepositoryPaths.NoLinks(file);
                    var info = new FileInfo(file);
                    if (info.Length > 64_000 || rules.Count >= 2000) { Reason("ignore-rule-limit"); stop = true; return; }
                    if (bytes + info.Length > limits.MaxBytes) { Reason("ignore-byte-limit"); stop = true; return; }
                    var data = ReadBounded(file, 64_000, ct);
                    if (bytes + data.Length > limits.MaxBytes) { Reason("ignore-byte-limit"); stop = true; return; }
                    bytes += data.Length;
                    foreach (var line in new UTF8Encoding(false, true).GetString(data).Split('\n'))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (rules.Count >= 2000) { Reason("ignore-rule-limit"); stop = true; return; }
                        // Unsupported glob syntax must not silently weaken privacy exclusions.
                        if (line.Length > 1000 || (!line.StartsWith('#') && line.IndexOfAny(['[', ']', '\\']) >= 0))
                        { Reason("unsupported-ignore-rule"); stop = true; return; }
                        // Git ignores unescaped trailing spaces; escape syntax is rejected above.
                        var rule = IgnoreRule.Create(Relative(directory), line.TrimEnd('\r', ' '));
                        if (rule is not null) rules.Add(rule);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or UserFacingException)
                { Reason("unreadable-ignore-rules"); stop = true; }
            }
        }
        string Relative(string file) => System.IO.Path.GetRelativePath(workspace, file).Replace('\\', '/');
        bool Ignored(string relative, bool directory)
        {
            bool ignored = false;
            foreach (var rule in rules) if (rule.Matches(relative, directory)) ignored = !rule.Negated;
            return ignored;
        }
        bool Excluded(string relative, bool directory)
        {
            if (relative.Split('/').Any(part => ExcludedDirectories.Contains(part))) return true;
            return Sensitive(relative);
        }
        void Visit(string file, bool directory)
        {
            ct.ThrowIfCancellationRequested();
            if (stop) return;
            if (visited >= limits.MaxVisitedEntries) { Reason("entry-limit"); stop = true; return; }
            visited++;
            var relative = Relative(file);
            try { RepositoryPaths.NoLinks(file); }
            catch (UserFacingException) { skipped++; Reason("links-skipped"); return; }
            if (Excluded(relative, directory) || Ignored(relative, directory)) { skipped++; return; }
            if (directory) { Walk(file); return; }
            var name = System.IO.Path.GetFileName(file);
            if (!SourceExtensions.Contains(System.IO.Path.GetExtension(file)) && name is not ("Dockerfile" or "Makefile" or "LICENSE" or ".editorconfig" or ".gitignore" or ".ignore")) { skipped++; return; }
            try
            {
                var info = new FileInfo(file);
                if (info.Length > limits.MaxFileBytes) { skipped++; Reason("file-byte-limit"); return; }
                if (sources.Count >= limits.MaxFiles || bytes + info.Length > limits.MaxBytes || characters >= limits.MaxEvidenceCharacters)
                { skipped++; Reason("scan-budget"); stop = true; return; }
                var content = ReadBounded(file, limits.MaxFileBytes, ct);
                if (bytes + content.Length > limits.MaxBytes) { Reason("scan-budget"); stop = true; return; }
                bytes += content.Length;
                var text = new UTF8Encoding(false, true).GetString(content).TrimStart('\uFEFF');
                if (text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')) || MemoryPolicy.HasSecret(text))
                { skipped++; Reason("binary-or-sensitive-content"); return; }
                if (text.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase))
                { skipped++; return; }
                var lines = text.Replace("\r\n", "\n").Split('\n');
                var selected = new List<string>(); int used = 0;
                foreach (var line in lines.Take(limits.MaxLinesPerFile))
                {
                    if (used + line.Length + 1 > Math.Min(12_000, limits.MaxEvidenceCharacters - characters)) break;
                    selected.Add(line); used += line.Length + 1;
                }
                if (selected.Count == 0) { skipped++; Reason("evidence-limit"); return; }
                if (selected.Count < lines.Length) Reason("evidence-truncated");
                var evidence = string.Join('\n', selected); characters += evidence.Length;
                sources.Add(new(relative, Hash(content), 1, selected.Count, evidence));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            { skipped++; Reason("unreadable-or-changing-source"); }
        }
        void Walk(string directory)
        {
            int previous = rules.Count;
            LoadRules(directory);
            if (!stop)
            {
                try
                {
                    foreach (var item in Directory.EnumerateFileSystemEntries(directory))
                    {
                        if (stop) break;
                        ct.ThrowIfCancellationRequested();
                        Visit(item, (File.GetAttributes(item) & FileAttributes.Directory) != 0);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Reason("unreadable-directory"); }
            }
            rules.RemoveRange(previous, rules.Count - previous);
        }
        // Scoped scans still inherit ignore rules and exclusions from their ancestors.
        var relativeTarget = Relative(target);
        var components = relativeTarget.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var ancestor = workspace; bool blocked = false;
        if (relativeTarget != ".")
        {
            LoadRules(workspace);
            for (int i = 0; i < components.Length - 1 && !stop; i++)
            {
                ancestor = System.IO.Path.Combine(ancestor, components[i]);
                if (Excluded(Relative(ancestor), true) || Ignored(Relative(ancestor), true)) { blocked = true; break; }
                LoadRules(ancestor);
            }
        }
        if (blocked) { skipped++; Reason("scope-excluded"); }
        else Visit(target, Directory.Exists(target));
        return new(Guid.NewGuid().ToString("N"), repositoryId, relativeTarget, full, started, sources.ToArray(),
            new(visited, sources.Count, bytes, characters, skipped, reasons.Count == 0 && !blocked, reasons.Order().ToArray()), limits);
    }

    internal static byte[] ReadBounded(string path, int maxBytes, CancellationToken ct)
    {
        RepositoryPaths.NoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maxBytes) throw new IOException("File exceeds scan byte limit.");
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = stream.Read(buffer, 0, Math.Min(buffer.Length, maxBytes + 1 - (int)output.Length))) > 0)
        {
            ct.ThrowIfCancellationRequested(); output.Write(buffer, 0, count);
            if (output.Length > maxBytes) throw new IOException("File changed beyond scan byte limit.");
        }
        RepositoryPaths.NoLinks(path);
        return output.ToArray();
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static bool Sensitive(string path)
    {
        return path.Split('/').Any(name =>
            name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(name, @"(?i)(secret|credential|password|token|id_rsa|id_ed25519|\.pem$|\.pfx$|\.p12$|\.key$|\.keystore$|\.lock$|lock\.json$|\.sqlite|\.db$|\.log$)", RegexOptions.CultureInvariant) ||
            name.Equals(".ssh", StringComparison.OrdinalIgnoreCase) || name.Equals(".aws", StringComparison.OrdinalIgnoreCase));
    }
    private sealed record IgnoreRule(string Base, Regex Pattern, bool Negated, bool DirectoryOnly)
    {
        public bool Matches(string path, bool directory)
        {
            var prefix = Base == "." ? "" : Base + "/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
            return (!DirectoryOnly || directory) && Pattern.IsMatch(path[prefix.Length..]);
        }
        public static IgnoreRule? Create(string directory, string line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) return null;
            bool negate = line.StartsWith('!'); if (negate) line = line[1..];
            if (line.Length == 0 || line.Length > 1000) return null;
            bool dir = line.EndsWith('/'); line = line.TrimEnd('/');
            bool anchored = line.StartsWith('/'); line = line.TrimStart('/');
            var expression = new StringBuilder(anchored || line.Contains('/') ? "^" : "(?:^|/)");
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < line.Length && line[i + 1] == '/') { expression.Append("(?:.*/)?"); i++; }
                    else expression.Append(".*");
                }
                else if (line[i] == '*') expression.Append("[^/]*");
                else if (line[i] == '?') expression.Append("[^/]");
                else expression.Append(Regex.Escape(line[i].ToString()));
            }
            expression.Append('$');
            return new(directory, new Regex(expression.ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50)), negate, dir);
        }
    }
}

internal static class RepositoryPaths
{
    internal static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal static string Canonical(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    internal static string Inside(string root, string relative)
    {
        if (System.IO.Path.IsPathRooted(relative)) throw new UserFacingException("Repository paths must be relative.");
        var result = Canonical(System.IO.Path.Combine(root, relative));
        if (!result.Equals(root, Comparison) && !result.StartsWith(System.IO.Path.EndsInDirectorySeparator(root) ? root : root + System.IO.Path.DirectorySeparatorChar, Comparison))
            throw new UserFacingException("Repository path escapes the workspace.");
        NoLinks(result); return result;
    }
    internal static void NoLinks(string path)
    {
        var current = Canonical(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new UserFacingException("Repository links and reparse points are not allowed.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            var parent = System.IO.Path.GetDirectoryName(current);
            if (parent == current || parent is null) break;
            current = parent;
        }
    }
}
