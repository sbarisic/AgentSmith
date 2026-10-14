using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSmith.CodeAnalysis;
using AgentSmith.Core;

namespace AgentSmith.Tools;

public sealed class NavigationTools : IDisposable
{
    private readonly string lifetime = Guid.NewGuid().ToString("N");
    private readonly SourceSymbols syntax = new();
    private readonly Dictionary<string, (AnalysisBundle Bundle, SemanticAnalysis Analysis, string Inventory)> loaded = [];
    private readonly HashSet<string> invalidated = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    public static bool CanLoad(Session s) => s.ParentConversationId is null && s.Workflow is WorkflowMode.Work or WorkflowMode.Document
        && WorkflowPolicy.Enabled(s, new("csharp_load", "", new(), PermissionMode.Execute, true, ToolEffect.Process));

    public PreparedOperation Prepare(ToolExecutionContext ctx, JsonObject args, ProcessManager processes)
    {
        if (!CanLoad(ctx.Session)) throw new UserFacingException("Project loading requires an Execute parent in Work or Document.");
        var project = WorkspaceTools.Resolve(ctx.Workspace, args["project"]!.ToString());
        if (!File.Exists(project) || Path.GetExtension(project).ToLowerInvariant() is not (".csproj" or ".sln" or ".slnx")) throw new UserFacingException("Select a workspace .csproj, .sln or .slnx file.");
        foreach (var key in new[] { "configuration", "framework" })
            if (args[key]!.ToString().Length > 80 || args[key]!.ToString().Any(c => !char.IsLetterOrDigit(c) && c is not ('.' or '-' or '_'))) throw new UserFacingException("Invalid configuration or framework.");
        // Never evict a snapshot still granted to a running worker.
        var previous = ctx.Session.NavigationSnapshots.Where(s => s.OwnerLifetime == lifetime && s.State != "Unavailable").ToArray();
        NavigationSnapshot? replace = null;
        if (previous.Length >= 2)
        {
            replace = previous.FirstOrDefault(s => s.Project.Equals(args["project"]!.ToString(), StringComparison.OrdinalIgnoreCase)
                && s.State != "Loading" && (ctx.Store is not IWorkerStore workerStore || !workerStore.Workers(ctx.Session.Id).Any(w => w.Active && w.NavigationSnapshotIds.Contains(s.Id))));
            if (replace is null) throw new UserFacingException("Two navigation snapshots are already loaded. Finish workers holding them; reload one of the same projects to replace its snapshot.");
        }
        var snapshot = new NavigationSnapshot { OwnerLifetime = lifetime, Project = args["project"]!.ToString(),
            Configuration = args["configuration"]!.ToString() is { Length: > 0 } config ? config : "Debug", Framework = args["framework"]!.ToString() };
        snapshot.BundlePath = Path.Combine(ctx.ArtifactDirectory, "navigation-" + snapshot.Id + ".json");
        var entry = Path.Combine(AppContext.BaseDirectory, "smith.dll");
        if (!File.Exists(entry) || Path.GetFileNameWithoutExtension(entry) != "smith") throw new UserFacingException("Project loading needs the installed Smith CLI helper.");
        var executable = Path.Combine(AppContext.BaseDirectory, "smith.exe");
        var command = new List<string>();
        if (!File.Exists(executable)) { executable = ProcessManager.ResolveExecutable("dotnet"); command.Add(entry); }
        command.AddRange(["--internal-csharp-load", snapshot.BundlePath + ".request", snapshot.BundlePath]);
        var processArgs = new JsonObject { ["executable"] = executable, ["arguments"] = new JsonArray(command.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
            ["workingDirectory"] = ".", ["environment"] = new JsonArray(new JsonObject { ["name"] = "MSBUILDDISABLENODEREUSE", ["value"] = "1" }), ["timeoutSeconds"] = 120 };
        var prepared = processes.Prepare(ctx, new ToolCall { Name = "process_start" }, processArgs);
        if (replace is not null)
        {
            replace.State = "Unavailable"; replace.Reason = "Replaced by a newer preparation of this project.";
            lock (loaded) { if (loaded.Remove(replace.Id, out var cached)) cached.Analysis.Dispose(); }
        }
        snapshot.ProcessId = prepared.OperationId!; ctx.Session.NavigationSnapshots.Add(snapshot);
        return prepared;
    }

    public async Task<ToolOutput> Start(ToolExecutionContext ctx, ToolCall call, ProcessManager processes)
    {
        if (!CanLoad(ctx.Session)) throw new UserFacingException("Project loading is no longer permitted.");
        var snapshot = ctx.Session.NavigationSnapshots.Single(s => s.ProcessId == call.Prepared!.OperationId);
        var request = new LoadRequest(ctx.Workspace, WorkspaceTools.Resolve(ctx.Workspace, snapshot.Project), snapshot.Configuration, snapshot.Framework);
        Directory.CreateDirectory(ctx.ArtifactDirectory);
        File.WriteAllText(ArtifactAccess.Validate(ctx, snapshot.BundlePath + ".request"), Json.Serialize(request));
        var output = await processes.ExecuteAsync(ctx, new ToolCall { Name = "process_start", Prepared = call.Prepared });
        var value = JsonNode.Parse(output.Content)!;
        return new(Json.Serialize(new { snapshotId = snapshot.Id, processId = snapshot.ProcessId, state = value["state"]?.ToString(), cursor = value["cursor"]?.GetValue<long>() ?? 0,
            notice = "Project evaluation runs as your account. Wait for the process, then use csharp_status. No restore or normal build requested. Process output is retained in its log." }));
    }

    public async Task ValidateGrants(ToolExecutionContext ctx, string[] ids)
    {
        if (ids.Length > 2 || ctx.Session.ParentConversationId is not null) throw new UserFacingException("Share at most two parent-owned snapshots.");
        foreach (var id in ids)
        {
            var status = JsonNode.Parse((await Read(ctx, "csharp_status", new JsonObject { ["snapshotId"] = id })).Content)!;
            if (status["state"]!.ToString() is not ("Ready" or "Partial")) throw new UserFacingException("Only ready, fresh parent snapshots can be shared. Use csharp_status first.");
        }
    }

    public async Task<ToolOutput> Read(ToolExecutionContext ctx, string name, JsonNode args)
    {
        await gate.WaitAsync(ctx.Cancellation);
        try
        {
            if (name == "csharp_symbols") return Symbols(ctx, args);
            var id = args["snapshotId"]!.ToString();
            var owner = ctx.Session;
            if (owner.ParentConversationId is { } parent)
            {
                if (!owner.NavigationSnapshotIds.Contains(id)) throw new UserFacingException("Snapshot is not explicitly granted to this worker.");
                owner = ctx.Store.Get(parent) ?? throw new UserFacingException("Snapshot owner is unavailable.");
            }
            var snapshot = owner.NavigationSnapshots.SingleOrDefault(s => s.Id == id) ?? throw new UserFacingException("Snapshot does not belong to this conversation.");
            var ownerContext = ctx with { Session = owner, Emit = null };
            string? reason = snapshot.Reason;
            if (snapshot.OwnerLifetime != lifetime) { snapshot.State = "Unavailable"; reason = "Snapshots do not survive CLI restart. An eligible parent must load the project again."; }
            else if (snapshot.State == "Loading")
            {
                lock (loaded)
                    if (loaded.TryGetValue(id, out var existing)) snapshot.State = existing.Bundle.Limitations.Count > 0 ? "Partial" : "Ready";
                var process = (ctx.Store.Get(owner.Id) ?? owner).Processes.Single(p => p.Id == snapshot.ProcessId);
                if (snapshot.State == "Loading" && process.State is not ("Prepared" or "Running"))
                {
                    if (process.State != "Exited" || process.ExitCode != 0 || !process.TerminationConfirmed)
                    { snapshot.State = "Unavailable"; reason = "Loading stopped or failed. Inspect its process log; no evaluation was replayed."; }
                    else
                    {
                      try
                      {
                        var path = ArtifactAccess.Validate(ownerContext, snapshot.BundlePath);
                        if (!File.Exists(path) || new FileInfo(path).Length > ArtifactAccess.MaxBytes) throw new UserFacingException("Navigation helper returned no bounded bundle. Inspect its process log.");
                        var bundle = JsonSerializer.Deserialize<AnalysisBundle>(File.ReadAllText(path), Json.Options) ?? throw new InvalidDataException("Invalid navigation bundle.");
                        if (bundle.Version != 1) throw new InvalidDataException("Unsupported navigation bundle.");
                        if (bundle.Failure is not null || bundle.Projects.Count == 0) { snapshot.State = "Unavailable"; reason = bundle.Failure ?? "No eligible C# projects were loaded."; }
                        else
                        {
                            // Validate transferred source paths and hashes again before constructing Roslyn objects.
                            foreach (var source in bundle.Projects.SelectMany(p => p.Documents))
                                if (ReadSource(ctx.Workspace, Path.Combine(ctx.Workspace, source.Path), true).Hash != source.Hash) throw new UserFacingException("Source changed during loading. Reload the project.");
                            var inventory = Inventory(ctx.Workspace, ctx.Cancellation);
                            if (bundle.Inventory != inventory) throw new UserFacingException("Workspace source or configuration changed during preparation. Reload the project.");
                            if (bundle.Inputs.Any(p => HashOrMissing(p.Key) != p.Value)) throw new UserFacingException("Project inputs changed during loading. Reload the project.");
                            lock (loaded) loaded[id] = (bundle, new SemanticAnalysis(bundle, ctx.Cancellation), inventory);
                            snapshot.State = bundle.Limitations.Count > 0 ? "Partial" : "Ready";
                            if (owner.Id == ctx.Session.Id)
                                snapshot.ArtifactId = ToolArtifacts.Save(ctx, "tool-result", Json.Serialize(new { kind = "navigation-provenance", snapshotId = id,
                                    snapshot.Project, snapshot.Configuration, snapshot.Framework, snapshot.CreatedAt, snapshot.ProcessId,
                                    documents = bundle.Projects.SelectMany(p => p.Documents.Select(d => new { project = p.Name, d.Path, d.Hash })),
                                    limitations = bundle.Limitations.Select(s => Activity.Clean(s, 300)) })).Id;
                        }
                      }
                      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UserFacingException or InvalidDataException or JsonException or ArgumentException or BadImageFormatException)
                      {
                          snapshot.State = "Unavailable";
                          reason = ex is UserFacingException or InvalidDataException ? Activity.Clean(ex.Message, 500) : "Navigation bundle could not be validated. Inspect the loading process; then reload the project.";
                          lock (loaded) if (loaded.Remove(id, out var rejected)) rejected.Analysis.Dispose();
                      }
                    }
                }
            }
            (AnalysisBundle Bundle, SemanticAnalysis Analysis, string Inventory) cached = default;
            bool available; lock (loaded) available = loaded.TryGetValue(id, out cached);
            if (available && snapshot.State is "Ready" or "Partial")
            {
                if (invalidated.Contains(id) || Inventory(ctx.Workspace, ctx.Cancellation) != cached.Inventory || cached.Bundle.Inputs.Any(p => HashOrMissing(p.Key) != p.Value))
                { snapshot.State = "Stale"; reason = "Source, project configuration or references changed. Settle editing workers and reload with csharp_load."; }
            }
            snapshot.Reason = reason;
            if (owner.Id == ctx.Session.Id) ctx.Save("navigation.status", new { snapshot.Id, snapshot.State });
            // Worker reads never save a mutable copy of the parent session.
            if (name == "csharp_status") return Bounded(Json.Serialize(new { kind = "navigation", snapshotId = id, state = snapshot.State, snapshot.Project,
                snapshot.Configuration, snapshot.Framework, snapshot.ProcessId, snapshot.CreatedAt, snapshot.ArtifactId, reason,
                projects = available ? cached.Bundle.Projects.Count : 0, documents = available ? cached.Bundle.Projects.Sum(p => p.Documents.Count) : 0,
                limitations = available ? cached.Bundle.Limitations.Take(4).Select(s => Activity.Clean(s, 200)) : [], omittedLimitations = available ? Math.Max(0, cached.Bundle.Limitations.Count - 4) : 0,
                coverage = "Loaded project/target variants only; no analyzers or generators. Filesystem freshness is checked, not an OS snapshot." }));
            if (!available || snapshot.State is not ("Ready" or "Partial")) throw new UserFacingException(reason ?? $"Snapshot is {snapshot.State}. Use csharp_status and reload if necessary.");
            var relative = Path.GetRelativePath(ctx.Workspace, WorkspaceTools.Resolve(ctx.Workspace, args["path"]!.ToString(), imageAccess: true));
            var operation = args["operation"]!.ToString();
            if (!cached.Bundle.Projects.Any(p => p.Documents.Any(d => d.Path.Equals(relative, StringComparison.OrdinalIgnoreCase))))
                throw new UserFacingException("This file is not covered by the selected snapshot. Use csharp_symbols or load its project.");
            var matches = await cached.Analysis.Query(relative, args["line"]!.GetValue<int>(), args["column"]!.GetValue<int>(), operation, ctx.Cancellation);
            var identity = AnalysisHash.Text(id + relative + args["line"] + ":" + args["column"] + operation);
            return Page(ctx, matches.Cast<object>().ToArray(), args["cursor"]!.ToString(), identity, new { snapshotId = id, operation, state = snapshot.State,
                notice = "Ambiguous symbols return candidate declarations labeled isCandidate, not resolved references. Linked-file project variants are separate evidence.",
                candidates = cached.Bundle.Projects.Where(p => p.Documents.Any(d => d.Path.Equals(relative, StringComparison.OrdinalIgnoreCase))).Take(10).Select(p => Activity.Clean(p.Name, 100)),
                limitations = cached.Bundle.Limitations.Take(8).Select(s => Activity.Clean(s, 300)) });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { throw new UserFacingException(ex.Message); }
        finally { gate.Release(); }
    }

    private ToolOutput Symbols(ToolExecutionContext ctx, JsonNode args)
    {
        var path = WorkspaceTools.Resolve(ctx.Workspace, args["path"]!.ToString());
        var query = args["query"]!.ToString();
        if (query.Length > 512) throw new UserFacingException("Symbol query exceeds 512 characters.");
        var files = Scan(ctx.Workspace, path, ctx.Cancellation, out var omitted);
        var matches = new List<object>(); var hashes = new StringBuilder(); var read = 0;
        foreach (var file in files)
        {
            try
            {
                var source = ReadSource(ctx.Workspace, file); hashes.Append(source.Path).Append(source.Hash); read++;
                matches.AddRange(syntax.Find(source, query, ctx.Cancellation));
                if (matches.Count > 5000) { matches = matches.Take(5000).ToList(); omitted.Add("Result limit reached; narrow path/query."); break; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UserFacingException) { omitted.Add(Path.GetRelativePath(ctx.Workspace, file) + ": unreadable, excluded or oversized."); }
        }
        return Page(ctx, matches, args["cursor"]!.ToString(), AnalysisHash.Text(path + query + hashes), new { method = "syntax", scannedFiles = read,
            omitted = omitted.Take(15), omittedCount = omitted.Count, coverage = "Declarations only, not overload resolution, project membership or complete references." });
    }

    internal static ToolOutput Page(ToolExecutionContext ctx, IReadOnlyList<object> matches, string cursor, string identity, object coverage)
    {
        var offset = 0;
        if (cursor.Length > 0)
        {
            var parts = cursor.Split(':');
            if (parts.Length != 2 || parts[0] != identity || !int.TryParse(parts[1], out offset) || offset < 0 || offset > matches.Count) throw new UserFacingException("Cursor is stale or belongs to another query; start with an empty cursor.");
        }
        var value = new JsonObject { ["kind"] = "navigation", ["coverage"] = JsonNode.Parse(Json.Serialize(coverage)), ["total"] = matches.Count };
        var items = new JsonArray(); var size = Encoding.UTF8.GetByteCount(value.ToJsonString());
        foreach (var match in matches.Skip(offset).Take(50))
        {
            var node = JsonNode.Parse(Json.Serialize(match))!;
            var length = Encoding.UTF8.GetByteCount(node.ToJsonString());
            if (size + length > 7000)
            {
                if (items.Count == 0) throw new UserFacingException("One navigation result exceeds the response budget; narrow the query or read its source directly.");
                break;
            }
            items.Add(node); size += length;
        }
        value["items"] = items;
        value["cursor"] = offset + items.Count < matches.Count ? identity + ":" + (offset + items.Count) : null;
        Bounded(value.ToJsonString());
        value["artifactId"] = ToolArtifacts.Save(ctx, "tool-result", value.ToJsonString()).Id;
        return Bounded(value.ToJsonString());
    }
    private static ToolOutput Bounded(string json) => Encoding.UTF8.GetByteCount(json) <= 8192 ? new(json)
        : throw new UserFacingException("Navigation response exceeds 8 KiB. Narrow the path/query or inspect the retained provenance artifact for coverage details.");
    public static SourceFile ReadSource(string root, string absolute, bool generated = false)
    {
        var relative = Path.GetRelativePath(root, absolute);
        var path = WorkspaceTools.Resolve(root, relative, imageAccess: generated);
        var bytes = FileOperations.ReadBytes(path);
        return new(relative, FileOperations.Decode(bytes), FileOperations.Hash(bytes));
    }
    private static List<string> Scan(string root, string path, CancellationToken ct, out List<string> omissions, bool configuration = false)
    {
        omissions = []; var result = new List<string>();
        if (File.Exists(path)) { if (Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase)) result.Add(path); return result; }
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((path, 0)); var visited = 0;
        while (pending.TryPop(out var entry))
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Depth > 20) { omissions.Add("Depth limit reached."); continue; }
            try
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(entry.Path).Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (++visited > 10000) { omissions.Add("Scan limit reached; narrow the path."); return result; }
                    try { WorkspaceTools.Resolve(root, Path.GetRelativePath(root, child)); }
                    catch (UserFacingException) { omissions.Add(Path.GetRelativePath(root, child) + ": excluded or link."); continue; }
                    if (Directory.Exists(child)) pending.Push((child, entry.Depth + 1));
                    else if (Path.GetExtension(child).Equals(".cs", StringComparison.OrdinalIgnoreCase) || configuration && Path.GetExtension(child).ToLowerInvariant() is ".csproj" or ".sln" or ".slnx" or ".props" or ".targets" or ".json") result.Add(child);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { omissions.Add("Unreadable directory."); }
        }
        result.Sort(StringComparer.OrdinalIgnoreCase); return result;
    }
    private static string HashOrMissing(string path)
    { try { return File.Exists(path) ? AnalysisHash.File(path) : "missing"; } catch (IOException) { return "unreadable"; } }
    public static string Inventory(string root, CancellationToken ct)
    {
        var files = Scan(root, root, ct, out var omissions, true);
        // Source additions/deletions and configuration changes are checked even for
        // files not in the loaded target. Never silently approve an incomplete scan.
        if (omissions.Any(s => s.Contains("limit", StringComparison.Ordinal) || s == "Unreadable directory.")) throw new UserFacingException("Workspace inventory is incomplete; narrow the workspace before semantic loading.");
        var parts = files.Select(p => { ct.ThrowIfCancellationRequested(); return p + HashOrMissing(p); }).ToList();
        foreach (var directory in files.Select(Path.GetDirectoryName).Append(root).Distinct())
            for (var item = new DirectoryInfo(directory!); item is not null; item = item.Parent)
                foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.Config" })
                { var p = Path.Combine(item.FullName, name); parts.Add(p + HashOrMissing(p)); }
        return AnalysisHash.Text(string.Join('\n', parts.Distinct().Order(StringComparer.OrdinalIgnoreCase)));
    }
    public void Dispose() { foreach (var cached in loaded.Values) cached.Analysis.Dispose(); gate.Dispose(); }
    public void Invalidate(ToolExecutionContext ctx, IEnumerable<string> paths)
    {
        var owner = ctx.Session.ParentConversationId is { } parent ? ctx.Store.Get(parent) : ctx.Session;
        if (owner is null) return;
        var changed = paths.Select(p => Path.GetFullPath(Path.Combine(ctx.Workspace, p))).ToArray();
        foreach (var snapshot in owner.NavigationSnapshots.Where(s => s.OwnerLifetime == lifetime))
            lock (loaded)
                if (loaded.TryGetValue(snapshot.Id, out var cached) && changed.Any(p => cached.Bundle.Inputs.ContainsKey(p)
                    || Path.GetExtension(p).ToLowerInvariant() is ".cs" or ".csproj" or ".props" or ".targets" or ".sln" or ".slnx" or ".json"))
                    invalidated.Add(snapshot.Id);
    }
}

public static class NavigationHelper
{
    public static async Task<int> Run(string requestPath, string outputPath)
    {
        AnalysisBundle bundle;
        try
        {
            var request = JsonSerializer.Deserialize<LoadRequest>(File.ReadAllText(requestPath), Json.Options) ?? throw new InvalidDataException("Invalid load request.");
            var inventory = NavigationTools.Inventory(request.Workspace, CancellationToken.None);
            bundle = await ProjectLoader.Load(request, path => NavigationTools.ReadSource(request.Workspace, path, true), CancellationToken.None);
            bundle.Inventory = inventory;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Avoid dumping project task output, environments, or arbitrary exception bodies.
            bundle = new() { Failure = ex is InvalidDataException ? ex.Message : "Project loading failed. Check installed SDK, selected framework and restored dependencies; inspect the owned process log." };
        }
        var content = Json.Serialize(bundle);
        if (Encoding.UTF8.GetByteCount(content) > ArtifactAccess.MaxBytes) content = Json.Serialize(new AnalysisBundle { Failure = "Prepared snapshot exceeds the 20 MiB transfer limit." });
        File.WriteAllText(outputPath, content, new UTF8Encoding(false));
        return 0;
    }
}
