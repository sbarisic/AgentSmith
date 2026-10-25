using System.Diagnostics;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AgentSmith.Tools;

public sealed record CaptureDevice(string Id, string Name, bool Default);
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
    internal static CaptureDevice[] Devices()
    {
        using var devices = new MMDeviceEnumerator();
        string? defaultId = null;
        try { using var current = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); defaultId = current.ID; }
        catch (System.Runtime.InteropServices.COMException) { }
        using var endpoints = devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        return endpoints.Select(d => { using (d) return new CaptureDevice(d.ID, d.FriendlyName, d.ID == defaultId); }).ToArray();
    }
    public WindowsCaptureSource(string id)
    {
        using var devices = new MMDeviceEnumerator();
        device = devices.GetDevice(id);
        try
        {
            recorder = new WasapiRecorderBuilder().WithDevice(device).WithLoopbackCapture().WithSharedMode().WithBufferLength(100).Build();
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
