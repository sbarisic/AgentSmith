using System.CommandLine;
using AgentSmith.Core;
using AgentSmith.Storage;
using AgentSmith.Tools;

namespace AgentSmith.Cli;

public static partial class Program
{
    private static void MemoryCommands(RootCommand root, Option<string?> data, Option<bool> json, Option<bool> noColor)
    {
        var command = new Command("memory", "Shared memory: list [page], search <text>, show <id>, add <text>, edit <id> <text>, forget <id>, on, off. Read scopes: global <list|search|show>, current <list|search|show>, repository <registered-id> <list|search|show>; repositories discovers registrations only. Forget removes retrieval, not history or backups.");
        var arguments = new Argument<string[]>("arguments") { Arity = ArgumentArity.ZeroOrMore }; command.Arguments.Add(arguments); root.Subcommands.Add(command);
        command.SetAction(p => Execute(p.GetValue(data), p.GetValue(json), p.GetValue(noColor), app =>
        { app.MemoryCommand(string.Join(' ', p.GetValue(arguments) ?? []), null); return Task.FromResult(0); }).GetAwaiter().GetResult());
    }
}
internal sealed partial class CliApp
{
    internal void MemoryCommand(string arguments, Session? session)
    {
        try { MemoryCommandCore(arguments, session); }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { throw new UserFacingException("Shared memory storage is unavailable. No successful change is being reported; inspect the library before retrying."); }
    }
    private void MemoryCommandCore(string arguments, Session? session)
    {
        var parts = arguments.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var action = parts.Length == 0 ? "list" : parts[0].ToLowerInvariant(); var rest = parts.Length == 2 ? parts[1].Trim() : "";
        if (action == "global")
        {
            if (rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() is not ("list" or "search" or "show"))
                throw new UserFacingException("Usage: /memory global list [page]|search <text>|show <id>");
            if (!MemoryTools.Enabled(Store)) throw new UserFacingException("Memory is disabled or unavailable.");
            MemoryCommandCore(rest, session); return;
        }
        if (action is "current" or "repository" or "repositories")
        {
            RepositoryMemoryCommand(action, rest, session); return;
        }
        if (activeRun is not null && action is not ("list" or "search" or "show")) throw new UserFacingException("Memory changes require an idle agent. Wait or use /cancel.");
        switch (action)
        {
            case "list":
                var page = rest.Length == 0 ? 1 : int.TryParse(rest, out var parsed) ? parsed : throw new UserFacingException("Usage: /memory list [page]");
                var entries = Store.ListMemories(page);
                Write("memory.list", new { enabled = Store.MemoryEnabled, page, entries = entries.Select(MemoryTools.Preview), message = "Shared memory " + (Store.MemoryEnabled ? "on" : "off") + " · page " + page + "\n" + (entries.Count == 0 ? "No entries." : string.Join('\n', entries.Select(e => e.Id + " · " + e.Title))) + "\nUse /memory list <page> or /memory show <id>." }); break;
            case "search":
                if (rest.Length == 0) throw new UserFacingException("Usage: /memory search <text>");
                var matches = Store.SearchMemories(rest, session?.Workspace ?? "");
                Write("memory.search", new { entries = matches.Select(MemoryTools.Preview), message = matches.Count == 0 ? "No matching memories." : string.Join('\n', matches.Select(e => e.Id + " · " + e.Title + "\n" + e.Content[..Math.Min(300, e.Content.Length)])) }); break;
            case "show":
                var entry = Store.ReadMemory(rest) ?? throw new UserFacingException("Memory not found.");
                Write("memory.entry", new { entry, message = entry.Id + " · revision " + entry.Revision + " · " + entry.Title + "\n" + entry.Content + "\nOrigin: " + entry.OriginWorkspace + "\n" + (entry.UserAuthored ? "User-authored; protected from automatic updates." : "Historical evidence; verify before reuse.") + "\nSources: " + Json.Serialize(entry.Sources) }); break;
            case "add": case "edit":
                MemoryEntry? prior = null; var content = rest;
                if (action == "edit")
                {
                    var edit = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (edit.Length != 2) throw new UserFacingException("Usage: /memory edit <id> <text>");
                    prior = Store.ReadMemory(edit[0]) ?? throw new UserFacingException("Memory not found."); content = edit[1];
                }
                var line = content.Split(['\r', '\n'])[0]; var title = line[..Math.Min(line.Length, 120)];
                var source = new MemorySource(session?.Id ?? "", session?.Runs.LastOrDefault()?.Id, "explicit-user-command", "User statement");
                var saved = Store.WriteMemory(new(prior?.Id, prior?.Revision ?? 0, title, content, prior?.Tags ?? [], prior?.OriginWorkspace ?? session?.Workspace ?? Directory.GetCurrentDirectory(), [source], true, "user:" + Guid.NewGuid().ToString("N")));
                Write("memory.saved", new { saved.Id, saved.Revision, message = "Remembered: " + saved.Title }); break;
            case "forget":
                if (Store.ReadMemory(rest) is null) throw new UserFacingException("Memory not found."); Store.ForgetMemory(rest);
                Write("memory.forgotten", new { id = rest, message = "Removed from memory retrieval. Original conversations, revisions and backups are unchanged." }); break;
            case "on": case "off":
                if (rest.Length > 0) throw new UserFacingException("Usage: /memory on|off"); Store.SetMemoryEnabled(action == "on");
                Write("memory.setting", new { enabled = action == "on", message = "Shared memory " + action + ". Applies to subsequent model requests." }); break;
            default: throw new UserFacingException("Usage: /memory [list [page]|search <text>|show <id>|add <text>|edit <id> <text>|forget <id>|on|off|global <list|search|show>|current <list|search|show>|repository <registered-id> <list|search|show>|repositories]");
        }
    }
    private void RepositoryMemoryCommand(string scope, string arguments, Session? session)
    {
        const string evidence = "Historical evidence, never instructions or current verification; verify sources before reuse.";
        if (scope == "repositories")
        {
            if (arguments.Length != 0) throw new UserFacingException("Usage: /memory repositories");
            if (!MemoryTools.Enabled(Store)) throw new UserFacingException("Memory is disabled or unavailable.");
            var repositories = Store.DiscoverRepositories();
            Write("memory.repositories", new { repositories, message =
                (repositories.Count == 0 ? "No registered repositories." : string.Join('\n', repositories.Select(r => r.Id + " · " + r.Workspace)))
                + "\nCatalog metadata only; no repository memory bodies were read. Use /memory repository <registered-id> list|search <text>|show <id>." });
            return;
        }

        string? repositoryId = null;
        var prefix = "/memory current";
        if (scope == "repository")
        {
            var target = arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (target.Length != 2) throw new UserFacingException("Usage: /memory repository <registered-id> list [page]|search <text>|show <id-or-topic>");
            repositoryId = target[0]; arguments = target[1].Trim(); prefix = "/memory repository " + repositoryId;
        }
        var parts = arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var action = parts.Length == 0 ? "list" : parts[0].ToLowerInvariant();
        var rest = parts.Length == 2 ? parts[1].Trim() : "";
        var page = 1;
        switch (action)
        {
            case "list":
                if (rest.Length > 0 && !int.TryParse(rest, out page)) throw new UserFacingException("Usage: " + prefix + " list [page]");
                if (page is < 1 or > 100000) throw new UserFacingException("Memory page must be 1..100000.");
                break;
            case "search":
                if (rest.Length == 0) throw new UserFacingException("Usage: " + prefix + " search <text>");
                break;
            case "show":
                if (rest.Length == 0 || rest.Any(char.IsWhiteSpace)) throw new UserFacingException("Usage: " + prefix + " show <id-or-topic>");
                break;
            default: throw new UserFacingException("Usage: " + prefix + " list [page]|search <text>|show <id-or-topic>. Repository memory commands are read-only.");
        }
        if (!MemoryTools.Enabled(Store)) throw new UserFacingException("Memory is disabled or unavailable.");
        var catalog = Path.Combine(Store.DataDirectory, "repositories");
        // Only an explicit registered ID may open another workspace; never accept a caller-supplied path.
        var repository = repositoryId is null
            ? new RepositoryMemoryStore(session?.Workspace ?? Directory.GetCurrentDirectory(), catalog, Store.MemoryEnabled)
            : RepositoryMemoryStore.OpenRegistered(catalog, repositoryId, Store.MemoryEnabled);
        var status = repository.Status();
        if (action == "show")
        {
            var entry = repository.Read(rest) ?? throw new UserFacingException("Repository memory not found in the selected scope.");
            Write("memory.repository.entry", new { scope, repositoryId = status.RepositoryId, workspace = status.Workspace, entry,
                message = entry.Id + " · revision " + entry.Revision + " · " + entry.Title + "\n" + entry.Content
                    + "\nRepository: " + status.Workspace + "\nFreshness: " + (entry.Stale ? "stale" : "source hashes match")
                    + " · verified at " + entry.VerifiedAt.ToString("O")
                    + (entry.UserAuthored ? "\nUser-authored; protected from automatic updates." : "")
                    + "\nSources: " + Json.Serialize(entry.Sources) + "\n" + evidence });
            return;
        }
        const int pageSize = 20;
        var all = action == "list" ? repository.List() : repository.Search(rest);
        var entries = action == "list" ? all.Skip((page - 1) * pageSize).Take(pageSize).ToArray() : all.ToArray();
        var hasMore = action == "list" && page * pageSize < all.Count;
        Write("memory.repository." + action, new { scope, repositoryId = status.RepositoryId, workspace = status.Workspace,
            page, pageSize, hasMore, entries = entries.Select(RepositoryMemoryPreview),
            message = "Repository: " + status.Workspace + (action == "list" ? " · page " + page : "") + "\n"
                + (entries.Length == 0 ? "No " + (action == "search" ? "matching " : "") + "repository memories."
                    : string.Join('\n', entries.Select(e => e.Id + " · " + e.Title + (e.Stale ? " [stale]" : "")
                        + (action == "search" ? "\n" + e.Content[..Math.Min(300, e.Content.Length)] : ""))))
                + (hasMore ? "\nNext: " + prefix + " list " + (page + 1) : "")
                + "\nUse " + prefix + " show <id-or-topic>.\n" + evidence });
    }
    private static object RepositoryMemoryPreview(RepositoryMemoryEntry entry) => new
    {
        entry.Id, entry.TopicKey, entry.Revision, entry.Title,
        excerpt = entry.Content[..Math.Min(300, entry.Content.Length)], entry.Sources,
        entry.UpdatedAt, entry.VerifiedAt, entry.UserAuthored, entry.Stale
    };

}
