using System.CommandLine;
using AgentSmith.Core;
using AgentSmith.Tools;

namespace AgentSmith.Cli;

public static partial class Program
{
    private static void MemoryCommands(RootCommand root, Option<string?> data, Option<bool> json, Option<bool> noColor)
    {
        var command = new Command("memory", "Shared memory: list [page], search <text>, show <id>, add <text>, edit <id> <text>, forget <id>, on, off. Forget removes retrieval, not history or backups.");
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
            default: throw new UserFacingException("Usage: /memory [list [page]|search <text>|show <id>|add <text>|edit <id> <text>|forget <id>|on|off]");
        }
    }
}
