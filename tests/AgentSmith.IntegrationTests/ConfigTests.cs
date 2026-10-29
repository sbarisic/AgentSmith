using System.CommandLine;
using AgentSmith.Cli;
using AgentSmith.Core;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class ConfigTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "smith-config-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(root, "config.toml");
    private void Write(string text) { Directory.CreateDirectory(root); File.WriteAllText(ConfigPath, text); }

    [Fact]
    public void TomlOverridesLegacyDefaultsAndExplicitProviderDoesNotInheritOtherProvidersModel()
    {
        Write("provider = 'local'\nmodel = 'configured'\nendpoint = 'http://127.0.0.1:8081/v1'\nmax_output_tokens = 4096\n");
        using var app = new CliApp(root, true, true);
        app.Store.SaveChatDefault(new("legacy", ModelProvider.ChatGpt, "http://127.0.0.1:1234", "account", 16384));
        var configured = app.ResolveChatModel(null, null, null, 0);
        Assert.Equal("configured", configured.Model); Assert.Equal(ModelProvider.Local, configured.Provider);
        Assert.Equal("http://127.0.0.1:8081/v1", configured.Endpoint); Assert.Equal(4096, configured.MaxOutputTokens);
        Assert.Equal("", configured.AccountId);
        var explicitChoice = app.ResolveChatModel("override", ModelProvider.Local, "http://127.0.0.1:9999/v1", 1024);
        Assert.Equal("override", explicitChoice.Model); Assert.Equal(1024, explicitChoice.MaxOutputTokens);
        Assert.Equal("http://127.0.0.1:9999/v1", explicitChoice.Endpoint);
        var cloud = app.ResolveChatModel(null, ModelProvider.ChatGpt, null, 0);
        Assert.Equal("legacy", cloud.Model); Assert.Equal(16384, cloud.MaxOutputTokens); Assert.Equal("account", cloud.AccountId);
    }

    [Fact]
    public void ProviderSectionsSelectTheirOwnModelAndSaveWithoutChangingOtherSections()
    {
        Write("provider = 'chatgpt'\n[chatgpt]\nmodel = 'cloud' # cloud comment\nmax_output_tokens = 16384\n[local]\nmodel = 'qwen' # local comment\nendpoint = 'http://127.0.0.1:8081/v1'\nmax_output_tokens = 4096\n");
        using var app = new CliApp(root, true, true);
        Assert.Equal("cloud", app.ResolveChatModel(null, null, null, 0).Model);
        var local = app.ResolveChatModel(null, ModelProvider.Local, null, 0);
        Assert.Equal("qwen", local.Model); Assert.Equal(4096, local.MaxOutputTokens);
        Assert.Equal("http://127.0.0.1:8081/v1", local.Endpoint);
        app.SaveModelDefault(local with { Model = "next-local" });
        Assert.Equal("next-local", app.ResolveChatModel(null, null, null, 0).Model);
        Assert.Equal("cloud", app.ResolveChatModel(null, ModelProvider.ChatGpt, null, 0).Model);
        var text = File.ReadAllText(ConfigPath);
        Assert.Contains("model = 'cloud' # cloud comment", text);
        Assert.Contains("# local comment", text);
    }

    [Theory]
    [InlineData("[local]\nmodel = 'old' # retained\n")]
    [InlineData("[local]")]
    [InlineData("[local]\n")]
    [InlineData("local = { model = 'old' } # retained\n")]
    [InlineData("local = {} # retained\n")]
    [InlineData("local.model = 'old' # retained\n")]
    [InlineData("[chatgpt]\nmodel = 'cloud' # retained\n")]
    public void SectionSavesAddMissingSettingsForSupportedTomlForms(string text)
    {
        Write(text);
        SmithConfig.SaveModel(root, new("next", ModelProvider.Local, "http://127.0.0.1:8081/v1", "", 2048));
        var config = SmithConfig.Load(root);
        Assert.Equal(ModelProvider.Local, config.Provider);
        Assert.Equal("next", config.String("local.model"));
        Assert.Equal(2048, config.Integer("local.max_output_tokens"));
        Assert.Equal("http://127.0.0.1:8081/v1", config.String("local.endpoint"));
        if (text.Contains("# retained")) Assert.Contains("# retained", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void PolicyDefaultsRespectExplicitCommandLineValuesIncludingFalse()
    {
        Write("[permissions]\nmode = 'Inspect'\n[execution]\nmax_steps = 7\n[workers]\ndelegation = 'MANUAL'\n[display]\ncolor = false\nverbosity = 'verbose'\n");
        var config = SmithConfig.Load(root);
        var command = new RootCommand();
        var steps = new Option<int>("--max-steps") { DefaultValueFactory = _ => 150 };
        var mode = new Option<PermissionMode>("--mode") { DefaultValueFactory = _ => PermissionMode.Execute };
        var delegation = new Option<string>("--delegation") { DefaultValueFactory = _ => "proactive" };
        var color = new Option<bool>("--color") { DefaultValueFactory = _ => true };
        command.Options.Add(steps); command.Options.Add(mode); command.Options.Add(delegation); command.Options.Add(color);
        var defaults = command.Parse([]);
        Assert.Equal(7, config.Option(defaults, steps, "execution.max_steps"));
        Assert.Equal(PermissionMode.Inspect, config.Option(defaults, mode, "permissions.mode"));
        Assert.Equal("manual", config.Option(defaults, delegation, "workers.delegation"));
        Assert.False(config.Option(defaults, color, "display.color"));
        var explicitOptions = command.Parse(["--max-steps", "150", "--mode", "Edit", "--color", "true"]);
        Assert.Empty(explicitOptions.Errors);
        Assert.Equal(150, config.Option(explicitOptions, steps, "execution.max_steps"));
        Assert.Equal(PermissionMode.Edit, config.Option(explicitOptions, mode, "permissions.mode"));
        Assert.True(config.Option(explicitOptions, color, "display.color"));
    }

    [Theory]
    [InlineData("model = 'a'\nmodel = 'b'", "line")]
    [InlineData("[permissions]\nmode = 'Inspect'\nmode = 'Execute'", "line")]
    [InlineData("permissions.mode = 'Inspect'\n[permissions]\nmode = 'Execute'", "line")]
    [InlineData("model = \"unterminated", "line")]
    [InlineData("[permissions]\nmode = 'Exceute'", "permissions.mode")]
    [InlineData("provider = '7'", "provider")]
    [InlineData("[execution]\nmax_steps = '150'", "execution.max_steps")]
    [InlineData("[execution]\nmax_steps = 0", "execution.max_steps")]
    [InlineData("[workers]\nmax_minutes = 1441", "workers.max_minutes")]
    [InlineData("[workers]\nmax_concurrent = 17", "workers.max_concurrent")]
    [InlineData("[display]\ncolor = 'false'", "display.color")]
    [InlineData("[display]\nverbosity = 'silent'", "display.verbosity")]
    [InlineData("max_output_tokens = 0", "max_output_tokens")]
    [InlineData("endpoint = 'https://example.com/v1'", "endpoint")]
    [InlineData("[execution]\ncontext_budget = '12'", "execution.context_budget")]
    [InlineData("[permissions]\nmod = 'Execute'", "permissions.mod")]
    [InlineData("api_key = 'do-not-echo-this'", "api_key")]
    [InlineData("[chatgpt]\nendpoint = 'http://127.0.0.1:1234'", "chatgpt.endpoint")]
    [InlineData("[local]\nmax_output_tokens = 0", "local.max_output_tokens")]
    [InlineData("[lmstudio]\nendpoint = 'http://127.0.0.1:1234/v1'", "lmstudio.endpoint")]
    public void InvalidConfigurationFailsClearlyWithoutEchoingValues(string text, string expected)
    {
        Write(text);
        var error = Assert.Throws<UserFacingException>(() => SmithConfig.Load(root));
        Assert.Contains(ConfigPath, error.Message); Assert.Contains(expected, error.Message);
        Assert.DoesNotContain("do-not-echo-this", error.Message);
        Assert.Equal(text, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void ModelSavePreservesCommentsLiteralStringsMultilineValuesAndOtherSettings()
    {
        Write("# header\r\n\"model\" = '''\r\nold-model''' # keep me\r\nprovider = 'local'\r\n\r\n# policies\r\n[permissions]\r\nmode = 'Inspect' # unchanged\r\n[workers]\r\nmax_requests = 42\r\n");
        SmithConfig.SaveModel(root, new("C:\\models\\quoted \"model\"", ModelProvider.Local, "http://127.0.0.1:8081/v1", "", 4096));
        var result = File.ReadAllText(ConfigPath);
        Assert.Contains("# header\r\n", result); Assert.Contains("# keep me\r\n", result);
        Assert.Contains("mode = 'Inspect' # unchanged\r\n", result); Assert.Contains("max_requests = 42", result);
        var config = SmithConfig.Load(root);
        Assert.Equal("C:\\models\\quoted \"model\"", config.String("model")); Assert.Equal(4096, config.Integer("max_output_tokens"));
        Assert.Equal("http://127.0.0.1:8081/v1", config.String("endpoint"));
    }

    [Fact]
    public void InitMigratesSelectionWithoutCredentialsAndNeverOverwrites()
    {
        var selection = new ChatModelSelection("local-model", ModelProvider.Local, "http://127.0.0.1:8081/v1", "private-account-ref", 4096);
        SmithConfig.Initialize(root, selection);
        var original = File.ReadAllText(ConfigPath);
        Assert.DoesNotContain("private-account-ref", original);
        Assert.Equal("local-model", SmithConfig.Load(root).String("local.model"));
        Assert.Throws<UserFacingException>(() => SmithConfig.Initialize(root, null));
        Assert.Equal(original, File.ReadAllText(ConfigPath));
        Assert.False(SmithConfig.Load(Path.Combine(root, "other")).Exists);
    }

    [Fact]
    public void ChatGptInitAndModelSavesOmitLocalEndpointAndSwitchingBackRestoresIt()
    {
        var cloud = new ChatModelSelection("gpt-6-astra", ModelProvider.ChatGpt, "http://127.0.0.1:1234", "", 16384);
        SmithConfig.Initialize(root, cloud);
        Assert.Null(SmithConfig.Load(root).String("chatgpt.endpoint"));
        var local = new ChatModelSelection("local-model", ModelProvider.Local, "http://127.0.0.1:8081/v1", "", 4096);
        SmithConfig.SaveModel(root, local);
        Assert.Equal(local.Endpoint, SmithConfig.Load(root).String("local.endpoint"));
        File.AppendAllText(ConfigPath, "\n# retained comment\n");
        SmithConfig.SaveModel(root, cloud);
        var config = SmithConfig.Load(root);
        Assert.Null(config.String("chatgpt.endpoint")); Assert.Equal(cloud.Model, config.String("chatgpt.model"));
        Assert.Equal(local.Endpoint, config.String("local.endpoint"));
        Assert.Contains("# retained comment", File.ReadAllText(ConfigPath));
        SmithConfig.SaveModel(root, cloud);
        Assert.Null(SmithConfig.Load(root).String("chatgpt.endpoint"));
    }

    [Fact]
    public void RestartUsesEditedConfigWithoutMutatingExistingConversation()
    {
        string id;
        using (var app = new CliApp(root, true, true))
        {
            var session = new Session { Model = "original", Mode = PermissionMode.Edit, Delegation = "manual", Execution = new() { MaxRequests = 3 } };
            id = session.Id; app.Store.Save(session, new("created", id, null, new { }));
            app.SaveModelDefault(ChatModelSelection.From(session));
        }
        Write("provider = 'local'\nmodel = 'new'\n[permissions]\nmode = 'Execute'\n[execution]\nmax_steps = 400\n");
        using var restarted = new CliApp(root, true, true);
        Assert.Equal("new", restarted.ResolveChatModel(null, null, null, 0).Model);
        var previous = restarted.Store.Get(id)!;
        Assert.Equal("original", previous.Model); Assert.Equal(PermissionMode.Edit, previous.Mode);
        Assert.Equal("manual", previous.Delegation); Assert.Equal(3, previous.Execution.MaxRequests);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
