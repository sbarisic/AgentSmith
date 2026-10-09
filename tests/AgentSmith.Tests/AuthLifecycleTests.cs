using System.Net;
using System.Text;
using System.Text.Json;
using AgentSmith.Core;
using AgentSmith.OpenAI;
using Xunit;

namespace AgentSmith.Tests;

public class AuthLifecycleTests
{
    private static (CredentialVault Vault, Account Account) Seed(TestDirectory dir)
    {
        var vault = new CredentialVault(dir.Path, new WindowsSecretProtector());
        var account = new Account { ClientId = "issued-client", Subject = "subject", AccessToken = "old-access", RefreshToken = "old-refresh", Scopes = ["chatgpt.tokens.use.direct"], ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        using (var lease = vault.Acquire()) vault.Write(new Credentials { ActiveId = account.Id, Accounts = [account] });
        return (vault, account);
    }
    [Fact]
    public async Task RefreshRotatesTokensAndPreservesRegistration()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); var (vault, account) = Seed(dir);
        var handler = new Handler(async request =>
        {
            var form = await request.Content!.ReadAsStringAsync();
            Assert.Contains("grant_type=refresh_token", form); Assert.Contains("client_id=issued-client", form);
            Assert.Contains("refresh_token=old-refresh", form); Assert.DoesNotContain("scope=", form);
            Assert.Throws<UserFacingException>(() => vault.Acquire());
            return Reply("{\"token_type\":\"Bearer\",\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":3600}");
        });
        using var http = new HttpClient(handler); var auth = new ChatGptAuth(http, vault, new IdentityValidator());
        Assert.Equal("new-access", await auth.AccessTokenAsync(account.Id, default));
        Assert.Equal("new-refresh", vault.Active().RefreshToken); Assert.Equal("issued-client", vault.Active().ClientId);
        Assert.Equal("new-access", await auth.AccessTokenAsync(account.Id, default)); Assert.Equal(1, handler.Count);
    }
    [Fact]
    public async Task FailedRefreshDoesNotOverwriteExistingCredentialsOrExposeToken()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); var (vault, account) = Seed(dir);
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("old-refresh should not be logged") })));
        var error = await Assert.ThrowsAsync<UserFacingException>(() => new ChatGptAuth(http, vault, new IdentityValidator()).AccessTokenAsync(account.Id, default));
        Assert.DoesNotContain("old-refresh", error.ToString()); Assert.Equal("old-refresh", vault.Active().RefreshToken);
    }
    [Fact]
    public async Task LogoutClearsLocalTokensWhenRemoteRevocationFails()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); var (vault, account) = Seed(dir);
        using var http = new HttpClient(new Handler(_ => throw new HttpRequestException("synthetic")));
        Assert.False(await new ChatGptAuth(http, vault, new IdentityValidator()).LogoutAsync(account.Id, default));
        var saved = vault.Active(); Assert.Empty(saved.AccessToken); Assert.Empty(saved.RefreshToken); Assert.Equal("issued-client", saved.ClientId);
    }
    [Fact]
    public async Task LogoutUsesDiscoveredTrustedEndpoint()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); var (vault, account) = Seed(dir);
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get) return Reply("{\"revocation_endpoint\":\"https://auth.openai.com/revoke\"}");
            Assert.Equal("https://auth.openai.com/revoke", request.RequestUri!.ToString());
            Assert.Contains("token_type_hint=refresh_token", await request.Content!.ReadAsStringAsync());
            return new(HttpStatusCode.OK);
        }));
        Assert.True(await new ChatGptAuth(http, vault, new IdentityValidator()).LogoutAsync(account.Id, default));
        Assert.Empty(vault.Active().IdToken);
    }
    private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Count++; return send(request); }
    }
}
