using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSmith.Tools;
using NAudio.Wave;
using Xunit;

namespace AgentSmith.Tests;

[Collection("AudioService")]
public sealed class CallTransportTests
{
    private sealed class Source : ICaptureSource
    {
        public WaveFormat Format { get; } = new(16000, 16, 1);
        public event CapturePacket? Data;
        public event Action<Exception?>? Stopped;
        public void Start() => Data?.Invoke(new byte[3200], 0, CaptureClock.Qpc(), false, false);
        public void Stop() => Stopped?.Invoke(null);
        public void Dispose() { }
    }
    [Fact]
    public async Task PairedCaptureUsesSharedPipeMuteStatusAndTwoTrackExport()
    {
        using var directory = new TestDirectory(); using var stop = new CancellationTokenSource();
        var host = new AudioServiceHost(directory.Path);
        host.CaptureManager.Devices = () => [new("output", "Synthetic output", true)];
        host.CaptureManager.Microphones = () => [new("input", "Synthetic microphone", true, "microphone")];
        host.CaptureManager.CreateSource = _ => new Source();
        var running = host.Run(stop.Token);
        try
        {
            var legacy = await AudioCaptureControl.Request(directory.Path, "devices"); Assert.Single(legacy.AsArray());
            var devices = await AudioCaptureControl.Request(directory.Path, "devices", new() { ["all"] = true }); Assert.Equal(2, devices.AsArray().Count);
            var id = Guid.NewGuid().ToString("N");
            await AudioCaptureControl.Request(directory.Path, "start", JsonSerializer.SerializeToNode(new CaptureStart(id, "output", MicrophoneId: "input"))!.AsObject());
            var muted = await AudioCaptureControl.Request(directory.Path, "mute", new() { ["id"] = id, ["muted"] = true });
            Assert.True(muted["Tracks"]![1]!["Muted"]!.GetValue<bool>());
            Assert.Empty(muted["Tracks"]![0]!["Chunks"]!.AsArray());
            var status = (await AudioCaptureControl.Request(directory.Path, "stop", new() { ["id"] = id })).Deserialize<CaptureManifest>()!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (status.State == "Finalizing") { await Task.Delay(30, deadline.Token); status = (await AudioCaptureControl.Request(directory.Path, "status", new() { ["id"] = id })).Deserialize<CaptureManifest>()!; }
            var output = Path.Combine(directory.Path, "export"); await AudioCaptureControl.Export(directory.Path, id, output);
            var saved = RecordingAnalysisStore.ReadJson<CaptureManifest>(Path.Combine(output, "manifest.json"));
            Assert.Equal(2, saved.Version); Assert.All(saved.AllChunks(), c => Assert.True(File.Exists(Path.Combine(output, c.File))));
            Assert.True(File.Exists(Path.Combine(output, "call.m3u"))); Assert.True(File.Exists(Path.Combine(output, "mic.m3u")));
            Assert.False(File.Exists(Path.Combine(output, "recording.m3u")));
            await AudioCaptureControl.Request(directory.Path, "delete", new() { ["id"] = id });
            Assert.Empty((await AudioCaptureControl.Request(directory.Path, "list")).AsArray());
        }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
}
