using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AgentSmith.Application;
using AgentSmith.Core;
using AgentSmith.OpenAI;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Xunit;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AgentSmith.Tests;

public sealed class RecorderAgentTests
{
    private static ChatModelSelection Local(string model) => new(model, ModelProvider.Local, "http://127.0.0.1:8081/v1", "", 8192);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulSignInPreservesCliAccountUnlessExplicitlyRequested(bool setActive)
    {
        using var directory = new TestDirectory();
        var vault = new CredentialVault(directory.Path, new WindowsSecretProtector());
        using (vault.Acquire()) vault.Write(new() { ActiveId = "cli", Accounts = [new() { Id = "cli" }] });
        using var rsa = RSA.Create(2048); var key = new RsaSecurityKey(rsa) { KeyId = "fixture" };
        var config = new OpenIdConnectConfiguration { Issuer = OAuthProtocol.Issuer }; config.SigningKeys.Add(key);
        var idToken = "";
        using var http = new HttpClient(new Handler(_ => Json(JsonSerializer.Serialize(new {
            token_type = "Bearer", access_token = "fixture-access", refresh_token = "fixture-refresh", expires_in = 3600,
            id_token = idToken, scope = "chatgpt.tokens.use.direct"
        }))));
        var auth = new ChatGptAuth(http, vault, new IdentityValidator(new StaticConfigurationManager<OpenIdConnectConfiguration>(config)));
        var account = await auth.LoginAsync(null, _ => { }, default, (url, _) => {
            var query = new Uri(url).Query.TrimStart('?').Split('&').Select(s => s.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
            idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor {
                Issuer = OAuthProtocol.Issuer, Audience = "fixture-client", Expires = DateTime.UtcNow.AddHours(1),
                Claims = new Dictionary<string, object> { ["sub"] = "fixture-user", ["nonce"] = query["nonce"], ["email"] = "fixture@example.test" },
                SigningCredentials = new(key, SecurityAlgorithms.RsaSha256)
            });
            return Task.FromResult<string?>(query["redirect_uri"] + "?state=" + query["state"] + "&code=fixture-code&client_id=fixture-client");
        }, setActiveAccount: setActive);
        Assert.Equal(setActive ? account.Id : "cli", vault.Read().ActiveId);
        Assert.Equal("fixture@example.test", vault.Active(account.Id).Email);
    }

    [Fact]
    public async Task ChatGptCatalogUsesExplicitRecorderAccountWithoutChangingTheActiveAccount()
    {
        using var directory = new TestDirectory(); var vault = new CredentialVault(directory.Path, new WindowsSecretProtector());
        using (vault.Acquire()) vault.Write(new() { ActiveId = "cli", Accounts = [new() { Id = "cli" },
            new() { Id = "recorder", AccessToken = "fixture-recorder-token", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), Scopes = ["chatgpt.tokens.use.direct"] }] });
        using var agents = new RecorderAgents(directory.Path, new Handler(request => {
            Assert.Equal("fixture-recorder-token", request.Headers.Authorization?.Parameter); Assert.Null(request.Content);
            return Json("""{"models":[{"slug":"fixture-model","display_name":"Fixture model","visibility":"list"}]}""");
        }));
        Assert.Equal("fixture-model", Assert.Single(await agents.Models(ModelProvider.ChatGpt, "", "recorder", default)).Id);
        Assert.Equal("cli", agents.ActiveAccountId());
    }

    [Fact]
    public void RecorderPreferencesSurviveReloadWithoutChangingCliAndInvalidDraftIsNotApplied()
    {
        using var directory = new TestDirectory();
        var store = new SessionStore(directory.Path); store.SaveChatDefault(Local("cli"));
        using (var recorder = new CallRecorder(directory.Path, Path.Combine(directory.Path, "smith.exe")))
        {
            Assert.Equal("cli", recorder.SummaryAgent().Model);
            recorder.SavePreferences(new() { SummaryAgent = Local("recorder") });
            Assert.Equal("cli", store.ChatDefault()!.Model);
            store.SaveChatDefault(Local("changed-cli"));
            Assert.Equal("recorder", recorder.SummaryAgent().Model);
            Assert.Throws<UserFacingException>(() => recorder.SavePreferences(new() { SummaryAgent = Local("unsaved"), MaxRequests = -1 }));
            Assert.Equal("recorder", recorder.SummaryAgent().Model);
        }
        using var reloaded = new CallRecorder(directory.Path, Path.Combine(directory.Path, "smith.exe"));
        Assert.Equal("recorder", reloaded.SummaryAgent().Model);
        Assert.Equal("changed-cli", store.ChatDefault()!.Model);
    }

    [Fact]
    public void OlderPreferencesWithoutAgentStillLoad()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "desktop"));
        File.WriteAllText(Path.Combine(directory.Path, "desktop", "preferences.json"), """{"OutputId":"headset","SummaryLanguage":"hr"}""");
        using var recorder = new CallRecorder(directory.Path, Path.Combine(directory.Path, "smith.exe"));
        Assert.Equal("headset", recorder.Preferences.OutputId); Assert.Equal("hr", recorder.Preferences.SummaryLanguage);
        Assert.Null(recorder.Preferences.SummaryAgent);
    }

    [Theory]
    [InlineData("https://example.com/v1")]
    [InlineData("http://127.0.0.1:8081/v1?token=secret")]
    public void SavedSettingsRetainEndpointRestrictions(string endpoint) =>
        Assert.Throws<UserFacingException>(() => RecorderAgents.Validate(Local("test") with { Endpoint = endpoint }));

    [Fact]
    public async Task CatalogRequestsContainNoTranscriptOrAccountAuthorization()
    {
        using var directory = new TestDirectory();
        var requests = 0;
        using var agents = new RecorderAgents(directory.Path, new Handler(request =>
        {
            requests++;
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Content); Assert.Null(request.Headers.Authorization);
            return Json(request.RequestUri!.AbsolutePath == "/v1/models" ? """{"data":[{"id":"test"}]}""" : "{}");
        }));
        var models = await agents.Models(ModelProvider.Local, "http://127.0.0.1:8081/v1", "", default);
        Assert.Equal("test", Assert.Single(models).Id); Assert.Equal(2, requests);
    }

    [Fact]
    public async Task LmStudioShowsUnloadedModelsAndSummaryDoesNotRequireTools()
    {
        using var directory = new TestDirectory();
        const string catalog = """{"models":[{"type":"llm","key":"text-model","display_name":"Text model","loaded_instances":[{"config":{"context_length":32768}}],"capabilities":{"trained_for_tool_use":false}},{"type":"llm","key":"unloaded","loaded_instances":[]}]}""";
        using var agents = new RecorderAgents(directory.Path, new Handler(_ => Json(catalog)));
        var models = await agents.Models(ModelProvider.LmStudio, "http://127.0.0.1:1234", "", default);
        Assert.True(models[0].Available); Assert.False(models[1].Available);
        using var http = new HttpClient(new Handler(_ => Json(catalog)));
        var vault = new CredentialVault(directory.Path, new WindowsSecretProtector());
        var session = new Session { Model = "text-model", Connection = new() { Provider = ModelProvider.LmStudio, Endpoint = "http://127.0.0.1:1234" } };
        await RecordingModels.Configure(session, http, vault, new(http, vault, new()), default, summaryOnly: true);
        Assert.Equal(32768, session.Connection.LoadedContextTokens);
        await Assert.ThrowsAsync<UserFacingException>(() => RecordingModels.Configure(session, http, vault, new(http, vault, new()), default));
    }

    [Fact]
    public async Task FailedCatalogAndCancelledSignInDoNotChangeAccountsOrPreferences()
    {
        using var directory = new TestDirectory();
        var vault = new CredentialVault(directory.Path, new WindowsSecretProtector());
        using (vault.Acquire()) vault.Write(new() { ActiveId = "cli", Accounts = [new() { Id = "cli", Email = "fixture@example.test" }, new() { Id = "recorder" }] });
        var before = File.ReadAllBytes(Path.Combine(directory.Path, "credentials.dpapi"));
        using var agents = new RecorderAgents(directory.Path, new Handler(_ => throw new HttpRequestException("offline")));
        Assert.Equal(2, agents.Accounts().Length);
        await Assert.ThrowsAsync<HttpRequestException>(() => agents.Models(ModelProvider.Local, "http://127.0.0.1:8081/v1", "", default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agents.SignIn(null, _ => { }, cancellation.Token));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(directory.Path, "credentials.dpapi")));
        Assert.Equal("cli", agents.ActiveAccountId());
    }

    [Fact]
    public async Task ExplicitRestartPreservesEvidenceAndCompletedSummaryUntilReplacementSucceeds()
    {
        using var directory = new TestDirectory(); using var analysis = new RecordingAnalysis(directory.Path);
        var job = new RecordingAnalysisJob { Prepared = true, Frames = 16000, Stage = "Done", State = "Completed" };
        Directory.CreateDirectory(analysis.Store.DirectoryFor(job.Id)); analysis.Store.Save(job);
        var transcript = analysis.Store.FileFor(job.Id, "transcript.jsonl");
        const string evidence = """{"id":"s1","speaker":"You","text":"We agreed to ship.","startSeconds":0,"endSeconds":1}""";
        File.WriteAllText(transcript, evidence);
        const string result = """{"items":[{"text":"Agreed to ship.","segments":["s1"]}]}""";
        await analysis.CompleteSummary(job.Id, (_, _) => Task.FromResult(result), "old-agent", default);
        var original = File.ReadAllText(analysis.Store.FileFor(job.Id, "summary.md"));
        var blocked = await analysis.CompleteSummary(job.Id, (_, _) => throw new InvalidOperationException("Must not be called"), "new-agent", default);
        Assert.Contains("Summary model changed", blocked.Error);
        var failed = await analysis.CompleteSummary(job.Id, (_, _) => throw new HttpRequestException("offline"), "new-agent", default, restart: true);
        Assert.Equal("old-agent", failed.CompletedSummaryModel); Assert.Equal("new-agent", failed.SummaryModel);
        Assert.Equal(original, File.ReadAllText(analysis.Store.FileFor(job.Id, "summary.md")));
        var calls = 0;
        Task<string> Replacement(string _, CancellationToken ct) { calls++; return Task.FromResult(result.Replace("Agreed", "They agreed")); }
        var uncertain = await analysis.CompleteSummary(job.Id, Replacement, "new-agent", default);
        Assert.Equal(0, calls); Assert.Contains("uncertain", uncertain.Error);
        var complete = await analysis.CompleteSummary(job.Id, Replacement, "new-agent", default, retryUncertain: true);
        Assert.Equal(1, calls); Assert.Equal("Completed", complete.State); Assert.Equal("new-agent", complete.CompletedSummaryModel);
        Assert.Equal(evidence, File.ReadAllText(transcript));
        Assert.Contains("They agreed", File.ReadAllText(analysis.Store.FileFor(job.Id, "summary.md")));
        var export = Path.Combine(directory.Path, "export"); analysis.Export(job.Id, export);
        Assert.Equal("new-agent", RecordingAnalysisStore.ReadJson<RecordingAnalysisJob>(Path.Combine(export, "job.json")).CompletedSummaryModel);
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(response(request)); }
    }
}
