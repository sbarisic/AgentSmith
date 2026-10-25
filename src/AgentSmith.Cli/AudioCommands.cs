using System.CommandLine;
using AgentSmith.Core;
using AgentSmith.Tools;

namespace AgentSmith.Cli;

public static partial class Program
{
    private static void AudioCommands(RootCommand root, Option<string?> data, Option<bool> json, Option<bool> noColor)
    {
        var command = new Command("audio", "Manage local Whisper small INT8, YAMNet and anonymous speaker separation (Windows x64 CPU). Setup explicitly downloads a private runtime and models; inference stays offline.");
        root.Subcommands.Add(command);
        CaptureCommands(command, data, json);
        RecordingAnalysisCommands(command, data, json, noColor);
        foreach (var name in new[] { "setup", "status" })
        {
            var action = new Command(name, name == "setup" ? "Install local audio engines explicitly." : "Inspect engines and service.");
            command.Subcommands.Add(action);
            action.SetAction(p => Execute(p.GetValue(data), p.GetValue(json), p.GetValue(noColor), async app =>
            {
                if (name == "status") { app.AudioStatus(); return 0; }
                using var cancel = new CancellationTokenSource();
                ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
                Console.CancelKeyPress += handler;
                try { await LocalAudioInstallation.Setup(app.Store.DataDirectory, message => app.Write("audio.setup", new { message }), cancel.Token); }
                finally { Console.CancelKeyPress -= handler; }
                return 0;
            }).GetAwaiter().GetResult());
        }
    }
}

internal sealed partial class CliApp
{
    internal void AudioStatus()
    {
        var status = LocalAudioInstallation.Status(Store.DataDirectory);
        var service = AudioServiceControl.Status(Store.DataDirectory).GetAwaiter().GetResult();
        var error = Path.Combine(Store.DataDirectory, "audio", "last-error.txt");
        Write("audio.status", new { status.Transcription, status.Classification, status.Diarization, status.Location, helper = service.Helper, service,
            message = status.Message + "\n" + Program.ServiceSummary(service) + (File.Exists(error) ? "\nLast setup error: " + File.ReadAllText(error) : "") });
    }
}
