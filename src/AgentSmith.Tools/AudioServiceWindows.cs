using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using AgentSmith.Core;
using Microsoft.Win32.SafeHandles;

namespace AgentSmith.Tools;

internal sealed record AudioEndpoint(string Directory, string Identity)
{
    internal string Pipe => "AgentSmith.Audio." + Identity;
    internal static AudioEndpoint Resolve(string directory, bool create = false)
    {
        if (!OperatingSystem.IsWindows()) throw new UserFacingException("The audio service requires Windows.");
        var full = SmithPaths.DataDirectory(directory);
        if (create) System.IO.Directory.CreateDirectory(Path.Combine(full, "audio"));
        // Packaged Windows hosts may virtualize LocalAppData children even when the parent already
        // exists outside the package. Resolve the actual audio storage handle, then its data parent.
        var audioAnchor = System.IO.Directory.Exists(Path.Combine(full, "audio"));
        // Resolve junctions and case aliases using the opened directory, including a not-yet-created suffix.
        var ancestor = audioAnchor ? Path.Combine(full, "audio") : full; var suffix = new Stack<string>();
        while (!System.IO.Directory.Exists(ancestor))
        {
            suffix.Push(Path.GetFileName(ancestor));
            ancestor = Path.GetDirectoryName(ancestor) ?? throw new IOException("Invalid data directory.");
        }
        using var handle = AudioServiceWindows.OpenDirectory(ancestor);
        var final = new StringBuilder(32768);
        var length = AudioServiceWindows.GetFinalPathNameByHandle(handle, final, final.Capacity, 0);
        if (length == 0 || length >= final.Capacity) throw new IOException("Cannot resolve data directory.");
        full = final.ToString();
        full = full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + full[8..] : full.Replace(@"\\?\", "");
        while (suffix.TryPop(out var name)) full = Path.Combine(full, name);
        if (audioAnchor) full = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(full))!;
        full = Path.TrimEndingDirectorySeparator(full);
        // Package virtualization can merge a real parent directory with redirected child files.
        // A known installation pointer is stronger evidence than the merged directory handle.
        var pointer = Path.Combine(full, "audio", "current");
        if (File.Exists(pointer))
        {
            if ((File.GetAttributes(pointer) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked installation pointer is unsupported.");
            using var file = new FileStream(pointer, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var resolved = new StringBuilder(32768);
            var size = AudioServiceWindows.GetFinalPathNameByHandle(file.SafeFileHandle, resolved, resolved.Capacity, 0);
            if (size == 0 || size >= resolved.Capacity) throw new IOException("Cannot resolve audio installation pointer.");
            var physical = resolved.ToString().Replace(@"\\?\", "");
            full = Path.GetDirectoryName(Path.GetDirectoryName(physical))!;
        }
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AudioServiceWindows.Sid + "\n" + full.ToUpperInvariant())));
        return new(full, identity);
    }
}

internal static class AudioServiceWindows
{
    internal static string Sid
    {
        get { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(); using var identity = WindowsIdentity.GetCurrent(); return identity.User!.Value; }
    }
    internal static SafeFileHandle OpenDirectory(string path)
    {
        var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot open data directory.", new Win32Exception()); }
        return handle;
    }
    internal static NamedPipeServerStream CreatePipe(string name, bool first)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P(A;;GA;;;" + Sid + ")", 1, out var descriptor, out _))
            throw new Win32Exception();
        try
        {
            var security = new Security { Length = Marshal.SizeOf<Security>(), Descriptor = descriptor };
            // Overlapped duplex, first-instance protection, byte mode, local clients only.
            var handle = CreateNamedPipeW(@"\\.\pipe\" + name, 3 | 0x40000000u | (first ? 0x00080000u : 0),
                8, 255, AudioWire.FrameLimit, AudioWire.FrameLimit, 0, ref security);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot own the audio service pipe.", new Win32Exception()); }
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally { LocalFree(descriptor); }
    }
    internal static int VerifyPeer(PipeStream pipe, bool server)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        uint pid;
        var found = server ? GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid) : GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid);
        if (!found) throw new IOException("Cannot identify audio service peer.");
        using var process = Process.GetProcessById((int)pid);
        if (!OpenProcessToken(process.Handle, 8, out var token)) throw new IOException("Cannot validate audio service peer.");
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            if (identity.User?.Value != Sid) throw new IOException("Audio service peer belongs to another user.");
        return (int)pid;
    }
    internal static void Launch(string executable, IReadOnlyList<string> arguments)
    {
        if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out var inJob)) throw new Win32Exception();
        var startup = new Startup { Size = Marshal.SizeOf<Startup>() };
        var command = new StringBuilder(string.Join(' ', new[] { executable }.Concat(arguments).Select(WindowsChild.Quote)));
        // DETACHED_PROCESS plus BREAKAWAY_FROM_JOB when needed. Never inherit handles or the caller's kill job.
        if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, false, 8u | (inJob ? 0x01000000u : 0),
            IntPtr.Zero, AppContext.BaseDirectory, ref startup, out var info))
            throw new UserFacingException("Cannot start a detached audio service (Windows error " + Marshal.GetLastWin32Error() +
                "). Run smith service run in a separate terminal outside the enclosing process job.");
        CloseHandle(info.Thread); CloseHandle(info.Process);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Security { public int Length; public IntPtr Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct Startup { public int Size; public IntPtr Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags; public ushort Show, ReservedSize; public IntPtr ReservedPointer, Input, Output, Error; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, int length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateNamedPipeW(string name, uint open, uint mode, uint instances, uint output, uint input, uint timeout, ref Security security);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string cwd, ref Startup startup, out ProcessInfo info);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
