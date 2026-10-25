using AgentSmith.Tools;
using NAudio.Wave;
using Xunit;

namespace AgentSmith.Tests;

public sealed class AudioCaptureTests
{
    internal sealed class Source : ICaptureSource
    {
        public WaveFormat Format { get; } = new(16000, 16, 1);
        public event CapturePacket? Data;
        public event Action<Exception?>? Stopped;
        public void Start() { }
        public void Stop() => Stopped?.Invoke(null);
        public void Dispose() { }
        public void Feed(int frames, long position, long qpc, bool gap = false, bool invalidTime = false) => Data?.Invoke(new byte[frames * 2], position, qpc, gap, invalidTime);
        public void Lost() => Stopped?.Invoke(new IOException("Device lost"));
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "smith-capture-" + Guid.NewGuid().ToString("N"));
        internal readonly Source Source = new();
        internal long Clock = 100_000_000;
        internal readonly AudioCaptureManager Manager;
        internal Fixture()
        {
            Manager = new(Root) { CreateSource = _ => Source, Devices = () => [new("test", "Test output", true)], FreeBytes = () => 100L << 30, Now = () => Clock, ChunkBytes = 16000 };
        }
        internal Task<CaptureManifest> Start(string? id = null) => Manager.Start(new(id ?? Guid.NewGuid().ToString("N")));
        public void Dispose() { Manager.Shutdown(true).GetAwaiter().GetResult(); Manager.Dispose(); Directory.Delete(Root, true); }
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }
    [Fact]
    public async Task ConcurrentStartsAreSerializedAndLostReplyRetryReturnsSameRecording()
    {
        using var f = new Fixture(); var id = Guid.NewGuid().ToString("N");
        var both = await Task.WhenAll(f.Start(id), f.Start(id));
        Assert.All(both, m => Assert.Equal(id, m.Id));
        await Assert.ThrowsAsync<AgentSmith.Core.UserFacingException>(() => f.Start());
        var stopped = await f.Manager.Stop(id); Assert.Equal("Completed", stopped.State);
        Assert.Equal("Completed", (await f.Start(id)).State);
        Assert.Equal("Completed", (await f.Manager.Stop(id)).State);
        Assert.Throws<AgentSmith.Core.UserFacingException>(() => f.Manager.Status(null));
    }
    [Fact]
    public async Task RotationSilenceAndExportLeasePreserveAllFrames()
    {
        using var f = new Fixture(); var start = await f.Start();
        f.Source.Feed(16000, 0, f.Clock + 10_000_000); f.Clock += 30_000_000;
        var finished = await f.Manager.Stop(start.Id);
        Assert.Equal("Completed", finished.State); Assert.Equal(48000, finished.Frames); Assert.Equal(6, finished.Chunks.Count);
        Assert.Equal(32000, finished.Coverage.Sum(c => c.Frames));
        long offset = 0;
        foreach (var chunk in finished.Chunks)
        {
            Assert.Equal(offset, chunk.StartFrame); offset += chunk.Frames;
            var path = f.Manager.ChunkPath(start.Id, chunk.File);
            using var wave = new WaveFileReader(path); Assert.Equal(chunk.Frames * 2, wave.Length); Assert.Equal(chunk.Hash, AudioCaptureStore.Hash(path));
        }
        using (await f.Manager.AcquireExport(start.Id)) await Assert.ThrowsAsync<IOException>(() => f.Manager.Delete(start.Id));
        await f.Manager.Delete(start.Id); Assert.Empty(f.Manager.List(1));
    }
    [Theory]
    [InlineData("overflow")]
    [InlineData("device")]
    [InlineData("timestamp")]
    [InlineData("discontinuity")]
    public async Task CaptureFailuresAreInterruptedAndNeverReportedComplete(string kind)
    {
        using var f = new Fixture(); var start = await f.Start();
        f.Source.Feed(160, 0, f.Clock); await Until(() => f.Manager.Status(start.Id).Frames > 0);
        if (kind == "overflow") { f.Manager.BufferLimit = 1; f.Source.Feed(160, 160, f.Clock + 100_000); }
        if (kind == "device") f.Source.Lost();
        if (kind == "timestamp") f.Source.Feed(160, 160, 0, invalidTime: true);
        if (kind == "discontinuity")
        {
            f.Source.Feed(160, 500, f.Clock + 1_000_000, gap: true);
            await Until(() => f.Manager.Status(start.Id).Error is not null);
            Assert.NotNull(f.Manager.Active); await f.Manager.Stop(start.Id);
        }
        await Until(() => f.Manager.Active is null);
        Assert.Equal("Interrupted", f.Manager.Status(start.Id).State); Assert.NotNull(f.Manager.Status(start.Id).Error);
    }
    [Fact]
    public async Task DiskReserveStopsAndRetainsPriorRecording()
    {
        using var f = new Fixture(); var start = await f.Start(); f.Source.Feed(160, 0, f.Clock);
        f.Manager.FreeBytes = () => 0;
        await Until(() => f.Manager.Active is null);
        Assert.Equal("Interrupted", f.Manager.Status(start.Id).State);
        await Assert.ThrowsAsync<AgentSmith.Core.UserFacingException>(() => f.Start());
        Assert.Single(f.Manager.List(1));
    }
    [Fact]
    public async Task StorageBudgetDoesNotDeleteEarlierAudio()
    {
        using var f = new Fixture(); f.Manager.ChunkBytes = 16 * 1024 * 1024;
        var start = await f.Manager.Start(new(Guid.NewGuid().ToString("N"), MaxStorageGiB: .01));
        f.Clock += 4_000_000_000;
        var result = await f.Manager.Stop(start.Id);
        // Stop may return Finalizing after its five-second control-response budget.
        if (result.State == "Finalizing") { await Until(() => f.Manager.Active is null); result = f.Manager.Status(start.Id); }
        Assert.Equal("Interrupted", result.State); Assert.Contains("budget", result.Error); Assert.NotEmpty(result.Chunks);
        Assert.True(result.Bytes < .01 * (1L << 30));
    }
    [Fact]
    public async Task RecoveryRepairsUnfinalizedHeadersAndDoesNotRestart()
    {
        using var f = new Fixture(); var start = await f.Start(); f.Source.Feed(160, 0, f.Clock); await Until(() => f.Manager.Status(start.Id).Frames > 0);
        var complete = await f.Manager.Stop(start.Id);
        var store = new AudioCaptureStore(f.Root); complete.State = "Recording"; complete.Chunks.Clear(); store.Save(complete);
        var path = Directory.GetFiles(store.DirectoryFor(start.Id), "*.wav").Single();
        using (var file = File.OpenWrite(path)) { file.Position = 4; file.Write(new byte[4]); }
        store.Recover(); var recovered = store.Read(start.Id);
        Assert.Equal("Interrupted", recovered.State); Assert.Single(recovered.Chunks); Assert.Equal(160, recovered.Frames);
    }
    [Fact]
    public async Task IdentifiersAndActiveDeletionAreRejected()
    {
        using var f = new Fixture(); var start = await f.Start();
        Assert.Throws<AgentSmith.Core.UserFacingException>(() => f.Manager.Status("../outside"));
        await Assert.ThrowsAsync<AgentSmith.Core.UserFacingException>(() => f.Manager.Delete(start.Id));
        await Assert.ThrowsAsync<AgentSmith.Core.UserFacingException>(() => f.Manager.AcquireExport(start.Id));
    }
}
