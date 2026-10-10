using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.OpenAI;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace AgentSmith.Tests;

public class ProtocolTests
{
    private static OAuthAttempt Attempt() => new("state", "nonce", "verifier", "http://127.0.0.1:1455/auth/callback", null);
    [Theory]
    [InlineData("?state=wrong&code=secret&client_id=oaiapp_test")]
    [InlineData("?state=state&code=secret")]
    [InlineData("?state=state&code=secret&client_id=dynamic_agent_client")]
    [InlineData("?state=state&error=access_denied")]
    [InlineData("?state=state&state=state&code=secret&client_id=oaiapp_test")]
    public void InvalidCallbackRejected(string query) => Assert.Throws<UserFacingException>(() => OAuthProtocol.ValidateCallback(new Uri(Attempt().RedirectUri + query), Attempt()));
    [Fact]
    public void RegistrationUsesIssuedClientAndPkce()
    {
        var result = OAuthProtocol.ValidateCallback(new Uri(Attempt().RedirectUri + "?state=state&code=code&client_id=oaiapp_test"), Attempt());
        Assert.Equal("oaiapp_test", result.ClientId);
        var uri = OAuthProtocol.AuthorizeUrl(Attempt(), "urn:uuid:host", null);
        Assert.Contains("code_challenge_method=S256", uri); Assert.Contains("agent_name_hint=AgentSmith", uri); Assert.DoesNotContain("code_verifier", uri);
        var returning = Attempt() with { ClientId = "old" };
        Assert.Throws<UserFacingException>(() => OAuthProtocol.ValidateCallback(new Uri(returning.RedirectUri + "?state=state&code=x&client_id=new"), returning));
    }
    [Fact]
    public void IdentityAloneDoesNotGrantInference()
    {
        var account = new Account(); using var tokens = JsonDocument.Parse("{\"token_type\":\"Bearer\",\"access_token\":\"seeded-secret\",\"expires_in\":3600,\"scope\":\"openid email\"}");
        ChatGptAuth.ApplyTokens(account, tokens.RootElement); Assert.False(account.CanInfer);
    }
    [Theory]
    [InlineData("wrong", "client", "subject", false)]
    [InlineData("nonce", "wrong", "subject", false)]
    [InlineData("nonce", "client", "other", false)]
    [InlineData("nonce", "client", "subject", true)]
    public async Task IdentityValidationRejectsMismatchAndExpiry(string nonce, string audience, string subject, bool expired)
    {
        using var rsa = RSA.Create(2048); var key = new RsaSecurityKey(rsa) { KeyId = "test" };
        var config = new OpenIdConnectConfiguration { Issuer = OAuthProtocol.Issuer }; config.SigningKeys.Add(key);
        var validator = new IdentityValidator(new StaticConfigurationManager<OpenIdConnectConfiguration>(config));
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = OAuthProtocol.Issuer,
            Audience = audience,
            Claims = new Dictionary<string, object> { { "sub", subject }, { "nonce", nonce } },
            IssuedAt = DateTime.UtcNow.AddHours(-2),
            NotBefore = DateTime.UtcNow.AddHours(-2),
            Expires = expired ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        });
        await Assert.ThrowsAsync<UserFacingException>(() => validator.ValidateAsync(token, "client", "nonce", "subject", default));
    }
    [Fact]
    public async Task ValidIdentityAccepted()
    {
        using var rsa = RSA.Create(2048); var key = new RsaSecurityKey(rsa) { KeyId = "test" }; var config = new OpenIdConnectConfiguration(); config.SigningKeys.Add(key);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor { Issuer = OAuthProtocol.Issuer, Audience = "client", Claims = new Dictionary<string, object> { { "sub", "subject" }, { "nonce", "nonce" } }, Expires = DateTime.UtcNow.AddHours(1), SigningCredentials = new(key, SecurityAlgorithms.RsaSha256) });
        Assert.Equal("subject", (await new IdentityValidator(new StaticConfigurationManager<OpenIdConnectConfiguration>(config)).ValidateAsync(token, "client", "nonce", null, default)).Subject);
    }
    [Fact]
    public async Task SseHandlesFragmentedUtf8AndReturnsCompleteOutput()
    {
        var json = new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["status"] = "completed", ["output"] = new JsonArray(RuntimeTests.Call("a", "workspace_list", "{\"path\":\".\"}")) } };
        var data = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Živjo\"}\r\n\r\ndata: " + json.ToJsonString() + "\n\n";
        using var stream = new FragmentedStream(Encoding.UTF8.GetBytes(data)); var events = new List<ModelEvent>();
        await foreach (var ev in ChatGptModelClient.ParseStreamAsync(stream, default)) events.Add(ev);
        Assert.Equal("Živjo", events[0].Text); Assert.Single(events.Last().Output!);
    }
    [Theory]
    [InlineData("data: {\"type\":\"response.function_call_arguments.delta\",\"delta\":\"{\"}\n\n")]
    [InlineData("data: {\"type\":\"response.failed\"}\n\n")]
    [InlineData("data: nonsense\n\n")]
    [InlineData("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n")]
    public async Task IncompleteOrFailedStreamRejected(string sse)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        await Assert.ThrowsAsync<UserFacingException>(async () => { await foreach (var ev in ChatGptModelClient.ParseStreamAsync(stream, default)) Assert.NotEqual("completed", ev.Type); });
    }
    [Fact]
    public async Task CompletedItemsSurviveEmptyCompletionEnvelope()
    {
        var done = new JsonObject { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = RuntimeTests.Call("live-shape", "file_read", "{\"path\":\"evidence.txt\",\"startLine\":1,\"lineCount\":1}") };
        var sse = "data: " + done.ToJsonString() + "\n\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n";
        using var stream = new FragmentedStream(Encoding.UTF8.GetBytes(sse));
        var events = new List<ModelEvent>(); await foreach (var ev in ChatGptModelClient.ParseStreamAsync(stream, default)) events.Add(ev);
        Assert.Equal("live-shape", Assert.Single(Assert.Single(events).Output!)["call_id"]!.ToString());
    }
    [Fact]
    public void RequestUsesChatGptContractWithoutSecrets()
    {
        var request = ChatGptModelClient.BuildRequest(new("model", "instructions", [], new AgentSmith.Tools.WorkspaceTools().Definitions));
        Assert.False(request["store"]!.GetValue<bool>()); Assert.True(request["stream"]!.GetValue<bool>());
        Assert.Equal(16384, request["max_output_tokens"]!.GetValue<int>());
        Assert.Equal("namespace", request["tools"]![0]!["type"]!.ToString());
        foreach (var unsupported in new[] { "temperature", "previous_response_id", "conversation", "metadata" }) Assert.False(request.ContainsKey(unsupported));
    }
    [Fact]
    public void DpapiVaultDoesNotStorePlaintextAndLocks()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); var vault = new CredentialVault(dir.Path, new WindowsSecretProtector());
        var data = new Credentials { Accounts = [new() { AccessToken = "seeded-sensitive-access", RefreshToken = "seeded-sensitive-refresh" }] };
        using (var lease = vault.Acquire()) { vault.Write(data); Assert.Throws<UserFacingException>(() => vault.Acquire()); }
        Assert.DoesNotContain("seeded-sensitive", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dir.Path, "credentials.dpapi"))));
        Assert.Equal("seeded-sensitive-access", vault.Read().Accounts[0].AccessToken);
    }
}
internal sealed class FragmentedStream(byte[] data) : MemoryStream(data)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
}
