using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentSmith.Tests;

public sealed class PlanningTests
{
    public static PlanRevision Revision(string goal = "Fix the bug") => new() { Goal = goal, Deliverables = ["Working code"], Constraints = ["Preserve unrelated edits"], Steps = [new() { Id = "inspect", Description = "Inspect", Verification = "Read evidence" }] };
    private static string Arguments(PlanRevision r, int expected) => Json.Serialize(new { expectedRevision = expected, r.Goal, r.Deliverables, r.Constraints, r.Questions, r.Steps, r.Note });
    [Fact]
    public async Task PlanDispatchBlocksEffectsAndPausesQueuedCallsUntilReview()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); using var registry = new ToolRegistry();
        var session = new Session { Workspace = dir.Path, Workflow = WorkflowMode.Plan };
        var model = new ScriptedModel([RuntimeTests.Done(RuntimeTests.Call("blocked", "file_create", Json.Serialize(new { path = "bad.txt", content = "no" })), RuntimeTests.Call("plan", "plan_update", Arguments(Revision(), 0)), RuntimeTests.Call("submit", "plan_submit", "{}"), RuntimeTests.Call("tail", "workspace_list", "{\"path\":\".\"}"))]);
        var run = await new AgentRunner(store, model, registry).RunAsync(session, "plan", null, _ => { }, default);
        Assert.Equal(RunState.AwaitingPlan, run.State); Assert.False(File.Exists(Path.Combine(dir.Path, "bad.txt")));
        Assert.Equal(CallState.Pending, run.Calls.Last().State); Assert.NotNull(Planning.Active(session)!.PendingReview);
        Assert.DoesNotContain(model.Requests.Single().Tools, t => t.Capability > PermissionMode.Inspect);
        var saved = store.Get(session.Id)!; Assert.NotNull(Planning.Active(saved)!.PendingReview);
        await Assert.ThrowsAsync<UserFacingException>(() => new AgentRunner(store, model, registry).RunAsync(saved, null, null, _ => { }, default));
    }
    [Fact]
    public void ScopeRevisionAndApprovalAreExplicit()
    {
        using var dir = new TestDirectory(); var ctx = ProcessTests.Context(dir.Path); ctx.Session.Workflow = WorkflowMode.Plan;
        var plan = Planning.Update(ctx, 0, Revision()); var review = Planning.Submit(ctx.Session);
        Assert.Throws<UserFacingException>(() => Planning.Decide(ctx.Session, "stale", "implement", "", PermissionMode.Execute));
        Assert.Throws<UserFacingException>(() => Planning.Decide(ctx.Session, review.Id, "implement", ""));
        Planning.Decide(ctx.Session, review.Id, "implement", "approved", PermissionMode.Edit);
        Assert.Equal(WorkflowMode.Work, ctx.Session.Workflow);
        var progress = Revision(); progress.Steps[0].Status = "Completed"; progress.Steps[0].Note = "Read a.cs lines 1-5";
        Planning.Update(ctx, 1, progress); Assert.Equal(WorkflowMode.Work, ctx.Session.Workflow);
        Assert.Throws<UserFacingException>(() => Planning.Update(ctx, 1, Revision()));
        Planning.Update(ctx, 2, Revision("Additional deliverable")); Assert.Equal(WorkflowMode.Plan, ctx.Session.Workflow);
        Assert.Single(plan.Approvals); Assert.Equal(1, plan.Approvals[0].Revision);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InternalToolReceiptSurvivesFailureBeforeResultPersistence(bool afterCommit)
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); using var registry = new ToolRegistry();
        var session = new Session { Workspace = dir.Path, Workflow = WorkflowMode.Plan };
        var faults = new FaultStore(store, afterCommit);
        var model = new ScriptedModel([RuntimeTests.Done(RuntimeTests.Call("update", "plan_update", Arguments(Revision(), 0)))]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AgentRunner(faults, model, registry).RunAsync(session, "plan", null, _ => { }, default));
        var saved = store.Get(session.Id)!;
        registry.RecoverSession(new(saved, saved.Runs.Last(), store, default));
        if (afterCommit) { Assert.Single(Planning.Active(saved)!.Revisions); Assert.Equal(CallState.Completed, saved.Runs.Last().Calls.Single().State); }
        else { Assert.Null(Planning.Active(saved)); Assert.Equal(CallState.Started, saved.Runs.Last().Calls.Single().State); }
    }
    private sealed class FaultStore(SessionStore store, bool after) : ISessionStore
    {
        public string DataDirectory => store.DataDirectory;
        public IDisposable Acquire(string id) => store.Acquire(id);
        public Session? Get(string id) => store.Get(id);
        public IReadOnlyList<Session> List() => store.List();
        public void SaveProcess(string id, ProcessRecord process) => store.SaveProcess(id, process);
        public void Save(Session session, AgentEvent ev)
        {
            if (ev.Type == "plan.updated" && !after) throw new InvalidOperationException("before commit");
            store.Save(session, ev);
            if (ev.Type == "plan.updated") throw new InvalidOperationException("after commit");
        }
    }
    [Fact]
    public async Task ProgressOnlyUpdatePreservesApprovedScopeAndExplicitScopeChangePauses()
    {
        using var dir = new TestDirectory(); using var registry = new ToolRegistry(); var ctx = ProcessTests.Context(dir.Path); ctx.Session.Workflow = WorkflowMode.Plan;
        Planning.Update(ctx, 0, Revision()); var review = Planning.Submit(ctx.Session); Planning.Decide(ctx.Session, review.Id, "implement", "", PermissionMode.Execute);
        var revision = Revision(); revision.Steps[0].Status = "In progress";
        var args = new { expectedRevision = 1, goal = (string?)null, deliverables = (string[]?)null, constraints = (string[]?)null, questions = Array.Empty<string>(), revision.Steps, note = "progress" };
        var output = await ProcessTests.Invoke(registry, ctx, "plan_update", args);
        Assert.Equal(WorkflowMode.Work, ctx.Session.Workflow); Assert.False(output["reviewRequired"]!.GetValue<bool>());
        Assert.Equal("Fix the bug", Planning.Active(ctx.Session)!.Current.Goal);
        var changed = new ToolCall { Id = "scope-change", Name = "plan_update", Arguments = Arguments(Revision("Different goal"), 2) };
        changed.Prepared = registry.Prepare(ctx, changed); var result = await registry.ExecuteAsync(ctx, changed);
        Assert.True(result.PlanReview); Assert.Equal(WorkflowMode.Plan, ctx.Session.Workflow);
    }
    [Fact]
    public void InvalidProgressAndOpenQuestionsCannotBeSubmitted()
    {
        using var dir = new TestDirectory(); var ctx = ProcessTests.Context(dir.Path); ctx.Session.Workflow = WorkflowMode.Plan;
        var revision = Revision(); revision.Steps[0].Status = "Completed";
        Assert.Throws<UserFacingException>(() => Planning.Update(ctx, 0, revision));
        revision = Revision(); revision.Questions = ["Which behavior?"]; Planning.Update(ctx, 0, revision);
        Assert.Throws<UserFacingException>(() => Planning.Submit(ctx.Session));
        Assert.Null(Planning.Active(ctx.Session)!.PendingReview);
    }
    [Fact]
    public void MigrationFromV2PreservesPermissionsAndCalls()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path);
        var session = new Session { Mode = PermissionMode.Edit, Runs = [new Run { Calls = [new ToolCall { Id = "old-call" }] }] };
        store.Save(session, new("created", session.Id, null, new { }));
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(dir.Path, "sessions.db")))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DROP TABLE plans; DROP TABLE artifacts; DROP TABLE verifications; DROP TABLE receipts; UPDATE schema_version SET version=2; UPDATE conversations SET snapshot=json_remove(snapshot,'$.workflow');"; cmd.ExecuteNonQuery(); }
        var upgraded = new SessionStore(dir.Path).Get(session.Id)!;
        Assert.Equal(WorkflowMode.Work, upgraded.Workflow); Assert.Equal(PermissionMode.Edit, upgraded.Mode); Assert.Equal("old-call", upgraded.Runs.Single().Calls.Single().Id);
        Assert.NotEmpty(Directory.GetFiles(dir.Path, "sessions-v2-*.backup.db"));
    }
}
