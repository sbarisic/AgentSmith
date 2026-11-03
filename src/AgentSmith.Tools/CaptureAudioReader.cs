using System.Buffers.Binary;
using NAudio.Wave;

namespace AgentSmith.Tools;

/// <summary>Bounded, seekable mono view of retained native chunks on the common call clock.</summary>
public sealed class CaptureAudioReader : IDisposable
{
    private readonly string directory;
    private readonly CaptureManifest track;
    private readonly CaptureAnchor[] anchors;
    private WaveFileReader? reader;
    private int chunkIndex = -1;
    private long bufferStart = -1;
    private int bufferFrames;
    private readonly byte[] buffer;
    public CaptureAudioReader(string directory, CaptureManifest track)
    {
        this.directory = directory; this.track = track;
        RecordingAnalysisStore.CheckPath(directory);
        RecordingAnalysis.ValidateCapture(track, track.Id, true);
        anchors = track.Anchors.ToArray();
        for (var i = 0; i < anchors.Length; i++)
            if (anchors[i].Frame < 0 || anchors[i].Frame > track.Frames || anchors[i].Qpc < track.StartQpc ||
                i > 0 && (anchors[i].Frame <= anchors[i - 1].Frame || anchors[i].Qpc <= anchors[i - 1].Qpc)) throw new IOException("Invalid track timing anchors.");
        buffer = new byte[8192 * track.BlockAlign];
    }
    public static double SourceFrame(CaptureManifest track, IReadOnlyList<CaptureAnchor> anchors, double seconds)
    {
        if (anchors.Count == 0) return seconds * track.SampleRate;
        var qpc = track.StartQpc + seconds * 10_000_000;
        var lo = 0; var hi = anchors.Count - 1;
        while (lo < hi) { var mid = (lo + hi + 1) / 2; if (anchors[mid].Qpc <= qpc) lo = mid; else hi = mid - 1; }
        var a = anchors[lo];
        if (lo + 1 < anchors.Count && qpc >= a.Qpc)
        {
            var b = anchors[lo + 1];
            return a.Frame + (qpc - a.Qpc) * (b.Frame - a.Frame) / (b.Qpc - a.Qpc);
        }
        return a.Frame + (qpc - a.Qpc) * track.SampleRate / 10_000_000;
    }
    public float Sample(double seconds)
    {
        if (seconds < 0 || seconds >= track.DurationSeconds) return 0;
        var position = SourceFrame(track, anchors, seconds);
        var frame = (long)Math.Floor(position);
        var fraction = position - frame;
        return (float)(Native(frame) * (1 - fraction) + Native(frame + 1) * fraction);
    }
    private double Native(long frame)
    {
        if (frame < 0 || frame >= track.Frames) return 0;
        if (chunkIndex < 0 || frame < track.Chunks[chunkIndex].StartFrame || frame >= track.Chunks[chunkIndex].StartFrame + track.Chunks[chunkIndex].Frames)
        {
            var lo = 0; var hi = track.Chunks.Count - 1;
            while (lo < hi) { var mid = (lo + hi + 1) / 2; if (track.Chunks[mid].StartFrame <= frame) lo = mid; else hi = mid - 1; }
            if (hi < 0) return 0;
            var chunk = track.Chunks[lo]; if (frame < chunk.StartFrame || frame >= chunk.StartFrame + chunk.Frames) return 0;
            var path = Path.Combine(directory, chunk.File); RecordingAnalysisStore.CheckPath(path);
            reader?.Dispose(); reader = new WaveFileReader(path); chunkIndex = lo; bufferStart = -1;
            if (reader.WaveFormat.SampleRate != track.SampleRate || reader.WaveFormat.BlockAlign != track.BlockAlign || reader.Length != chunk.Frames * track.BlockAlign)
                throw new IOException("Audio chunk does not match its manifest.");
        }
        if (bufferStart < 0 || frame < bufferStart || frame >= bufferStart + bufferFrames)
        {
            bufferStart = frame;
            reader!.Position = (frame - track.Chunks[chunkIndex].StartFrame) * track.BlockAlign;
            bufferFrames = reader.Read(buffer, 0, buffer.Length) / track.BlockAlign;
        }
        if (frame >= bufferStart + bufferFrames) return 0;
        var start = checked((int)(frame - bufferStart) * track.BlockAlign);
        var size = track.BitsPerSample / 8;
        var floating = track.FloatingPoint ?? track.Format.Contains("float", StringComparison.OrdinalIgnoreCase);
        double sum = 0;
        for (var channel = 0; channel < track.Channels; channel++)
        {
            var data = buffer.AsSpan(start + channel * size, size);
            sum += size switch {
                4 when floating => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data)),
                8 when floating => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(data)),
                1 => (data[0] - 128) / 128d,
                2 => BinaryPrimitives.ReadInt16LittleEndian(data) / 32768d,
                3 => ((data[0] | data[1] << 8 | data[2] << 16) << 8 >> 8) / 8388608d,
                4 => BinaryPrimitives.ReadInt32LittleEndian(data) / 2147483648d,
                _ => throw new IOException("Unsupported PCM sample encoding.") };
        }
        if (!double.IsFinite(sum)) throw new IOException("Non-finite audio sample.");
        return Math.Clamp(sum / track.Channels, -1, 1);
    }
    public void Dispose() => reader?.Dispose();
}
