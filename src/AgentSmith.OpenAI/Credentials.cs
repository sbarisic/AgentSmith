using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentSmith.Core;

namespace AgentSmith.OpenAI;

public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ClientId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Email { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string IdToken { get; set; } = "";
    public string[] Scopes { get; set; } = [];
    public DateTimeOffset ExpiresAt { get; set; }
    public bool CanInfer => Scopes.Contains("chatgpt.tokens.use.direct") && AccessToken.Length > 0;
}
public sealed class Credentials
{
    public string HostId { get; set; } = "urn:uuid:" + Guid.NewGuid();
    public string? ActiveId { get; set; }
    public List<Account> Accounts { get; set; } = [];
}
public interface ISecretProtector
{
    byte[] Protect(byte[] value);
    byte[] Unprotect(byte[] value);
}
public sealed class WindowsSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] value)
    {
        if (!OperatingSystem.IsWindows()) throw new UserFacingException("Credential storage currently supports Windows only.");
        return ProtectedData.Protect(value, null, DataProtectionScope.CurrentUser);
    }
    public byte[] Unprotect(byte[] value)
    {
        if (!OperatingSystem.IsWindows()) throw new UserFacingException("Credential storage currently supports Windows only.");
        return ProtectedData.Unprotect(value, null, DataProtectionScope.CurrentUser);
    }
}
public sealed class CredentialVault(string directory, ISecretProtector protector)
{
    private readonly string root = Path.GetFullPath(directory);
    public IDisposable Acquire()
    {
        Directory.CreateDirectory(root);
        try { return new FileStream(Path.Combine(root, "credentials.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new UserFacingException("Another AgentSmith process is using account credentials. Retry after it finishes."); }
    }
    public Credentials Read()
    {
        var path = Path.Combine(root, "credentials.dpapi");
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<Credentials>(protector.Unprotect(File.ReadAllBytes(path)), Json.Options) ?? throw new JsonException(); }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { throw new UserFacingException("Cannot read protected AgentSmith credentials for this Windows user."); }
    }
    public void Write(Credentials credentials)
    {
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "credentials.dpapi");
        var temporary = target + ".tmp";
        File.WriteAllBytes(temporary, protector.Protect(Encoding.UTF8.GetBytes(Json.Serialize(credentials))));
        File.Move(temporary, target, true);
    }
    public Account Active(string? id = null)
    {
        using var lease = Acquire();
        var data = Read();
        return data.Accounts.SingleOrDefault(a => a.Id == (id ?? data.ActiveId)) ?? throw new UserFacingException("No selected ChatGPT account. Run smith auth login first.");
    }
}
