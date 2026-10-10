using AgentSmith.Cli;
using AgentSmith.Core;
using AgentSmith.LmStudio;
using AgentSmith.OpenAI;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class LocalProviderTests
{
    private static IModelClient Unwrap(IModelClient client) => client is AgentSmith.Runtime.SessionModelClient wrapper ? wrapper.Inner : client;

    [Fact]
    public void WorkerResolutionExplainsEffectiveStoreAndDoesNotFallBackToAnotherDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-profile-resolution-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var configured = new CliApp(Path.Combine(root, "configured"), true, true, new StringReader(""));
            using var empty = new CliApp(Path.Combine(root, "empty"), true, true, new StringReader(""));
            configured.Store.PutProfile(new() { Name = "bonsai-edit", Model = "local", Mode = PermissionMode.Edit });
            Assert.Single(configured.ResolveWorkers(" BONSAI-EDIT, bonsai-edit "));
            Assert.Empty(configured.ResolveWorkers(null));
            var missing = Assert.Throws<UserFacingException>(() => empty.ResolveWorkers("bonsai-edit"));
            Assert.Contains(empty.Store.DataDirectory, missing.Message); Assert.Contains("Available profiles: (none)", missing.Message);
            var typo = Assert.Throws<UserFacingException>(() => configured.ResolveWorkers("bonsai-typo"));
            Assert.Contains(configured.Store.DataDirectory, typo.Message); Assert.Contains("Available profiles: bonsai-edit", typo.Message);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void FactoryAndProfileSnapshotsRetainProtocolAcrossRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-local-routing-" + Guid.NewGuid().ToString("N"));
        try
        {
            string id;
            using (var app = new CliApp(root, true, true, new StringReader("")))
            {
                var profile = new WorkerProfile { Name = "bonsai-edit", Model = "old-model", Mode = PermissionMode.Edit, Connection = new() { Provider = ModelProvider.LmStudio } };
                app.Store.PutProfile(profile);
                var session = new Session { EnabledWorkers = [profile] }; id = session.Id;
                app.Store.Save(session, new("test", id, null, new { }));
                app.Store.PutProfile(new() { Name = profile.Name, Model = "bonsai2", Mode = PermissionMode.Edit, Connection = new() { Provider = ModelProvider.Local, Endpoint = Program.ResolveEndpoint(ModelProvider.Local, null) } });
            }
            using (var app = new CliApp(root, true, true, new StringReader("")))
            {
                Assert.IsType<ChatGptModelClient>(Unwrap(app.ModelClient(app.Store.Get(id)!)));
                var old = app.Store.Get(id)!.EnabledWorkers.Single(); Assert.Equal("old-model", old.Model);
                Assert.IsType<LmStudioModelClient>(Unwrap(app.ModelClient(new() { Connection = old.Connection })));
                var current = app.Store.Profiles().Single(); Assert.Equal("bonsai2", current.Model);
                Assert.Equal("http://127.0.0.1:8080/v1", current.Connection.Endpoint);
                Assert.IsType<LocalChatModelClient>(Unwrap(app.ModelClient(new() { Connection = current.Connection })));
                Assert.Equal("http://127.0.0.1:1234", Program.ResolveEndpoint(ModelProvider.LmStudio, null));
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void LocalHeaderDoesNotShowChatGptBillingOrUnknownCapacityAsZero()
    {
        var output = new StringWriter(); var renderer = new TerminalRenderer(output, false);
        renderer.Render("conversation", new { model = "bonsai2", provider = "Local", endpoint = "http://127.0.0.1:8080/v1", mode = "Inspect", workflow = "Work", workspace = "fixture" });
        Assert.Contains("Local Chat Completions", output.ToString()); Assert.Contains("not reported", output.ToString()); Assert.DoesNotContain("chatgpt.com", output.ToString());
    }
}
