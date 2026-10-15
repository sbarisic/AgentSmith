using AgentSmith.Core;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

public sealed class AudioProcessTests
{
    [Fact]
    public void AbandonedAudioIsRemovedButLiveLeasesArePreserved()
    {
        using var root = new TestDirectory();
        using var live = new AudioTemporary(root.Path); File.WriteAllText(Path.Combine(live.Directory, "input.wav"), "live");
        var abandoned = Path.Combine(root.Path, "audio", "jobs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(abandoned); File.WriteAllText(Path.Combine(abandoned, "input.wav"), "abandoned");
        using var next = new AudioTemporary(root.Path);
        Assert.False(Directory.Exists(abandoned)); Assert.True(File.Exists(Path.Combine(live.Directory, "input.wav")));
    }

    [Fact]
    public void InvalidComponentEvidenceIsRejected()
    {
        var data = System.Text.Json.Nodes.JsonNode.Parse("""{"startSeconds":0,"durationSeconds":1,"classification":{"status":"succeeded","classMap":[],"aggregateScores":[],"windows":[]}}""")!.AsObject();
        Assert.Throws<UserFacingException>(() => LocalAudioProcess.Validate(data, "audio_classify"));
        data["durationSeconds"] = 121d;
        Assert.Throws<UserFacingException>(() => LocalAudioProcess.Validate(data, "audio_classify"));
    }
    private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    [Fact]
    public async Task CancellationAndDeadlineTerminateHelper()
    {
        using var directory = new TestDirectory(); using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalAudioProcess.Run(PowerShell, ["-NoProfile", "-Command", "Start-Sleep -Seconds 60"], directory.Path, null, cancellation.Token));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
    }
    [Theory]
    [InlineData("exit 3", "failed")]
    [InlineData("[Console]::Out.Write(('a' * 2200000))", "exceeded")]
    public async Task CrashesAndOversizedOutputFail(string script, string expected)
    {
        using var directory = new TestDirectory(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var error = await Assert.ThrowsAsync<UserFacingException>(() => LocalAudioProcess.Run(PowerShell, ["-NoProfile", "-Command", script], directory.Path, null, timeout.Token));
        Assert.Contains(expected, error.Message);
    }
    [Theory]
    [InlineData("Write-Output 'bad json'")]
    [InlineData("Write-Output '{\"protocol\":2,\"type\":\"progress\"}'")]
    public async Task InvalidProtocolIsRejected(string script)
    {
        using var directory = new TestDirectory(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAnyAsync<Exception>(() => LocalAudioProcess.Run(PowerShell, ["-NoProfile", "-Command", script], directory.Path, null, timeout.Token, protocol: true));
    }
}
