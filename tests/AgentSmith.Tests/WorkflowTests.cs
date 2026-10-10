using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentSmith.Tests;

public sealed class WorkflowTests
{
    public static string Update(int revision = 0, string[]? evidence = null) => Json.Serialize(new { expectedRevision = revision, summary = "Documentation inspected; no commands run.", coverage = "README and linked source inspected.", limitations = new[] { "External links unverified." }, evidence = evidence ?? [], findings = Array.Empty<ReviewFinding>() });
    private static ToolCall Call(string name, string args, string? id = null) => new() { Id = id ?? Guid.NewGuid().ToString("N"), Name = name, Arguments = args };
    [Theory]
    [InlineData(WorkflowMode.Review)]
    [InlineData(WorkflowMode.Plan)]
    public void ReadOnlyWorkflowCannotBeBypassedByExecuteOrDirectFileLayer(WorkflowMode mode)
    {
        using var dir = new TestDirectory(); var ctx = ProcessTests.Context(dir.Path); ctx.Session.Workflow = mode;
        using var registry = new ToolRegistry();
        Assert.DoesNotContain(registry.Available(ctx.Session), t => t.Capability > PermissionMode.Inspect);
        var call = Call("file_create", "{\"path\":\"test.md\",\"content\":\"bad\"}");
        Assert.Throws<UserFacingException>(() => registry.Prepare(ctx, call));
        Assert.Throws<UserFacingException>(() => FileOperations.Prepare(ctx, call, JsonNode.Parse(call.Arguments)!.AsObject()));
    }
    [Fact]
    public void DocumentAllowsTextButRejectsSourceMovesAndUndo()
    {
        using var dir = new TestDirectory(); var ctx = ProcessTests.Context(dir.Path); ctx.Session.Workflow = WorkflowMode.Document;
        var create = Call("file_create", "{\"path\":\"docs/README.MD\",\"content\":\"hello\"}");
        var record = FileOperations.Prepare(ctx, create, JsonNode.Parse(create.Arguments)!.AsObject()); FileOperations.Apply(ctx, record);
        var move = Call("file_move", Json.Serialize(new { source = "docs/README.MD", destination = "code.cs", expectedHash = FileOperations.Hash("hello"u8.ToArray()) }));
        Assert.Throws<UserFacingException>(() => FileOperations.Prepare(ctx, move, JsonNode.Parse(move.Arguments)!.AsObject()));
        ctx.Session.Workflow = WorkflowMode.Work;
        var code = Call("file_create", "{\"path\":\"code.cs\",\"content\":\"code\"}");
        var codeRecord = FileOperations.Prepare(ctx, code, JsonNode.Parse(code.Arguments)!.AsObject()); FileOperations.Apply(ctx, codeRecord);
        ctx.Session.Workflow = WorkflowMode.Document; Assert.Throws<UserFacingException>(() => FileOperations.Undo(ctx, codeRecord.Id));
        FileOperations.Undo(ctx, record.Id); Assert.True(File.Exists(Path.Combine(dir.Path, "code.cs"))); Assert.False(File.Exists(Path.Combine(dir.Path, "docs/README.MD")));
    }
    [Fact]
    public async Task FinishIsDurableReturnsToWorkAndNeverDispatchesTail()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(Path.Combine(dir.Path, "data")); using var tools = new ToolRegistry();
        var s = new Session { Workspace = dir.Path, Workflow = WorkflowMode.Document, Mode = PermissionMode.Inspect };
        var model = new ScriptedModel([RuntimeTests.Done(RuntimeTests.Message())], [RuntimeTests.Done(RuntimeTests.Call("update", "workflow_update", Update()), RuntimeTests.Call("finish", "workflow_finish", "{\"expectedRevision\":1}"), RuntimeTests.Call("tail", "file_create", "{\"path\":\"bad.md\",\"content\":\"no\"}"))]);
        var run = await new AgentRunner(store, model, tools).RunAsync(s, "Draft documentation", null, _ => { }, default);
        Assert.Equal(2, model.Requests.Count); Assert.Equal(RunState.Completed, run.State); Assert.Equal(WorkflowMode.Work, s.Workflow); Assert.Equal(PermissionMode.Inspect, s.Mode);
        Assert.Contains("Authoritative completion feedback", model.Requests[1].Instructions);
        Assert.Contains("workflow_report_pending", model.Requests[1].Instructions);
        Assert.Null(store.Get(s.Id)!.Runs.Last().CompletionRequirement);
        Assert.False(File.Exists(Path.Combine(dir.Path, "bad.md"))); Assert.Contains("not executed", run.Calls.Last().Result!);
        store.Enqueue(s.Id, "follow up"); var queued = store.ClaimQueued(s); Assert.NotNull(queued); Assert.Equal(WorkflowMode.Work, store.Get(s.Id)!.Workflow);
        var saved = store.Get(s.Id)!; Assert.Equal("Completed", saved.Reports.Single().State); Assert.True(saved.Receipts["finish"].FinishRun);
        Assert.Contains(saved.Artifacts, a => a.Kind == "workflow-report" && File.Exists(a.Path)); Assert.Contains("external links", WorkflowTools.Markdown(saved.Reports.Single()), StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task CompletionFeedbackSurvivesPauseAndRefreshesOnResume()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); using var tools = new ToolRegistry();
        var s = new Session { Workspace = dir.Path, Workflow = WorkflowMode.Document, Mode = PermissionMode.Inspect };
        var paused = await new AgentRunner(store, new ScriptedModel([RuntimeTests.Done(RuntimeTests.Message("Draft done"))]), tools) { MaxSteps = 1 }
            .RunAsync(s, "Draft documentation", null, _ => { }, default);
        Assert.Equal(RunState.Paused, paused.State);
        var saved = store.Get(s.Id)!; Assert.Equal("workflow_report_pending", saved.Runs.Last().CompletionRequirement!.Code);
        var model = new ScriptedModel([RuntimeTests.Done(RuntimeTests.Call("update", "workflow_update", Update()), RuntimeTests.Call("finish", "workflow_finish", "{\"expectedRevision\":1}"))]);
        var finished = await new AgentRunner(store, model, tools).RunAsync(saved, null, null, _ => { }, default);
        Assert.Contains("workflow_report_pending", model.Requests[0].Instructions); Assert.Equal(RunState.Completed, finished.State);
        Assert.Null(store.Get(s.Id)!.Runs.Last().CompletionRequirement);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionRecoveryDoesNotDuplicateReportsOrExecuteTail(bool afterCommit)
    {
        using var dir = new TestDirectory(); var store = new SessionStore(Path.Combine(dir.Path, "data")); using var tools = new ToolRegistry();
        var s = new Session { Workspace = dir.Path, Workflow = WorkflowMode.Document };
        var model = new ScriptedModel([RuntimeTests.Done(RuntimeTests.Call("update", "workflow_update", Update()), RuntimeTests.Call("finish", "workflow_finish", "{\"expectedRevision\":1}"), RuntimeTests.Call("tail", "file_create", "{\"path\":\"bad.md\",\"content\":\"no\"}"))]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AgentRunner(new FaultStore(store, afterCommit), model, tools).RunAsync(s, "Document", null, _ => { }, default));
        var saved = store.Get(s.Id)!; tools.RecoverSession(new(saved, saved.Runs.Last(), store, default));
        Assert.Single(saved.Reports.Single().Revisions); Assert.False(File.Exists(Path.Combine(dir.Path, "bad.md")));
        if (afterCommit)
        {
            var resumed = await new AgentRunner(store, new ScriptedModel(), tools).RunAsync(saved, null, null, _ => { }, default);
            Assert.Equal(RunState.Completed, resumed.State); Assert.Equal(WorkflowMode.Work, saved.Workflow); Assert.False(File.Exists(Path.Combine(dir.Path, "bad.md")));
        }
        else { Assert.Equal("InProgress", saved.Reports.Single().State); Assert.False(saved.Receipts.ContainsKey("finish")); }
    }
    [Fact]
    public async Task RequestLimitDoesNotFinishWorkflowAndCancelledQuestionsDoNotBlockLaterReports()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); using var registry = new ToolRegistry();
        var s = new Session { Workspace = dir.Path, Workflow = WorkflowMode.Document };
        s.Runs.Add(new() { State = RunState.Cancelled, Question = new() { Text = "Old cancelled question" } });
        var run = await new AgentRunner(store, new ScriptedModel([RuntimeTests.Done(RuntimeTests.Message())]), registry) { MaxSteps = 1 }.RunAsync(s, "Draft", null, _ => { }, default);
        Assert.Equal(RunState.Paused, run.State); Assert.Equal(WorkflowMode.Document, s.Workflow); Assert.Equal("InProgress", s.Reports.Single().State);
        var resumed = new AgentRunner(store, new ScriptedModel([RuntimeTests.Done(RuntimeTests.Call("u", "workflow_update", Update()), RuntimeTests.Call("f", "workflow_finish", "{\"expectedRevision\":1}"))]), registry);
        await resumed.RunAsync(s, null, null, _ => { }, default); Assert.Equal(WorkflowMode.Work, s.Workflow);
    }
    [Fact]
    public async Task RevisionAndEvidenceValidationHappenBeforeEffects()
    {
        using var dir = new TestDirectory(); var ctx = ProcessTests.Context(dir.Path); ctx.Session.Workflow = WorkflowMode.Document;
        using var tools = new ToolRegistry(); WorkflowPolicy.Begin(ctx.Session, "Document");
        var wrong = Call("workflow_update", Update(evidence: ["tool:invented"])); Assert.Throws<UserFacingException>(() => tools.Prepare(ctx, wrong));
        var valid = Call("workflow_update", Update()); valid.Prepared = tools.Prepare(ctx, valid); await tools.ExecuteAsync(ctx, valid); await tools.ExecuteAsync(ctx, valid);
        Assert.Single(WorkflowPolicy.Active(ctx.Session)!.Revisions);
        Assert.Throws<UserFacingException>(() => tools.Prepare(ctx, Call("workflow_update", Update())));
        ctx.Run.Question = new() { Text = "Pending" };
        Assert.Throws<UserFacingException>(() => tools.Prepare(ctx, Call("workflow_finish", "{\"expectedRevision\":1}")));
    }
    [Fact]
    public void V7MigrationPreservesWorkflowApprovalsQueueAndCreatesBackup()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path);
        var s = new Session { Mode = PermissionMode.Edit, Workflow = WorkflowMode.Plan };
        store.Save(s, new("created", s.Id, null, new { })); store.Enqueue(s.Id, "pending"); store.HoldQueue(s.Id, "Paused");
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(dir.Path, "sessions.db")))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DROP TABLE workflow_reports; DROP TABLE workflow_revisions; DROP TABLE steering; UPDATE schema_version SET version=7;"; cmd.ExecuteNonQuery(); }
        var migrated = new SessionStore(dir.Path); var saved = migrated.Get(s.Id)!;
        Assert.Empty(saved.Reports); Assert.Equal(WorkflowMode.Plan, saved.Workflow); Assert.Equal(PermissionMode.Edit, saved.Mode); Assert.Equal("Paused", migrated.Queue(s.Id).HoldReason); Assert.Single(migrated.Queue(s.Id).Items); Assert.NotEmpty(Directory.GetFiles(dir.Path, "sessions-v7-*.backup.db"));
    }
    [Fact]
    public async Task GitEvidenceCapturesPagesUntrackedAndDetectsDrift()
    {
        using var dir = new TestDirectory(); var workspace = Path.Combine(dir.Path, "repo"); Directory.CreateDirectory(workspace);
        Git(workspace, "init"); Git(workspace, "config", "user.email", "test@example.invalid"); Git(workspace, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(workspace, "a.cs"), "original\n"); Git(workspace, "add", "."); Git(workspace, "commit", "-m", "initial");
        File.WriteAllText(Path.Combine(workspace, "a.cs"), string.Join('\n', Enumerable.Range(0, 5000).Select(i => "line " + i)));
        File.WriteAllText(Path.Combine(workspace, "new.md"), "untracked");
        var store = new SessionStore(Path.Combine(dir.Path, "data")); var s = new Session { Workspace = workspace, Workflow = WorkflowMode.Review }; var run = new Run(); s.Runs.Add(run);
        var ctx = new ToolExecutionContext(s, run, store, default); store.Save(s, new("created", s.Id, run.Id, new { })); using var registry = new ToolRegistry();
        await registry.StartWorkflowAsync(ctx); var report = WorkflowPolicy.Active(s)!;
        Assert.Equal(2, report.Baseline!.FileHashes.Count);
        var artifact = s.Artifacts.Single(a => a.Path.EndsWith("working-tree.txt")); Assert.True(new FileInfo(artifact.Path).Length > 24000);
        var page = ArtifactAccess.Execute(ctx, "artifact_read", new JsonObject { ["artifactId"] = artifact.Id, ["cursor"] = 0, ["maxCharacters"] = 4000 }); Assert.Contains("hasMore", page.Content);
        var index = File.ReadAllBytes(Path.Combine(workspace, ".git", "index"));
        var update = Call("workflow_update", Update(evidence: ["artifact:" + artifact.Id])); update.Prepared = registry.Prepare(ctx, update); await registry.ExecuteAsync(ctx, update);
        File.AppendAllText(Path.Combine(workspace, "new.md"), "changed");
        var finish = Call("workflow_finish", "{\"expectedRevision\":1}"); finish.Prepared = registry.Prepare(ctx, finish); await registry.ExecuteAsync(ctx, finish);
        Assert.Contains(report.Drift, d => d.Contains("changed during review")); Assert.Equal(index, File.ReadAllBytes(Path.Combine(workspace, ".git", "index")));
        s.Workflow = WorkflowMode.Review; var committed = WorkflowPolicy.Begin(s, "root commit", "commit", "HEAD");
        var snapshot = await ReviewEvidence.Capture(ctx, committed, registry.Processes, true); Assert.Null(snapshot.Base); Assert.NotNull(snapshot.Head);
        var comparison = WorkflowPolicy.Begin(s, "branch", "base", "HEAD"); var baseline = await ReviewEvidence.Capture(ctx, comparison, registry.Processes, true); Assert.Equal(baseline.Head, baseline.Base);
        await Assert.ThrowsAsync<UserFacingException>(() => ReviewEvidence.Capture(ctx, WorkflowPolicy.Begin(s, "bad", "commit", "--help"), registry.Processes, true));
    }
    [Fact]
    public async Task PathReviewWorksWithoutGitAndMissingRepositoryFailsClearly()
    {
        using var dir = new TestDirectory(); File.WriteAllText(Path.Combine(dir.Path, "file.txt"), "hello"); var ctx = ProcessTests.Context(dir.Path); ctx.Session.Workflow = WorkflowMode.Review; using var processes = new ProcessManager();
        var r = WorkflowPolicy.Begin(ctx.Session, "path", "path", "file.txt"); var snapshot = await ReviewEvidence.Capture(ctx, r, processes, true); Assert.Single(snapshot.FileHashes);
        await Assert.ThrowsAsync<UserFacingException>(() => ReviewEvidence.Capture(ctx, WorkflowPolicy.Begin(ctx.Session, "changes"), processes, true));
        Assert.Throws<UserFacingException>(() => WorkspaceTools.Resolve(dir.Path, "../outside.md"));
    }
    private static void Git(string directory, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true }; foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!; p.WaitForExit(); Assert.Equal(0, p.ExitCode);
    }
    private sealed class FaultStore(SessionStore inner, bool after) : ISessionStore
    {
        public string DataDirectory => inner.DataDirectory;
        public Session? Get(string id) => inner.Get(id);
        public IReadOnlyList<Session> List() => inner.List();
        public IDisposable Acquire(string id) => inner.Acquire(id);
        public void SaveProcess(string id, ProcessRecord p) => inner.SaveProcess(id, p);
        public void Save(Session s, AgentEvent e) { if (e.Type == "workflow.finished" && !after) throw new InvalidOperationException("before commit"); inner.Save(s, e); if (e.Type == "workflow.finished") throw new InvalidOperationException("after commit"); }
    }
}
