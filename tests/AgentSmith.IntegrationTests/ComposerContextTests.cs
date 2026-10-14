using AgentSmith.Cli;
using AgentSmith.Core;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class ComposerContextTests
{
    [Fact]
    public void WorkerSummaryTracksActiveWorkersAndRemovesSettledOnes()
    {
        WorkerRecord[] workers = [new() { Number = 9, State = "Running" }, new() { Number = 8, State = "Prepared" },
            new() { Number = 7, State = "Completed" }, new() { Number = 6, State = "Failed" }];
        Assert.Equal("agents 2 [#8, #9]", ComposerContext.WorkerSummary(workers));
        workers[0].State = "Cancelling";
        Assert.Equal("agents 2 [#8, #9]", ComposerContext.WorkerSummary(workers));
        workers[0].State = "Cancelled";
        Assert.Equal("agents 1 [#8]", ComposerContext.WorkerSummary(workers));
        workers[1].State = "Blocked";
        Assert.Equal("agents 0", ComposerContext.WorkerSummary(workers));
    }

    [Fact]
    public void WorkerSummaryBoundsLargeListsButRetainsTotal()
    {
        var workers = Enumerable.Range(1, 16).Select(n => new WorkerRecord { Number = n, State = "Running" });
        Assert.Equal("agents 16 [#1, #2, #3, #4, #5, #6, #7, #8, +8]", ComposerContext.WorkerSummary(workers));
    }

    private static Session SessionWith(string? manifest) => new()
    {
        Model = "gpt-6-astra", Workflow = WorkflowMode.Work, Mode = PermissionMode.Execute,
        Runs = [new() { ContextManifest = manifest }]
    };

    [Theory]
    [InlineData(false, "ctx last ~32k/128k tok (25%)")]
    [InlineData(true, "ctx last 32k/128k tok (25%)")]
    public void ShowsLastInputAgainstWorkingBudgetWithMeasurementConfidence(bool exact, string expected)
    {
        var session = SessionWith(Json.Serialize(new { unit = "tokens", workingBudget = 128000, characters = 250000,
            measurement = new { tokens = 32000, exact }, capacity = new { contextTokens = 1050000 } }));
        var line = ComposerContext.From(session).Line(160);
        Assert.Contains(expected, line);
        Assert.Contains("gpt-6-astra", line); Assert.Contains("reasoning: default", line); Assert.Contains("Work / Execute", line);
        Assert.DoesNotContain("1050", line);
    }

    [Fact]
    public void CharacterFallbackIsNotPresentedAsTokens()
    {
        var line = ComposerContext.From(SessionWith("{\"characters\":60000,\"limit\":120000}")).Line(160);
        Assert.Contains("ctx last ~60k/120k chars (50%)", line);
        Assert.DoesNotContain(" tok", line);
    }

    [Theory]
    [InlineData(null, "ctx pending")]
    [InlineData("broken", "ctx unavailable")]
    [InlineData("[]", "ctx unavailable")]
    [InlineData("{\"unit\":\"tokens\",\"workingBudget\":0}", "ctx unavailable")]
    [InlineData("{\"unit\":\"tokens\",\"workingBudget\":\"bad\"}", "ctx unavailable")]
    public void MissingOrInvalidMeasurementsDoNotInventUsage(string? manifest, string expected)
        => Assert.Contains(expected, ComposerContext.From(SessionWith(manifest)).Line(160));

    [Fact]
    public void WidthChangesRetainCompactUsageAndNewSnapshotsReplaceOldCounts()
    {
        var session = SessionWith(Json.Serialize(new { unit = "tokens", workingBudget = 128000, measurement = new { tokens = 96000, exact = false } }));
        foreach (var width in new[] { 40, 60, 90, 160 })
        {
            var line = ComposerContext.From(session).Line(width);
            Assert.True(line.Length <= width);
            Assert.Contains("75%", line); Assert.Contains("Work", line);
        }
        foreach (var width in new[] { 0, 1, 16, 32 }) Assert.True(ComposerContext.From(session).Line(width).Length <= width);
        session.Runs[^1].ContextManifest = Json.Serialize(new { unit = "tokens", workingBudget = 128000, measurement = new { tokens = 32000, exact = false } });
        Assert.Contains("25%", ComposerContext.From(session).Line(160));
    }
}
