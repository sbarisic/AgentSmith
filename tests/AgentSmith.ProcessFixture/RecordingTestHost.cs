using AgentSmith.Tools;
using NAudio.Wave;

namespace AgentSmith.ProcessFixture;

/// <summary>Named-pipe capture fixture: never opens a playback device.</summary>
public sealed class RecordingTestHost : IAsyncDisposable
{
    private sealed class Source : ICaptureSource
    {
        public WaveFormat Format { get; } = new(16000, 16, 1);
        public event CapturePacket? Data;
        public event Action<Exception?>? Stopped;
        public void Start() => Data?.Invoke(new byte[3200], 0, CaptureClock.Qpc(), false, false);
        public void Stop() => Stopped?.Invoke(null);
        public void Dispose() { }
    }
    private readonly CancellationTokenSource stop = new();
    private readonly Task running;
    public RecordingTestHost(string directory)
    {
        var host = new AudioServiceHost(directory);
        host.CaptureManager.CreateSource = _ => new Source();
        host.CaptureManager.Devices = () => [new("fixture", "Synthetic playback", true)];
        running = host.Run(stop.Token);
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); stop.Dispose(); }
}
