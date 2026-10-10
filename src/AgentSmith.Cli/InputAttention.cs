using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AgentSmith.Cli;

/// <summary>One notification per input request, independent of rendering and key polling.</summary>
internal sealed class InputAttention(bool enabled, Func<string> readTitle, Action<string> writeTitle, Action sound) : IDisposable
{
    private string? waitingFor;
    private string? originalTitle;
    private bool disposed;

    public static InputAttention Create(bool enabled) => new(enabled && OperatingSystem.IsWindows(),
        () => OperatingSystem.IsWindows() ? Console.Title : "",
        title => { if (OperatingSystem.IsWindows()) Console.Title = title; },
        () => { if (OperatingSystem.IsWindows() && !MessageBeep(0x40)) MessageBeep(uint.MaxValue); });

    public void WaitFor(string request)
    {
        if (!enabled || disposed || waitingFor == request) return;
        waitingFor = request;
        BestEffort(() =>
        {
            originalTitle ??= readTitle();
            var title = string.IsNullOrWhiteSpace(originalTitle) ? "AgentSmith" : originalTitle;
            writeTitle(title.StartsWith("[!] ", StringComparison.Ordinal) ? title : "[!] " + title);
        });
        BestEffort(sound);
    }

    public void Working()
    {
        if (!enabled || disposed) return;
        waitingFor = null;
        if (originalTitle is { } title) BestEffort(() => writeTitle(title));
        originalTitle = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        Working(); disposed = true;
    }

    private static void BestEffort(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or Win32Exception or ExternalException or ArgumentException) { }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint type);
}
