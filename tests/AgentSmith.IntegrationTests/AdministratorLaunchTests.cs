using System.ComponentModel;
using AgentSmith.Cli;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class AdministratorLaunchTests
{
    [Theory]
    [InlineData("smith.exe", false)]
    [InlineData("dotnet.exe", true)]
    public void RelaunchPreservesArgumentsAndWorkingDirectory(string host, bool dotnet)
    {
        var executable = Path.Combine(Path.GetTempPath(), "Smith tools", host);
        var assembly = Path.Combine(Path.GetTempPath(), "Smith tools", "smith.dll");
        var directory = Path.Combine(Path.GetTempPath(), "project with spaces");
        string[] args = ["run", "--admin", "--prompt", "Keep \"quotes\" & $variables; unchanged", "--workspace", "relative path\\", "--data-dir", ""];
        var start = AdministratorLaunch.CreateStartInfo(executable, assembly, directory, args);
        Assert.Equal(executable, start.FileName);
        Assert.Equal(directory, start.WorkingDirectory);
        Assert.True(start.UseShellExecute);
        Assert.Equal("runas", start.Verb);
        Assert.False(start.RedirectStandardInput);
        Assert.False(start.RedirectStandardOutput);
        Assert.Equal(dotnet ? new[] { assembly }.Concat(args) : args, start.ArgumentList);
    }

    [Fact]
    public async Task ChildExitCodeIsReturned()
    {
        using var error = new StringWriter();
        Assert.Equal(7, await AdministratorLaunch.RelaunchAsync(() => Task.FromResult(7), error));
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData(1223, 2, "cancelled")]
    [InlineData(5, 1, "Could not launch")]
    public async Task FailedElevationStopsWithClearError(int nativeError, int exitCode, string message)
    {
        using var error = new StringWriter();
        var attempts = 0;
        var result = await AdministratorLaunch.RelaunchAsync(() =>
        {
            attempts++;
            throw new Win32Exception(nativeError);
        }, error);
        Assert.Equal(exitCode, result);
        Assert.Equal(1, attempts);
        Assert.Contains(message, error.ToString());
    }
}
