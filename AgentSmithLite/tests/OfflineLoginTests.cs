using System.Net;
using System.Security.Cryptography;
using System.Text;
using AgentSmith.Core;
using AgentSmith.OpenAI;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

internal static class OfflineLoginTests
{
    internal static async Task Success(string root)
    {
        using var fixture = new Fixture(root);
        var account = await fixture.Auth.LoginAsync(null, _ => { }, default, (url, _) =>
        {
            fixture.Authorization = Parse(new Uri(url).Query);
            Check(fixture.Authorization["agent_name_hint"] == "AgentSmithLite", "Incorrect app name.");
            Check(fixture.Authorization["ext_agent_host_id"] == fixture.HostId, "Destination host identity changed.");
            Check(fixture.Authorization["code_challenge_method"] == "S256", "PKCE missing.");
            Check(!fixture.Authorization.ContainsKey("code_verifier"), "Verifier was exposed in the link.");
            return Task.FromResult<string?>(fixture.Callback());
        });
        Check(fixture.Handler.Calls == 1, "Unexpected number of code exchanges.");
        Check(account.Subject == "offline-subject" && account.CanInfer, "Identity/permission not stored.");
        using var lease = fixture.Vault.Acquire();
        var credentials = fixture.Vault.Read();
        Check(credentials.HostId == fixture.HostId, "Host ID was overwritten.");
        Check(credentials.ActiveId == account.Id, "New login not selected.");
        Check(credentials.Accounts.Single(a => a.Id == fixture.PreviousId).AccessToken == "old-fixture-token", "Previous account was modified.");
        Check(credentials.Accounts.Single(a => a.Id == account.Id).RefreshToken == "offline-refresh-fixture", "Refresh token not retained.");
    }

    internal static async Task InvalidCallbacks(string root)
    {
        foreach (var kind in new[] { "wrong-state", "wrong-host", "wrong-port", "wrong-path", "duplicate-state", "fragment", "oversized", "missing-client", "denied", "not-url" })
        {
            using var fixture = new Fixture(root);
            try
            {
                await fixture.Auth.LoginAsync(null, _ => { }, default, (url, _) =>
                {
                    fixture.Authorization = Parse(new Uri(url).Query);
                    var callback = fixture.Callback();
                    callback = kind switch
                    {
                        "wrong-state" => callback.Replace(fixture.Authorization["state"], "wrong-state", StringComparison.Ordinal),
                        "wrong-host" => callback.Replace("127.0.0.1", "localhost", StringComparison.Ordinal),
                        "wrong-port" => callback.Replace(":" + new Uri(fixture.Authorization["redirect_uri"]).Port + "/", ":1/", StringComparison.Ordinal),
                        "wrong-path" => callback.Replace("/auth/callback", "/callback", StringComparison.Ordinal),
                        "duplicate-state" => callback + "&state=another",
                        "fragment" => callback + "#fragment",
                        "oversized" => callback + new string('x', 16001),
                        "missing-client" => callback.Replace("&client_id=offline-client", "", StringComparison.Ordinal),
                        "denied" => callback + "&error=access_denied",
                        _ => "not a callback URL"
                    };
                    return Task.FromResult<string?>(callback);
                });
                throw new Exception("Invalid callback accepted: " + kind);
            }
            catch (UserFacingException) { }
            Check(fixture.Handler.Calls == 0, "Invalid callback reached token exchange: " + kind);
            fixture.Unchanged();
        }
    }

    internal static async Task Cancellation(string root)
    {
        using var fixture = new Fixture(root);
        using var cancel = new CancellationTokenSource();
        var prompted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var login = fixture.Auth.LoginAsync(null, _ => { }, cancel.Token, async (_, ct) =>
        {
            prompted.SetResult(true);
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        });
        await prompted.Task;
        Check(!login.IsCompleted, "Login did not wait for user input.");
        cancel.Cancel();
        try { await login; throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        Check(fixture.Handler.Calls == 0, "Cancellation exchanged a code.");
        fixture.Unchanged();
        try
        {
            await fixture.Auth.LoginAsync(null, _ => { }, default, (_, _) => Task.FromResult<string?>(null));
            throw new Exception("EOF was not cancellation.");
        }
        catch (OperationCanceledException) { }
        fixture.Unchanged();
    }

    internal static async Task FailedExchange(string root)
    {
        using var fixture = new Fixture(root);
        fixture.Handler.Status = HttpStatusCode.BadRequest;
        try
        {
            await fixture.Auth.LoginAsync(null, _ => { }, default, (url, _) =>
            {
                fixture.Authorization = Parse(new Uri(url).Query);
                return Task.FromResult<string?>(fixture.Callback());
            });
            throw new Exception("Rejected exchange accepted.");
        }
        catch (UserFacingException ex) { Check(!ex.Message.Contains("offline-code-fixture"), "Code leaked into an error."); }
        Check(fixture.Handler.Calls == 1, "Rejected exchange retried.");
        fixture.Unchanged();
    }

    private static Dictionary<string, string> Parse(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class Fixture : IDisposable
    {
        private readonly RSA rsa = RSA.Create(2048);
        private readonly HttpClient http;
        internal readonly CredentialVault Vault;
        internal readonly ChatGptAuth Auth;
        internal readonly TokenHandler Handler;
        internal readonly string PreviousId;
        internal readonly string HostId;
        internal Dictionary<string, string> Authorization = new();

        internal Fixture(string root)
        {
            Vault = new CredentialVault(Path.Combine(root, "offline-" + Guid.NewGuid().ToString("N")), new WindowsSecretProtector());
            using (Vault.Acquire())
            {
                var saved = new Credentials();
                var previous = new Account { AccessToken = "old-fixture-token" };
                saved.Accounts.Add(previous); saved.ActiveId = previous.Id;
                PreviousId = previous.Id; HostId = saved.HostId;
                Vault.Write(saved);
            }
            var key = new RsaSecurityKey(rsa) { KeyId = "offline-fixture-key" };
            var configuration = new OpenIdConnectConfiguration { Issuer = OAuthProtocol.Issuer };
            configuration.SigningKeys.Add(key);
            Handler = new TokenHandler(async request =>
            {
                Check(request.RequestUri!.ToString() == OAuthProtocol.Issuer + "/api/accounts/oauth/token", "Unexpected token endpoint.");
                var form = Parse(await request.Content!.ReadAsStringAsync());
                Check(form["grant_type"] == "authorization_code", "Wrong grant.");
                Check(form["client_id"] == "offline-client" && form["code"] == "offline-code-fixture", "Callback values lost.");
                Check(form["redirect_uri"] == Authorization["redirect_uri"], "Redirect URI changed during exchange.");
                Check(Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))) == Authorization["code_challenge"], "PKCE verifier does not match.");
                var jwt = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
                {
                    Issuer = OAuthProtocol.Issuer, Audience = "offline-client", Expires = DateTime.UtcNow.AddMinutes(5),
                    Claims = new Dictionary<string, object> { ["sub"] = "offline-subject", ["nonce"] = Authorization["nonce"], ["email"] = "offline@example.invalid" },
                    SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
                });
                return Json.Serialize(new { token_type = "Bearer", access_token = "offline-access-fixture", refresh_token = "offline-refresh-fixture", id_token = jwt, expires_in = 3600, scope = OAuthProtocol.Scope });
            });
            http = new HttpClient(Handler);
            Auth = new ChatGptAuth(http, Vault, new IdentityValidator(new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration)), "AgentSmithLite");
        }

        internal string Callback() => Authorization["redirect_uri"] + "?code=offline-code-fixture&state=" + Authorization["state"] + "&client_id=offline-client";
        internal void Unchanged()
        {
            using var lease = Vault.Acquire();
            var saved = Vault.Read();
            Check(saved.ActiveId == PreviousId && saved.Accounts.Count == 1 && saved.HostId == HostId, "Failed sign-in changed existing account state.");
            Check(saved.Accounts[0].AccessToken == "old-fixture-token", "Failed sign-in replaced credentials.");
        }
        public void Dispose() { http.Dispose(); rsa.Dispose(); }
    }

    private sealed class TokenHandler(Func<HttpRequestMessage, Task<string>> response) : HttpMessageHandler
    {
        internal int Calls;
        internal HttpStatusCode Status = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return new HttpResponseMessage(Status) { Content = new StringContent(await response(request), Encoding.UTF8, "application/json") };
        }
    }
}
