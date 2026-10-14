using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentSmith.Tests;

public sealed class PlanCatalogTests
{
    private static (Session Session, WorkPlan Plan) SavePlan(SessionStore store, string workspace, string goal = "Implement movement")
    {
        var session = new Session { Workspace = workspace }; var run = new Run(); session.Runs.Add(run);
        var plan = Planning.Update(new(session, run, store, default), 0, PlanningTests.Revision(goal));
        store.Save(session, new("plan.updated", session.Id, run.Id, new { }));
        return (session, plan);
    }

    [Fact]
    public void OriginAndLatestRevisionSurviveRestartWithoutTransferringOwnership()
    {
        using var dir = new TestDirectory(); var state = Path.Combine(dir.Path, "state"); var store = new SessionStore(state);
        var (source, plan) = SavePlan(store, Path.Combine(dir.Path, "."));
        var created = plan.CreatedAt;
        Planning.Update(new(source, source.Runs[0], store, default), 1, PlanningTests.Revision("Updated scope"));
        plan.Approvals.Add(new() { Revision = 2, Mode = PermissionMode.Execute });
        store.Save(source, new("plan.updated", source.Id, null, new { }));
        var other = new Session { Workspace = dir.Path, Mode = PermissionMode.Inspect };
        var reopened = new SessionStore(state);
        var listing = Assert.Single(reopened.ListPlans(other.Workspace).Plans);
        Assert.Equal(source.Id, listing.SourceConversationId); Assert.Equal(2, listing.Revision);
        Assert.Equal(PlanDirectory.Normalize(dir.Path), listing.OriginWorkspace); Assert.False(listing.OriginInferred);
        Assert.NotNull(created); Assert.Equal(created, listing.CreatedAt);
        var fetched = reopened.ReadPlan(other.Workspace, plan.Id);
        fetched.Current.Goal = "Attempted detached mutation";
        Assert.Equal("Updated scope", reopened.ReadPlan(dir.Path, plan.Id).Current.Goal);
        Assert.Null(other.ActivePlanId); Assert.Empty(other.Plans); Assert.Equal(PermissionMode.Inspect, other.Mode);
        Assert.Single(reopened.Get(source.Id)!.Plans.Single().Approvals);
        // Origin remains the creation directory even if a later session snapshot changes workspace.
        source.Workspace = Path.Combine(dir.Path, "different");
        store.Save(source, new("saved", source.Id, null, new { }));
        Assert.Single(store.ListPlans(dir.Path).Plans); Assert.Empty(store.ListPlans(source.Workspace).Plans);
    }

    [Fact]
    public void ExactDirectoryMatchingPaginationAndDataDirectoryIsolation()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(Path.Combine(dir.Path, "state"));
        for (var i = 0; i < 12; i++) SavePlan(store, dir.Path, new string('g', 400));
        var (_, childPlan) = SavePlan(store, Path.Combine(dir.Path, "child"));
        SavePlan(store, dir.Path + "-sibling");
        var queryPath = dir.Path + Path.DirectorySeparatorChar;
        if (OperatingSystem.IsWindows()) queryPath = queryPath.ToUpperInvariant().Replace('\\', '/');
        var first = store.ListPlans(queryPath); var second = store.ListPlans(queryPath, 2);
        Assert.Equal(10, first.Plans.Count); Assert.True(first.HasMore); Assert.Equal(2, second.Plans.Count); Assert.False(second.HasMore);
        Assert.Equal(12, first.Plans.Concat(second.Plans).Select(p => p.Id).Distinct().Count());
        Assert.All(first.Plans, p => Assert.True(p.Goal.Length <= 200));
        Assert.Throws<UserFacingException>(() => store.ReadPlan(dir.Path, childPlan.Id));
        Assert.Throws<UserFacingException>(() => store.ReadPlan(dir.Path, "' OR 1=1 --"));
        Assert.Throws<UserFacingException>(() => store.ListPlans(dir.Path, 0));
        Assert.Empty(new SessionStore(Path.Combine(dir.Path, "other-state")).ListPlans(dir.Path).Plans);
    }

    [Fact]
    public void LegacyLookupInfersOriginWithoutRewritingHistoryOrInventingCreationTime()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(Path.Combine(dir.Path, "state"));
        var (source, plan) = SavePlan(store, dir.Path);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(store.DataDirectory, "sessions.db") }.ToString()); db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE plans SET payload=json_remove(payload,'$.originWorkspace','$.originInferred','$.createdAt') WHERE id=$id";
        command.Parameters.AddWithValue("$id", plan.Id); command.ExecuteNonQuery();
        command.CommandText = "SELECT payload FROM plans WHERE id=$id";
        var before = (string)command.ExecuteScalar()!;
        var fetched = store.ReadPlan(dir.Path, plan.Id); var listing = Assert.Single(store.ListPlans(dir.Path).Plans);
        Assert.True(fetched.OriginInferred); Assert.True(listing.OriginInferred); Assert.Null(fetched.CreatedAt);
        Assert.Equal(source.Id, fetched.SourceConversationId); Assert.Equal(PlanDirectory.Normalize(dir.Path), fetched.OriginWorkspace);
        Assert.Equal(before, (string)command.ExecuteScalar()!);
        // A subsequent normal save can add inferred provenance, without adding a revision.
        plan.OriginWorkspace = ""; plan.CreatedAt = null;
        store.Save(source, new("saved", source.Id, null, new { }));
        var saved = store.Get(source.Id)!.Plans.Single();
        Assert.True(saved.OriginInferred); Assert.Null(saved.CreatedAt); Assert.Single(saved.Revisions);
    }

    [Theory]
    [InlineData(WorkflowMode.Plan)]
    [InlineData(WorkflowMode.Review)]
    [InlineData(WorkflowMode.Work)]
    public async Task WorkersCanFetchEvidenceButCannotModifyPlansOrExpandPermissions(WorkflowMode workflow)
    {
        using var dir = new TestDirectory(); var store = new SessionStore(Path.Combine(dir.Path, "state"));
        var (source, plan) = SavePlan(store, dir.Path);
        using var registry = new ToolRegistry();
        using var agents = new WorkerCoordinator(store, store, registry, _ => throw new InvalidOperationException("No inference needed."));
        var child = new Session { Workspace = dir.Path, ParentConversationId = source.Id, WorkerId = "worker", Workflow = workflow, Mode = PermissionMode.Inspect };
        var ctx = new ToolExecutionContext(child, new Run(), store, default);
        foreach (var name in new[] { "plan_list", "plan_fetch" }) Assert.Contains(agents.Available(child), t => t.Name == name && t.Effect == ToolEffect.ReadOnly);
        foreach (var name in new[] { "plan_update", "plan_submit", "plan_read" }) Assert.DoesNotContain(agents.Available(child), t => t.Name == name);
        var call = new ToolCall { Id = "fetch", Name = "plan_fetch", Arguments = Json.Serialize(new { planId = plan.Id }) };
        call.Prepared = agents.Prepare(ctx, call);
        var result = JsonNode.Parse((await agents.ExecuteAsync(ctx, call)).Content)!;
        Assert.Equal(plan.Id, result["id"]!.ToString()); Assert.Contains("does not activate or approve", result["notice"]!.ToString());
        Assert.Null(child.ActivePlanId); Assert.Empty(child.Plans); Assert.Equal(PermissionMode.Inspect, child.Mode);
        var mutation = new ToolCall { Name = "plan_update", Arguments = "{}" };
        Assert.Throws<UserFacingException>(() => agents.Prepare(ctx, mutation));
        var invalid = new ToolCall { Name = "plan_list", Arguments = "{\"page\":0}" };
        Assert.Throws<UserFacingException>(() => registry.Prepare(ctx, invalid));
    }
}
