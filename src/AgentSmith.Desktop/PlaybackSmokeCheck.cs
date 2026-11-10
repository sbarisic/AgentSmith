using AgentSmith.Tools;
using NAudio.Wave;

namespace AgentSmith.Desktop;

// Exercises the real playback controller and audio readers without opening an output device.
internal static class PlaybackSmokeCheck
{
    public static void Run(string directory, CaptureManifest fixture)
    {
        // Deliberately ends between 48 kHz frames, as real capture clocks commonly do.
        var capture = new CaptureManifest { Version = 2, TimelineSeconds = 1.00001,
            Tracks = fixture.Version == 2 ? fixture.Tracks : [fixture] };
        using var output = new SilentOutput();
        using var playback = new CallPlayback(directory, capture, output);
        var first = new byte[4096]; var replay = new byte[4096];
        playback.Toggle();
        var count = playback.Read(first);
        if (count == 0) throw new InvalidOperationException("Playback fixture has no frames.");
        var pausedAt = playback.Position;
        playback.Toggle();
        if (playback.Playing || playback.Position != pausedAt) throw new InvalidOperationException("Pause lost the playback position.");
        playback.Toggle();
        if (!playback.Playing || playback.Position != pausedAt) throw new InvalidOperationException("Resume did not preserve the playback position.");
        while (playback.Read(replay) > 0) { }
        // WASAPI can still report Playing while its last buffer drains.
        playback.Toggle();
        AssertRestart(playback, "draining the final buffer");
        if (playback.Read(replay) != count || !first.AsSpan(0, count).SequenceEqual(replay.AsSpan(0, count)))
            throw new InvalidOperationException("Replay did not read the beginning of the recording.");
        while (playback.Read(replay) > 0) { }
        output.Stop(); // Natural completion has now been reported by the player.
        playback.Toggle();
        AssertRestart(playback, "natural completion at a fractional frame");
        playback.Toggle(); // Pause, then move the scrubber to the end.
        playback.Seek(capture.DurationSeconds);
        playback.Toggle();
        AssertRestart(playback, "seeking to the end while paused");
    }

    private static void AssertRestart(CallPlayback playback, string scenario)
    {
        if (!playback.Playing || playback.Position != 0)
            throw new InvalidOperationException($"Play did not restart from the beginning after {scenario}.");
    }

    internal sealed class SilentOutput : IWavePlayer
    {
        private IWaveProvider? source;
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat => source!.WaveFormat;
        public float Volume { get; set; } = 1;
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public void Init(IWaveProvider waveProvider) => source = waveProvider;
        public void Play() => PlaybackState = PlaybackState.Playing;
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop() { PlaybackState = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new StoppedEventArgs(null)); }
        public void Dispose() { }
    }
}
