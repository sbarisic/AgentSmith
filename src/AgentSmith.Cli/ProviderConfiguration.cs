using AgentSmith.Core;
using AgentSmith.OpenAI;
using AgentSmith.LmStudio;
namespace AgentSmith.Cli;
internal static class ProviderConfiguration
{
    public static async Task Configure(Session session, HttpClient http, CredentialVault Vault, ChatGptAuth Auth, CancellationToken ct, bool summaryOnly = false)
    {
        if (session.Connection.MaxOutputTokens == 0) session.Connection.MaxOutputTokens = session.Connection.Provider == ModelProvider.ChatGpt ? 16384 : 8192;
        if (!Enum.IsDefined(session.Connection.Provider)) throw new UserFacingException("Use provider chatgpt, lmstudio, or local.");
        if (session.Connection.MaxOutputTokens is < 256 or > 32768) throw new UserFacingException("Output budget must be 256..32768 tokens.");
        if (session.Connection.Provider == ModelProvider.LmStudio)
        {
            var model = (await new LmStudioModelClient(http, session.Connection).ModelsAsync(ct)).SingleOrDefault(m => m.Id == session.Model && m.Loaded) ?? throw new UserFacingException("Selected LM Studio model is not loaded. Run smith models --provider lmstudio.");
            if (!summaryOnly && !model.ToolUse) throw new UserFacingException("Local agent models must advertise trained tool use.");
            session.Connection.LoadedContextTokens = model.ContextTokens; session.AccountId = "";
            session.Connection.Capabilities = new() { ContextTokens = model.ContextTokens, Vision = model.Vision, Source = "LM Studio loaded instance", Confidence = "reported" };
        }
        else if (session.Connection.Provider == ModelProvider.Local)
        {
            var model = (await new LocalChatModelClient(http, session.Connection).ModelsAsync(ct)).SingleOrDefault(m => m.Id == session.Model)
                ?? throw new UserFacingException("Selected model is not in the local server catalog. Run smith models --provider local.");
            session.Connection.LoadedContextTokens = model.ContextTokens; session.AccountId = "";
            session.Connection.Capabilities = new() { ContextTokens = model.ContextTokens, Vision = model.Vision, ConcurrentRequests = model.Slots, Source = "Local /v1/models and /props", Confidence = "reported" };
        }
        else
        {
            var account = Vault.Active(session.AccountId.Length == 0 ? null : session.AccountId); session.AccountId = account.Id;
            var model = (await Auth.ModelsAsync(account.Id, ct)).SingleOrDefault(m => m.Id == session.Model) ?? throw new UserFacingException("Model is not in the selected ChatGPT account catalog.");
            var published = ModelCatalog.Published(session.Model);
            session.Connection.Capabilities = new() { ContextTokens = model.ContextTokens ?? published.ContextTokens, Vision = model.Vision ?? published.Vision, Source = model.ContextTokens is not null ? "ChatGPT account catalog" : published.Source, Confidence = model.ContextTokens is not null ? "reported" : published.Confidence };
        }
    }
}
