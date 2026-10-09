using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentSmith.Core;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AgentSmith.OpenAI;

public sealed record OAuthAttempt(string State, string Nonce, string Verifier, string RedirectUri, string? ClientId);
public sealed record CallbackResult(string Code, string ClientId);
public sealed record VerifiedIdentity(string Subject, string Email);

public static class OAuthProtocol
{
    public const string Resource = "https://api.openai.com/v1";
    public const string Issuer = "https://auth.openai.com";
    public const string Scope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    public static string RandomValue() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    public static string AuthorizeUrl(OAuthAttempt attempt, string hostId, Account? returning)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = attempt.ClientId ?? "dynamic_agent_client",
            ["ext_agent_host_id"] = hostId,
            ["response_type"] = "code",
            ["redirect_uri"] = attempt.RedirectUri,
            ["scope"] = Scope,
            ["resource"] = Resource,
            ["state"] = attempt.State,
            ["nonce"] = attempt.Nonce,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(attempt.Verifier)))
        };
        if (attempt.ClientId is null) query["agent_name_hint"] = "AgentSmith";
        if (!string.IsNullOrEmpty(returning?.IdToken)) query["id_token_hint"] = returning.IdToken;
        return Issuer + "/api/accounts/authorize?" + string.Join('&', query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    }
    public static CallbackResult ValidateCallback(Uri uri, OAuthAttempt attempt)
    {
        var expected = new Uri(attempt.RedirectUri);
        if (uri.GetLeftPart(UriPartial.Path) != expected.GetLeftPart(UriPartial.Path)) throw new UserFacingException("Invalid authorization callback URI.");
        var values = new Dictionary<string, string>();
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            if (!values.TryAdd(key, pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "")) throw new UserFacingException("Duplicate authorization callback field.");
        }
        if (!values.TryGetValue("state", out var state) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(attempt.State))) throw new UserFacingException("Authorization state validation failed.");
        if (values.ContainsKey("error")) throw new UserFacingException("ChatGPT sign-in was declined or failed. No credentials were changed.");
        var clientId = values.GetValueOrDefault("client_id") ?? attempt.ClientId;
        if (string.IsNullOrEmpty(clientId) || clientId == "dynamic_agent_client" || (attempt.ClientId is not null && clientId != attempt.ClientId)) throw new UserFacingException("The issued client registration is missing or does not match.");
        if (!values.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)) throw new UserFacingException("Missing authorization code.");
        return new(code, clientId);
    }
}
public sealed class IdentityValidator
{
    private readonly IConfigurationManager<OpenIdConnectConfiguration> configuration;
    public IdentityValidator(IConfigurationManager<OpenIdConnectConfiguration>? configuration = null)
    {
        this.configuration = configuration ?? new ConfigurationManager<OpenIdConnectConfiguration>(OAuthProtocol.Issuer + "/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever());
    }
    public async Task<VerifiedIdentity> ValidateAsync(string token, string clientId, string? nonce, string? expectedSubject, CancellationToken ct)
    {
        var config = await configuration.GetConfigurationAsync(ct);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = OAuthProtocol.Issuer,
            ValidAudience = clientId,
            IssuerSigningKeys = config.SigningKeys,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        });
        if (!result.IsValid) throw new UserFacingException("ChatGPT identity-token validation failed.");
        var claims = result.ClaimsIdentity;
        var subject = claims.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(subject) || (expectedSubject is not null && subject != expectedSubject) || (nonce is not null && claims.FindFirst("nonce")?.Value != nonce))
            throw new UserFacingException("ChatGPT identity or nonce does not match this sign-in attempt.");
        return new(subject, claims.FindFirst("email")?.Value ?? "");
    }
}
public sealed class ChatGptAuth(HttpClient http, CredentialVault vault, IdentityValidator validator)
{
    public async Task<Account> LoginAsync(string? accountId, Action<string> notify, CancellationToken ct)
    {
        Account? previous;
        string hostId;
        using (vault.Acquire())
        {
            var data = vault.Read();
            previous = accountId is null ? null : data.Accounts.SingleOrDefault(a => a.Id == accountId) ?? throw new UserFacingException("Unknown account ID.");
            hostId = data.HostId;
            vault.Write(data);
        }
        using var listener = new HttpListener();
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        var redirect = $"http://127.0.0.1:{port}/auth/callback";
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var attempt = new OAuthAttempt(OAuthProtocol.RandomValue(), OAuthProtocol.RandomValue(), OAuthProtocol.RandomValue(), redirect, previous?.ClientId);
        notify("Continue with ChatGPT in your browser. Waiting for explicit sign-in and consent; Ctrl+C cancels.");
        try { Process.Start(new ProcessStartInfo(OAuthProtocol.AuthorizeUrl(attempt, hostId, previous)) { UseShellExecute = true }); }
        catch { throw new UserFacingException("Could not open the system browser. Configure a default browser and retry sign-in."); }
        CallbackResult callback;
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(ct);
            if (context.Request.Url?.AbsolutePath != "/auth/callback") { context.Response.StatusCode = 404; context.Response.Close(); continue; }
            try
            {
                if (context.Request.HttpMethod != "GET" || context.Request.RawUrl?.Length > 16000) throw new UserFacingException("Invalid authorization callback request.");
                callback = OAuthProtocol.ValidateCallback(context.Request.Url!, attempt);
                var message = Encoding.UTF8.GetBytes("Authorization received. Return to AgentSmith to check sign-in completion.");
                context.Response.ContentType = "text/plain; charset=utf-8";
                context.Response.Headers["Cache-Control"] = "no-store";
                await context.Response.OutputStream.WriteAsync(message, ct);
                context.Response.Close();
                break;
            }
            catch { context.Response.StatusCode = 400; context.Response.Close(); throw; }
        }
        using var tokens = await ExchangeAsync(new() { ["grant_type"] = "authorization_code", ["client_id"] = callback.ClientId, ["code"] = callback.Code, ["code_verifier"] = attempt.Verifier, ["redirect_uri"] = redirect, ["resource"] = OAuthProtocol.Resource }, ct);
        var token = tokens.RootElement.GetProperty("id_token").GetString()!;
        var identity = await validator.ValidateAsync(token, callback.ClientId, attempt.Nonce, previous?.Subject, ct);
        using (vault.Acquire())
        {
            var data = vault.Read();
            var account = data.Accounts.SingleOrDefault(a => a.ClientId == callback.ClientId && a.Subject == identity.Subject) ?? new Account { ClientId = callback.ClientId, Subject = identity.Subject };
            account.Email = identity.Email;
            account.Scopes = []; // A new sign-in must establish permission again.
            ApplyTokens(account, tokens.RootElement);
            if (!data.Accounts.Contains(account)) data.Accounts.Add(account);
            data.ActiveId = account.Id;
            vault.Write(data);
            return account;
        }
    }
    public async Task<string> AccessTokenAsync(string accountId, CancellationToken ct)
    {
        using var lease = vault.Acquire();
        var data = vault.Read();
        var account = data.Accounts.SingleOrDefault(a => a.Id == accountId) ?? throw new UserFacingException("Saved conversation account is unavailable. Sign in with that registration again.");
        if (!account.CanInfer) throw new UserFacingException("This account has no active ChatGPT plan permission. Run auth login --account with its ID.");
        if (account.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
        {
            if (account.RefreshToken.Length == 0) throw new UserFacingException("ChatGPT sign-in expired. Sign in again.");
            using var tokens = await ExchangeAsync(new() { ["grant_type"] = "refresh_token", ["client_id"] = account.ClientId, ["refresh_token"] = account.RefreshToken, ["resource"] = OAuthProtocol.Resource }, ct);
            if (tokens.RootElement.TryGetProperty("id_token", out var id)) await validator.ValidateAsync(id.GetString()!, account.ClientId, null, account.Subject, ct);
            ApplyTokens(account, tokens.RootElement);
            vault.Write(data);
            if (!account.CanInfer) throw new UserFacingException("ChatGPT plan permission is no longer granted.");
        }
        return account.AccessToken;
    }
    public static void ApplyTokens(Account account, JsonElement tokens)
    {
        if (!tokens.TryGetProperty("token_type", out var type) || !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)) throw new UserFacingException("Unsupported token response.");
        var access = tokens.GetProperty("access_token").GetString();
        var expiry = tokens.GetProperty("expires_in").GetInt32();
        if (string.IsNullOrEmpty(access) || expiry <= 0) throw new UserFacingException("Invalid token response.");
        account.AccessToken = access;
        account.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiry);
        if (tokens.TryGetProperty("refresh_token", out var refresh)) account.RefreshToken = refresh.GetString() ?? "";
        if (tokens.TryGetProperty("id_token", out var id)) account.IdToken = id.GetString() ?? "";
        if (tokens.TryGetProperty("scope", out var scope)) account.Scopes = (scope.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
    private async Task<JsonDocument> ExchangeAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await http.PostAsync(OAuthProtocol.Issuer + "/api/accounts/oauth/token", new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode) throw new UserFacingException($"ChatGPT token exchange failed (HTTP {(int)response.StatusCode}). Sign in again if access expired or was revoked.");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    public async Task<bool> LogoutAsync(string accountId, CancellationToken ct)
    {
        using var lease = vault.Acquire();
        var data = vault.Read();
        var account = data.Accounts.SingleOrDefault(a => a.Id == accountId) ?? throw new UserFacingException("Unknown account.");
        var confirmed = false;
        try
        {
            using var discovery = JsonDocument.Parse(await http.GetStringAsync(OAuthProtocol.Issuer + "/.well-known/openid-configuration", ct));
            var endpoint = new Uri(discovery.RootElement.GetProperty("revocation_endpoint").GetString()!);
            if (endpoint.Scheme != "https" || endpoint.Host != "auth.openai.com") throw new UserFacingException("Untrusted revocation endpoint.");
            using var response = await http.PostAsync(endpoint, new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = account.RefreshToken, ["token_type_hint"] = "refresh_token", ["client_id"] = account.ClientId }), ct);
            confirmed = response.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or UserFacingException) { }
        finally
        {
            account.AccessToken = account.RefreshToken = account.IdToken = "";
            account.Scopes = [];
            vault.Write(data);
        }
        return confirmed;
    }
    public async Task<IReadOnlyList<ModelChoice>> ModelsAsync(string accountId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, OAuthProtocol.Resource + "/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(accountId, ct));
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new UserFacingException($"Model discovery failed (HTTP {(int)response.StatusCode}).");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("models", out var models)) throw new UserFacingException("Unexpected ChatGPT model catalog format.");
        return models.EnumerateArray().Where(m => m.TryGetProperty("visibility", out var v) && v.GetString() == "list")
            .Select(m => new ModelChoice(m.GetProperty("slug").GetString()!, m.GetProperty("display_name").GetString()!)).ToArray();
    }
}
public sealed record ModelChoice(string Id, string Name);
