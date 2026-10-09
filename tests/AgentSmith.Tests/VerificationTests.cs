using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

public sealed class VerificationTests
{
    [Fact]
    public async Task RealBuildCapturesCompilerLocationsWithoutParsingConsoleText()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); using var registry = new ToolRegistry(); var ctx = ProcessTests.Context(dir.Path);
        File.WriteAllText(Path.Combine(dir.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(dir.Path, "Broken.cs"), "public class Broken { public int X => missingName; }");
        var start = await ProcessTests.Invoke(registry, ctx, "dotnet_build", new { project = "App.csproj", configuration = "", framework = "", timeoutSeconds = 60 });
        var (process, _) = await ProcessTests.Finish(registry, ctx, start); Assert.NotEqual(0, process["exitCode"]!.GetValue<int>());
        var result = await ProcessTests.Invoke(registry, ctx, "verification_result", new { verificationId = start["verificationId"]!.GetValue<string>() });
        Assert.Equal("complete", result["state"]!.GetValue<string>()); Assert.Contains("CS0103", result.ToJsonString()); Assert.Contains("Broken.cs", result.ToJsonString());
        Assert.NotEmpty(ctx.Session.Artifacts);
    }
    [Fact]
    public async Task RealVstestReportsPassedFailedAndSkippedCases()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); using var registry = new ToolRegistry(); var ctx = ProcessTests.Context(dir.Path);
        File.WriteAllText(Path.Combine(dir.Path, "Tests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.10.1"/><PackageReference Include="xunit" Version="2.9.3"/><PackageReference Include="xunit.runner.visualstudio" Version="4.0.0"/></ItemGroup></Project>
            """);
        File.WriteAllText(Path.Combine(dir.Path, "Tests.cs"), "using Xunit; public class Cases { [Fact] public void Pass() => Assert.True(true); [Fact] public void Fail() => Assert.Equal(1,2); [Fact(Skip=\"intentional\")] public void Skip() {} }");
        var projectText = File.ReadAllText(Path.Combine(dir.Path, "Tests.csproj")); var sourceText = File.ReadAllText(Path.Combine(dir.Path, "Tests.cs"));
        foreach (var name in new[] { "First", "Second" }) { Directory.CreateDirectory(Path.Combine(dir.Path, name)); File.WriteAllText(Path.Combine(dir.Path, name, "Tests.csproj"), projectText); File.WriteAllText(Path.Combine(dir.Path, name, "Tests.cs"), sourceText); }
        File.WriteAllText(Path.Combine(dir.Path, "Checks.slnx"), "<Solution><Folder Name=\"/First/\"><Project Path=\"First/Tests.csproj\"/></Folder><Folder Name=\"/Second/\"><Project Path=\"Second/Tests.csproj\"/></Folder></Solution>");
        var start = await ProcessTests.Invoke(registry, ctx, "dotnet_test", new { project = "Checks.slnx", configuration = "", framework = "", filter = "", timeoutSeconds = 120 });
        var (process, log) = await ProcessTests.Finish(registry, ctx, start); Assert.NotEqual(0, process["exitCode"]!.GetValue<int>());
        var result = await ProcessTests.Invoke(registry, ctx, "verification_result", new { verificationId = start["verificationId"]!.GetValue<string>() });
        Assert.True(result["state"]!.GetValue<string>() == "complete", log); Assert.Equal(12, result["total"]!.GetValue<int>()); Assert.Equal(4, result["passed"]!.GetValue<int>()); Assert.Equal(4, result["failed"]!.GetValue<int>()); Assert.Equal(4, result["skipped"]!.GetValue<int>());
        Assert.Contains("Fail", result.ToJsonString()); Assert.Equal(4, result["sourceArtifactIds"]!.AsArray().Count);
    }
    [Theory]
    [InlineData("missing", "unavailable")]
    [InlineData("malformed", "incomplete")]
    [InlineData("doctype", "incomplete")]
    [InlineData("zero", "no_tests")]
    [InlineData("interrupted", "incomplete")]
    [InlineData("inconsistent", "incomplete")]
    [InlineData("wrong-format", "incomplete")]
    public void IncompleteOrEmptyReportsNeverClaimTestsPassed(string fixture, string expected)
    {
        using var dir = new TestDirectory(); var ctx = ProcessTests.Context(dir.Path);
        var process = new ProcessRecord { State = fixture == "interrupted" ? "Interrupted" : "Exited", ExitCode = 0, TerminationConfirmed = fixture != "interrupted" }; ctx.Session.Processes.Add(process);
        var record = new VerificationRecord { Kind = "test", ProcessId = process.Id }; record.Directory = Path.Combine(ctx.ArtifactDirectory, record.Id); ctx.Session.Verifications.Add(record); Directory.CreateDirectory(record.Directory);
        var report = "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><ResultSummary><Counters total=\"0\" passed=\"0\" failed=\"0\"/></ResultSummary></TestRun>";
        if (fixture == "malformed") report = "broken XML";
        if (fixture == "inconsistent") report = report.Replace("total=\"0\"", "total=\"1\"");
        if (fixture == "wrong-format") report = "<other />";
        if (fixture == "doctype") report = "<!DOCTYPE x [<!ENTITY external SYSTEM 'file:///secret'>]><x>&external;</x>";
        if (fixture != "missing") File.WriteAllText(Path.Combine(record.Directory, "report.trx"), report);
        Assert.Equal(expected, JsonNode.Parse(DotnetVerification.Result(ctx, record.Id).Content)!["state"]!.GetValue<string>());
    }
    [Fact]
    public async Task ExplicitMtpAndPlanWorkflowRejectBuildExecution()
    {
        using var dir = new TestDirectory(); using var registry = new ToolRegistry(); var ctx = ProcessTests.Context(dir.Path);
        File.WriteAllText(Path.Combine(dir.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport></PropertyGroup></Project>");
        await Assert.ThrowsAsync<UserFacingException>(() => ProcessTests.Invoke(registry, ctx, "dotnet_test", new { project = "App.csproj", configuration = "", framework = "", filter = "", timeoutSeconds = 0 }));
        ctx.Session.Workflow = WorkflowMode.Plan;
        await Assert.ThrowsAsync<UserFacingException>(() => ProcessTests.Invoke(registry, ctx, "dotnet_build", new { project = "App.csproj", configuration = "", framework = "", timeoutSeconds = 0 }));
        Assert.Empty(ctx.Session.Processes);
    }
}
