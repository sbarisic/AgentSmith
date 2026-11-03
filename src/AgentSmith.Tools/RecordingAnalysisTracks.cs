using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Dsp;

namespace AgentSmith.Tools;

public sealed partial class RecordingAnalysis
{
    internal async Task PrepareTracks(RecordingAnalysisJob job, CaptureManifest manifest, string snapshot, string location, long capacity, Action<string> progress, CancellationToken ct)
    {
        _ = location;
        if (manifest.Id != job.Source || manifest.Version != 2 || manifest.State != "Completed" && !(manifest.State == "Interrupted" && job.Options.AllowInterrupted) || manifest.Tracks.Count != 2 ||
            !manifest.Tracks.Select(t => t.TrackId).Order().SequenceEqual(new[] { "call", "mic" })) throw new IOException("Invalid call manifest.");
        job.Version = 2; job.TrackCount = 2; job.Chunks.Clear(); job.Coverage.Clear();
        job.Frames = (long)Math.Round(manifest.DurationSeconds * 16000);
        if (job.Frames <= 0 || checked(job.Frames * 8) > capacity) throw new IOException("Call exceeds reserved analysis storage.");
        var path = Store.FileFor(job.Id, "normalized.f32");
        using (var output = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)))
        {
            foreach (var track in manifest.Tracks)
            {
                if (track.Id != manifest.Id || track.TrackId is not ("call" or "mic") || track.StartQpc != manifest.StartQpc || track.State != "Completed" && !job.Options.AllowInterrupted) throw new IOException("Invalid call track.");
                ct.ThrowIfCancellationRequested();
                foreach (var chunk in track.Chunks)
                {
                    var source = Path.Combine(snapshot, chunk.File);
                    if (!AudioCaptureStore.ValidChunk(chunk.File) || new FileInfo(source).Length != chunk.Bytes || RecordingAnalysisStore.Hash(source) != chunk.Hash) throw new IOException("Capture chunk changed.");
                }
                var offset = output.BaseStream.Position / 4;
                using var reader = new CaptureAudioReader(snapshot, track);
                // Warp at the native sample rate, then low-pass/resample to the speech engine's rate.
                // Direct 48 kHz -> 16 kHz point sampling would alias high-frequency call audio.
                var resampled = new ClockResampler(new ClockSamples(reader, track.SampleRate));
                var samples = new float[16000];
                for (long frame = 0; frame < job.Frames;)
                {
                    ct.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(samples.Length, job.Frames - frame);
                    Store.CheckBudget(job.Options.MaxStorageGiB, count * 4);
                    if (frame % (16000 * 10) == 0) progress($"Aligning {track.TrackId} audio · {frame / 16000}/{job.DurationSeconds:0}s");
                    if (resampled.Read(samples.AsSpan(0, count)) != count) throw new IOException("Track resampling ended unexpectedly.");
                    output.Write(MemoryMarshal.AsBytes(samples.AsSpan(0, count))); frame += count;
                }
                if (track.Frames == 0) job.Coverage.Add(new(0, job.Frames, "Missing", track.TrackId));
                else foreach (var chunk in PlanChunks(job.Frames, false))
                    { chunk.Index = job.Chunks.Count; chunk.TrackId = track.TrackId; chunk.SourceOffset = offset; job.Chunks.Add(chunk); }
                foreach (var gap in track.Coverage)
                {
                    // Native coverage remains available in the source manifest; the analysis view uses the call clock.
                    var start = TimelineFrame(track, gap.StartFrame);
                    var end = TimelineFrame(track, gap.StartFrame + gap.Frames);
                    job.Coverage.Add(new(Math.Clamp(start, 0, job.Frames), Math.Max(0, Math.Min(job.Frames, end) - Math.Clamp(start, 0, job.Frames)), gap.Kind, track.TrackId));
                }
                if (track.State == "Interrupted") job.Coverage.Add(new(job.Frames, 0, "InterruptedSourceUnknownTail", track.TrackId));
            }
            output.Flush(); ((FileStream)output.BaseStream).Flush(true);
        }
        job.SourceHash = RecordingAnalysisStore.Hash(Path.Combine(snapshot, "manifest.json"));
        RecordingAnalysisStore.WriteJson(Store.FileFor(job.Id, "preparation.json"), new { version = 2, frames = job.Frames, hash = RecordingAnalysisStore.Hash(path) });
        job.Prepared = true; Store.Save(job); await Task.CompletedTask;
    }
    private sealed class ClockSamples(CaptureAudioReader reader, int sampleRate) : ISampleProvider
    {
        private long frame;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        public int Read(Span<float> buffer)
        {
            for (var i = 0; i < buffer.Length; i++, frame++) buffer[i] = reader.Sample(frame / (double)sampleRate);
            return buffer.Length;
        }
    }
    private sealed class ClockResampler
    {
        private readonly ISampleProvider source;
        private readonly WdlResampler resampler = new();
        internal ClockResampler(ISampleProvider source)
        {
            this.source = source;
            resampler.SetMode(false, 0, true, 64, 32);
            resampler.SetFeedMode(false); resampler.SetRates(source.WaveFormat.SampleRate, 16000);
        }
        internal int Read(Span<float> output)
        {
            var needed = resampler.ResamplePrepare(output.Length, 1, out var input);
            var read = source.Read(input[..needed]);
            return resampler.ResampleOut(output, read, output.Length, 1);
        }
    }
    private static long TimelineFrame(CaptureManifest track, long frame)
    {
        var anchors = track.Anchors;
        if (anchors.Count == 0) return (long)Math.Round(frame * 16000d / track.SampleRate);
        var index = anchors.FindLastIndex(a => a.Frame <= frame); if (index < 0) index = 0;
        var a = anchors[index];
        var ticks = index + 1 < anchors.Count && frame >= a.Frame
            ? a.Qpc + (frame - a.Frame) * (double)(anchors[index + 1].Qpc - a.Qpc) / (anchors[index + 1].Frame - a.Frame)
            : a.Qpc + (frame - a.Frame) * 10_000_000d / track.SampleRate;
        return (long)Math.Round((ticks - track.StartQpc) * 16000 / 10_000_000);
    }
}
