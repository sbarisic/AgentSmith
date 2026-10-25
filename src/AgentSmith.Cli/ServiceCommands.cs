using System.CommandLine;
using AgentSmith.Core;
using AgentSmith.Tools;

namespace AgentSmith.Cli;

public static partial class Program
{
    internal static async Task EnsureAudioService(string directory, CancellationToken ct)
    {
        if ((await AudioServiceControl.Status(directory, ct)).Available) return;
        var executable = Environment.ProcessPath ?? throw new UserFacingException("Cannot locate Smith. Run smith service start.");
        string[] prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? [typeof(Program).Assembly.Location] : [];
        await AudioServiceControl.Start(directory, executable, prefix, ct);
    }
    internal static string ServiceSummary(AudioServiceStatus s) =>
        $"{s.Message}\nService directory: {s.DataDirectory}\nIdentity: {s.Identity}\nBuild: {s.Build} · protocol: {s.Protocol}" +
        $"\nService PID: {s.ProcessId?.ToString() ?? "stopped"} · helper PID: {s.HelperProcessId?.ToString() ?? "none"} · helper: {s.Helper}" +
        $"\nActive: {s.ActiveJobs} · waiting/uploads: {s.WaitingJobs} · clients: {s.Clients} · draining: {s.Draining}" +
        $"\nLast selected generation: {s.Generation ?? "none"}" + (s.Capture is { } c ? $"\nRecording: {c.Id} · {c.State} · {c.Device} · {c.DurationSeconds:0}s · {c.Bytes} bytes" : "\nRecording: none");
    private static void ServiceCommands(RootCommand root, Option<string?> data, Option<bool> json)
    {
        var service = new Command("service", "Manage the shared local audio service. Explicit startup; no model downloads or account access.");
        root.Subcommands.Add(service);
        foreach (var name in new[] { "start", "run", "status", "stop" })
        {
            var command = new Command(name, name switch
            {
                "start" => "Start a hidden independent service, or return the compatible running instance.",
                "run" => "Run the audio service in this terminal.",
                "status" => "Inspect the service without starting it or loading models.",
                _ => "Drain accepted jobs and stop the service."
            });
            service.Subcommands.Add(command);
            var force = new Option<bool>("--force") { Description = "Interrupt active and queued audio jobs immediately." };
            if (name == "stop") command.Options.Add(force);
            command.SetAction(async (p, ct) =>
            {
                var directory = SmithPaths.DataDirectory(p.GetValue(data));
                void Write(string type, object payload) => Console.WriteLine(p.GetValue(json)
                    ? Json.Serialize(new { schemaVersion = 1, type, payload })
                    : payload is AudioServiceStatus s
                        ? ServiceSummary(s) + "\n" + s.Engines.Message
                        : payload.ToString());
                try
                {
                    if (name == "run")
                    {
                        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
                        Console.CancelKeyPress += cancel;
                        try { await new AudioServiceHost(directory).Run(stop.Token); }
                        finally { Console.CancelKeyPress -= cancel; }
                        return 0;
                    }
                    AudioServiceStatus status;
                    if (name == "start")
                    {
                        var executable = Environment.ProcessPath ?? throw new UserFacingException("Cannot locate the current executable.");
                        string[] prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                            ? [typeof(Program).Assembly.Location] : [];
                        status = await AudioServiceControl.Start(directory, executable, prefix, ct);
                    }
                    else if (name == "stop") status = await AudioServiceControl.Stop(directory, p.GetValue(force), ct);
                    else status = await AudioServiceControl.Status(directory, ct);
                    Write("service.status", status); return name == "status" && !status.Available ? 2 : 0;
                }
                catch (OperationCanceledException) { Write("error", "Service command cancelled."); return 130; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UserFacingException or System.ComponentModel.Win32Exception)
                { Write("error", ex.Message); return 2; }
            });
        }
    }
}
