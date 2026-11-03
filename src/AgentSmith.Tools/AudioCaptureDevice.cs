using System.Diagnostics;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AgentSmith.Tools;

public sealed record CaptureDevice(string Id, string Name, bool Default, string Kind = "output", bool CommunicationsDefault = false);
internal delegate void CapturePacket(ReadOnlySpan<byte> bytes, long position, long qpc, bool discontinuity, bool timestampError);
internal interface ICaptureSource : IDisposable
{
    WaveFormat Format { get; }
    event CapturePacket? Data;
    event Action<Exception?>? Stopped;
    void Start();
    void Stop();
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsCaptureSource : ICaptureSource
{
    private readonly MMDevice device;
    private readonly WasapiRecorder recorder;
    public WaveFormat Format => recorder.WaveFormat;
    public event CapturePacket? Data;
    public event Action<Exception?>? Stopped;
    internal static CaptureDevice[] Devices(bool microphone = false)
    {
        using var devices = new MMDeviceEnumerator();
        var flow = microphone ? DataFlow.Capture : DataFlow.Render;
        string? defaultId = null, communicationsId = null;
        try { using var current = devices.GetDefaultAudioEndpoint(flow, Role.Multimedia); defaultId = current.ID; }
        catch (System.Runtime.InteropServices.COMException) { }
        try { using var current = devices.GetDefaultAudioEndpoint(flow, Role.Communications); communicationsId = current.ID; }
        catch (System.Runtime.InteropServices.COMException) { }
        using var endpoints = devices.EnumerateAudioEndPoints(flow, DeviceState.Active);
        return endpoints.Select(d => { using (d) return new CaptureDevice(d.ID, d.FriendlyName, d.ID == defaultId, microphone ? "microphone" : "output", d.ID == communicationsId); }).ToArray();
    }
    public WindowsCaptureSource(string id)
    {
        using var devices = new MMDeviceEnumerator();
        device = devices.GetDevice(id);
        try
        {
            var builder = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode().WithBufferLength(100);
            if (device.DataFlow == DataFlow.Render) builder = builder.WithLoopbackCapture();
            recorder = builder.Build();
            recorder.DataAvailable += (bytes, flags, position, qpc) => Data?.Invoke(bytes, checked((long)position), checked((long)qpc),
                flags.HasFlag(AudioClientBufferFlags.DataDiscontinuity), flags.HasFlag(AudioClientBufferFlags.TimestampError));
            recorder.RecordingStopped += (_, e) => Stopped?.Invoke(e.Exception);
        }
        catch { device.Dispose(); throw; }
    }
    public void Start() => recorder.StartRecording();
    public void Stop() => recorder.StopRecording();
    public void Dispose() { recorder.Dispose(); device.Dispose(); }
}

internal static class CaptureClock
{
    internal static long Qpc() => (long)(Stopwatch.GetTimestamp() * (10_000_000d / Stopwatch.Frequency));
}
