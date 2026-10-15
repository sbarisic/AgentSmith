using System.CommandLine;
using AgentSmith.Core;
using AgentSmith.Tools;

namespace AgentSmith.Cli;

public static partial class Program
{
    private static void AudioCommands(RootCommand root, Option<string?> data, Option<bool> json, Option<bool> noColor)
    {
        var command = new Command("audio", "Manage local Whisper small INT8 and YAMNet (Windows x64 CPU). Setup explicitly downloads a private runtime and models; inference stays offline.");
        var action = new Argument<string>("action") { Description = "setup or status" }; command.Arguments.Add(action); root.Subcommands.Add(command);
        command.SetAction(p => Execute(p.GetValue(data), p.GetValue(json), p.GetValue(noColor), async app =>
        {
            switch (p.GetValue(action))
            {
                case "status": app.AudioStatus(); return 0;
                case "setup":
                    using (var cancel = new CancellationTokenSource())
                    {
                        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
                        Console.CancelKeyPress += handler;
                        try { await LocalAudioInstallation.Setup(app.Store.DataDirectory, message => app.Write("audio.setup", new { message }), cancel.Token); }
                        finally { Console.CancelKeyPress -= handler; }
                    }
                    return 0;
                default: throw new UserFacingException("Usage: smith audio setup|status [--data-dir <directory>]");
            }
        }).GetAwaiter().GetResult());
    }
}

internal sealed partial class CliApp
{
    internal void AudioStatus()
    {
        var status = LocalAudioInstallation.Status(Store.DataDirectory);
        var error = Path.Combine(Store.DataDirectory, "audio", "last-error.txt");
        Write("audio.status", new { status.Transcription, status.Classification, status.Location,
            message = status.Message + (File.Exists(error) ? "\nLast setup error: " + File.ReadAllText(error) : "") });
    }
}
