using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

[Collection("AudioService")]
public sealed class SharedAudioServiceTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly TestDirectory Directory = new();
        internal readonly CancellationTokenSource Stop = new();
        internal readonly AudioServiceHost Host;
        internal readonly Task Running;
        internal readonly string Input;
        internal AudioSubmission Submission(int start = 0) => new(Guid.NewGuid().ToString("N"), "fake", ".wav", 44,
            Convert.ToHexString(SHA256.HashData(new byte[44])), "audio_diarize", start, 0, "auto", ["diarization"], Environment.TickCount64 + 300_000);
        internal Fixture()
        {
            Input = Path.Combine(Directory.Path, "input.wav"); File.WriteAllBytes(Input, new byte[44]);
            Host = new(Directory.Path) { ValidateInstallation = _ => Directory.Path };
            Host.Infer = (_, _, _, _) => Task.FromResult(Result());
            Running = Host.Run(Stop.Token);
        }
        internal Task<AudioConnection> Connect() => AudioConnection.Open(AudioEndpoint.Resolve(Directory.Path), Stop.Token);
        internal Task<NamedPipeClientStream> Raw() => AudioConnection.Connect(AudioEndpoint.Resolve(Directory.Path), "client", Stop.Token);
        public async ValueTask DisposeAsync()
        {
            Stop.Cancel(); await Running.WaitAsync(TimeSpan.FromSeconds(10)); Stop.Dispose(); Directory.Dispose();
        }
    }
    private static JsonObject Result() => new() { ["startSeconds"] = 0d, ["durationSeconds"] = 1d,
        ["diarization"] = new JsonObject { ["status"] = "succeeded", ["speakerCount"] = 0d, ["turns"] = new JsonArray() } };
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
    private static async Task<AudioMessage> Read(NamedPipeClientStream pipe)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await AudioWire.Read(pipe, timeout.Token);
    }
    private static async Task Send(Stream pipe, AudioMessage message)
    {
        using var gate = new SemaphoreSlim(1);
        await AudioWire.Write(pipe, message, gate, default);
    }

    [Fact]
    public async Task StatusDoesNotCreateStorageAndAliasesResolveToOneService()
    {
        using var directory = new TestDirectory();
        var missing = Path.Combine(directory.Path, "missing");
        Assert.False((await AudioServiceControl.Status(missing)).Available);
        Assert.False(System.IO.Directory.Exists(missing));
        await using var f = new Fixture();
        var a = AudioEndpoint.Resolve(f.Directory.Path);
        var b = AudioEndpoint.Resolve(Path.Combine(f.Directory.Path.ToUpperInvariant(), ".", "unused", ".."));
        Assert.Equal(a.Identity, b.Identity);
        var status = await AudioServiceControl.Status(f.Directory.Path);
        Assert.True(status.Available); Assert.Equal("unloaded", status.Helper); Assert.Equal(0, status.Clients);
        Assert.False(File.Exists(Path.Combine(f.Directory.Path, "sessions.db")));
        var second = new AudioServiceHost(f.Directory.Path);
        await Assert.ThrowsAsync<IOException>(() => second.Run());
    }

    [Fact]
    public async Task MultiplexedParentAndWorkerRequestsSerializeAndReturnOwnedResults()
    {
        await using var f = new Fixture();
        var simultaneous = 0; var max = 0; var calls = 0;
        f.Host.Infer = async (_, request, _, ct) =>
        {
            max = Math.Max(max, Interlocked.Increment(ref simultaneous)); Interlocked.Increment(ref calls);
            await Task.Delay(60, ct); Interlocked.Decrement(ref simultaneous);
            var result = Result(); result["marker"] = request["startSeconds"]!.DeepClone(); return result;
        };
        using var client = await f.Connect();
        var results = await Task.WhenAll(Enumerable.Range(1, 4).Select(i => client.Run(f.Submission(i), f.Input, _ => { }, default)));
        Assert.Equal(1, max); Assert.Equal(4, calls);
        Assert.Equal(new[] { 1, 2, 3, 4 }, results.Select(r => r["marker"]!.GetValue<int>()));
        await Until(() => f.Host.Status().WaitingJobs == 0 && f.Host.Status().ActiveJobs == 0);
    }

    [Fact]
    public async Task QueuedCancellationReleasesCapacityWithoutInterruptingActiveJob()
    {
        await using var f = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        f.Host.Infer = async (_, _, _, ct) => { Interlocked.Increment(ref calls); await release.Task.WaitAsync(ct); return Result(); };
        using var client = await f.Connect();
        var active = client.Run(f.Submission(), f.Input, _ => { }, default);
        await Until(() => f.Host.Status().ActiveJobs == 1);
        using var cancel = new CancellationTokenSource();
        var queued = client.Run(f.Submission(), f.Input, _ => { }, cancel.Token);
        await Until(() => f.Host.Status().WaitingJobs == 1);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        await Until(() => f.Host.Status().WaitingJobs == 0);
        Assert.False(active.IsCompleted); Assert.Equal(1, calls);
        release.SetResult(); await active;
    }

    [Fact]
    public async Task ActiveCancellationAndHelperFailureAllowNextRequest()
    {
        await using var f = new Fixture();
        f.Host.Infer = async (_, request, _, ct) =>
        {
            if (request["startSeconds"]!.GetValue<int>() == 1) await Task.Delay(Timeout.Infinite, ct);
            if (request["startSeconds"]!.GetValue<int>() == 2) throw new IOException("helper crashed");
            return Result();
        };
        using var client = await f.Connect(); using var cancel = new CancellationTokenSource();
        var active = client.Run(f.Submission(1), f.Input, _ => { }, cancel.Token);
        await Until(() => f.Host.Status().ActiveJobs == 1);
        var next = client.Run(f.Submission(), f.Input, _ => { }, default);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active.WaitAsync(TimeSpan.FromSeconds(3)));
        try { await next.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { throw new Exception(System.Text.Json.JsonSerializer.Serialize(f.Host.Status())); }
        await Assert.ThrowsAsync<UserFacingException>(() => client.Run(f.Submission(2), f.Input, _ => { }, default));
        await client.Run(f.Submission(), f.Input, _ => { }, default);
    }

    [Fact]
    public async Task PerClientQueueLimitAndForcedShutdownAreExplicit()
    {
        await using var f = new Fixture();
        f.Host.Infer = async (_, _, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return Result(); };
        using var client = await f.Connect();
        var active = client.Run(f.Submission(), f.Input, _ => { }, default);
        await Until(() => f.Host.Status().ActiveJobs == 1);
        var waiting = Enumerable.Range(0, 4).Select(_ => client.Run(f.Submission(), f.Input, _ => { }, default)).ToArray();
        await Until(() => f.Host.Status().WaitingJobs == 4);
        var error = await Assert.ThrowsAsync<UserFacingException>(() => client.Run(f.Submission(), f.Input, _ => { }, default));
        Assert.Contains("queue is full", error.Message);
        var stop = AudioServiceControl.Stop(f.Directory.Path, true);
        foreach (var job in waiting.Prepend(active)) await Assert.ThrowsAnyAsync<Exception>(() => job);
        Assert.False((await stop).Available);
    }

    [Fact]
    public async Task GracefulStopDrainsAcceptedJobAndRejectsNewWork()
    {
        await using var f = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Host.Infer = async (_, _, _, ct) => { await release.Task.WaitAsync(ct); return Result(); };
        using var client = await f.Connect();
        var active = client.Run(f.Submission(), f.Input, _ => { }, default);
        await Until(() => f.Host.Status().ActiveJobs == 1);
        var stop = AudioServiceControl.Stop(f.Directory.Path, false);
        await Until(() => f.Host.Status().Draining);
        await Assert.ThrowsAsync<UserFacingException>(() => client.Run(f.Submission(), f.Input, _ => { }, default));
        release.SetResult(); await active; Assert.False((await stop).Available);
    }

    [Theory]
    [InlineData("hash")] [InlineData("length")] [InlineData("duplicate")] [InlineData("unknown")]
    public async Task InvalidUploadsAndRequestIdentitiesDoNotReachInference(string kind)
    {
        await using var f = new Fixture(); var calls = 0;
        f.Host.Infer = (_, _, _, _) => { calls++; return Task.FromResult(Result()); };
        using var pipe = await f.Raw(); var id = Guid.NewGuid().ToString("N"); var submission = f.Submission();
        if (kind == "hash") submission = submission with { Hash = new string('A', 64) };
        await Send(pipe, new("submit", id, AudioWire.Data(submission))); Assert.Equal("accepted", (await Read(pipe)).Type);
        if (kind == "duplicate") await Send(pipe, new("submit", id, AudioWire.Data(submission)));
        else if (kind == "unknown") await Send(pipe, new("cancel", Guid.NewGuid().ToString("N")));
        else
        {
            await Send(pipe, new("upload", id, Bytes: new byte[kind == "length" ? 43 : 44]));
            await Send(pipe, new("uploaded", id));
        }
        Assert.Equal("error", (await Read(pipe)).Type); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task UploadTimeoutAndDeadlineReleaseReservations()
    {
        await using var f = new Fixture(); f.Host.UploadTimeout = TimeSpan.FromMilliseconds(80);
        using var pipe = await f.Raw();
        var id = Guid.NewGuid().ToString("N");
        await Send(pipe, new("submit", id, AudioWire.Data(f.Submission())));
        Assert.Equal("accepted", (await Read(pipe)).Type); Assert.Equal("error", (await Read(pipe)).Type);
        await Until(() => f.Host.Status().WaitingJobs == 0);
        id = Guid.NewGuid().ToString("N");
        await Send(pipe, new("submit", id, AudioWire.Data(f.Submission() with { DeadlineTicks = Environment.TickCount64 - 1 })));
        Assert.Equal("error", (await Read(pipe)).Type);
    }

    [Theory]
    [InlineData("frame")] [InlineData("null")] [InlineData("version")] [InlineData("build")] [InlineData("identity")]
    public async Task MalformedAndIncompatibleConnectionsAreRejected(string kind)
    {
        await using var f = new Fixture(); var endpoint = AudioEndpoint.Resolve(f.Directory.Path);
        using var pipe = new NamedPipeClientStream(".", endpoint.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000);
        if (kind == "frame") { var size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, 65537); await pipe.WriteAsync(size); }
        else if (kind == "null") await Send(pipe, new("hello"));
        else await Send(pipe, new("hello", Data: AudioWire.Data(new AudioHello(kind == "version" ? 99 : AudioWire.Version,
            kind == "build" ? "other" : AudioWire.Build, kind == "identity" ? "other" : endpoint.Identity, "client", Environment.ProcessId))));
        Assert.Equal("error", (await Read(pipe)).Type);
        Assert.True((await AudioServiceControl.Status(f.Directory.Path)).Available);
    }

    [Fact]
    public async Task DisconnectionCancelsWorkAndNextConnectionCanContinue()
    {
        await using var f = new Fixture();
        f.Host.Infer = async (_, request, _, ct) => { if (request["startSeconds"]!.GetValue<int>() == 1) await Task.Delay(Timeout.Infinite, ct); return Result(); };
        var client = await f.Connect();
        var job = client.Run(f.Submission(1), f.Input, _ => { }, default);
        await Until(() => f.Host.Status().ActiveJobs == 1);
        client.Dispose(); await Assert.ThrowsAnyAsync<Exception>(() => job);
        await Until(() => f.Host.Status().ActiveJobs == 0 && f.Host.Status().Clients == 0);
        using var next = await f.Connect();
        await next.Run(f.Submission(), f.Input, _ => { }, default);
    }

    [Fact]
    public async Task GenerationIsPinnedAndChangedFingerprintIsRejected()
    {
        await using var f = new Fixture(); f.Host.ValidateInstallation = null;
        var seen = new List<string>();
        f.Host.Infer = (directory, _, _, _) => { seen.Add(Path.GetFileName(directory)); return Task.FromResult(Result()); };
        AudioSubmission Install(int protocol)
        {
            var name = Guid.NewGuid().ToString("N");
            var helper = Path.Combine(f.Directory.Path, "audio", "installations", name, "helper");
            System.IO.Directory.CreateDirectory(helper);
            File.WriteAllText(Path.Combine(helper, "downloads.json"), protocol == 1 ? """{"protocol":1}""" : """{"helperProtocol":2}""");
            File.WriteAllText(Path.Combine(helper, "audio_helper.py"), "# test helper");
            File.WriteAllText(Path.Combine(helper, "requirements.lock"), "# test lock");
            var fingerprint = LocalAudioInstallation.Fingerprint(helper);
            File.WriteAllText(Path.Combine(helper, "..", "ready.json"), Json.Serialize(new { fingerprint }));
            return f.Submission() with { Generation = name, Fingerprint = fingerprint };
        }
        var old = Install(1); var current = Install(2);
        using var client = await f.Connect();
        await client.Run(old, f.Input, _ => { }, default);
        await client.Run(current, f.Input, _ => { }, default);
        await client.Run(old, f.Input, _ => { }, default);
        Assert.Equal(new[] { old.Generation, current.Generation, old.Generation }, seen);
        await Assert.ThrowsAsync<UserFacingException>(() => client.Run(current with { Fingerprint = "changed" }, f.Input, _ => { }, default));
        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public async Task OversizedResultDoesNotPoisonNextRequest()
    {
        await using var f = new Fixture();
        f.Host.Infer = (_, request, _, _) =>
        {
            var result = Result();
            if (request["startSeconds"]!.GetValue<int>() == 1) result["extra"] = new string('x', LocalAudioProcess.OutputLimit);
            return Task.FromResult(result);
        };
        using var client = await f.Connect();
        await Assert.ThrowsAsync<UserFacingException>(() => client.Run(f.Submission(1), f.Input, _ => { }, default));
        await client.Run(f.Submission(), f.Input, _ => { }, default);
    }

    [Fact]
    public async Task QueuedDeadlineIncludesTimeBehindActiveWork()
    {
        await using var f = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        f.Host.Infer = async (_, _, _, ct) => { calls++; await release.Task.WaitAsync(ct); return Result(); };
        using var client = await f.Connect();
        var active = client.Run(f.Submission(), f.Input, _ => { }, default);
        await Until(() => f.Host.Status().ActiveJobs == 1);
        var queued = client.Run(f.Submission() with { DeadlineTicks = Environment.TickCount64 + 100 }, f.Input, _ => { }, default);
        await Assert.ThrowsAsync<UserFacingException>(() => queued.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, calls); Assert.False(active.IsCompleted);
        release.SetResult(); await active;
    }

    [Fact]
    public void ConcurrentTemporaryCreationAndCleanupPreserveActiveSnapshots()
    {
        using var root = new TestDirectory();
        Parallel.For(0, 300, _ =>
        {
            using var upload = new AudioTemporary(root.Path, "service-jobs");
            var path = Path.Combine(upload.Directory, "input.wav"); File.WriteAllBytes(path, new byte[100]);
            Thread.Sleep(10);
            Assert.True(File.Exists(path));
        });
    }
}
