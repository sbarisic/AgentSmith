using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

[CollectionDefinition("AudioService", DisableParallelization = true)]
public sealed class AudioServiceCollection { }

[Collection("AudioService")]
public sealed class AudioServiceTests
{
    private sealed class Host : IDisposable
    {
        public readonly TestDirectory Directory = new();
        internal readonly LocalAudioHost Service = new();

        public Host()
        {
            System.IO.Directory.CreateDirectory(Path.Combine(Directory.Path, "helper"));
            File.WriteAllText(Path.Combine(Directory.Path, "helper", "downloads.json"), "{\"helperProtocol\":2}");
            var script = Path.Combine(Directory.Path, "server.ps1");
            File.WriteAllText(script, """
                while ($line = [Console]::ReadLine()) {
                    $request = $line | ConvertFrom-Json
                    if ($request.testMode -eq 'sleep') { Start-Sleep -Seconds 60 }
                    if ($request.testMode -eq 'crash') { exit 3 }
                    if ($request.testMode -eq 'bad') { [Console]::WriteLine('invalid'); continue }
                    if ($request.testMode -eq 'large') { [Console]::WriteLine(('x' * 2200000)); continue }
                    $id = $request.requestId
                    if ($request.testMode -eq 'wrong-id') { $id = 'wrong' }
                    $result = @{ startSeconds=0; durationSeconds=1; processId=$PID; diarization=@{status='succeeded';speakerCount=0;turns=@()} }
                    [Console]::WriteLine((@{protocol=2;requestId=$id;type='result';result=$result} | ConvertTo-Json -Compress -Depth 8))
                    [Console]::WriteLine((@{protocol=2;requestId=$id;type='complete'} | ConvertTo-Json -Compress))
                }
                """);
            Service.Launch = _ => WindowsChild.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                ["-NoProfile", "-File", script], Directory.Path, LocalAudioHost.OfflineEnvironment);

        }
        public Task<JsonObject> Run(string mode = "ok", CancellationToken ct = default) => Service.Infer(Directory.Path,
            new() { ["operation"] = "audio_diarize", ["testMode"] = mode }, _ => { }, ct);
        public void Dispose()
        { Service.Dispose(); Directory.Dispose(); }
    }

    [Fact]
    public async Task ConsecutiveRequestsReuseProcessAndIdleExpiryReleasesIt()
    {
        using var host = new Host(); var first = await host.Run(); var second = await host.Run();
        Assert.Equal(first["processId"]!.ToString(), second["processId"]!.ToString());
        host.Service.IdleTimeout = TimeSpan.FromMilliseconds(100); await host.Run();
        var watch = Stopwatch.StartNew();
        while (host.Service.State != "unloaded" && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(30);
        Assert.Equal("unloaded", host.Service.State);
        try { using var process = Process.GetProcessById(int.Parse(first["processId"]!.ToString())); Assert.True(process.HasExited); }
        catch (ArgumentException) { /* The process has already been reaped. */ }
    }

    [Theory]
    [InlineData("crash")] [InlineData("bad")] [InlineData("large")] [InlineData("wrong-id")]
    public async Task BrokenHelperIsDiscardedAndNextRequestStartsFresh(string failure)
    {
        using var host = new Host(); var first = await host.Run();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAnyAsync<Exception>(() => host.Run(failure, deadline.Token));
        Assert.Equal("unloaded", host.Service.State);
        var next = await host.Run(); Assert.NotEqual(first["processId"]!.ToString(), next["processId"]!.ToString());
    }

    [Fact]
    public async Task CancellationKillsActiveHelperAndAllowsNextRequest()
    {
        using var host = new Host(); await host.Run();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Run("sleep", deadline.Token));
        Assert.Equal("unloaded", host.Service.State);
        await host.Run();
    }
}
