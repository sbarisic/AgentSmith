using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AgentSmith.Cli;
using AgentSmith.Core;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class ModeCommandTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "smith-modes-" + Guid.NewGuid().ToString("N"));
    public ModeCommandTests() => Directory.CreateDirectory(root);
    private Session Create(CliApp app)
    {
        var session = new Session { Workspace = root, Mode = PermissionMode.Inspect };
        app.Store.Save(session, new("created", session.Id, null, new { })); return session;
    }
    [Fact]
    public async Task WebSearchSelectionPersistsWithoutStartingInference()
    {
        using var app = new CliApp(root, true, true); var session = Create(app);
        Assert.Equal("native", session.WebSearch);
        foreach (var policy in new[] { "off", "basic", "native" })
        {
            await app.HandleCommand("/web-search " + policy, session);
            Assert.Equal(policy, app.Store.Get(session.Id)!.WebSearch);
        }
        await app.HandleCommand("/web-search automatic", session);
        Assert.Equal("native", app.Store.Get(session.Id)!.WebSearch);
        Assert.Empty(session.Runs); Assert.Empty(session.History);
    }
    [Fact]
    public void HostedSearchProgressIsReadable()
    {
        using var output = new StringWriter(); var renderer = new TerminalRenderer(output, false);
        renderer.Render("web.search.progress", new { message = "Searching web…" });
        renderer.Render("web.search.progress", new { message = "Web search completed" });
        Assert.Contains("› Searching web", output.ToString());
        Assert.Contains("› Web search completed", output.ToString());
    }
    [Fact]
    public async Task WorkerLimitsAreLocalValidatedAndPersistent()
    {
        using var app = new CliApp(root, true, true); var session = Create(app);
        await app.HandleCommand("/worker-limits 8 2", session);
        Assert.Equal(8, app.Store.Get(session.Id)!.WorkerPolicy.MaxConcurrent);
        foreach (var input in new[] { "/worker-limits", "/worker-limits 0 2", "/worker-limits 17 2", "/worker-limits 6", "/worker-limits many 2" })
            Assert.Equal(CliApp.CommandAction.Handled, await app.HandleCommand(input, session));
        Assert.Equal(8, app.Store.Get(session.Id)!.WorkerPolicy.MaxConcurrent);
        Assert.Empty(session.Runs); Assert.Empty(session.History);
    }
    [Fact]
    public async Task WorkerBudgetPersistsAndConcurrencyChangesPreserveIt()
    {
        using var app = new CliApp(root, true, true); var session = Create(app);
        await app.HandleCommand("/worker-budget 200 45", session);
        await app.HandleCommand("/worker-limits 8 1", session);
        foreach (var input in new[] { "/worker-budget", "/worker-budget 0 30", "/worker-budget 10001 30", "/worker-budget 20 0", "/worker-budget 20 1441", "/worker-budget many 30", "/worker-budget 20" })
            Assert.Equal(CliApp.CommandAction.Handled, await app.HandleCommand(input, session));
        var saved = app.Store.Get(session.Id)!.WorkerPolicy;
        Assert.Equal(200, saved.MaxRequests); Assert.Equal(45, saved.MaxMinutes);
        Assert.Equal(8, saved.MaxConcurrent); Assert.Equal(1, saved.MaxLocalConcurrent);
        Assert.Empty(session.Runs); Assert.Empty(session.History);
    }
    [Theory]
    [InlineData("plan", WorkflowMode.Plan)]
    [InlineData("planning", WorkflowMode.Plan)]
    [InlineData("document", WorkflowMode.Document)]
    [InlineData("documentation", WorkflowMode.Document)]
    [InlineData("review", WorkflowMode.Review)]
    public async Task SelectionPersistsWithoutStartingWorkOrExpandingPermissions(string choice, WorkflowMode expected)
    {
        using var app = new CliApp(root, true, true); var session = Create(app);
        await app.HandleCommand("/mode " + choice, session);
        Assert.Equal(expected, app.Store.Get(session.Id)!.Workflow);
        Assert.Equal(PermissionMode.Inspect, session.Mode); Assert.Empty(session.Runs); Assert.Empty(session.History);
        await app.HandleCommand("/work", session);
        Assert.Equal(WorkflowMode.Work, session.Workflow); Assert.Equal(PermissionMode.Inspect, session.Mode);
    }
    [Theory]
    [InlineData("/plan Make a plan to implement player movement")]
    [InlineData("/plan new Make a plan to implement player movement")]
    [InlineData("/mode plan Make a plan to implement player movement")]
    public async Task PlanningShorthandDispatchesExactTaskInPlan(string command)
    {
        var model = new Model(); using var app = new CliApp(root, true, true, new StringReader("/exit\n")) { ModelOverride = model };
        var session = Create(app); await app.HandleCommand(command, session);
        Assert.Equal(WorkflowMode.Plan, session.Workflow);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.Chat(session, null, false, timeout.Token);
        Assert.NotNull(model.Request);
        Assert.Equal("Make a plan to implement player movement", model.Request.Input.Last()["content"]!.ToString());
        Assert.DoesNotContain(model.Request.Tools, t => t.Name is "file_create" or "process_start");
    }
    [Fact]
    public async Task PendingDecisionsAndUnfinishedWorkCannotBeBypassed()
    {
        using var app = new CliApp(root, true, true); var s = Create(app); s.Workflow = WorkflowMode.Plan;
        Planning.Update(app.Context(s), 0, new PlanRevision { Goal = "Keep scope", Steps = [new() { Id = "one", Description = "Inspect", Verification = "Read" }] });
        Planning.Submit(s); var plan = Planning.Active(s)!;
        foreach (var command in new[] { "/plan Different scope", "/mode document", "/mode review" }) await app.HandleCommand(command, s);
        Assert.Equal(WorkflowMode.Plan, s.Workflow); Assert.Same(plan, Planning.Active(s)); Assert.NotNull(plan.PendingReview); Assert.Empty(plan.Approvals);
        s.Runs.Add(new() { State = RunState.Running });
        await app.HandleCommand("/mode work", s);
        Assert.Equal(WorkflowMode.Plan, s.Workflow); Assert.NotNull(plan.PendingReview);
    }
    [Fact]
    public async Task InvalidModesAndReservedPlanCommandsStayLocal()
    {
        using var app = new CliApp(root, true, true); var s = Create(app);
        foreach (var command in new[] { "/mode unknown", "/mode 3", "/mode work extra", "/plan show extra", "/plan new" }) await app.HandleCommand(command, s);
        Assert.Equal(WorkflowMode.Work, s.Workflow); Assert.Empty(s.Runs); Assert.Empty(s.History);
        await app.HandleCommand("/mode edit", s); Assert.Equal(PermissionMode.Edit, s.Mode);
    }
    [Fact]
    public void HelpIsGroupedAndIncludesTaskShortcuts()
    {
        using var output = new StringWriter(); var renderer = new TerminalRenderer(output, false);
        renderer.Render("help", CommandHelp.Build(""));
        Assert.Contains("Start a task", output.ToString()); Assert.Contains("/plan <task>", output.ToString()); Assert.Contains("/help all", output.ToString());
        output.GetStringBuilder().Clear(); renderer.Render("help", CommandHelp.Build("all"));
        foreach (var command in new[] { "/review", "/report export", "/memory", "/queue", "/recover", "/mode" }) Assert.Contains(command, output.ToString());
        Assert.Throws<UserFacingException>(() => CommandHelp.Build("unexpected"));
    }
    private sealed class Model : IModelClient
    {
        public ModelRequest? Request;
        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Request = request; await Task.Yield();
            yield return new("completed", Output: [new JsonObject { ["role"] = "assistant", ["content"] = "Plan investigation complete." }]);
        }
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
}
