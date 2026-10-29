using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AgentSmith.Core;
using AgentSmith.Storage;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class ConfigStartupTests
{
    [Fact]
    public async Task RealCliUsesTomlForNewChatsOverridesFlagsAndKeepsResumeSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-config-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
        var server = Serve();
        var configPath = Path.Combine(root, "config.toml");
        try
        {
            await File.WriteAllTextAsync(configPath, $"""
                provider = "local"
                [chatgpt]
                model = "cloud-saved"
                max_output_tokens = 16384
                [local]
                model = "fixture"
                endpoint = "{endpoint}"
                max_output_tokens = 1024
                [permissions]
                mode = "Inspect"
                [execution]
                max_steps = 7
                max_minutes = 8
                context_budget = "4096"
                [workers]
                delegation = "manual"
                max_concurrent = 3
                max_local_concurrent = 2
                max_requests = 12
                max_minutes = 9
                [display]
                color = false
                verbosity = "verbose"
                """);
            var models = await Invoke(["models"]);
            Assert.Contains("fixture", models);
            await Invoke(["chat", "--workspace", root]);
            var store = new SessionStore(root);
            var first = Assert.Single(store.List());
            Assert.Equal("fixture", first.Model); Assert.Equal(ModelProvider.Local, first.Connection.Provider);
            Assert.Equal(endpoint, first.Connection.Endpoint); Assert.Equal(1024, first.Connection.MaxOutputTokens);
            Assert.Equal(PermissionMode.Inspect, first.Mode); Assert.Equal(7, first.Execution.MaxRequests); Assert.Equal(8, first.Execution.MaxMinutes);
            Assert.Equal("4096", first.ContextPolicy.Budget); Assert.Equal("manual", first.Delegation);
            Assert.Equal(3, first.WorkerPolicy.MaxConcurrent); Assert.Equal(2, first.WorkerPolicy.MaxLocalConcurrent);
            Assert.Equal(12, first.WorkerPolicy.MaxRequests); Assert.Equal(9, first.WorkerPolicy.MaxMinutes);
            await Invoke(["chat", "--workspace", root, "--mode", "Edit", "--max-steps", "150", "--max-minutes", "20", "--delegation", "proactive", "--max-workers", "4", "--max-output-tokens", "2048"]);
            var second = store.List().Single(s => s.Id != first.Id);
            Assert.Equal(PermissionMode.Edit, second.Mode); Assert.Equal(150, second.Execution.MaxRequests);
            Assert.Equal(20, second.Execution.MaxMinutes); Assert.Equal("proactive", second.Delegation);
            Assert.Equal(4, second.WorkerPolicy.MaxConcurrent); Assert.Equal(2048, second.Connection.MaxOutputTokens);
            await Invoke(["resume", first.Id]);
            var resumed = store.Get(first.Id)!;
            Assert.Equal(PermissionMode.Inspect, resumed.Mode); Assert.Equal(7, resumed.Execution.MaxRequests);
            Assert.Equal(1024, resumed.Connection.MaxOutputTokens); Assert.Empty(resumed.Runs);
            await Invoke(["model", "alternate"]);
            Assert.Contains("model = \"alternate\"", await File.ReadAllTextAsync(configPath));
            Assert.Contains("model = \"cloud-saved\"", await File.ReadAllTextAsync(configPath));
            Assert.Contains("alternate", await Invoke(["model"]));
        }
        finally
        {
            stop.Cancel(); listener.Stop();
            try { await server; } catch (OperationCanceledException) { } catch (SocketException) { }
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
                Assert.StartsWith("GET ", request); // This acceptance test must not invoke inference or tools.
                while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
                var body = request.StartsWith("GET /v1/models ", StringComparison.Ordinal)
                    ? "{\"data\":[{\"id\":\"fixture\",\"meta\":{\"n_ctx\":32768}},{\"id\":\"alternate\",\"meta\":{\"n_ctx\":32768}}]}" : "{}";
                var response = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
                await stream.WriteAsync(response, stop.Token);
            }
        }
        async Task<string> Invoke(string[] args)
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(AgentSmith.Cli.Program).Assembly.Location);
            foreach (var arg in new[] { "--data-dir", root, "--json" }.Concat(args)) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync("/exit\n"); process.StandardInput.Close();
            try { await process.WaitForExitAsync(stop.Token); }
            catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
            var text = await output;
            Assert.True(process.ExitCode == 0, text + await error);
            return text;
        }
    }
}
