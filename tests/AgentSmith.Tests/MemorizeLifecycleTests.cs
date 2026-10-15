using AgentSmith.Core;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

public sealed class MemorizeLifecycleTests
{
    [Theory]
    [InlineData(PermissionMode.Inspect, false, false)]
    [InlineData(PermissionMode.Inspect, true, false)]
    [InlineData(PermissionMode.Edit, false, false)]
    [InlineData(PermissionMode.Edit, true, false)]
    [InlineData(PermissionMode.Execute, false, false)]
    [InlineData(PermissionMode.Execute, true, false)]
    [InlineData(PermissionMode.Execute, false, true)]
    [InlineData(PermissionMode.Execute, true, true)]
    public async Task FinishedMemorizeAllowsFollowupWithoutAnotherReport(PermissionMode permission, bool reload, bool queued)
    {
        using var dir = new TestDirectory();
        var store = new SessionStore(Path.Combine(dir.Path, "data"));
        using var tools = new ToolRegistry();
        var session = new Session { Workspace = dir.Path, Workflow = WorkflowMode.Memorize, Mode = permission };
        var model = new ScriptedModel([RuntimeTests.Done(
            RuntimeTests.Call("update", "workflow_update", PartialReport()),
            RuntimeTests.Call("finish", "workflow_finish", "{\"expectedRevision\":1}"),
            RuntimeTests.Call("tail", "file_create", "{\"path\":\"bad.md\",\"content\":\"no\"}"))]);
        var events = new List<AgentEvent>();
        await new AgentRunner(store, model, tools).RunAsync(session, "Memorize this folder", null, e =>
        {
            events.Add(e);
            if (e.Type == "workflow.finished") Assert.Equal(WorkflowMode.Work, store.Get(session.Id)!.Workflow);
        }, default);

        Assert.Equal(RunState.Completed, session.Runs.Last().State);
        Assert.Equal(WorkflowMode.Work, session.Workflow);
        Assert.Equal(WorkflowMode.Work, store.Get(session.Id)!.Workflow);
        Assert.Single(session.Reports.Single().Revisions);
        Assert.True(session.Receipts["finish"].FinishRun);
        Assert.False(File.Exists(Path.Combine(dir.Path, "bad.md")));
        Assert.Contains("not executed", session.Runs.Last().Calls.Last().Result!);
        var reportId = session.ActiveReportId;
        var artifact = Assert.Single(session.Artifacts, a => a.Kind == "workflow-report");
        var artifactBody = File.ReadAllText(artifact.Path);
        if (reload) session = store.Get(session.Id)!;
        const string question = "What did you memorize? Can you show me a 10 bullet point list?";
        if (queued)
        {
            store.Enqueue(session.Id, question);
            Assert.NotNull(store.ClaimQueued(session));
        }
        var answer = new ScriptedModel([RuntimeTests.Done(RuntimeTests.Message("Here are the saved memories in ten points."))]);
        var followup = await new AgentRunner(store, answer, tools).RunAsync(session, queued ? null : question, null, events.Add, default);
        Assert.Equal(RunState.Completed, followup.State);
        Assert.Single(answer.Requests);
        Assert.Empty(followup.Calls);
        Assert.Null(followup.CompletionRequirement);
        Assert.DoesNotContain(events, e => e.Type == "run.completion.pending");
        Assert.Equal(reportId, Assert.Single(session.Reports).Id);
        Assert.Single(session.Reports.Single().Revisions);
        Assert.Equal(artifact.Id, session.Reports.Single().ArtifactId);
        Assert.Equal(artifactBody, File.ReadAllText(artifact.Path));
        Assert.Equal(WorkflowMode.Work, session.Workflow);
        Assert.Equal(permission, session.Mode);
        Assert.Equal(permission, store.Get(session.Id)!.Mode);
    }

    private static string PartialReport() => Json.Serialize(new
    {
        expectedRevision = 0, summary = "Saved bounded repository knowledge; no commands run.",
        coverage = "Partial: inspected 21 files and reached the evidence limit.",
        limitations = new[] { "Repository-wide coverage is incomplete. No builds or tests run." },
        evidence = Array.Empty<string>(), findings = Array.Empty<ReviewFinding>()
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InterruptedFinishPreservesDurableStateAndRecovers(bool afterCommit, bool legacyReceipt)
    {
        using var dir = new TestDirectory();
        var workspace = Path.Combine(dir.Path, "repo");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "README.md"), "Widgets use a queue.\n");
        var store = new SessionStore(Path.Combine(dir.Path, "data"));
        var scan = store.ScanRepository(workspace);
        var source = Assert.Single(scan.Sources);
        store.CommitRepository(workspace, scan.Id,
            [new("widgets", 0, "Widgets", "Widgets use a queue.", ["widget"], [new(source.Path, source.Hash, source.StartLine, source.EndLine)])], "seed");
        var memoryBefore = Json.Serialize(store.SearchRepositoryMemories(workspace, "widget"));
        using var tools = new ToolRegistry();
        var session = new Session { Workspace = workspace, Workflow = WorkflowMode.Memorize, Mode = PermissionMode.Execute };
        var model = new ScriptedModel([RuntimeTests.Done(
            RuntimeTests.Call("update", "workflow_update", PartialReport()),
            RuntimeTests.Call("finish", "workflow_finish", "{\"expectedRevision\":1}"),
            RuntimeTests.Call("tail", "file_create", "{\"path\":\"bad.md\",\"content\":\"no\"}"))]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AgentRunner(new FaultStore(store, afterCommit), model, tools)
            .RunAsync(session, "Memorize this folder", null, _ => { }, default));
        session = store.Get(session.Id)!;
        Assert.Equal(afterCommit ? WorkflowMode.Work : WorkflowMode.Memorize, session.Workflow);
        Assert.Equal(afterCommit ? "Completed" : "InProgress", session.Reports.Single().State);
        Assert.Equal(afterCommit, session.Receipts.ContainsKey("finish"));
        if (legacyReceipt)
        {
            // Older releases persisted a completed report/receipt while retaining Memorize.
            session.Workflow = WorkflowMode.Memorize;
            store.Save(session, new("legacy.fixture", session.Id, null, new { }));
            session = store.Get(session.Id)!;
        }
        tools.RecoverSession(new(session, session.Runs.Last(), store, default));
        var retry = afterCommit ? new ScriptedModel() : new ScriptedModel([RuntimeTests.Done(
            RuntimeTests.Call("retry-finish", "workflow_finish", "{\"expectedRevision\":1}"))]);
        await new AgentRunner(store, retry, tools).RunAsync(session, null, null, _ => { }, default);
        Assert.Equal(afterCommit ? 0 : 1, retry.Requests.Count);
        Assert.Equal(RunState.Completed, session.Runs.Last().State);
        Assert.Equal(WorkflowMode.Work, store.Get(session.Id)!.Workflow);
        Assert.Equal(PermissionMode.Execute, session.Mode);
        Assert.Equal("Completed", Assert.Single(session.Reports).State);
        Assert.Single(session.Reports.Single().Revisions);
        Assert.Single(session.Artifacts, a => a.Kind == "workflow-report");
        Assert.False(File.Exists(Path.Combine(workspace, "bad.md")));
        if (afterCommit) Assert.Contains("not executed", session.Runs.Single().Calls.Single(c => c.Id == "tail").Result!);
        Assert.Equal(memoryBefore, Json.Serialize(store.SearchRepositoryMemories(workspace, "widget")));
        Assert.Single(Directory.GetFiles(Path.Combine(store.DataDirectory, "artifacts"), "*.md", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UnfinishedMemorizeStillRequiresReportFinalization()
    {
        using var dir = new TestDirectory();
        var store = new SessionStore(Path.Combine(dir.Path, "data"));
        using var tools = new ToolRegistry();
        var session = new Session { Workspace = dir.Path, Workflow = WorkflowMode.Memorize };
        var model = new ScriptedModel(
            [RuntimeTests.Done(RuntimeTests.Message("Done"))],
            [RuntimeTests.Done(RuntimeTests.Message("Done"))],
            [RuntimeTests.Done(RuntimeTests.Message("Done"))]);
        var run = await new AgentRunner(store, model, tools).RunAsync(session, "Memorize", null, _ => { }, default);
        Assert.Equal(RunState.Paused, run.State);
        Assert.Equal(PauseReason.NoProgress, run.PauseReason);
        Assert.Equal("workflow_report_pending", run.CompletionRequirement!.Code);
        Assert.Equal(WorkflowMode.Memorize, session.Workflow);
        Assert.Equal("InProgress", Assert.Single(session.Reports).State);
        Assert.DoesNotContain(tools.Available(session), t => t.Name == "file_create");
    }

    private sealed class FaultStore(SessionStore inner, bool after) : ISessionStore
    {
        public string DataDirectory => inner.DataDirectory;
        public Session? Get(string id) => inner.Get(id);
        public IReadOnlyList<Session> List() => inner.List();
        public IDisposable Acquire(string id) => inner.Acquire(id);
        public void SaveProcess(string id, ProcessRecord process) => inner.SaveProcess(id, process);
        public void Save(Session session, AgentEvent e)
        {
            if (e.Type == "workflow.finished" && !after) throw new InvalidOperationException("Before durable completion");
            inner.Save(session, e);
            if (e.Type == "workflow.finished") throw new InvalidOperationException("Interrupted after durable completion");
        }
    }
}
