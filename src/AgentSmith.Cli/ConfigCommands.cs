using System.CommandLine;
using AgentSmith.Core;

namespace AgentSmith.Cli;

public static partial class Program
{
    private static void ConfigCommands(RootCommand root, Option<string?> data, Option<bool> json, Option<bool> noColor)
    {
        var command = new Command("config", "Show the config.toml location and whether it exists.");
        root.Subcommands.Add(command);
        command.SetAction(async (p, ct) => await Execute(p.GetValue(data), p.GetValue(json), p.GetValue(noColor), app =>
        {
            app.Write("config", new { path = app.Config.Path, exists = app.Config.Exists,
                message = app.Config.Path + (app.Config.Exists ? "\nEdit this file to change defaults for new conversations." : "\nNo config file. Run smith config init to create one from existing defaults.") });
            return Task.FromResult(0);
        }));
        var init = new Command("init", "Create config.toml from existing model defaults; never overwrite an existing file.");
        command.Subcommands.Add(init);
        init.SetAction(async (p, ct) => await Execute(p.GetValue(data), p.GetValue(json), p.GetValue(noColor), app =>
        {
            ChatModelSelection? selection = null;
            if (app.Store.ChatDefault() is not null || app.Store.List().Any(s => s.ParentConversationId is null && !string.IsNullOrWhiteSpace(s.Model)))
                selection = app.ResolveChatModel(null, null, null, 0);
            SmithConfig.Initialize(app.Store.DataDirectory, selection);
            app.Write("config", new { path = app.Config.Path, exists = true, message = "Created " + app.Config.Path });
            return Task.FromResult(0);
        }));
    }
}
