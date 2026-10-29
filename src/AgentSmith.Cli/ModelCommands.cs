using AgentSmith.Core;
using System.CommandLine;

namespace AgentSmith.Cli;

public static partial class Program
{
    private static void ModelCommands(RootCommand root, Option<string?> data, Option<bool> json, Option<bool> noColor,
        Option<ModelProvider> provider, Option<string?> endpoint, Option<int> output)
    {
        var command = new Command("model", "Show or save the default model for new chats.");
        var id = new Argument<string?>("id") { Arity = ArgumentArity.ZeroOrOne };
        command.Arguments.Add(id); root.Subcommands.Add(command);
        command.SetAction(async (p, ct) => await Execute(p.GetValue(data), p.GetValue(json), p.GetValue(noColor), async app =>
        {
            if (p.GetValue(id) is not { } model) { app.ShowModel(null); return 0; }
            var selection = app.ResolveChatModel(model, p.GetResult(provider) is { Implicit: false } ? p.GetValue(provider) : null, p.GetValue(endpoint), p.GetValue(output));
            var candidate = new Session { Model = selection.Model, AccountId = selection.AccountId, Connection = selection.Connection() };
            await app.ConfigureModel(candidate, ct);
            candidate.ContextPolicy.WorkingTokens(candidate.Connection);
            app.SaveModelDefault(ChatModelSelection.From(candidate)); app.ShowModel(null); return 0;
        }));
    }
}

internal sealed partial class CliApp
{
    internal void SaveModelDefault(ChatModelSelection selection)
    {
        SmithConfig.SaveModel(Store.DataDirectory, selection);
        Config = SmithConfig.Load(Store.DataDirectory);
        Store.SaveChatDefault(selection);
    }
    internal ChatModelSelection ResolveChatModel(string? model, ModelProvider? provider, string? endpoint, int output)
    {
        var saved = Store.ChatDefault();
        // Existing installations can start a fresh chat using their most recent parent model.
        saved ??= Store.List().FirstOrDefault(s => s.ParentConversationId is null && !string.IsNullOrWhiteSpace(s.Model)) is { } recent
            ? ChatModelSelection.From(recent) : null;
        var configuredProvider = Config.Provider ?? saved?.Provider ?? ModelProvider.ChatGpt;
        var selectedProvider = provider ?? configuredProvider;
        var compatible = saved?.Provider == selectedProvider ? saved : null;
        var selectedModel = model ?? Config.ProviderString(selectedProvider, "model", configuredProvider) ?? compatible?.Model
            ?? throw new UserFacingException("No default model is configured. Run smith models, then smith model <id>, or start smith chat --model <id>.");
        if (string.IsNullOrWhiteSpace(selectedModel) || selectedModel.Length > 2048 || selectedModel.Any(char.IsControl))
            throw new UserFacingException("Invalid model ID. Use smith models to list available IDs.");
        return new(selectedModel, selectedProvider, endpoint ?? Config.ProviderString(selectedProvider, "endpoint", configuredProvider) ?? compatible?.Endpoint ?? Program.ResolveEndpoint(selectedProvider, null),
            compatible?.AccountId ?? "", output == 0 ? Config.ProviderInteger(selectedProvider, "max_output_tokens", configuredProvider) ?? compatible?.MaxOutputTokens ?? (selectedProvider == ModelProvider.ChatGpt ? 16384 : 8192) : output);
    }
    internal void ShowModel(Session? session)
    {
        ChatModelSelection? saved = null;
        try { saved = ResolveChatModel(null, null, null, 0); }
        catch (UserFacingException) when (Config.String("model") is null && Store.ChatDefault() is null) { }
        Write("model.selection", new { model = session?.Model, provider = session?.Connection.Provider.ToString(), defaults = saved,
            message = (session is null ? "" : $"Current: {session.Model} · {session.Connection.Provider}\n")
                + (saved is null ? "No saved default. Use /model <id> or smith model <id>." : $"New chats: {saved.Model} · {saved.Provider}\nStart with smith chat.") });
    }
    internal Func<Session, CancellationToken, Task>? ConfigureModelOverride { get; set; }
    private async Task<bool> ModelCommand(string name, string args, Session session)
    {
        if (name is not ("/model" or "/models")) return false;
        if (name == "/models")
        {
            if (args.Length > 0) throw new UserFacingException("Usage: /models");
            using var discovery = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await ShowModels(session.Connection.Provider, session.Connection.Endpoint, discovery.Token, string.IsNullOrEmpty(session.AccountId) ? null : session.AccountId); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            { throw new UserFacingException("Could not retrieve the model catalog. Check the provider connection and try /models again. The model selection is unchanged."); }
            return true;
        }
        if (args.Length == 0) { ShowModel(session); return true; }
        if (activeRun is not null || Unfinished(session) || Store.Workers(session.Id).Any(w => w.Active || w.ReservationsHeld)
            || Planning.Active(session)?.PendingReview is not null)
            throw new UserFacingException("Change models between runs. Finish or cancel current work and settle pending decisions/recovery first. /model shows the current selection.");
        if (args.Length > 2048 || args.Any(char.IsControl)) throw new UserFacingException("Invalid model ID. Use /models to list available IDs.");
        var candidate = new Session { Model = args.Trim('"'), AccountId = session.AccountId,
            Connection = new() { Provider = session.Connection.Provider, Endpoint = session.Connection.Endpoint, MaxOutputTokens = session.Connection.MaxOutputTokens } };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            if (ConfigureModelOverride is not null) await ConfigureModelOverride(candidate, timeout.Token);
            else await ConfigureModel(candidate, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        { throw new UserFacingException("Model validation was unavailable. The current model and saved default are unchanged. Check the provider connection and retry."); }
        session.ContextPolicy.WorkingTokens(candidate.Connection);
        SmithConfig.SaveModel(Store.DataDirectory, ChatModelSelection.From(candidate));
        Config = SmithConfig.Load(Store.DataDirectory);
        Store.SelectChatModel(session, candidate);
        Write("model.selection", new { model = session.Model, provider = session.Connection.Provider.ToString(),
            message = $"Next prompt: {session.Model} · {session.Connection.Provider}. Saved as the default for new chats in this data directory. Worker profiles are unchanged." });
        return true;
    }
}
