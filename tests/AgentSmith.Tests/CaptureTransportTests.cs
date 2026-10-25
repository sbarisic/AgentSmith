using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

[Collection("AudioService")]
public sealed class CaptureTransportTests
{
    [Fact]
    public async Task ControlsStreamExportAndRejectDuplicatesWithoutModelsOrConversationDatabase()
    {
        using var directory = new TestDirectory();
        var source = new AudioCaptureTests.Source();
        var host = new AudioServiceHost(directory.Path);
        host.CaptureManager.CreateSource = _ => source;
        host.CaptureManager.Devices = () => [new("fake", "Fake endpoint", true)];
        using var stop = new CancellationTokenSource(); var running = host.Run(stop.Token);
        try
        {
            var operation = Guid.NewGuid().ToString("N");
            var request = JsonSerializer.SerializeToNode(new CaptureStart(operation))!.AsObject();
            var started = await AudioCaptureControl.Request(directory.Path, "start", request);
            Assert.Equal(operation, started["Id"]!.ToString());
            Assert.Equal(operation, (await AudioCaptureControl.Request(directory.Path, "start", request))["Id"]!.ToString());
            Assert.Equal("unloaded", (await AudioServiceControl.Status(directory.Path)).Helper);
            source.Feed(1600, 0, CaptureClock.Qpc());
            await Task.Delay(150);
            var final = await AudioCaptureControl.Request(directory.Path, "stop", new() { ["id"] = operation });
            Assert.Equal("Completed", final["State"]!.ToString());
            var destination = Path.Combine(directory.Path, "export");
            await AudioCaptureControl.Export(directory.Path, operation, destination);
            var manifest = JsonSerializer.Deserialize<CaptureManifest>(await File.ReadAllTextAsync(Path.Combine(destination, "manifest.json")))!;
            Assert.NotEmpty(manifest.Chunks);
            Assert.All(manifest.Chunks, chunk => Assert.Equal(chunk.Hash, AudioCaptureStore.Hash(Path.Combine(destination, chunk.File))));
            Assert.True(File.Exists(Path.Combine(destination, "recording.m3u")));
            await Assert.ThrowsAsync<AgentSmith.Core.UserFacingException>(() => AudioCaptureControl.Export(directory.Path, operation, destination));
            await Assert.ThrowsAsync<AgentSmith.Core.UserFacingException>(() => AudioCaptureControl.Request(directory.Path, "delete", new() { ["id"] = "../escape" }));
            await AudioCaptureControl.Request(directory.Path, "delete", new() { ["id"] = operation });
            Assert.Empty((JsonArray)await AudioCaptureControl.Request(directory.Path, "list"));
            Assert.False(File.Exists(Path.Combine(directory.Path, "sessions.db")));
        }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
    [Theory]
    [InlineData(false, "Completed")]
    [InlineData(true, "Interrupted")]
    public async Task ServiceShutdownFinalizesOrInterruptsCapture(bool force, string expected)
    {
        using var directory = new TestDirectory(); var source = new AudioCaptureTests.Source();
        var host = new AudioServiceHost(directory.Path);
        host.CaptureManager.CreateSource = _ => source;
        host.CaptureManager.Devices = () => [new("fake", "Fake endpoint", true)];
        var running = host.Run();
        var id = Guid.NewGuid().ToString("N");
        await AudioCaptureControl.Request(directory.Path, "start", JsonSerializer.SerializeToNode(new CaptureStart(id))!.AsObject());
        source.Feed(1600, 0, CaptureClock.Qpc());
        await Task.Delay(100);
        await AudioServiceControl.Stop(directory.Path, force);
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(expected, new AudioCaptureStore(directory.Path).Read(id).State);
    }
}
