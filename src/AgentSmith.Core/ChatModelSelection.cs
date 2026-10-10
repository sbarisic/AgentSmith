namespace AgentSmith.Core;

// Configuration references only; capabilities are rediscovered before use.
public sealed record ChatModelSelection(string Model, ModelProvider Provider, string Endpoint, string AccountId, int MaxOutputTokens)
{
    public static ChatModelSelection From(Session session) => new(session.Model, session.Connection.Provider,
        session.Connection.Endpoint, session.AccountId, session.Connection.MaxOutputTokens);
    public ModelConnection Connection() => new() { Provider = Provider, Endpoint = Endpoint, MaxOutputTokens = MaxOutputTokens };
}
