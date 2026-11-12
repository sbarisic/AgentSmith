using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Storage;
using AgentSmith.Tools;

namespace AgentSmith.ProcessFixture;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var root = args[0];
        var store = new SessionStore(Path.Combine(root, "state"));
        var session = new Session { Workspace = root }; var run = new Run(); session.Runs.Add(run);
        store.Save(session, new("created", session.Id, run.Id, new { }));
        using var registry = new ToolRegistry();
        var ctx = new ToolExecutionContext(session, run, store, default);
        var script = "[Console]::WriteLine($PID); $child=Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 300' -PassThru -WindowStyle Hidden; [Console]::WriteLine($child.Id); Start-Sleep 300";
        var call = new ToolCall { Name = "process_start", Id = "start", Arguments = Json.Serialize(new { executable = "pwsh", arguments = new[] { "-NoProfile", "-Command", script }, workingDirectory = ".", environment = Array.Empty<object>(), timeoutSeconds = 600 }) };
        call.Prepared = registry.Prepare(ctx, call); call.State = CallState.Started; run.Calls.Add(call); ctx.Save("intent", new { });
        var result = JsonNode.Parse((await registry.ExecuteAsync(ctx, call)).Content)!;
        var text = "";
        while (true)
        {
            foreach (var chunk in result["output"]!.AsArray()) text += chunk!["text"]!.GetValue<string>();
            if (text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length >= 2) break;
            var poll = new ToolCall { Name = "process_poll", Prepared = new(Json.Serialize(new { processId = result["processId"]!.GetValue<string>(), cursor = result["cursor"]!.GetValue<int>(), waitMilliseconds = 1000 })) };
            result = JsonNode.Parse((await registry.ExecuteAsync(ctx, poll)).Content)!;
        }
        Console.WriteLine(Json.Serialize(new { sessionId = session.Id, ids = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray() }));
        await Console.In.ReadLineAsync();
    }
}
