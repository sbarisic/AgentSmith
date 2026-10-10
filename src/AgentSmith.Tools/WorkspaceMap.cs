using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using AgentSmith.Core;

namespace AgentSmith.Tools;

public static class WorkspaceMap
{
    public static void Validate(ToolExecutionContext ctx, JsonNode args)
    {
        if (args["depth"]!.GetValue<int>() is < 0 or > 8 || args["maxEntries"]!.GetValue<int>() is < 0 or > 500)
            throw new UserFacingException("Map depth is 0..8 and maxEntries is 0..500; zero selects defaults (3, 200).");
        if (!Directory.Exists(WorkspaceTools.Resolve(ctx.Workspace, args["path"]!.GetValue<string>()))) throw new UserFacingException("Map path must be a directory.");
    }
    public static ToolOutput Execute(ToolExecutionContext ctx, ToolCall call)
    {
        var args = JsonNode.Parse(call.Prepared?.Arguments ?? call.Arguments)!; Validate(ctx, args);
        var path = args["path"]!.GetValue<string>(); var root = WorkspaceTools.Resolve(ctx.Workspace, path);
        var depth = args["depth"]!.GetValue<int>(); if (depth == 0) depth = 3;
        var max = args["maxEntries"]!.GetValue<int>(); if (max == 0) max = 200;
        var entries = new List<object>(); var projects = new List<object>(); var issues = new List<string>();
        var extensions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); var documents = new List<string>(); var candidates = new List<string>();
        var queue = new Queue<(string Path, int Depth)>(); queue.Enqueue((root, 0)); var visited = 0; var depthLimited = false; var excluded = 0; var pathCharacters = 0; var sizeLimited = false;
        while (visited < 10000 && !sizeLimited && queue.TryDequeue(out var current))
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            try
            {
                // Resolve again before entering each directory. Never traverse links discovered after enumeration.
                WorkspaceTools.Resolve(ctx.Workspace, Path.GetRelativePath(ctx.Workspace, current.Path));
                foreach (var item in Directory.EnumerateFileSystemEntries(current.Path))
                {
                    ctx.Cancellation.ThrowIfCancellationRequested(); if (++visited > 10000) break;
                    var relative = Path.GetRelativePath(ctx.Workspace, item);
                    pathCharacters += relative.Length; if (pathCharacters > 262144) { sizeLimited = true; break; }
                    try
                    {
                        WorkspaceTools.Resolve(ctx.Workspace, relative);
                        var directory = Directory.Exists(item);
                        entries.Add(new { path = relative, kind = directory ? "directory" : "file", depth = current.Depth });
                        if (directory) { if (current.Depth < depth - 1) queue.Enqueue((item, current.Depth + 1)); else depthLimited = true; continue; }
                        var ext = Path.GetExtension(item).ToLowerInvariant(); extensions[ext] = extensions.GetValueOrDefault(ext) + 1;
                        if (ext is ".md" or ".markdown" or ".rst" or ".adoc" or ".txt") documents.Add(relative);
                        if (Path.GetFileName(item) is "Program.cs" or "Startup.cs") candidates.Add(relative);
                        if (ext is ".csproj" or ".sln" or ".slnx")
                        {
                            if (projects.Count < 32) projects.Add(Manifest(ctx.Workspace, item, relative));
                            else if (!issues.Contains("Manifest detail limit reached (32).")) issues.Add("Manifest detail limit reached (32).");
                        }
                    }
                    catch (UserFacingException) { excluded++; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or DecoderFallbackException or RegexMatchTimeoutException)
                    { if (issues.Count < 100) issues.Add(relative + ": unreadable or invalid manifest/file."); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UserFacingException)
            { if (issues.Count < 100) issues.Add(Path.GetRelativePath(ctx.Workspace, current.Path) + ": directory unavailable."); }
        }
        var entryScanLimited = visited > 10000 || visited >= 10000 && queue.Count > 0;
        var full = new { kind = "workspace-map", path, depth, visited = Math.Min(visited,10000), scanLimited = entryScanLimited || sizeLimited, entryScanLimited, sizeLimited, depthLimited, excluded,
            entries, extensions, documents, entryPointCandidates = candidates, projects, issues,
            note = "Static declarations only; no MSBuild evaluation or semantic symbol analysis. Counts cover scanned files only. Narrow the path to inspect omitted subtrees." };
        var artifact = ToolArtifacts.Save(ctx, "workspace-map", Json.Serialize(full));
        // Keep summary small even for long paths/manifests; full bounded scan remains searchable.
        var summary = JsonNode.Parse(Json.Serialize(new { kind = "workspace-map", path = Clip(path), depth, entries = entries.Take(max), totalEntries = entries.Count,
            scanLimited = entryScanLimited || sizeLimited, entryScanLimited, sizeLimited, depthLimited, excluded, artifactId = artifact.Id, projects = projects.Take(8), issues = issues.Take(8), note = full.note }))!.AsObject();
        while (Encoding.UTF8.GetByteCount(summary.ToJsonString()) > 8192)
        {
            var list = new[] { "projects", "entries", "issues" }.Select(k => summary[k]!.AsArray()).FirstOrDefault(a => a.Count > 0);
            if (list is null) break;
            list.RemoveAt(list.Count - 1);
        }
        summary["omittedEntries"] = entries.Count - summary["entries"]!.AsArray().Count;
        summary["omittedProjects"] = projects.Count - summary["projects"]!.AsArray().Count;
        summary["omittedIssues"] = issues.Count - summary["issues"]!.AsArray().Count;
        return new(summary.ToJsonString());
    }
    private static object Manifest(string workspace, string path, string relative)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 262144) return new { path = relative, limitation = "Manifest exceeds 256 KiB." };
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
        var text = new UTF8Encoding(false,true).GetString(bytes).TrimStart('\uFEFF');
        string Reference(string value)
        {
            if (value.Contains("$(", StringComparison.Ordinal)) return value + " [unresolved]";
            try
            {
                var target = Path.GetRelativePath(workspace, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, value)));
                WorkspaceTools.Resolve(workspace, target, true); return Clip(target);
            }
            catch (Exception ex) when (ex is UserFacingException or ArgumentException or IOException) { return "[outside scope or invalid]"; }
        }
        if (Path.GetExtension(path).Equals(".sln", StringComparison.OrdinalIgnoreCase))
            return new { path = relative, projects = Regex.Matches(text, "(?m)^Project\\([^\\r\\n]*=\\s*\"[^\"]*\",\\s*\"([^\"]+)\"", RegexOptions.None, TimeSpan.FromSeconds(1)).Select(m => Reference(m.Groups[1].Value)).Take(100).ToArray() };
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 262144 });
        var doc = XDocument.Load(reader);
        var declarations = doc.Descendants().Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks" or "OutputType" or "IsTestProject" or "PackageReference" or "ProjectReference" || e.Name.LocalName == "Project" && e.Attribute("Path") is not null)
            .Take(100).Select(e => new { kind = e.Name.LocalName, value = Clip(e.Attribute("Include")?.Value ?? e.Attribute("Path")?.Value ?? e.Value),
                version = Clip(e.Attribute("Version")?.Value ?? e.Elements().FirstOrDefault(c => c.Name.LocalName == "Version")?.Value ?? ""),
                condition = Clip(string.Join("; ", e.AncestorsAndSelf().Select(a => a.Attribute("Condition")?.Value).Where(v => v is not null))),
                reference = e.Name.LocalName == "ProjectReference" || e.Attribute("Path") is not null ? Reference(e.Attribute("Include")?.Value ?? e.Attribute("Path")!.Value) : null }).ToArray();
        return new { path = relative, declarations, conditionalValuesNotEvaluated = true };
    }
    private static string Clip(string value) => value[..Math.Min(value.Length,500)];
}

public static class ToolArtifacts
{
    public static ArtifactRecord Save(ToolExecutionContext ctx, string kind, string text)
    {
        var path = ArtifactAccess.Validate(ctx, Path.Combine(ctx.ArtifactDirectory, Guid.NewGuid().ToString("N") + ".txt"));
        Directory.CreateDirectory(ctx.ArtifactDirectory);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { file.Write(Encoding.UTF8.GetBytes(text)); file.Flush(true); }
        return ArtifactAccess.Register(ctx, path, kind);
    }
}
