using AgentSmith.Tools;
using NAudio.Wave;

namespace AgentSmith.Desktop;

internal sealed class CallPlayback : IWaveProvider, IDisposable
{
    private readonly CaptureAudioReader[] tracks;
    private readonly double duration;
    private readonly object sync = new();
    private readonly WasapiPlayer player;
    private long frame;
    private int selectedTrack;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
    public double Position { get { lock (sync) return frame / 48000d; } }
    public bool Playing => player.PlaybackState == PlaybackState.Playing;
    public event Action<Exception>? Failed;
    public CallPlayback(string directory, CaptureManifest capture)
    {
        duration = capture.DurationSeconds;
        var sources = capture.Version == 2 ? capture.Tracks : [capture];
        var opened = new List<CaptureAudioReader>();
        try
        {
            foreach (var track in sources) opened.Add(new(directory, track));
            tracks = opened.ToArray(); player = new WasapiPlayerBuilder().WithSharedMode().WithLatency(100).Build();
            try { player.Init(this); }
            catch { player.Dispose(); throw; }
            player.PlaybackStopped += (_, e) => { if (e.Exception is not null) Failed?.Invoke(e.Exception); };
        }
        catch { foreach (var track in opened) track.Dispose(); throw; }
    }
    public void Toggle() { if (Playing) player.Pause(); else { if (Position >= duration) Seek(0); player.Play(); } }
    public void Seek(double seconds)
    {
        var wasPlaying = Playing; player.Stop();
        lock (sync) frame = (long)(Math.Clamp(seconds, 0, duration) * 48000);
        if (wasPlaying) player.Play();
    }
    public void Select(int selection) { lock (sync) selectedTrack = selection; }
    public int Read(Span<byte> buffer)
    {
        lock (sync)
        {
            var samples = (int)Math.Min(buffer.Length / 4, Math.Max(0, (long)(duration * 48000) - frame));
            for (var i = 0; i < samples; i++, frame++)
            {
                var seconds = frame / 48000d;
                float value = selectedTrack == 0 ? tracks.Sum(t => t.Sample(seconds)) / tracks.Length : selectedTrack <= tracks.Length ? tracks[selectedTrack - 1].Sample(seconds) : 0;
                BitConverter.TryWriteBytes(buffer.Slice(i * 4, 4), value);
            }
            return samples * 4;
        }
    }
    public void Dispose() { player.Stop(); player.Dispose(); foreach (var track in tracks) track.Dispose(); }
}
