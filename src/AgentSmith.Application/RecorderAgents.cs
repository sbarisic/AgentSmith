using AgentSmith.Core;
using AgentSmith.LmStudio;
using AgentSmith.OpenAI;

namespace AgentSmith.Application;

public sealed record RecorderAccount(string Id, string Label);
public sealed record RecorderModel(string Id, string Label, bool Available = true);

// Catalog requests never contain recording evidence. Secrets stay in the existing protected vault.
public sealed class RecorderAgents : IDisposable
{
    private readonly HttpClient http;
    private readonly CredentialVault vault;
    private readonly ChatGptAuth auth;
    public RecorderAgents(string directory, HttpMessageHandler? handler = null, ISecretProtector? protector = null)
    {
        http = handler is null ? new() : new(handler);
        vault = new(directory, protector ?? new WindowsSecretProtector());
        auth = new(http, vault, new IdentityValidator());
    }
    public RecorderAccount[] Accounts()
    {
        using var lease = vault.Acquire();
        return vault.Read().Accounts.Select(a => new RecorderAccount(a.Id, a.Email.Length > 0 ? a.Email : a.Id)).ToArray();
    }
    public string ActiveAccountId()
    {
        using var lease = vault.Acquire();
        return vault.Read().ActiveId ?? "";
    }
    public async Task<RecorderAccount> SignIn(string? accountId, Action<string> progress, CancellationToken ct)
    {
        var account = await auth.LoginAsync(accountId, progress, ct, setActiveAccount: false);
        return new(account.Id, account.Email);
    }
    public static void Validate(ChatModelSelection selection)
    {
        if (!Enum.IsDefined(selection.Provider) || string.IsNullOrWhiteSpace(selection.Model) ||
            selection.Model.Length > 2048 || selection.Model.Any(char.IsControl))
            throw new UserFacingException("Choose a provider and enter or select a model ID.");
        if (selection.MaxOutputTokens is < 256 or > 32768) throw new UserFacingException("Output budget must be 256..32768 tokens.");
        ValidateConnection(selection.Provider, selection.Endpoint, selection.AccountId);
    }
    public static void ValidateConnection(ModelProvider provider, string endpoint, string accountId)
    {
        if (provider == ModelProvider.ChatGpt)
        {
            if (string.IsNullOrWhiteSpace(accountId)) throw new UserFacingException("Choose a ChatGPT account or sign in.");
        }
        else if (provider == ModelProvider.LmStudio) _ = LmStudioModelClient.Endpoint(endpoint);
        else if (provider == ModelProvider.Local) _ = LocalChatModelClient.Endpoint(endpoint);
        else throw new UserFacingException("Choose ChatGPT, LM Studio, or Local.");
    }
    public async Task<IReadOnlyList<RecorderModel>> Models(ModelProvider provider, string endpoint, string accountId, CancellationToken ct)
    {
        ValidateConnection(provider, endpoint, accountId);
        var connection = new ModelConnection { Provider = provider, Endpoint = endpoint };
        if (provider == ModelProvider.ChatGpt)
            return (await auth.ModelsAsync(accountId, ct)).Select(m => new RecorderModel(m.Id, m.Name + " · " + m.Id)).ToArray();
        if (provider == ModelProvider.LmStudio)
            return (await new LmStudioModelClient(http, connection).ModelsAsync(ct))
                .Select(m => new RecorderModel(m.Id, m.Name + (m.Loaded ? "" : " · not loaded"), m.Loaded)).ToArray();
        return (await new LocalChatModelClient(http, connection).ModelsAsync(ct)).Select(m => new RecorderModel(m.Id, m.Id)).ToArray();
    }
    public void Dispose() => http.Dispose();
}
