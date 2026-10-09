using AgentSmith.Core;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

public class ToolsTests
{
    [Theory]
    [InlineData("../outside")]
    [InlineData("C:/Windows")]
    [InlineData("file.txt:secret")]
    public void RejectEscapes(string path)
    { using var dir = new TestDirectory(); Assert.Throws<UserFacingException>(() => WorkspaceTools.Resolve(dir.Path, path)); }
    [Fact]
    public async Task ReadIncludesLinesAndScopedInstructions()
    {
        using var dir = new TestDirectory(); Directory.CreateDirectory(Path.Combine(dir.Path, "sub"));
        File.WriteAllText(Path.Combine(dir.Path, "AGENTS.md"), "Root conventions");
        File.WriteAllText(Path.Combine(dir.Path, "sub", "AGENTS.md"), "Local conventions");
        File.WriteAllText(Path.Combine(dir.Path, "sub", "file.txt"), "one\ntwo\nthree");
        var tools = new WorkspaceTools(); Assert.Contains("Root conventions", tools.Instructions(dir.Path));
        var output = await tools.ExecuteAsync(dir.Path, new() { Name = "file_read", Arguments = "{\"path\":\"sub/file.txt\",\"startLine\":2,\"lineCount\":1}" }, default);
        Assert.Contains("2: two", output.Content); Assert.Contains("Local conventions", output.Content); Assert.DoesNotContain("1: one", output.Content);
    }
    [Fact]
    public async Task BinaryAndLargeFilesRejected()
    {
        using var dir = new TestDirectory(); File.WriteAllBytes(Path.Combine(dir.Path, "data"), [0, 1, 2]);
        var tools = new WorkspaceTools(); var call = new ToolCall { Name = "file_read", Arguments = "{\"path\":\"data\",\"startLine\":1,\"lineCount\":1}" };
        await Assert.ThrowsAsync<UserFacingException>(() => tools.ExecuteAsync(dir.Path, call, default));
        File.WriteAllText(Path.Combine(dir.Path, "data"), new string('x', 262145));
        await Assert.ThrowsAsync<UserFacingException>(() => tools.ExecuteAsync(dir.Path, call, default));
    }
    [Fact]
    public async Task ListingIgnoresBuildFilesAndIsBounded()
    {
        using var dir = new TestDirectory(); Directory.CreateDirectory(Path.Combine(dir.Path, "bin")); File.WriteAllText(Path.Combine(dir.Path, "bin", "secret.txt"), "ignored");
        for (var i = 0; i < 210; i++) File.WriteAllText(Path.Combine(dir.Path, $"file{i}.txt"), "test");
        var output = await new WorkspaceTools().ExecuteAsync(dir.Path, new() { Name = "workspace_list", Arguments = "{\"path\":\".\"}" }, default);
        Assert.Contains("\"truncated\":true", output.Content); Assert.DoesNotContain("secret", output.Content);
    }
    [Theory]
    [InlineData("{\"path\":\"file\",\"startLine\":\"wrong\",\"lineCount\":1}")]
    [InlineData("{\"path\":\"file\",\"startLine\":1,\"lineCount\":1,\"extra\":1}")]
    [InlineData("{\"path\":\"file\"}")]
    public async Task InvalidArgumentsRejected(string arguments)
    { using var dir = new TestDirectory(); await Assert.ThrowsAsync<UserFacingException>(() => new WorkspaceTools().ExecuteAsync(dir.Path, new() { Name = "file_read", Arguments = arguments }, default)); }
    [Fact]
    public void JunctionRejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestDirectory(); using var outside = new TestDirectory();
        var link = Path.Combine(root.Path, "junction");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, outside.Path }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        process.WaitForExit(); Assert.Equal(0, process.ExitCode);
        try { Assert.Throws<UserFacingException>(() => WorkspaceTools.Resolve(root.Path, "junction")); }
        finally { Directory.Delete(link); }
    }
}
