using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Core;

namespace AgentSmith.Tools;

/// <summary>Fixed, read-only Git commands and workspace-bounded review evidence.</summary>
public static class ReviewEvidence
{
    private const int OutputLimit = 8 * 1024 * 1024;
    public static async Task<ReviewSnapshot> Capture(ToolExecutionContext ctx, WorkflowReport report, ProcessManager processes, bool retain)
    {
        var snapshot = new ReviewSnapshot();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var states = new StringBuilder();
        async Task<string> Git(params string[] command)
        {
            var common = new[] { "--no-pager", "--no-optional-locks", "--literal-pathspecs", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=NUL", "-c", "diff.external=", "-c", "core.pager=cat", "-C", ctx.Workspace };
            var args = new JsonObject { ["executable"] = "git", ["arguments"] = new JsonArray(common.Concat(command).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()), ["workingDirectory"] = ".", ["environment"] = new JsonArray(new JsonObject { ["name"] = "GIT_TERMINAL_PROMPT", ["value"] = "0" }), ["timeoutSeconds"] = 30 };
            var start = new ToolCall { Id = "review-git-" + Guid.NewGuid().ToString("N"), Name = "process_start" };
            start.Prepared = processes.Prepare(ctx, start, args); ctx.Save("review.evidence.started", new { report.Id, command = command[0] });
            var result = await processes.ExecuteAsync(ctx, start); var text = new StringBuilder(); var error = new StringBuilder(); bool overflow = false;
            while (true)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                var data = JsonNode.Parse(result.Content)!;
                foreach (var chunk in data["output"]!.AsArray())
                {
                    var target = chunk!["stream"]!.ToString() == "stdout" ? text : error;
                    var value = chunk["text"]!.ToString(); var room = OutputLimit - target.Length;
                    if (value.Length > room) overflow = true;
                    target.Append(value.AsSpan(0, Math.Min(room, value.Length)));
                }
                if (data["state"]!.ToString() != "Running" && data["hasMoreOutput"]?.GetValue<bool>() != true)
                {
                    if (data["exitCode"]?.GetValue<int>() != 0) throw new UserFacingException(command.Length > 1 && command[1] == "--show-toplevel" ? "Workspace is not a readable Git repository. Use /review --path for a non-Git audit." : "Review Git inspection failed. Check the selected reference and repository.");
                    if (overflow || data["truncated"]?.GetValue<bool>() == true) snapshot.Limitations.Add("Git output exceeded retained review evidence bounds; coverage is incomplete.");
                    return text.ToString();
                }
                var poll = new ToolCall { Name = "process_poll", Prepared = new(new JsonObject { ["processId"] = data["processId"]!.ToString(), ["cursor"] = data["cursor"]!.GetValue<long>(), ["waitMilliseconds"] = 1000 }.ToJsonString()) };
                result = await processes.ExecuteAsync(ctx, poll);
            }
        }
        void Artifact(string name, string content)
        {
            states.Append(name).Append('\n').Append(content);
            if (!retain) return;
            Directory.CreateDirectory(ctx.ArtifactDirectory);
            var path = ArtifactAccess.Validate(ctx, Path.Combine(ctx.ArtifactDirectory, report.Id + "-" + name + ".txt"));
            File.WriteAllText(path, content, new UTF8Encoding(false));
            snapshot.ArtifactIds.Add(ArtifactAccess.Register(ctx, path, "review-evidence").Id);
        }
        void AddPaths(string nulSeparated)
        {
            foreach (var path in nulSeparated.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (paths.Count >= 2000) { snapshot.Limitations.Add("Review inventory exceeds 2000 files; remaining files were not captured."); break; }
                paths.Add(path);
            }
        }
        if (report.TargetKind == "path")
        {
            var path = WorkspaceTools.Resolve(ctx.Workspace, report.Target);
            if (File.Exists(path)) paths.Add(Path.GetRelativePath(ctx.Workspace, path));
            else if (Directory.Exists(path))
            {
                var pending = new Stack<string>(); pending.Push(path); var examined = 0;
                while (pending.TryPop(out var directory) && examined < 5000 && paths.Count < 2000)
                {
                    foreach (var item in Directory.EnumerateFileSystemEntries(directory))
                    {
                        if (++examined > 5000 || paths.Count >= 2000) break;
                        ctx.Cancellation.ThrowIfCancellationRequested();
                        var relative = Path.GetRelativePath(ctx.Workspace, item);
                        try { WorkspaceTools.Resolve(ctx.Workspace, relative); if (Directory.Exists(item)) pending.Push(item); else paths.Add(relative); }
                        catch (UserFacingException) { snapshot.Limitations.Add("Excluded path: " + relative); }
                    }
                }
                if (examined >= 5000 || paths.Count >= 2000) snapshot.Limitations.Add("Path inventory limit reached; coverage is incomplete.");
            }
            else throw new UserFacingException("Review path does not exist.");
        }
        else
        {
            var root = Path.GetFullPath((await Git("rev-parse", "--show-toplevel")).Trim());
            var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ctx.Workspace));
            if (!workspace.Equals(root, StringComparison.OrdinalIgnoreCase) && !workspace.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new UserFacingException("Git repository does not contain the workspace.");
            async Task<string> Resolve(string reference)
            {
                if (reference.Length is 0 or > 256 || reference.StartsWith('-') || reference.Any(char.IsControl)) throw new UserFacingException("Invalid Git reference.");
                return (await Git("rev-parse", "--verify", "--end-of-options", reference + "^{commit}")).Trim();
            }
            // Unborn repositories still support a working-tree review.
            if (report.TargetKind == "changes")
            {
                try { snapshot.Head = await Resolve("HEAD"); } catch (UserFacingException) { snapshot.Limitations.Add("HEAD could not be resolved (possibly an unborn branch)."); }
                Artifact("status", await Git("status", "--porcelain=v1", "--untracked-files=all", "--", "."));
                Artifact("staged", await Git("diff", "--cached", "--relative", "--no-renames", "--no-ext-diff", "--no-textconv", "--", "."));
                Artifact("working-tree", await Git("diff", "--relative", "--no-renames", "--no-ext-diff", "--no-textconv", "--", "."));
                AddPaths(await Git("diff", "--cached", "--relative", "--no-renames", "--name-only", "-z", "--", "."));
                AddPaths(await Git("diff", "--relative", "--no-renames", "--name-only", "-z", "--", "."));
                Artifact("index", await Git("ls-files", "--stage", "--", "."));
                var untracked = await Git("ls-files", "--others", "--exclude-standard", "-z", "--", "."); AddPaths(untracked);
                Artifact("untracked", untracked.Replace('\0', '\n'));
            }
            else
            {
                var selected = await Resolve(report.Target);
                if (report.TargetKind == "base")
                {
                    snapshot.Head = await Resolve("HEAD"); snapshot.Base = (await Git("merge-base", selected, snapshot.Head)).Trim();
                    Artifact("comparison", await Git("diff", "--relative", "--no-renames", "--no-ext-diff", "--no-textconv", snapshot.Base, snapshot.Head, "--", "."));
                    AddPaths(await Git("diff", "--relative", "--no-renames", "--name-only", "-z", snapshot.Base, snapshot.Head, "--", "."));
                }
                else if (report.TargetKind == "commit")
                {
                    snapshot.Head = selected;
                    var parents = (await Git("rev-list", "--parents", "-n", "1", selected)).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    snapshot.Base = parents.Length > 1 ? parents[1] : null;
                    Artifact("comparison", await Git("show", "--format=fuller", "--first-parent", "--root", "--relative", "--no-renames", "--no-ext-diff", "--no-textconv", selected, "--", "."));
                    AddPaths(await Git("diff-tree", "--root", "--first-parent", "-m", "--no-commit-id", "--relative", "--no-renames", "--name-only", "-r", "-z", selected, "--", "."));
                }
                else throw new UserFacingException("Unknown review target.");
                snapshot.Limitations.Add("Comparison artifacts contain committed changes; file reads show current workspace contents, not historical file contents.");
            }
        }
        foreach (var path in paths.Order(StringComparer.OrdinalIgnoreCase))
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            try
            {
                var full = WorkspaceTools.Resolve(ctx.Workspace, path, true);
                snapshot.FileHashes[path] = File.Exists(full) ? FileOperations.Hash(FileOperations.ReadBytes(full)) : "missing";
            }
            catch (Exception ex) when (ex is UserFacingException or IOException or UnauthorizedAccessException) { snapshot.Limitations.Add("Uncaptured binary, excluded, oversized or inaccessible file: " + path); }
        }
        Artifact("inventory", Json.Serialize(snapshot.FileHashes));
        snapshot.StateHash = FileOperations.Hash(Encoding.UTF8.GetBytes(states.ToString() + snapshot.Head + snapshot.Base));
        snapshot.Limitations = snapshot.Limitations.Distinct().Take(100).ToList();
        return snapshot;
    }
}
