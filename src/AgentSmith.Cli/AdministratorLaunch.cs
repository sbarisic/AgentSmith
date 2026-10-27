using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace AgentSmith.Cli;

internal static class AdministratorLaunch
{
    public static async Task<int?> TryRunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("--admin is supported only on Windows.");
            return 2;
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return null;

        return await RelaunchAsync(async () =>
        {
            var start = CreateStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException(),
                typeof(Program).Assembly.Location, Environment.CurrentDirectory, args);
            Console.Error.WriteLine("Requesting administrator approval. Smith will open in a new console.");
            using var process = Process.Start(start) ?? throw new InvalidOperationException();
            await process.WaitForExitAsync();
            return process.ExitCode;
        }, Console.Error);
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string assembly, string directory, string[] args)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = directory,
            WindowStyle = ProcessWindowStyle.Normal
        };
        // Support both the published apphost and `dotnet smith.dll` / `dotnet run`.
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(assembly);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    internal static async Task<int> RelaunchAsync(Func<Task<int>> launch, TextWriter error)
    {
        try { return await launch(); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            await error.WriteLineAsync("Administrator launch cancelled. Smith did not run the command.");
            return 2;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync("Could not launch Smith as administrator. Check that the executable is accessible and elevation is permitted.");
            return 1;
        }
    }
}
