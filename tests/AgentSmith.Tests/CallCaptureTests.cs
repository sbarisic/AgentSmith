using AgentSmith.Core;
using AgentSmith.Tools;
using NAudio.Wave;
using Xunit;

namespace AgentSmith.Tests;

public sealed class CallCaptureTests
{
    [Fact]
    public void StereoMeterIncludesTheRightChannel() => Assert.Equal(.5, AudioMeter.Peak(new byte[] { 0, 0, 0, 64 }, new WaveFormat(48000, 16, 2)));
    private sealed class Source(int rate = 16000) : ICaptureSource
    {
        public WaveFormat Format { get; } = new(rate, 16, 1);
        public event CapturePacket? Data;
        public event Action<Exception?>? Stopped;
        public bool FailStart, Started, Disposed;
        public void Start() { if (FailStart) throw new IOException("Device busy"); Started = true; }
        public void Stop() { Started = false; Stopped?.Invoke(null); }
        public void Dispose() { Disposed = true; Started = false; }
        public void Feed(int frames, long position, long time, short value = 16384, bool discontinuity = false)
        {
            var bytes = new byte[frames * 2]; for (var i = 0; i < frames; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 2, 2), value);
            Data?.Invoke(bytes, position, time, discontinuity, false);
        }
        public void Lose() => Stopped?.Invoke(new IOException("Device disconnected"));
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly TestDirectory Directory = new();
        internal readonly Source Call = new(), Mic = new(48000);
        internal readonly AudioCaptureManager Manager;
        internal long Clock = 100_000_000;
        internal Fixture()
        {
            Manager = new(Directory.Path) {
                Devices = () => [new("call-device", "Headset output", true)],
                Microphones = () => [new("mic-device", "Headset microphone", true, "microphone")],
                CreateSource = id => id == "call-device" ? Call : Mic,
                FreeBytes = () => 100L << 30, Now = () => Clock, ChunkBytes = 16000 };
        }
        internal Task<CaptureManifest> Start(double budget = 10) => Manager.Start(new(Guid.NewGuid().ToString("N"), "call-device", budget, MicrophoneId: "mic-device"));
        public void Dispose() { Manager.Shutdown(true).GetAwaiter().GetResult(); Manager.Dispose(); Directory.Dispose(); }
    }
    private static async Task Until(Func<bool> predicate)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!predicate()) await Task.Delay(10, deadline.Token); }
    private static async Task<CaptureManifest> Stop(Fixture f, string id)
    { await f.Manager.Stop(id); await Until(() => f.Manager.Active is null); return f.Manager.Status(id); }

    [Fact]
    public async Task TwoTracksHaveCommonClockSeparateFormatsAndNoCrossTrackChunks()
    {
        using var f = new Fixture(); var start = await f.Start();
        f.Call.Feed(16000, 0, f.Clock); f.Mic.Feed(48000, 0, f.Clock); f.Clock += 10_000_000;
        await Until(() => f.Manager.Status(start.Id).Tracks.All(t => t.Frames > 0));
        var result = await Stop(f, start.Id);
        Assert.Equal("Completed", result.State); Assert.Equal(2, result.Version);
        Assert.Equal(result.Tracks[0].StartQpc, result.Tracks[1].StartQpc);
        Assert.Equal(new[] { 16000, 48000 }, result.Tracks.Select(t => t.SampleRate));
        Assert.All(result.Tracks, t => Assert.All(t.Chunks, c => Assert.StartsWith(t.TrackId + "-chunk-", c.File)));
        Assert.Equal(result.AllChunks().Sum(c => c.Bytes), result.Bytes);
        var store = new AudioCaptureStore(f.Directory.Path); var saved = store.Read(start.Id); Assert.Equal(2, saved.Tracks.Count);
        foreach (var track in saved.Tracks)
        {
            using var reader = new CaptureAudioReader(store.DirectoryFor(start.Id), track);
            Assert.InRange(reader.Sample(.5), .499f, .501f);
        }
    }
    [Fact]
    public async Task RecorderMuteNeverWritesSuppressedMicrophoneSamples()
    {
        using var f = new Fixture(); var start = await f.Start(); f.Manager.Mute(start.Id, true);
        f.Call.Feed(16000, 0, f.Clock); f.Mic.Feed(48000, 0, f.Clock); f.Clock += 10_000_000;
        await Until(() => f.Manager.Status(start.Id).Tracks.All(t => t.Frames > 0));
        f.Manager.Mute(start.Id, false); f.Mic.Feed(48000, 48000, f.Clock); f.Call.Feed(16000, 16000, f.Clock); f.Clock += 10_000_000;
        await Until(() => f.Manager.Status(start.Id).Tracks[1].Frames >= 96000);
        var result = await Stop(f, start.Id); var mic = result.Tracks[1];
        Assert.Contains(mic.Coverage, c => c.Kind == "IntentionalMute" && c.Frames == 48000);
        using var reader = new CaptureAudioReader(new AudioCaptureStore(f.Directory.Path).DirectoryFor(start.Id), mic);
        Assert.Equal(0, reader.Sample(.5)); Assert.InRange(reader.Sample(1.5), .499f, .501f);
    }
    [Fact]
    public async Task FailedSecondStartRollsBackFirstAndAllowsAnotherRecording()
    {
        using var f = new Fixture(); f.Mic.FailStart = true;
        await Assert.ThrowsAsync<IOException>(() => f.Start());
        Assert.False(f.Call.Started); Assert.True(f.Call.Disposed); Assert.True(f.Mic.Disposed); Assert.Null(f.Manager.Active);
        Assert.Equal("Failed", Assert.Single(f.Manager.List(1)).State);
    }
    [Fact]
    public async Task UnavailableSecondEndpointDoesNotOpenFirst()
    {
        using var f = new Fixture(); f.Manager.Microphones = () => [];
        await Assert.ThrowsAsync<UserFacingException>(() => f.Start()); Assert.False(f.Call.Started); Assert.Empty(f.Manager.List(1));
    }
    [Fact]
    public async Task DeviceLossStopsBothAndPreservesOtherTrack()
    {
        using var f = new Fixture(); var start = await f.Start();
        f.Call.Feed(1600, 0, f.Clock); f.Mic.Feed(4800, 0, f.Clock);
        await Until(() => f.Manager.Status(start.Id).Tracks.All(t => t.Frames > 0)); f.Mic.Lose();
        await Until(() => f.Manager.Active is null); var saved = f.Manager.Status(start.Id);
        Assert.Equal("Interrupted", saved.State); Assert.All(saved.Tracks, t => Assert.NotEmpty(t.Chunks));
        Assert.False(f.Call.Started); Assert.False(f.Mic.Started);
    }
    [Fact]
    public async Task PairBudgetIsSharedAndRetainsAudio()
    {
        using var f = new Fixture(); f.Manager.ChunkBytes = 16 * 1024 * 1024; var start = await f.Start(.01);
        f.Call.Feed(1600, 0, f.Clock); f.Mic.Feed(4800, 0, f.Clock);
        await Until(() => f.Manager.Status(start.Id).Tracks.All(t => t.Frames > 0)); f.Clock += 1_000_000_000;
        var result = await Stop(f, start.Id);
        Assert.Equal("Interrupted", result.State); Assert.True(result.Bytes < .01 * 1073741824); Assert.NotEmpty(result.AllChunks());
    }
    [Fact]
    public async Task PairRecoveryReadsTrackCheckpointsAndMarksUnknownTail()
    {
        using var f = new Fixture(); var start = await f.Start(); f.Call.Feed(1600, 0, f.Clock); f.Mic.Feed(4800, 0, f.Clock);
        await Until(() => f.Manager.Status(start.Id).Tracks.All(t => t.Frames > 0)); var complete = await Stop(f, start.Id);
        var store = new AudioCaptureStore(f.Directory.Path); complete.State = "Recording"; foreach (var track in complete.Tracks) track.Chunks.Clear(); store.Save(complete);
        store.Recover(); var recovered = store.Read(start.Id);
        Assert.Equal("Interrupted", recovered.State); Assert.All(recovered.Tracks, t => { Assert.NotEmpty(t.Chunks); Assert.Equal("Interrupted", t.State); });
    }
    [Fact]
    public async Task SecondClientCannotStartWhileCallIsActive()
    { using var f = new Fixture(); await f.Start(); await Assert.ThrowsAsync<UserFacingException>(() => f.Start()); }

    [Theory]
    [InlineData(.0001, .37)]
    [InlineData(-.0002, 1.13)]
    public void TwoHourTimingCompensatesStartOffsetAndClockDrift(double drift, double offset)
    {
        var track = new CaptureManifest { SampleRate = 48000, StartQpc = 100_000_000 };
        var anchors = Enumerable.Range(0, 7201).Select(second => new CaptureAnchor((long)Math.Round(second * 48000 * (1 + drift)), track.StartQpc + (long)((second + offset) * 10_000_000))).ToArray();
        foreach (var seconds in new[] { offset + .1, 3600.25 + offset, 7199.8 + offset })
        {
            var native = CaptureAudioReader.SourceFrame(track, anchors, seconds);
            var expected = (seconds - offset) * 48000 * (1 + drift);
            Assert.InRange(Math.Abs(native - expected) / 48000, 0, .0001);
        }
    }
    [Fact]
    public void TwoHourNativeChunkPlaybackKeepsMarkersAlignedWithinOneHundredMilliseconds()
    {
        using var directory = new TestDirectory();
        var events = new[] { 2d, 3600d, 7199d };
        foreach (var configuration in new[] { ("call", .0001, .37), ("mic", -.0002, 1.13) })
        {
            const int rate = 8000;
            var drift = configuration.Item2; var offset = configuration.Item3;
            var track = new CaptureManifest { Id = Guid.NewGuid().ToString("N"), TrackId = configuration.Item1, State = "Completed", SampleRate = rate,
                Channels = 1, BitsPerSample = 16, BlockAlign = 2, Format = "16 bit PCM", StartQpc = 100_000_000,
                Frames = (long)Math.Ceiling(7200 * rate * (1 + drift)), TimelineSeconds = 7202 };
            track.Anchors = Enumerable.Range(0, 7201).Select(second => new CaptureAnchor((long)Math.Round(second * rate * (1 + drift)), track.StartQpc + (long)((second + offset) * 10_000_000))).ToList();
            var index = 0;
            for (long start = 0; start < track.Frames; start += rate * 60)
            {
                var frames = Math.Min(rate * 60, track.Frames - start); var name = $"{track.TrackId}-chunk-{++index:D6}.wav";
                using (var file = new FileStream(Path.Combine(directory.Path, name), FileMode.CreateNew, FileAccess.ReadWrite))
                using (var writer = new BinaryWriter(file))
                {
                    writer.Write("RIFF"u8); writer.Write((int)(36 + frames * 2)); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                    writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write((int)(frames * 2));
                    file.SetLength(44 + frames * 2);
                    foreach (var seconds in events)
                    {
                        var marker = (long)Math.Round((seconds - offset) * rate * (1 + drift));
                        for (var sample = Math.Max(start, marker - 80); sample < Math.Min(start + frames, marker + 80); sample++)
                        { file.Position = 44 + (sample - start) * 2; writer.Write((short)16384); }
                    }
                }
                track.Chunks.Add(new(name, start, frames, 44 + frames * 2, "fixture"));
            }
            using var reader = new CaptureAudioReader(directory.Path, track);
            foreach (var seconds in events)
            {
                Assert.InRange(reader.Sample(seconds), .49f, .51f);
                Assert.Equal(0, reader.Sample(seconds - .1)); Assert.Equal(0, reader.Sample(seconds + .1));
            }
        }
    }
    [Theory]
    [InlineData("../chunk-000001.wav")]
    [InlineData("mic-../chunk-000001.wav")]
    [InlineData("call-manifest.json")]
    public void TrackChunkNamesRejectTraversal(string name) => Assert.False(AudioCaptureStore.ValidChunk(name));
}
