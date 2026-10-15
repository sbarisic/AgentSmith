using AgentSmith.Core;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

public sealed class MemorizeLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinishAndRecoveryKeepMemorizeUntilExplicitExit(bool interrupt)
    {
        using var dir = new TestDirectory();
        var store = new SessionStore(Path.Combine(dir.Path, "data"));
        using var tools = new ToolRegistry();
        var session = new Session { Workspace = dir.Path, Workflow = WorkflowMode.Memorize, Mode = PermissionMode.Execute };
        var model = new ScriptedModel([RuntimeTests.Done(
            RuntimeTests.Call("update", "workflow_update", WorkflowTests.Update()),
            RuntimeTests.Call("finish", "workflow_finish", "{\"expectedRevision\":1}"),
            RuntimeTests.Call("tail", "file_create", "{\"path\":\"bad.md\",\"content\":\"no\"}"))]);
        if (interrupt)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new AgentRunner(new FaultStore(store), model, tools)
                .RunAsync(session, "Memorize this folder", null, _ => { }, default));
            session = store.Get(session.Id)!;
            tools.RecoverSession(new(session, session.Runs.Last(), store, default));
            await new AgentRunner(store, new ScriptedModel(), tools).RunAsync(session, null, null, _ => { }, default);
        }
        else
            await new AgentRunner(store, model, tools).RunAsync(session, "Memorize this folder", null, _ => { }, default);

        Assert.Equal(RunState.Completed, session.Runs.Last().State);
        Assert.Equal(WorkflowMode.Memorize, session.Workflow);
        Assert.Equal(WorkflowMode.Memorize, store.Get(session.Id)!.Workflow);
        Assert.Single(session.Reports.Single().Revisions);
        Assert.True(session.Receipts["finish"].FinishRun);
        Assert.False(File.Exists(Path.Combine(dir.Path, "bad.md")));
        Assert.Contains("not executed", session.Runs.Last().Calls.Last().Result!);
        Assert.DoesNotContain(tools.Available(session), t => t.Name == "file_create");
    }

    private sealed class FaultStore(SessionStore inner) : ISessionStore
    {
        public string DataDirectory => inner.DataDirectory;
        public Session? Get(string id) => inner.Get(id);
        public IReadOnlyList<Session> List() => inner.List();
        public IDisposable Acquire(string id) => inner.Acquire(id);
        public void SaveProcess(string id, ProcessRecord process) => inner.SaveProcess(id, process);
        public void Save(Session session, AgentEvent e)
        {
            inner.Save(session, e);
            if (e.Type == "workflow.finished") throw new InvalidOperationException("Interrupted after durable completion");
        }
    }
}
