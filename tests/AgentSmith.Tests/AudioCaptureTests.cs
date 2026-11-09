using AgentSmith.Tools;
using NAudio.Wave;
using Xunit;

namespace AgentSmith.Tests;

public sealed class AudioCaptureTests
{
    internal sealed class Source(int rate = 16000) : ICaptureSource
    {
        public WaveFormat Format { get; } = new(rate, 16, 1);
        public event CapturePacket? Data;
        public event Action<Exception?>? Stopped;
        public void Start() { }
        public void Stop() => Stopped?.Invoke(null);
        public void Dispose() { }
        public void Feed(int frames, long position, long qpc, bool gap = false, bool invalidTime = false, byte value = 0)
        {
            var bytes = new byte[frames * 2]; Array.Fill(bytes, value);
            Data?.Invoke(bytes, position, qpc, gap, invalidTime);
        }
        public void Lost() => Stopped?.Invoke(new IOException("Device lost"));
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "smith-capture-" + Guid.NewGuid().ToString("N"));
        internal readonly Source Source;
        internal long Clock = 100_000_000;
        internal readonly AudioCaptureManager Manager;
        internal Fixture(int rate = 16000)
        {
            Source = new(rate);
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
    public async Task CaptureFailuresAreInterruptedAndNeverReportedComplete(string kind)
    {
        using var f = new Fixture(); var start = await f.Start();
        f.Source.Feed(160, 0, f.Clock); await Until(() => f.Manager.Status(start.Id).Frames > 0);
        if (kind == "overflow") { f.Manager.BufferLimit = 1; f.Source.Feed(160, 160, f.Clock + 100_000); }
        if (kind == "device") f.Source.Lost();
        if (kind == "timestamp") f.Source.Feed(160, 160, 0, invalidTime: true);
        await Until(() => f.Manager.Active is null);
        Assert.Equal("Interrupted", f.Manager.Status(start.Id).State); Assert.NotNull(f.Manager.Status(start.Id).Error);
    }
    [Theory]
    [InlineData(-625)] // one 16 kHz frame of QPC rounding
    [InlineData(0)]
    [InlineData(625)]
    public async Task StalledDeviceCounterUsesCorroboratingTimestampsWithoutLosingSamples(long rounding)
    {
        using var f = new Fixture(); var start = await f.Start();
        f.Source.Feed(160, 9000, f.Clock, value: 7);
        for (var i = 1; i < 100; i++)
            f.Source.Feed(160, 0, f.Clock + i * 100_000 + rounding, value: 7);
        f.Clock += 10_000_000;
        var result = await f.Manager.Stop(start.Id);
        Assert.Equal("Completed", result.State);
        Assert.Null(result.Error); Assert.Empty(result.Coverage); Assert.Equal(16000, result.Frames);
        var diagnostics = Assert.IsType<CaptureTimingDiagnostics>(result.TimingDiagnostics);
        Assert.Equal(100, diagnostics.Packets); Assert.Equal(99, diagnostics.PositionRegressions);
        Assert.Equal(99, diagnostics.TimestampFallbacks); Assert.Equal(0, diagnostics.DeviceDiscontinuities);
        Assert.Equal(0, diagnostics.OverlapFramesDiscarded); Assert.Equal(8, diagnostics.Observations.Count);
        foreach (var chunk in result.Chunks)
        {
            using var wave = new WaveFileReader(f.Manager.ChunkPath(start.Id, chunk.File));
            var samples = new byte[checked((int)wave.Length)]; wave.ReadExactly(samples);
            Assert.All(samples, sample => Assert.Equal((byte)7, sample));
        }
        var saved = new AudioCaptureStore(f.Root).Read(start.Id);
        Assert.Equal(99, saved.TimingDiagnostics!.TimestampFallbacks);
    }
    [Theory]
    [InlineData(true, 100_000, "DeviceDiscontinuity")]
    [InlineData(false, 1_000_000, "PositionUncertain")]
    [InlineData(false, 0, "PositionUncertain")]
    public async Task CounterFallbackNeverHidesWindowsGlitchesOrUncertainTimestamps(bool flagged, long timestampOffset, string coverage)
    {
        using var f = new Fixture(); var start = await f.Start();
        f.Source.Feed(160, 0, f.Clock);
        f.Source.Feed(160, 0, f.Clock + timestampOffset, gap: flagged);
        f.Clock += 2_000_000;
        var result = await f.Manager.Stop(start.Id);
        Assert.Equal("Completed", result.State); // Normal stop; coverage warnings are retained for analysis.
        Assert.Contains(result.Coverage, c => c.Kind == coverage);
        Assert.Equal(0, result.TimingDiagnostics!.TimestampFallbacks);
        Assert.Equal(flagged ? 1 : 0, result.TimingDiagnostics.DeviceDiscontinuities);
        Assert.Contains(flagged ? "Windows reported" : "position moved backwards", result.Error);
    }
    [Fact]
    public async Task LegacyManifestHasNoTimingDiagnostics()
    {
        using var f = new Fixture(); var start = await f.Start();
        var result = await f.Manager.Stop(start.Id);
        result.TimingDiagnostics = null;
        var store = new AudioCaptureStore(f.Root); store.Save(result);
        Assert.Null(store.Read(start.Id).TimingDiagnostics);
    }
    [Fact]
    public async Task ResampledBluetoothPositionsAndRecordedTimestampJitterPreserveEverySample()
    {
        using var f = new Fixture(48000); var start = await f.Start();
        // Packet timing pattern observed on the headset: 16 kHz position units,
        // 48 kHz delivered samples, and uneven QPC increments despite continuous data.
        long[] times = [0, 80883, 160092, 240429, 401199, 480243, 560339, 720100];
        for (var i = 0; i < times.Length; i++)
            f.Source.Feed(i == 0 ? 463 : 480, i * 160, f.Clock + times[i], value: 7);
        const int frames = 463 + 7 * 480;
        f.Clock += (long)Math.Round(frames * 10_000_000d / 48000);
        var result = await f.Manager.Stop(start.Id);
        Assert.Equal("Completed", result.State); Assert.Null(result.Error);
        Assert.Equal(frames, result.Frames); Assert.Empty(result.Coverage);
        Assert.Equal(0, result.TimingDiagnostics!.PositionRegressions);
        Assert.Equal(7, result.TimingDiagnostics.PositionUnitMismatches);
        Assert.Equal(0, result.TimingDiagnostics.OverlapFramesDiscarded);
        foreach (var chunk in result.Chunks)
        {
            using var reader = new WaveFileReader(f.Manager.ChunkPath(start.Id, chunk.File));
            var bytes = new byte[checked((int)reader.Length)]; reader.ReadExactly(bytes);
            Assert.All(bytes, b => Assert.Equal((byte)7, b));
        }
    }
    [Fact]
    public async Task FlaggedOverlappingTimestampsKeepAudioAndCoverageOnNormalStop()
    {
        using var f = new Fixture(48000); var start = await f.Start();
        f.Source.Feed(480, 0, f.Clock, value: 7);
        f.Source.Feed(480, 160, f.Clock + 80_000, gap: true, value: 9);
        f.Clock += 200_000;
        var result = await f.Manager.Stop(start.Id);
        Assert.Equal("Completed", result.State); Assert.NotNull(result.Error);
        Assert.Equal(960, result.Frames);
        Assert.Contains(result.Coverage, c => c.Kind == "DeviceDiscontinuity");
        Assert.Equal(0, result.TimingDiagnostics!.OverlapFramesDiscarded);
        using var reader = new WaveFileReader(f.Manager.ChunkPath(start.Id, result.Chunks[0].File));
        var bytes = new byte[1920]; reader.ReadExactly(bytes);
        Assert.All(bytes.Take(960), b => Assert.Equal((byte)7, b));
        Assert.All(bytes.Skip(960), b => Assert.Equal((byte)9, b));
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
