using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AgentSmith.Core;
using AgentSmith.Storage;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class MemorizeStartupTests
{
    [Theory]
    [InlineData("Inspect")]
    [InlineData("Execute")]
    public async Task CliStartupAndResumeRetainMemorizeWithoutScanningOrInference(string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-memorize-startup-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "state");
        Directory.CreateDirectory(workspace);
        var source = Path.Combine(workspace, "README.md");
        await File.WriteAllTextAsync(source, "# Startup fixture\nSource must remain unchanged.\n");
        var original = await File.ReadAllBytesAsync(source);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
        var requests = new ConcurrentQueue<string>();
        var server = Serve();
        try
        {
            var first = await Invoke(["chat", "--workflow", "memorize", "--mode", mode,
                "--workspace", workspace, "--provider", "local", "--endpoint", endpoint, "--model", "fixture"],
                "/memorize status\n/exit\n");
            Assert.True(first.Code == 0, first.Output + first.Error);
            Assert.Contains("\"workflow\":\"Memorize\"", first.Output);
            var store = new SessionStore(data);
            var saved = Assert.Single(store.List());
            var session = store.Get(saved.Id)!;
            Assert.Equal(WorkflowMode.Memorize, session.Workflow);
            Assert.Equal(Enum.Parse<PermissionMode>(mode), session.Mode);
            Assert.Empty(session.Runs);
            var resumed = await Invoke(["resume", session.Id], "/memorize status\n/exit\n");
            Assert.True(resumed.Code == 0, resumed.Output + resumed.Error);
            Assert.Contains("\"workflow\":\"Memorize\"", resumed.Output);
            Assert.DoesNotContain("run.started", first.Output + resumed.Output);
            Assert.Equal(WorkflowMode.Memorize, store.Get(session.Id)!.Workflow);
            Assert.Empty(store.Get(session.Id)!.Runs);
            Assert.Equal(original, await File.ReadAllBytesAsync(source));
            Assert.False(Directory.Exists(Path.Combine(workspace, ".agentsmith", "memory")));
            Assert.Equal(4, requests.Count);
            Assert.All(requests, request => Assert.True(request is "GET /v1/models HTTP/1.1" or "GET /props HTTP/1.1", request));
        }
        finally
        {
            stop.Cancel(); listener.Stop();
            try { await server; } catch (OperationCanceledException) { }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }

        async Task Serve()
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var request = await reader.ReadLineAsync(stop.Token) ?? "";
                requests.Enqueue(request);
                while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
                var body = request.StartsWith("GET /v1/models ", StringComparison.Ordinal)
                    ? "{\"data\":[{\"id\":\"fixture\",\"meta\":{\"n_ctx\":131072}}]}" : "{}";
                var response = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
                await stream.WriteAsync(response, stop.Token);
            }
        }

        async Task<(int Code, string Output, string Error)> Invoke(string[] args, string input)
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(AgentSmith.Cli.Program).Assembly.Location);
            foreach (var arg in new[] { "--data-dir", data, "--json" }.Concat(args)) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(input); process.StandardInput.Close();
            try { await process.WaitForExitAsync(stop.Token); }
            catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
            return (process.ExitCode, await output, await error);
        }
    }
}
