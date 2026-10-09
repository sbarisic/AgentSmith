using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

public sealed class ProcessTests
{
    public static ToolExecutionContext Context(string directory)
    {
        var store = new SessionStore(Path.Combine(directory, "state")); var session = new Session { Workspace = directory }; var run = new Run(); session.Runs.Add(run);
        store.Save(session, new("created", session.Id, run.Id, new { })); return new(session, run, store, default);
    }
    public static async Task<JsonNode> Invoke(ToolRegistry registry, ToolExecutionContext ctx, string name, object args)
    {
        var call = new ToolCall { Id = Guid.NewGuid().ToString("N"), Name = name, Arguments = Json.Serialize(args) };
        call.Prepared = registry.Prepare(ctx, call); ctx.Save("intent", new { call.Id });
        return JsonNode.Parse((await registry.ExecuteAsync(ctx, call)).Content)!;
    }
    public static async Task<(JsonNode Result, string Text)> Finish(ToolRegistry registry, ToolExecutionContext ctx, JsonNode result)
    {
        var text = new System.Text.StringBuilder();
        while (true)
        {
            foreach (var chunk in result["output"]!.AsArray()) text.Append(chunk!["stream"] + ":" + chunk["text"]);
            if (result["state"]!.GetValue<string>() != "Running" && result["hasMoreOutput"]?.GetValue<bool>() != true) return (result, text.ToString());
            result = await Invoke(registry, ctx, "process_poll", new { processId = result["processId"]!.GetValue<string>(), cursor = result["cursor"]!.GetValue<int>(), waitMilliseconds = 1000 });
        }
    }
    [Fact]
    public async Task StreamsStdinEofAndExitCodeAreObserved()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); using var registry = new ToolRegistry(); var ctx = Context(dir.Path);
        var start = await Invoke(registry, ctx, "process_start", new { executable = "pwsh", arguments = new[] { "-NoProfile", "-Command", "$x=[Console]::In.ReadToEnd(); [Console]::Out.WriteLine($x); [Console]::Error.WriteLine('error-stream'); exit 7" }, workingDirectory = ".", environment = Array.Empty<object>(), timeoutSeconds = 10 });
        await Invoke(registry, ctx, "process_input", new { processId = start["processId"]!.GetValue<string>(), text = "text with spaces and quotes \"hi\"", closeStdin = true });
        var (result, text) = await Finish(registry, ctx, start);
        Assert.Equal(7, result["exitCode"]!.GetValue<int>()); Assert.Contains("stdout:text with spaces", text); Assert.Contains("stderr:error-stream", text);
        Assert.True(result["terminationConfirmed"]!.GetValue<bool>());
        Assert.True(File.Exists(result["logPath"]!.GetValue<string>()));
        Assert.Equal("Exited", ctx.Store.Get(ctx.Session.Id)!.Processes.Single().State);
    }
    [Fact]
    public async Task TimeoutAndExplicitStopTerminateOwnedChildren()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); using var registry = new ToolRegistry(); var ctx = Context(dir.Path);
        var args = new { executable = "pwsh", arguments = new[] { "-NoProfile", "-Command", "Start-Sleep 60" }, workingDirectory = ".", environment = Array.Empty<object>(), timeoutSeconds = 1 };
        var start = await Invoke(registry, ctx, "process_start", args);
        var (result, _) = await Finish(registry, ctx, start);
        Assert.Equal("Stopped", result["state"]!.GetValue<string>()); Assert.Contains("timeout", result["reason"]!.GetValue<string>());
        start = await Invoke(registry, ctx, "process_start", args with { timeoutSeconds = 10 });
        result = await Invoke(registry, ctx, "process_stop", new { processId = start["processId"]!.GetValue<string>() });
        Assert.True(result["terminationConfirmed"]!.GetValue<bool>());
    }
    [Fact]
    public async Task LiteralArgumentsAndEnvironmentOverridesSurviveNativeQuoting()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); using var registry = new ToolRegistry(); var ctx = Context(dir.Path);
        var script = Path.Combine(dir.Path, "script with spaces.ps1"); File.WriteAllText(script, "param([string]$Value) [Console]::WriteLine($Value); [Console]::WriteLine($env:SMITH_TEST_VALUE)");
        var value = "spaces \"quotes\" and trailing\\";
        var start = await Invoke(registry, ctx, "process_start", new { executable = "pwsh", arguments = new[] { "-NoProfile", "-File", script, value }, workingDirectory = ".", environment = new[] { new { name = "SMITH_TEST_VALUE", value = "injected-test-value" } }, timeoutSeconds = 10 });
        var (result, text) = await Finish(registry, ctx, start);
        Assert.Equal(0, result["exitCode"]!.GetValue<int>()); Assert.Contains(value, text); Assert.Contains("injected-test-value", text);
    }
    [Fact]
    public async Task OutputLimitsDrainWithoutHangingAndSessionLimitIsEnforced()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); using var registry = new ToolRegistry(); var ctx = Context(dir.Path);
        var start = await Invoke(registry, ctx, "process_start", new { executable = "pwsh", arguments = new[] { "-NoProfile", "-Command", "[Console]::Write(('x' * (22*1024*1024))); exit 0" }, workingDirectory = ".", environment = Array.Empty<object>(), timeoutSeconds = 30 });
        var id = start["processId"]!.GetValue<string>();
        var limit = System.Diagnostics.Stopwatch.StartNew();
        while (ctx.Session.Processes.Single().State == "Running" && limit.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(100);
        Assert.Equal("Exited", ctx.Session.Processes.Single().State);
        Assert.True(ctx.Session.Processes.Single().Truncated);
        Assert.True(new FileInfo(ctx.Session.Processes.Single().LogPath).Length <= 20 * 1024 * 1024);
        var poll = await Invoke(registry, ctx, "process_poll", new { processId = id, cursor = 0, waitMilliseconds = 0 });
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(poll.ToJsonString()) <= 32768);
        Assert.True(poll["hasMoreOutput"]!.GetValue<bool>());
        var sleeper = new { executable = "pwsh", arguments = new[] { "-NoProfile", "-Command", "Start-Sleep 30" }, workingDirectory = ".", environment = Array.Empty<object>(), timeoutSeconds = 30 };
        for (var i = 0; i < 4; i++) await Invoke(registry, ctx, "process_start", sleeper);
        await Assert.ThrowsAsync<UserFacingException>(() => Invoke(registry, ctx, "process_start", sleeper));
        await registry.Processes.StopAsync(ctx.Session);
    }
    [Fact]
    public async Task CancellationStopsOnlyCurrentRunThenModeDowngradeStopsTheRest()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); using var registry = new ToolRegistry(); var ctx = Context(dir.Path);
        var args = new { executable = "pwsh", arguments = new[] { "-NoProfile", "-Command", "Start-Sleep 60" }, workingDirectory = ".", environment = Array.Empty<object>(), timeoutSeconds = 60 };
        await Invoke(registry, ctx, "process_start", args);
        var second = new Run(); ctx.Session.Runs.Add(second);
        await Invoke(registry, ctx with { Run = second }, "process_start", args);
        await registry.CancelProcessesAsync(ctx with { Run = second });
        Assert.Equal("Running", ctx.Session.Processes[0].State);
        Assert.Equal("Stopped", ctx.Session.Processes[1].State);
        await registry.Processes.StopAsync(ctx.Session);
        Assert.All(ctx.Session.Processes, p => Assert.True(p.TerminationConfirmed));
    }
    [Fact]
    public async Task LocalDevelopmentServerRespondsThenStopsListening()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); using var registry = new ToolRegistry(); var ctx = Context(dir.Path);
        const string script = """
            $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
            $listener.Start()
            [Console]::WriteLine('PORT='+$listener.LocalEndpoint.Port)
            while ($true) {
              $client=$listener.AcceptTcpClient()
              $stream=$client.GetStream()
              $reader=[IO.StreamReader]::new($stream)
              do { $line=$reader.ReadLine() } while ($line)
              $body='AgentSmith development server'
              $bytes=[Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n$body")
              $stream.Write($bytes,0,$bytes.Length)
              $client.Dispose()
            }
            """;
        var result = await Invoke(registry, ctx, "process_start", new { executable = "pwsh", arguments = new[] { "-NoProfile", "-Command", script }, workingDirectory = ".", environment = Array.Empty<object>(), timeoutSeconds = 15 });
        var id = result["processId"]!.GetValue<string>(); var output = "";
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!output.Contains("PORT=") && timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            output += string.Concat(result["output"]!.AsArray().Select(c => c!["text"]!.GetValue<string>()));
            if (!output.Contains("PORT=")) result = await Invoke(registry, ctx, "process_poll", new { processId = id, cursor = result["cursor"]!.GetValue<long>(), waitMilliseconds = 1000 });
        }
        var match = System.Text.RegularExpressions.Regex.Match(output, @"PORT=(\d+)"); Assert.True(match.Success, output);
        var port = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        Assert.Equal("AgentSmith development server", await http.GetStringAsync($"http://127.0.0.1:{port}/"));
        var stopped = await Invoke(registry, ctx, "process_stop", new { processId = id });
        Assert.True(stopped["terminationConfirmed"]!.GetValue<bool>());
        using var connection = new System.Net.Sockets.TcpClient();
        await Assert.ThrowsAsync<System.Net.Sockets.SocketException>(async () => await connection.ConnectAsync("127.0.0.1", port));
    }
    [Fact]
    public async Task GitInspectionPreservesIndexAndDisablesExternalHelpers()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); using var registry = new ToolRegistry(); var ctx = Context(dir.Path);
        async Task Git(params string[] arguments)
        {
            var start = await Invoke(registry, ctx, "process_start", new { executable = "git", arguments, workingDirectory = ".", environment = Array.Empty<object>(), timeoutSeconds = 10 });
            var (result, text) = await Finish(registry, ctx, start); Assert.True(result["exitCode"]!.GetValue<int>() == 0, text);
        }
        await Git("init"); File.WriteAllText(Path.Combine(dir.Path, "tracked.txt"), "before\n"); await Git("add", "tracked.txt");
        var index = File.ReadAllBytes(Path.Combine(dir.Path, ".git", "index"));
        await Git("config", "diff.external", "missing-external-command"); await Git("config", "core.fsmonitor", "missing-fsmonitor-command");
        File.WriteAllText(Path.Combine(dir.Path, "tracked.txt"), "after\n"); File.WriteAllText(Path.Combine(dir.Path, "untracked.txt"), "user\n");
        ctx.Session.Mode = PermissionMode.Inspect;
        var status = await Invoke(registry, ctx, "git_status", new { }); Assert.Contains("untracked.txt", status["content"]!.GetValue<string>());
        var diff = await Invoke(registry, ctx, "git_diff", new { staged = false }); Assert.Equal(0, diff["exitCode"]!.GetValue<int>()); Assert.Contains("+after", diff["content"]!.GetValue<string>());
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(dir.Path, ".git", "index")));
    }
}
