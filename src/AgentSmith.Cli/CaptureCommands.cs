using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentSmith.Core;
using AgentSmith.Tools;

namespace AgentSmith.Cli;

public static partial class Program
{
    private static void CaptureCommands(Command audio, Option<string?> data, Option<bool> json)
    {
        foreach (var name in new[] { "devices", "capture" })
        {
            var command = new Command(name, name == "devices" ? "List Windows playback devices." : "Record output: start [device-id] [--max-storage-gib N], list [page], status [id], stop [id], export <id> <directory>, delete <id>. Requires smith service start; no microphone capture.");
            var args = new Argument<string[]>("arguments") { Arity = ArgumentArity.ZeroOrMore };
            var budget = new Option<double?>("--max-storage-gib") { Description = "Aggregate recording storage budget; default 10 GiB." };
            command.Arguments.Add(args); command.Options.Add(budget); audio.Subcommands.Add(command);
            command.SetAction(async (parse, ct) =>
            {
                try
                {
                    var words = new List<string> { name }; words.AddRange(parse.GetValue(args) ?? []);
                    if (parse.GetValue(budget) is { } value) { words.Add("--max-storage-gib"); words.Add(value.ToString(CultureInfo.InvariantCulture)); }
                    var result = await CaptureCommand(SmithPaths.DataDirectory(parse.GetValue(data)), words.ToArray(), null, ct);
                    if (parse.GetValue(json)) Console.WriteLine(Json.Serialize(new { type = "audio.capture", payload = result }));
                    else Console.WriteLine(TerminalRenderer.Safe(CaptureMessage(result)));
                    return 0;
                }
                catch (OperationCanceledException) { return 130; }
                catch (Exception ex) when (ex is UserFacingException or IOException or UnauthorizedAccessException or ArgumentException or JsonException)
                {
                    if (parse.GetValue(json)) Console.WriteLine(Json.Serialize(new { type = "error", payload = new { message = ex.Message } }));
                    else Console.Error.WriteLine(TerminalRenderer.Safe(ex.Message));
                    return 2;
                }
            });
        }
    }
    internal static string[] CaptureWords(string args) => Regex.Matches(args, "\"[^\"]*\"|'[^']*'|[^\\s]+").Select(m => m.Value.Trim('"', '\'')).ToArray();
    internal static async Task<JsonNode> CaptureCommand(string directory, string[] words, string? conversation, CancellationToken ct)
    {
        if (words is ["devices"]) return await AudioCaptureControl.Request(directory, "devices", ct: ct);
        if (words.Length < 2 || words[0] != "capture") throw new UserFacingException("Usage: /audio devices or /audio capture start|list|status|stop|export|delete.");
        var action = words[1]; var args = words.Skip(2).ToList(); var data = new JsonObject();
        if (action == "start")
        {
            double budget = 10;
            var at = args.IndexOf("--max-storage-gib");
            if (at >= 0)
            {
                if (at + 1 >= args.Count || !double.TryParse(args[at + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out budget)) throw new UserFacingException("Supply a numeric storage budget in GiB.");
                args.RemoveRange(at, 2);
            }
            if (args.Count > 1) throw new UserFacingException("Usage: capture start [device-id] [--max-storage-gib N]");
            await EnsureAudioService(directory, ct);
            data = JsonSerializer.SerializeToNode(new CaptureStart(Guid.NewGuid().ToString("N"), args.FirstOrDefault(), budget, conversation))!.AsObject();
        }
        else if (action == "list" && args.Count <= 1)
        {
            if (!int.TryParse(args.FirstOrDefault() ?? "1", out var page)) throw new UserFacingException("Supply a numeric page.");
            data["page"] = page;
        }
        else if (action is "status" or "stop" && args.Count <= 1) data["id"] = args.FirstOrDefault();
        else if (action == "delete" && args.Count == 1) data["id"] = args[0];
        else if (action == "export" && args.Count == 2) return new JsonObject { ["directory"] = await AudioCaptureControl.Export(directory, args[0], args[1], ct) };
        else throw new UserFacingException("Usage: capture start [device-id] [--max-storage-gib N] | list [page] | status [id] | stop [id] | export <id> <directory> | delete <id>");
        return await AudioCaptureControl.Request(directory, action, data, ct);
    }
    internal static string CaptureMessage(JsonNode result)
    {
        if (result is JsonArray list) return list.Count == 0 ? "No entries." : string.Join('\n', list.Select(n => n is null ? "" : CaptureMessage(n)));
        if (result["State"] is not null)
        {
            var device = result["Device"] is JsonObject d ? d["Name"]?.ToString() : result["Device"]?.ToString();
            return $"Recording {result["Id"]} · {result["State"]} · {device}\n{result["DurationSeconds"]} seconds · {result["Bytes"]} bytes · budget {result["BudgetBytes"]} bytes" +
                (result["BudgetWarning"]?.GetValue<bool>() == true ? " · STORAGE WARNING" : "") +
                (result["Error"] is { } error ? "\n" + error : "") + "\nOutput endpoint audio only; microphone is not included automatically.";
        }
        if (result["Name"] is not null) return $"{result["Id"]} · {result["Name"]}" + (result["Default"]?.GetValue<bool>() == true ? " · default" : "");
        return result.ToJsonString();
    }
}

internal sealed partial class CliApp
{
    private async Task CaptureCommand(string args, Session session)
    {
        var result = await Program.CaptureCommand(Store.DataDirectory, Program.CaptureWords(args), session.Id, CancellationToken.None);
        if (result is JsonObject && result["Id"] is { } selected && (args.StartsWith("capture start", StringComparison.Ordinal) || args.StartsWith("capture stop", StringComparison.Ordinal)))
        {
            Store.QueueCaptureSelection(session.Id, selected.ToString());
            if (activeRun is null) ApplyCaptureSelection(session);
        }
        if (result is JsonObject && result["Conversation"]?.ToString() == session.Id && result["Id"] is { } id) Store.RecordCapture(session.Id, id.ToString());
        var message = Program.CaptureMessage(result);
        if (result is JsonObject && result["State"] is { } state && (args.StartsWith("capture start", StringComparison.Ordinal) || args.StartsWith("capture stop", StringComparison.Ordinal)))
            message = state.ToString() == "Recording" ? "Recording computer audio · " + result["Device"]?["Name"] + ". Use /audio capture stop to finish."
                : state.ToString() is "Completed" or "Interrupted" ? $"Saved recording · {result["DurationSeconds"]} seconds · {state}. Ask me to summarize it." + (result["Error"] is { } error ? "\n" + error : "")
                : "Recording · " + state + ". Use /audio capture status to inspect progress.";
        Write("audio.capture", new { result, message });
        await RefreshRecording();
    }
    private void ApplyCaptureSelection(Session session)
    {
        if (Store.PendingCaptureSelection(session.Id) is not { } id) return;
        RecordingCoordinator.Select(Context(session), id);
        Store.ClearCaptureSelection(session.Id, id);
    }
    private async Task RecordingExitNotice()
    {
        var service = await AudioServiceControl.Status(Store.DataDirectory);
        if (service.Capture is { } capture) Write("audio.capture", new { capture, message = $"Recording {capture.Id} remains active on {capture.Device}. Stop it with smith audio capture stop {capture.Id} --data-dir \"{service.DataDirectory}\"." });
    }
    private async Task RefreshRecording()
    {
        var service = await AudioServiceControl.Status(Store.DataDirectory);
        input.RecordingStatus = service.Capture is { } c ? $"REC {c.Id[..8]} · {c.Device} · {c.DurationSeconds:0}s · {c.Bytes / 1048576d:0.0} MiB" + (c.BudgetWarning ? " · STORAGE WARNING" : "") : "";
        input.Refresh();
    }
}
