using AgentSmith.Core;
using Xunit;

namespace AgentSmith.Tests;

public sealed class MemorizePolicyTests
{
    [Theory]
    [InlineData("file_create", PermissionMode.Edit, ToolEffect.Workspace)]
    [InlineData("process_start", PermissionMode.Execute, ToolEffect.Process)]
    [InlineData("process_poll", PermissionMode.Execute, ToolEffect.ReadOnly)]
    [InlineData("dotnet_build", PermissionMode.Execute, ToolEffect.Process)]
    [InlineData("csharp_load", PermissionMode.Execute, ToolEffect.Process)]
    [InlineData("http-write", PermissionMode.Execute, ToolEffect.Network)]
    public void MemorizeDeniesEffectsEvenWithExecutePermission(string name, PermissionMode mode, ToolEffect effect)
    {
        var session = new Session { Mode = PermissionMode.Execute, Workflow = WorkflowMode.Memorize };
        Assert.False(WorkflowPolicy.Enabled(session, new(name, "", new(), mode, true, effect)));
        Assert.Throws<UserFacingException>(() => WorkflowPolicy.CheckFiles(session, ["README.md"]));
    }

    [Fact]
    public void OnlyParentCanMutateDedicatedMemory()
    {
        var session = new Session { Mode = PermissionMode.Inspect, Workflow = WorkflowMode.Memorize };
        var tool = new ToolDefinition("repository_memory_commit", "", new(), PermissionMode.Inspect, true, ToolEffect.MemoryState);
        Assert.True(WorkflowPolicy.Enabled(session, tool));
        session.ParentConversationId = "parent";
        Assert.False(WorkflowPolicy.Enabled(session, tool));
        Assert.True(WorkflowPolicy.Enabled(session, new("file_read", "", new(), PermissionMode.Inspect, false, ToolEffect.ReadOnly)));
    }
}
