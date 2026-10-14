using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public void OldReportsGroupDuplicatesAndPrioritizeFailuresWithoutChangingEvidence()
    {
        using var dir = new TestDirectory(); var ctx = ProcessTests.Context(dir.Path);
        var report = """{"state":"complete","exitCode":1,"diagnostics":[{"type":"warning","code":"W1","message":"warning"},{"type":"error","code":"CS0103","message":"missing","file":"src/A.cs","line":42,"column":13,"project":"First"},{"type":"error","code":"CS0103","message":"missing","file":"src/A.cs","line":42,"column":13,"project":"Second"}],"tests":[{"name":"PassedFirst","outcome":"Passed"},{"name":"Fail","outcome":"Failed","message":"expected"}]}""";
        var artifact = ToolArtifacts.Save(ctx, "verification", report);
        var output = Read(ctx, artifact.Id);
        var items = output["items"]!.AsArray();
        Assert.Equal("CS0103", items[0]!["code"]!.ToString()); Assert.Equal(2, items[0]!["occurrences"]!.GetValue<int>());
        Assert.Equal(2, items[0]!["projects"]!.AsArray().Count); Assert.Equal("failed", items[1]!["severity"]!.ToString());
        Assert.Equal("complete", output["state"]!.ToString()); Assert.Equal(1, output["exitCode"]!.GetValue<int>());
        Assert.Equal(report, File.ReadAllText(artifact.Path));
        var filtered = Read(ctx, artifact.Id, "errors", "src/A", "CS0103");
        Assert.Single(filtered["items"]!.AsArray()); Assert.Equal(items[0]!["id"]!.ToString(), filtered["items"]![0]!["id"]!.ToString());
    }
    [Fact]
    public void PagingIsBoundedAndWorkerGrantsDoNotPermitArbitraryAccess()
    {
        using var dir = new TestDirectory(); var ctx = ProcessTests.Context(dir.Path);
        var report = Json.Serialize(new { state = "complete", tests = Enumerable.Range(0, 150).Select(i => new { name = "T" + i, outcome = "Failed", message = new string('x', 1000) }) });
        var artifact = ToolArtifacts.Save(ctx, "verification", report);
        var first = Read(ctx, artifact.Id); Assert.True(Encoding.UTF8.GetByteCount(first.ToJsonString()) <= 8192); Assert.NotNull(first["cursor"]);
        var next = Read(ctx, artifact.Id, cursor: first["cursor"]!.ToString()); Assert.NotEqual(first["items"]![0]!["id"]!.ToString(), next["items"]![0]!["id"]!.ToString());
        Assert.Throws<UserFacingException>(() => Read(ctx, artifact.Id, "failed", cursor: first["cursor"]!.ToString()));
        var child = new Session { Workspace = dir.Path, ParentConversationId = ctx.Session.Id }; var run = new Run(); child.Runs.Add(run);
        var childContext = ctx with { Session = child, Run = run };
        Assert.Throws<UserFacingException>(() => Read(childContext, artifact.Id));
        child.ArtifactGrants.Add(new(ctx.Session.Id, artifact.Id));
        Assert.Equal(ctx.Session.Id, Read(childContext, artifact.Id)["sourceConversationId"]!.ToString());
        child.ParentConversationId = "someone-else"; Assert.Throws<UserFacingException>(() => Read(childContext, artifact.Id));
    }
    [Theory]
    [InlineData("not-json", "verification")]
    [InlineData("<broken", "test-report")]
    [InlineData("{\"type\":\"error\"}\nnot-json", "build-diagnostics")]
    public void MalformedReportsAreExplicit(string report, string kind)
    {
        using var dir = new TestDirectory(); var ctx = ProcessTests.Context(dir.Path);
        var artifact = ToolArtifacts.Save(ctx, kind, report);
        Assert.Contains("malformed", Assert.Throws<UserFacingException>(() => Read(ctx, artifact.Id)).Message);
    }
    [Fact]
    public void RawIncompleteBuildIsNotReportedAsComplete()
    {
        using var dir = new TestDirectory(); var ctx = ProcessTests.Context(dir.Path);
        var artifact = ToolArtifacts.Save(ctx, "build-diagnostics", "{\"type\":\"error\",\"code\":\"CS1\",\"message\":\"failure\"}\n");
        Assert.Equal("incomplete", Read(ctx, artifact.Id)["state"]!.ToString());
    }
    private static JsonNode Read(ToolExecutionContext ctx, string id, string filter = "all", string path = "", string query = "", string cursor = "") => JsonNode.Parse(Diagnostics.Read(ctx,
        new JsonObject { ["artifactId"] = id, ["filter"] = filter, ["path"] = path, ["query"] = query, ["cursor"] = cursor }).Content)!;
}
