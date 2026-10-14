using AgentSmith.Core;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

public sealed class DelegationInstructionTests
{
    [Theory]
    [InlineData("proactive")]
    [InlineData("manual")]
    public void GuidanceRequiresUsefulConcurrencyWithoutOverridingOptIn(string mode)
    {
        using var dir = new TestDirectory();
        var store = new SessionStore(Path.Combine(dir.Path, "data"));
        using var tools = new ToolRegistry();
        using var coordinator = new WorkerCoordinator(store, store, tools, _ => throw new InvalidOperationException());
        var session = new Session { Workspace = dir.Path, Delegation = mode,
            EnabledWorkers = [new() { Name = "editor", Model = "local" }] };
        var instructions = coordinator.SessionInstructions(session);
        var description = WorkerCoordinator.DelegationDefinitions.Single(d => d.Name == "agent_spawn").Description;
        foreach (var text in new[] { instructions, description })
        {
            Assert.Contains("two or more workers", text);
            Assert.Contains("identified independent work concurrently", text);
            Assert.Contains("even when delegation is explicitly requested", text);
            Assert.Contains("Waiting is valid", text);
            Assert.Contains("Never invent filler", text);
            Assert.DoesNotContain("unless the user explicitly requests delegation", text);
        }
        if (mode == "manual") Assert.Contains("Use workers only when the user explicitly requests delegation", instructions);
        session.EnabledWorkers.Clear();
        Assert.Equal("", coordinator.SessionInstructions(session));
        Assert.DoesNotContain(coordinator.Available(session), d => d.Name == "agent_spawn");
    }
}
