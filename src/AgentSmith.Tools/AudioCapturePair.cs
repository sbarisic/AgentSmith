using System.Buffers.Binary;
using AgentSmith.Core;
using NAudio.Wave;

namespace AgentSmith.Tools;

internal static class AudioMeter
{
    internal static double Peak(ReadOnlySpan<byte> data, WaveFormat format)
    {
        var size = format.BitsPerSample / 8;
        if (size == 0) return 0;
        double peak = 0;
        var floating = format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible e && e.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        for (var i = 0; i + size <= data.Length; i += size)
        {
            var value = size switch
            {
                4 when floating => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data[i..])),
                8 when floating => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(data[i..])),
                2 => BinaryPrimitives.ReadInt16LittleEndian(data[i..]) / 32768d,
                3 => ((data[i] | data[i + 1] << 8 | data[i + 2] << 16) << 8 >> 8) / 8388608d,
                4 => BinaryPrimitives.ReadInt32LittleEndian(data[i..]) / 2147483648d,
                1 => (data[i] - 128) / 128d,
                _ => 0
            };
            if (double.IsFinite(value)) peak = Math.Max(peak, Math.Abs(value));
        }
        return Math.Min(1, peak);
    }
}

internal sealed partial class AudioCaptureManager
{
    private CaptureManifest StartPair(CaptureStart request)
    {
        var call = Devices().SingleOrDefault(d => d.Id == request.DeviceId) ?? throw new UserFacingException("Select an available call output.");
        var mic = Microphones().SingleOrDefault(d => d.Id == request.MicrophoneId) ?? throw new UserFacingException("Select an available microphone.");
        var budget = checked((long)(request.MaxStorageGiB * 1073741824));
        var used = store.UsedBytes();
        if (used >= budget || FreeBytes() <= DiskReserve) throw new UserFacingException("Recording storage limit or free-disk reserve reached.");
        ICaptureSource? callSource = null, micSource = null;
        FileStream? lease = null;
        Pair? pair = null;
        try
        {
            // Both endpoints must open successfully before either starts.
            callSource = CreateSource(call.Id); micSource = CreateSource(mic.Id);
            store.DirectoryFor(request.OperationId, true); lease = store.Lease(request.OperationId);
            var manifest = new CaptureManifest { Version = 2, Id = request.OperationId, OperationId = request.OperationId,
                Device = call, BudgetBytes = budget, RetainedBytes = used, Conversation = request.Conversation,
                Owner = OperatingSystem.IsWindows() ? AudioServiceWindows.Sid : Environment.UserName };
            foreach (var item in new[] { ("call", call), ("mic", mic) }) manifest.Tracks.Add(new() {
                Id = manifest.Id, OperationId = manifest.Id, TrackId = item.Item1, Device = item.Item2, BudgetBytes = budget, RetainedBytes = used });
            pair = new Pair(this, manifest, callSource, micSource, lease, used);
            store.Save(manifest); pair.Start(); active?.Dispose(); active = pair; return pair.Snapshot();
        }
        catch
        {
            if (pair is not null) pair.Dispose();
            else { callSource?.Dispose(); micSource?.Dispose(); lease?.Dispose(); }
            throw;
        }
    }

    private sealed class Pair : ICaptureRecording
    {
        private readonly AudioCaptureManager owner;
        private readonly CaptureManifest manifest;
        private readonly Recording call, mic;
        private readonly FileStream lease;
        private readonly object sync = new();
        private Task done = Task.CompletedTask;
        private int stopping;
        public string Id => manifest.Id;
        public Task Done => done;
        internal Pair(AudioCaptureManager owner, CaptureManifest manifest, ICaptureSource a, ICaptureSource b, FileStream lease, long used)
        {
            this.owner = owner; this.manifest = manifest; this.lease = lease;
            call = new(owner, owner.store, manifest.Tracks[0], a, null, used);
            mic = new(owner, owner.store, manifest.Tracks[1], b, null, used);
            bytes = used;
            void Reserve(int count)
            {
                if (Interlocked.Add(ref bytes, count) >= manifest.BudgetBytes) throw new IOException("Recording storage budget reached; both tracks retained up to the limit.");
            }
            call.ReserveBytes = Reserve; mic.ReserveBytes = Reserve;
        }
        private long bytes;
        public void Start()
        {
            manifest.StartQpc = owner.Now(); call.SharedStart = mic.SharedStart = manifest.StartQpc;
            try { call.Start(); mic.Start(); manifest.State = "Recording"; owner.store.Save(Snapshot()); done = Task.Run(Monitor); }
            catch (Exception ex)
            {
                call.RequestStop(ex.Message); mic.RequestStop(ex.Message);
                Task.WhenAll(call.Done, mic.Done).GetAwaiter().GetResult();
                manifest.State = "Failed"; manifest.Error = ex.Message; manifest.Ended = DateTimeOffset.UtcNow;
                owner.store.Save(manifest); throw;
            }
        }
        public CaptureManifest Snapshot()
        {
            lock (sync)
            {
                var a = call.Snapshot(); var b = mic.Snapshot();
                manifest.Tracks = [a, b]; manifest.Bytes = a.Bytes + b.Bytes;
                manifest.TimelineSeconds = Math.Max(a.DurationSeconds, b.DurationSeconds);
                manifest.RetainedBytes = a.RetainedBytes + b.Bytes;
                manifest.BudgetWarning = manifest.RetainedBytes >= manifest.BudgetBytes * .8;
                return System.Text.Json.JsonSerializer.Deserialize<CaptureManifest>(System.Text.Json.JsonSerializer.Serialize(manifest))!;
            }
        }
        public void Mute(bool muted) => mic.Mute(muted);
        public void RequestStop(string? error)
        {
            Interlocked.Exchange(ref stopping, 1);
            lock (sync) manifest.State = "Finalizing";
            call.RequestStop(error); mic.RequestStop(error);
        }
        private async Task Monitor()
        {
            var checkpoint = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                while (!call.Done.IsCompleted || !mic.Done.IsCompleted)
                {
                    if (Volatile.Read(ref stopping) == 0 && (call.Done.IsCompleted || mic.Done.IsCompleted)) RequestStop("A recording device stopped; both tracks were finalized.");
                    if (checkpoint.Elapsed >= TimeSpan.FromSeconds(1)) { owner.store.Save(Snapshot()); checkpoint.Restart(); }
                    await Task.Delay(100);
                }
                await Task.WhenAll(call.Done, mic.Done);
                var final = Snapshot();
                lock (sync)
                {
                    manifest.State = final.Tracks.All(t => t.State == "Completed") ? "Completed" : "Interrupted";
                    manifest.Error = string.Join("; ", final.Tracks.Where(t => t.Error is not null).Select(t => t.TrackId + ": " + t.Error));
                    manifest.Ended = DateTimeOffset.UtcNow; owner.store.Save(manifest);
                }
            }
            catch (Exception ex)
            {
                RequestStop("Capture checkpoint failed: " + ex.Message);
                await Task.WhenAll(call.Done, mic.Done);
                Snapshot(); manifest.State = "Interrupted"; manifest.Error = ex.Message; manifest.Ended = DateTimeOffset.UtcNow;
                try { owner.store.Save(manifest); } catch (IOException) { /* Last durable checkpoint supports recovery. */ }
            }
            finally { lease.Dispose(); }
        }
        public void Dispose() { if (!done.IsCompleted) RequestStop("Recording host disposed."); done.GetAwaiter().GetResult(); call.Dispose(); mic.Dispose(); lease.Dispose(); }
    }
}
