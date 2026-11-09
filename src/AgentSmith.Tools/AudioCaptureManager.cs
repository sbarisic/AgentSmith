using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using AgentSmith.Core;
using NAudio.Wave;

namespace AgentSmith.Tools;

public sealed record CaptureStart(string OperationId, string? DeviceId = null, double MaxStorageGiB = 10, string? Conversation = null, string? MicrophoneId = null);
public sealed record CaptureSummary(string Id, string State, string Device, double DurationSeconds, long Bytes, long BudgetBytes, bool BudgetWarning, string? Error);

internal sealed partial class AudioCaptureManager : IDisposable
{
    private readonly AudioCaptureStore store;
    private readonly SemaphoreSlim gate = new(1);
    private ICaptureRecording? active;
    private bool shuttingDown;
    internal Func<CaptureDevice[]> Devices = () => OperatingSystem.IsWindows() ? WindowsCaptureSource.Devices() : throw new PlatformNotSupportedException();
    internal Func<CaptureDevice[]> Microphones = () => OperatingSystem.IsWindows() ? WindowsCaptureSource.Devices(true) : throw new PlatformNotSupportedException();
    internal Func<string, ICaptureSource> CreateSource = id => OperatingSystem.IsWindows() ? new WindowsCaptureSource(id) : throw new PlatformNotSupportedException();
    internal Func<long> FreeBytes;
    internal long DiskReserve = 1024L * 1024 * 1024;
    internal long BufferLimit = 16 * 1024 * 1024;
    internal int ChunkBytes = 16 * 1024 * 1024;
    internal Func<long> Now = CaptureClock.Qpc;
    internal AudioCaptureManager(string directory)
    {
        store = new(directory);
        FreeBytes = () => new DriveInfo(Path.GetPathRoot(store.Root)!).AvailableFreeSpace;
    }
    internal void Recover() => store.Recover();
    internal CaptureSummary? Active => active is { } r && !r.Done.IsCompleted ? Summarize(r.Snapshot()) : null;
    internal static CaptureSummary Summarize(CaptureManifest m) => new(m.Id, m.State, m.Device.Name, m.DurationSeconds, m.Bytes, m.BudgetBytes, m.BudgetWarning, m.Error);
    internal CaptureSummary[] List(int page)
    {
        if (page is < 1 or > 100000) throw new UserFacingException("Recording page must be positive.");
        return store.Ids().Skip((page - 1) * 20).Take(20).Select(id => Summarize(Status(id))).ToArray();
    }
    internal CaptureManifest Status(string? id)
    {
        var recording = active;
        if (string.IsNullOrEmpty(id)) id = Active?.Id ?? throw new UserFacingException("No active recording. Use capture list and an explicit ID.");
        return recording?.Id == id ? recording.Snapshot() : store.Read(id);
    }
    internal async Task<CaptureManifest> Start(CaptureStart request)
    {
        if (!Guid.TryParseExact(request.OperationId, "N", out _) || !double.IsFinite(request.MaxStorageGiB) || request.MaxStorageGiB is < 0.01 or > 100000 || request.Conversation?.Length > 128)
            throw new UserFacingException("Invalid capture operation or storage limit (0.01..100000 GiB).");
        await gate.WaitAsync();
        try
        {
            if (shuttingDown) throw new UserFacingException("Service is draining; recording cannot start.");
            // Use the persisted operation ID as the capture ID: retries never start twice, even after restart.
            if (store.Ids().Contains(request.OperationId)) return Status(request.OperationId);
            if (Active is not null) throw new UserFacingException("A recording is already active. Inspect capture status.");
            if (request.MicrophoneId is not null) return StartPair(request);
            var devices = Devices();
            var device = devices.SingleOrDefault(d => string.IsNullOrEmpty(request.DeviceId) ? d.Default : d.Id == request.DeviceId)
                ?? throw new UserFacingException("Playback device is unavailable. Run smith audio devices.");
            var budget = checked((long)(request.MaxStorageGiB * 1024 * 1024 * 1024));
            var used = store.UsedBytes();
            if (used >= budget || FreeBytes() <= DiskReserve) throw new UserFacingException("Recording storage limit or free-disk reserve reached. Export/delete recordings or raise the limit.");
            store.DirectoryFor(request.OperationId, true);
            var lease = store.Lease(request.OperationId);
            ICaptureSource? source = null;
            var manifest = new CaptureManifest { Id = request.OperationId, OperationId = request.OperationId,
                Owner = OperatingSystem.IsWindows() ? AudioServiceWindows.Sid : Environment.UserName,
                Conversation = request.Conversation, Device = device, BudgetBytes = budget, RetainedBytes = used };
            try
            {
                store.Save(manifest);
                source = CreateSource(device.Id);
                active?.Dispose();
                active = new Recording(this, store, manifest, source, lease, used);
                active.Start(); return active.Snapshot();
            }
            catch (Exception ex)
            {
                source?.Dispose(); lease?.Dispose();
                manifest.State = "Failed"; manifest.Error = ex.Message; manifest.Ended = DateTimeOffset.UtcNow; store.Save(manifest); throw;
            }
        }
        finally { gate.Release(); }
    }
    internal async Task<CaptureManifest> Stop(string? id, bool force = false)
    {
        await gate.WaitAsync();
        try
        {
            var manifest = Status(id);
            if (active?.Id == manifest.Id && !active.Done.IsCompleted)
            {
                active.RequestStop(force ? "Recording interrupted by forced service stop." : null);
                await Task.WhenAny(active.Done, Task.Delay(TimeSpan.FromSeconds(5)));
                return active.Snapshot();
            }
            return manifest;
        }
        finally { gate.Release(); }
    }
    internal async Task Shutdown(bool force)
    {
        await gate.WaitAsync();
        try { shuttingDown = true; active?.RequestStop(force ? "Recording interrupted by forced service stop." : null); }
        finally { gate.Release(); }
        if (active is { } recording) await recording.Done;
    }
    internal async Task<IDisposable> AcquireExport(string id)
    {
        await gate.WaitAsync();
        try
        {
            if (Status(id).State is "Starting" or "Recording" or "Finalizing") throw new UserFacingException("Stop recording before export.");
            return store.Lease(id);
        }
        finally { gate.Release(); }
    }
    internal string ChunkPath(string id, string name)
    {
        if (!AudioCaptureStore.ValidChunk(name)) throw new IOException("Invalid chunk name.");
        var path = Path.Combine(store.DirectoryFor(id), name); AudioCaptureStore.Check(path); return path;
    }
    internal async Task Delete(string id)
    {
        await gate.WaitAsync();
        try
        {
            if (Status(id).State is "Starting" or "Recording" or "Finalizing") throw new UserFacingException("Stop recording before deletion.");
            var directory = store.DirectoryFor(id);
            using (store.Lease(id))
            {
                if (Directory.EnumerateDirectories(directory).Any()) throw new IOException("Unexpected recording subdirectory.");
                var files = Directory.GetFiles(directory);
                foreach (var file in files) AudioCaptureStore.Check(file);
                foreach (var file in files.Where(f => Path.GetFileName(f) != "recording.lock")) File.Delete(file);
            }
            File.Delete(Path.Combine(directory, "recording.lock")); Directory.Delete(directory);
            if (active?.Id == id) { active.Dispose(); active = null; }
        }
        finally { gate.Release(); }
    }
    public void Dispose() { active?.Dispose(); gate.Dispose(); }

    private sealed record Packet(byte[] Data, long Position, long Qpc, bool Discontinuity, bool TimestampError, bool Muted);
    private interface ICaptureRecording : IDisposable
    {
        string Id { get; }
        Task Done { get; }
        CaptureManifest Snapshot();
        void Start();
        void RequestStop(string? error);
        void Mute(bool muted);
    }
    internal void Mute(string id, bool muted)
    {
        if (active?.Id != id || active.Done.IsCompleted) throw new UserFacingException("Recording is not active.");
        active.Mute(muted);
    }
    private sealed class Recording : ICaptureRecording
    {
        private readonly AudioCaptureManager owner;
        private readonly AudioCaptureStore store;
        private readonly CaptureManifest manifest;
        private readonly ICaptureSource source;
        private readonly FileStream? lease;
        private readonly long priorBytes;
        private readonly object sync = new();
        private readonly Channel<Packet> packets = Channel.CreateBounded<Packet>(1024);
        private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task done = Task.CompletedTask;
        private long queuedBytes;
        private long startQpc;
        private long stopQpc;
        private long lastPacketEnd = -1;
        private long lastPacketPosition = -1;
        private long nextPacketQpc;
        private long nextPacketFrame;
        private long chunkStart;
        private int chunkIndex;
        private long chunkData;
        private WaveFileWriter? writer;
        private FileStream? chunkStream;
        private string? chunkPath;
        private int stopRequested;
        private string? pendingError;
        private bool disposed;
        private int muted;
        private double level;
        private long lastDataAt;
        public void Mute(bool value) { Interlocked.Exchange(ref muted, value ? 1 : 0); }
        internal Action<int>? ReserveBytes;
        internal long SharedStart;
        public string Id => manifest.Id;
        public Task Done => done;
        internal Recording(AudioCaptureManager owner, AudioCaptureStore store, CaptureManifest manifest, ICaptureSource source, FileStream? lease, long priorBytes)
        {
            this.owner = owner; this.store = store; this.manifest = manifest; this.source = source; this.lease = lease; this.priorBytes = priorBytes;
            var format = source.Format;
            if (format.SampleRate is < 8000 or > 384000 || format.BlockAlign is < 1 or > 256) throw new IOException("Unsupported recording format.");
            manifest.SampleRate = format.SampleRate; manifest.Channels = format.Channels; manifest.BitsPerSample = format.BitsPerSample;
            manifest.BlockAlign = format.BlockAlign; manifest.Format = format.ToString();
            manifest.FloatingPoint = format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible extensible && extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
            source.Data += Data;
            source.Stopped += error => { if (error is not null) Fail(error.Message); else if (Volatile.Read(ref stopRequested) == 0) Fail("Capture device stopped unexpectedly."); stopped.TrySetResult(); };
        }
        public CaptureManifest Snapshot() { lock (sync) { var copy = JsonSerializer.Deserialize<CaptureManifest>(JsonSerializer.Serialize(manifest))!; copy.Level = Environment.TickCount64 - Interlocked.Read(ref lastDataAt) < 500 ? Volatile.Read(ref level) : 0; copy.Muted = Volatile.Read(ref muted) != 0; return copy; } }
        public void Start()
        {
            startQpc = SharedStart == 0 ? owner.Now() : SharedStart; manifest.StartQpc = startQpc;
            manifest.TimingDiagnostics = new();
            lock (sync) { manifest.State = "Recording"; store.Save(manifest); }
            try { source.Start(); done = Task.Run(Write); }
            catch { manifest.State = "Failed"; throw; }
        }
        // Device callbacks must never wait on a disk writer or a snapshot lock.
        private void Fail(string error) => Interlocked.CompareExchange(ref pendingError, error, null);
        private void Data(ReadOnlySpan<byte> bytes, long position, long qpc, bool discontinuity, bool timestampError)
        {
            if (bytes.Length == 0) return;
            if (bytes.Length % manifest.BlockAlign != 0) { Fail("Invalid capture frame data."); return; }
            if (Interlocked.Add(ref queuedBytes, bytes.Length) > owner.BufferLimit)
            { Interlocked.Add(ref queuedBytes, -bytes.Length); Fail("Capture buffer overflow or invalid frame data; audio is missing."); return; }
            var isMuted = Volatile.Read(ref muted) != 0;
            var data = isMuted ? new byte[bytes.Length] : bytes.ToArray();
            var peak = AudioMeter.Peak(data, source.Format);
            Volatile.Write(ref level, peak);
            Interlocked.Exchange(ref lastDataAt, Environment.TickCount64);
            var packet = new Packet(data, position, qpc, discontinuity, timestampError, isMuted);
            if (!packets.Writer.TryWrite(packet)) { Interlocked.Add(ref queuedBytes, -bytes.Length); Fail("Capture packet queue overflow; audio is missing."); }
        }
        public void RequestStop(string? error)
        {
            if (error is not null) Fail(error);
            if (Interlocked.Exchange(ref stopRequested, 1) != 0 || done.IsCompleted && manifest.State is not "Recording") return;
            stopQpc = owner.Now();
            lock (sync) manifest.State = "Finalizing";
            try { source.Stop(); } catch (Exception ex) { Fail(ex.Message); stopped.TrySetResult(); }
        }
        private long FrameAt(long qpc) => Math.Max(0, (long)Math.Round((qpc - startQpc) * (double)manifest.SampleRate / 10_000_000));
        private async Task Write()
        {
            var checkpoint = Stopwatch.StartNew();
            var stopWait = Stopwatch.StartNew();
            var priorTick = DateTimeOffset.UtcNow;
            try
            {
                while (true)
                {
                    var tick = DateTimeOffset.UtcNow;
                    if (tick - priorTick > TimeSpan.FromSeconds(5)) Fail("Capture processing was suspended or stalled; interval coverage is uncertain.");
                    priorTick = tick;
                    while (packets.Reader.TryRead(out var packet))
                    {
                        Interlocked.Add(ref queuedBytes, -packet.Data.Length);
                        lock (sync)
                        {
                            var diagnostics = manifest.TimingDiagnostics!;
                            diagnostics.Packets++;
                            if (packet.Discontinuity) diagnostics.DeviceDiscontinuities++;
                            if (packet.TimestampError || packet.Qpc <= 0)
                            {
                                diagnostics.TimestampErrors++;
                                Fail("Invalid capture timestamp; coverage is uncertain."); break;
                            }
                            var count = packet.Data.Length / manifest.BlockAlign;
                            var timestampFrame = FrameAt(packet.Qpc);
                            // Device positions can use the hardware rate (e.g. 16 kHz Bluetooth)
                            // while shared-mode packets contain 48 kHz samples. Compare successive
                            // positions, never a hardware position with a client-format sample count.
                            var positionRegressed = lastPacketPosition >= 0 && packet.Position <= lastPacketPosition;
                            if (positionRegressed) diagnostics.PositionRegressions++;
                            if (lastPacketPosition >= 0 && !positionRegressed && packet.Position != lastPacketEnd)
                                diagnostics.PositionUnitMismatches++;
                            // Some shared-mode drivers reset/stall the device counter. A valid QPC
                            // that agrees with the saved sample timeline (within one rounding frame)
                            // independently confirms continuity. Never override a Windows glitch flag.
                            var timestampFallback = positionRegressed && !packet.Discontinuity &&
                                Math.Abs(timestampFrame - nextPacketFrame) <= 1;
                            if (timestampFallback) diagnostics.TimestampFallbacks++;
                            // Packet timestamps on shared Bluetooth endpoints jitter by several ms.
                            // Within 20 ms, a forward counter and no WASAPI glitch flag establish
                            // continuity. Use sample counts for placement and periodic QPC anchors
                            // for clock drift; do not insert/drop samples on every jittery timestamp.
                            var continuous = lastPacketPosition >= 0 && !positionRegressed && !packet.Discontinuity &&
                                Math.Abs(packet.Qpc - nextPacketQpc) <= 200_000;
                            var target = continuous || timestampFallback
                                ? nextPacketFrame : timestampFrame;
                            if ((positionRegressed || packet.Discontinuity || lastPacketPosition >= 0 && packet.Position != lastPacketEnd) && diagnostics.Observations.Count < 8)
                                diagnostics.Observations.Add(new(lastPacketEnd, packet.Position, packet.Qpc, nextPacketFrame, timestampFrame, packet.Discontinuity));
                            if (packet.Discontinuity && lastPacketEnd >= 0)
                            {
                                manifest.Error ??= "Windows reported an audio capture glitch; inspect coverage for uncertain intervals.";
                                manifest.Coverage.Add(new(manifest.Frames, Math.Max(1, target - manifest.Frames), "DeviceDiscontinuity"));
                            }
                            else if (positionRegressed && !timestampFallback)
                            {
                                manifest.Error ??= "The audio device position moved backwards and its timestamps could not confirm continuous audio; inspect coverage.";
                                manifest.Coverage.Add(new(manifest.Frames, Math.Max(1, target - manifest.Frames), "PositionUncertain"));
                            }
                            lastPacketEnd = packet.Position + count;
                            lastPacketPosition = packet.Position;
                            nextPacketQpc = packet.Qpc + (long)Math.Round(count * 10_000_000d / manifest.SampleRate);
                            // Retain every delivered sample, including flagged packets. QPC jitter
                            // is not evidence of duplicate audio. Normalization uses saved anchors.
                            target = Math.Max(target, manifest.Frames);
                            nextPacketFrame = target + count;
                            if (target > manifest.Frames) Silence(target - manifest.Frames);
                            if (manifest.TrackId.Length > 0 && (manifest.Anchors.Count == 0 || manifest.Frames > manifest.Anchors[^1].Frame && packet.Qpc - manifest.Anchors[^1].Qpc >= 10_000_000))
                                manifest.Anchors.Add(new(manifest.Frames, packet.Qpc));
                            var savedStart = manifest.Frames;
                            Append(packet.Data);
                            if (packet.Muted) AddCoverage(savedStart, manifest.Frames - savedStart, "IntentionalMute");
                        }
                    }
                    lock (sync)
                    {
                        manifest.Error ??= Volatile.Read(ref pendingError);
                        if (Volatile.Read(ref pendingError) is not null) RequestStop(null);
                        if (checkpoint.Elapsed >= TimeSpan.FromSeconds(1))
                        {
                            if (owner.FreeBytes() <= owner.DiskReserve) { Fail("Free-disk reserve reached."); RequestStop(null); }
                            // Preserve quiet intervals, leaving a one-second window for delayed device packets.
                            if (stopRequested == 0) Silence(Math.Max(0, FrameAt(owner.Now() - 10_000_000) - manifest.Frames));
                            manifest.TimelineSeconds = Math.Max(0, (owner.Now() - startQpc) / 10_000_000d);
                            writer?.Flush(); chunkStream?.Flush(true); store.Save(manifest); checkpoint.Restart();
                        }
                    }
                    if (stopRequested == 0) stopWait.Restart();
                    if (stopRequested != 0 && (stopped.Task.IsCompleted || stopWait.Elapsed > TimeSpan.FromSeconds(5)))
                    {
                        if (!stopped.Task.IsCompleted) Fail("Capture stop timed out; trailing coverage is uncertain.");
                        else if (packets.Reader.TryPeek(out _)) continue;
                        break;
                    }
                    await Task.Delay(20);
                }
                lock (sync)
                {
                    manifest.Error ??= Volatile.Read(ref pendingError);
                    if (Volatile.Read(ref pendingError) is null) Silence(Math.Max(0, FrameAt(stopQpc) - manifest.Frames));
                    manifest.TimelineSeconds = Math.Max(0, (stopQpc - startQpc) / 10_000_000d);
                    FinalizeChunk();
                    // A normal stop completes capture even if some packets carried gap warnings.
                    // Coverage still makes analysis CompletedWithGaps. Only a fatal/forced stop
                    // requires the interrupted-capture consent/resume flow.
                    manifest.State = Volatile.Read(ref pendingError) is null ? "Completed" : "Interrupted";
                    manifest.Ended = DateTimeOffset.UtcNow; store.Save(manifest);
                }
            }
            catch (Exception ex)
            {
                Fail(ex.Message); RequestStop(null);
                lock (sync)
                {
                    manifest.Error ??= Volatile.Read(ref pendingError);
                    try { FinalizeChunk(); } catch (Exception finalError) { manifest.Error += " Finalization: " + finalError.Message; }
                    manifest.State = manifest.Frames > 0 ? "Interrupted" : "Failed"; manifest.Ended = DateTimeOffset.UtcNow;
                    try { store.Save(manifest); } catch (IOException) { /* Recovery retains the last durable checkpoint. */ }
                }
            }
            finally { source.Dispose(); chunkStream?.Dispose(); lease?.Dispose(); packets.Writer.TryComplete(); }
        }
        private void Silence(long frames)
        {
            if (frames <= 0) return;
            var start = manifest.Frames;
            var buffer = new byte[manifest.BlockAlign * 4096];
            while (frames > 0)
            {
                var count = (int)Math.Min(frames, 4096); Append(buffer.AsSpan(0, count * manifest.BlockAlign)); frames -= count;
            }
            AddCoverage(start, manifest.Frames - start, manifest.TrackId == "mic" ? "Missing" : "InferredSilence");
        }
        private void AddCoverage(long start, long frames, string kind)
        {
            if (frames <= 0) return;
            var prior = manifest.Coverage.LastOrDefault();
            if (prior is not null && prior.Kind == kind && prior.StartFrame + prior.Frames == start) manifest.Coverage[^1] = prior with { Frames = prior.Frames + frames };
            else manifest.Coverage.Add(new(start, frames, kind));
        }
        private void Append(ReadOnlySpan<byte> data)
        {
            ReserveBytes?.Invoke(data.Length);
            while (data.Length > 0)
            {
                if (ReserveBytes is null && priorBytes + manifest.Bytes + data.Length + 4096 >= manifest.BudgetBytes) throw new IOException("Recording storage budget reached; audio retained up to the limit.");
                if (writer is null)
                {
                    ReserveBytes?.Invoke(4096);
                    chunkStart = manifest.Frames; chunkData = 0;
                    chunkPath = Path.Combine(store.DirectoryFor(Id), $"{(manifest.TrackId.Length == 0 ? "" : manifest.TrackId + "-")}chunk-{++chunkIndex:000000}.wav");
                    chunkStream = new FileStream(chunkPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    writer = new WaveFileWriter(chunkStream, source.Format);
                }
                var limit = Math.Min(owner.ChunkBytes / manifest.BlockAlign * (long)manifest.BlockAlign, 60L * manifest.SampleRate * manifest.BlockAlign);
                var bytes = (int)Math.Min(data.Length, limit - chunkData);
                writer.Write(data[..bytes]); data = data[bytes..]; chunkData += bytes; manifest.Bytes += bytes;
                manifest.Frames += bytes / manifest.BlockAlign; manifest.RetainedBytes = priorBytes + manifest.Bytes;
                manifest.BudgetWarning = manifest.RetainedBytes >= manifest.BudgetBytes * .8;
                if (chunkData == limit) FinalizeChunk();
            }
        }
        private void FinalizeChunk()
        {
            if (writer is null) return;
            writer.Dispose(); writer = null;
            chunkStream!.Flush(true); chunkStream.Dispose(); chunkStream = null;
            var bytes = new FileInfo(chunkPath!).Length;
            manifest.Bytes += bytes - chunkData;
            manifest.Chunks.Add(new(Path.GetFileName(chunkPath!), chunkStart, chunkData / manifest.BlockAlign, bytes, AudioCaptureStore.Hash(chunkPath!)));
            store.Save(manifest);
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            RequestStop("Recording host disposed."); done.GetAwaiter().GetResult(); source.Dispose(); lease?.Dispose();
        }
    }
}
