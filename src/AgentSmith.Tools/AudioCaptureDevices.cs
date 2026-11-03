namespace AgentSmith.Tools;

public static class AudioCaptureDevices
{
    public static string ResolveDirectory(string directory) => AudioEndpoint.Resolve(directory, true).Directory;
    public static CaptureDevice[] List() => OperatingSystem.IsWindows()
        ? WindowsCaptureSource.Devices().Concat(WindowsCaptureSource.Devices(true)).ToArray()
        : throw new PlatformNotSupportedException("Windows audio devices are required.");
}
