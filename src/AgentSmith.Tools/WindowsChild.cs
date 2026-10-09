using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using AgentSmith.Core;

namespace AgentSmith.Tools;

/// <summary>Launch suspended, assign kill-on-close job, then resume. No child runs outside the job.</summary>
internal sealed class WindowsChild : IDisposable
{
    private IntPtr job;
    private IntPtr process;
    public StreamReader Output { get; private set; } = null!;
    public StreamReader Error { get; private set; } = null!;
    public StreamWriter Input { get; private set; } = null!;
    public bool Exited => WaitForSingleObject(process, 0) == 0;
    public bool TreeExited
    {
        get
        {
            if (!QueryInformationJobObject(job, 1, out var accounting, (uint)Marshal.SizeOf<BasicAccounting>(), IntPtr.Zero)) throw ErrorException();
            return accounting.ActiveProcesses == 0;
        }
    }
    public int ExitCode { get { if (!GetExitCodeProcess(process, out var code)) throw ErrorException(); return unchecked((int)code); } }
    private static Exception ErrorException() => new IOException("Windows process operation failed (" + Marshal.GetLastWin32Error() + ").");

    public static WindowsChild Start(string executable, IReadOnlyList<string> arguments, string cwd, IReadOnlyDictionary<string, string> overrides)
    {
        if (!OperatingSystem.IsWindows()) throw new UserFacingException("Process execution currently requires Windows.");
        var child = new WindowsChild();
        var handles = new List<IntPtr>();
        IntPtr environment = IntPtr.Zero; IntPtr attributes = IntPtr.Zero; IntPtr handleList = IntPtr.Zero; IntPtr thread = IntPtr.Zero;
        var attributesInitialized = false;
        try
        {
            var security = new Security { Length = Marshal.SizeOf<Security>(), Inherit = true };
            void Pipe(out IntPtr read, out IntPtr write) { if (!CreatePipe(out read, out write, ref security, 0)) throw ErrorException(); handles.Add(read); handles.Add(write); }
            Pipe(out var outputRead, out var outputWrite); Pipe(out var errorRead, out var errorWrite); Pipe(out var inputRead, out var inputWrite);
            foreach (var handle in new[] { outputRead, errorRead, inputWrite }) if (!SetHandleInformation(handle, 1, 0)) throw ErrorException();
            child.job = CreateJobObjectW(IntPtr.Zero, null);
            if (child.job == IntPtr.Zero) throw ErrorException();
            var limits = new ExtendedLimits(); limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(child.job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) throw ErrorException();
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal((nint)size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw ErrorException();
            attributesInitialized = true;
            handleList = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.Copy(new[] { inputRead, outputWrite, errorWrite }, 0, handleList, 3);
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, handleList, (nuint)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero)) throw ErrorException();
            var startup = new StartupEx { Attributes = attributes };
            startup.Info.Size = Marshal.SizeOf<StartupEx>(); startup.Info.Flags = 0x100; startup.Info.Input = inputRead; startup.Info.Output = outputWrite; startup.Info.Error = errorWrite;
            var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables()) values[(string)entry.Key] = (string)entry.Value!;
            foreach (var item in overrides) values[item.Key] = item.Value;
            var block = string.Join('\0', values.Select(v => v.Key + "=" + v.Value)) + "\0\0";
            environment = Marshal.StringToHGlobalUni(block);
            var command = new StringBuilder(string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote)));
            if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x4 | 0x400 | 0x80000 | 0x08000000, environment, cwd, ref startup, out var info)) throw ErrorException();
            child.process = info.Process; thread = info.Thread;
            if (!AssignProcessToJobObject(child.job, child.process)) { TerminateProcess(child.process, 1); throw ErrorException(); }
            child.Output = new StreamReader(new FileStream(new SafeFileHandle(outputRead, true), FileAccess.Read, 4096, false), Encoding.UTF8, true); handles.Remove(outputRead);
            child.Error = new StreamReader(new FileStream(new SafeFileHandle(errorRead, true), FileAccess.Read, 4096, false), Encoding.UTF8, true); handles.Remove(errorRead);
            child.Input = new StreamWriter(new FileStream(new SafeFileHandle(inputWrite, true), FileAccess.Write, 4096, false), new UTF8Encoding(false)) { AutoFlush = true }; handles.Remove(inputWrite);
            foreach (var handle in handles) CloseHandle(handle); handles.Clear();
            if (ResumeThread(thread) == uint.MaxValue) throw ErrorException();
            return child;
        }
        catch { child.Dispose(); throw; }
        finally
        {
            foreach (var handle in handles) CloseHandle(handle);
            if (thread != IntPtr.Zero) CloseHandle(thread);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            if (attributes != IntPtr.Zero) { if (attributesInitialized) DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
        }
    }
    public static string Quote(string value)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    public void Stop() { if (job != IntPtr.Zero && !TerminateJobObject(job, 1)) throw ErrorException(); }
    public void Dispose()
    {
        if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
        Input?.Dispose(); Output?.Dispose(); Error?.Dispose();
        if (process != IntPtr.Zero) { CloseHandle(process); process = IntPtr.Zero; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Security { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct Startup { public int Size; public IntPtr Reserved, Desktop, Title; public int X, Y, XSize, YSize, XChars, YChars, Fill, Flags; public short Show, ReservedSize; public IntPtr ReservedPointer, Input, Output, Error; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupEx { public Startup Info; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint LimitFlags; public nuint MinWorkingSet, MaxWorkingSet; public uint ActiveLimit; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicAccounting { public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime; public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref Security security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int type, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, int type, out BasicAccounting information, uint size, IntPtr returned);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string cwd, ref StartupEx startup, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
