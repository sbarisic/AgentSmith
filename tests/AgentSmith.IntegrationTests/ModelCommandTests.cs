using AgentSmith.Cli;
using AgentSmith.Core;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class ModelCommandTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "smith-model-selection-" + Guid.NewGuid().ToString("N"));
    private Session Create(CliApp app)
    {
        var session = new Session { Workspace = root, Model = "old", Mode = PermissionMode.Inspect, Workflow = WorkflowMode.Plan };
        app.Store.Save(session, new("created", session.Id, null, new { })); return session;
    }
    [Fact]
    public async Task IdleSelectionPersistsDefaultWithoutChangingHistoryWorkersOrPermissions()
    {
        using var app = new CliApp(root, true, true); var s = Create(app);
        s.EnabledWorkers = [new() { Name = "worker", Model = "worker-model" }];
        app.ConfigureModelOverride = (candidate, _) => { candidate.Connection.Capabilities = new() { ContextTokens = 64000 }; return Task.CompletedTask; };
        Assert.Equal(CliApp.CommandAction.Handled, await app.HandleCommand("/model next", s));
        Assert.Equal("next", app.Store.Get(s.Id)!.Model); Assert.Equal("next", app.ResolveChatModel(null, null, null, 0).Model);
        Assert.Equal(PermissionMode.Inspect, s.Mode); Assert.Equal(WorkflowMode.Plan, s.Workflow);
        Assert.Equal("worker-model", s.EnabledWorkers.Single().Model); Assert.Empty(s.Runs); Assert.Empty(s.History);
        Assert.Equal(64000, s.Connection.Capabilities.ContextTokens);
    }
    [Theory]
    [InlineData(RunState.Running)]
    [InlineData(RunState.Paused)]
    [InlineData(RunState.AwaitingUser)]
    [InlineData(RunState.RecoveryRequired)]
    public async Task UnfinishedWorkCannotBeSwitched(RunState state)
    {
        using var app = new CliApp(root, true, true); var s = Create(app); s.Runs.Add(new() { State = state });
        app.ConfigureModelOverride = (_, _) => throw new InvalidOperationException("Must reject before discovery");
        await app.HandleCommand("/model next", s);
        Assert.Equal("old", s.Model); Assert.Null(app.Store.ChatDefault());
    }
    [Fact]
    public async Task InvalidCatalogSelectionLeavesCurrentModelAndDefaultUnchanged()
    {
        using var app = new CliApp(root, true, true); var s = Create(app); app.Store.SaveChatDefault(ChatModelSelection.From(s));
        app.ConfigureModelOverride = (_, _) => throw new UserFacingException("Not in catalog");
        await app.HandleCommand("/model invalid", s);
        Assert.Equal("old", s.Model); Assert.Equal("old", app.Store.ChatDefault()!.Model);
    }
    [Fact]
    public void DefaultResolutionUsesSavedSelectionThenExplicitOverridesAndNeverAChild()
    {
        using var app = new CliApp(root, true, true); var s = Create(app);
        var child = new Session { Model = "child-model", ParentConversationId = s.Id }; app.Store.Save(child, new("created", child.Id, null, new { }));
        Assert.Equal("old", app.ResolveChatModel(null, null, null, 0).Model);
        app.Store.SaveChatDefault(new("local-model", ModelProvider.Local, "http://127.0.0.1:8080/v1", "", 8192));
        var defaults = app.ResolveChatModel(null, null, null, 0);
        Assert.Equal("local-model", defaults.Model); Assert.Equal(ModelProvider.Local, defaults.Provider);
        var explicitChoice = app.ResolveChatModel("cloud-model", ModelProvider.ChatGpt, null, 16384);
        Assert.Equal("cloud-model", explicitChoice.Model); Assert.Equal(ModelProvider.ChatGpt, explicitChoice.Provider); Assert.Equal("", explicitChoice.AccountId);
        Assert.Throws<UserFacingException>(() => app.ResolveChatModel(null, ModelProvider.ChatGpt, null, 0));
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
