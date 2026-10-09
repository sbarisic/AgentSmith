using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class CrashTests
{
    [Fact]
    public async Task KillingCliOwnerKillsItsProcessTreeAndRecoveryDoesNotRestartIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "smith-crash-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var info = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(typeof(ProcessFixture.Program).Assembly.Location); info.ArgumentList.Add(root);
        using var owner = Process.Start(info)!;
        try
        {
            var line = await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(line);
            var result = JsonNode.Parse(line)!;
            var ids = result["ids"]!.AsArray().Select(i => i!.GetValue<int>()).ToArray();
            Assert.Equal(2, ids.Length);
            var children = ids.Select(Process.GetProcessById).ToArray();
            owner.Kill(entireProcessTree: false); await owner.WaitForExitAsync();
            try { foreach (var child in children) await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { foreach (var child in children) child.Dispose(); }
            var store = new SessionStore(Path.Combine(root, "state")); var session = store.Get(result["sessionId"]!.GetValue<string>())!;
            using var registry = new ToolRegistry(); registry.RecoverSession(new(session, session.Runs[0], store, default));
            Assert.Equal("Interrupted", session.Processes.Single().State);
            Assert.Equal(CallState.Started, session.Runs[0].Calls.Single().State);
            Assert.False(session.Processes.Single().TerminationConfirmed);
        }
        finally
        {
            if (!owner.HasExited) { owner.Kill(); await owner.WaitForExitAsync(); }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
        }
    }
}
