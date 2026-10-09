using AgentSmith.Core;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

public sealed class RecoveryTests
{
    private sealed class FaultStore(SessionStore inner, string target) : ISessionStore
    {
        private bool failed;
        public string DataDirectory => inner.DataDirectory;
        public IDisposable Acquire(string id) => inner.Acquire(id);
        public Session? Get(string id) => inner.Get(id);
        public IReadOnlyList<Session> List() => inner.List();
        public void SaveProcess(string conversationId, ProcessRecord process) => inner.SaveProcess(conversationId, process);
        public void Save(Session session, AgentEvent e)
        {
            if (!failed && e.Type == target) { failed = true; throw new InvalidOperationException("Injected crash"); }
            inner.Save(session, e);
        }
    }
    [Theory]
    [InlineData("tool.started", false)]
    [InlineData("mutation.applied", true)]
    [InlineData("tool.completed", true)]
    public async Task CrashesAroundEffectBoundaryDoNotRepeatCompletedChanges(string fault, bool expectedFile)
    {
        using var dir = new TestDirectory(); var store = new SessionStore(Path.Combine(dir.Path, "state")); using var registry = new ToolRegistry();
        var session = new Session { Workspace = dir.Path };
        var model = new ScriptedModel([RuntimeTests.Done(RuntimeTests.Call("edit", "file_create", Json.Serialize(new { path = "new.txt", content = "one effect" })))]);
        var runner = new AgentRunner(new FaultStore(store, fault), model, registry);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(session, "create", null, _ => { }, default));
        Assert.Equal(expectedFile, File.Exists(Path.Combine(dir.Path, "new.txt")));
        var saved = store.Get(session.Id)!;
        if (expectedFile)
        {
            var next = new ScriptedModel([RuntimeTests.Done(RuntimeTests.Message())]);
            await new AgentRunner(store, next, registry).RunAsync(saved, null, null, _ => { }, default);
            Assert.Single(saved.Mutations); Assert.Equal(CallState.Completed, saved.Runs.Single().Calls.Single().State);
            Assert.Equal("one effect", File.ReadAllText(Path.Combine(dir.Path, "new.txt")));
        }
    }
}
