using System.CommandLine;
using AgentSmith.Core;
using AgentSmith.LmStudio;
using AgentSmith.OpenAI;
using AgentSmith.Runtime;

namespace AgentSmith.Cli;

public static partial class Program
{
    private static void WorkerCommands(RootCommand root, Option<string?> data, Option<bool> json, Option<bool> noColor, Option<ModelProvider> provider, Option<string> endpoint, Option<int> output)
    {
        var workers = new Command("workers", "Configure named worker profiles; enable them per conversation using --workers."); root.Subcommands.Add(workers);
        var list = new Command("list", "List saved worker profiles."); workers.Subcommands.Add(list);
        list.SetAction(async (p, ct) => await Execute(p.GetValue(data), p.GetValue(json), p.GetValue(noColor), app => { app.Write("workers", new { profiles = app.Store.Profiles(), message = string.Join('\n', app.Store.Profiles().Select(w => $"{w.Name} · {w.Connection.Provider} · {w.Model} · {w.Mode}")) }); return Task.FromResult(0); }));
        var remove = new Command("remove", "Remove a profile; existing conversation snapshots remain unchanged."); var removeName = new Argument<string>("name"); remove.Arguments.Add(removeName); workers.Subcommands.Add(remove);
        remove.SetAction(async (p, ct) => await Execute(p.GetValue(data), p.GetValue(json), p.GetValue(noColor), app => { app.Store.RemoveProfile(p.GetValue(removeName)!); app.Write("workers", new { message = "Worker profile removed." }); return Task.FromResult(0); }));
        var add = new Command("add", "Add or replace a worker profile."); var name = new Argument<string>("name"); var model = new Option<string>("--model") { Required = true }; var mode = new Option<PermissionMode>("--mode") { DefaultValueFactory = _ => PermissionMode.Inspect }; var account = new Option<string?>("--account");
        add.Arguments.Add(name); add.Options.Add(model); add.Options.Add(mode); add.Options.Add(account); workers.Subcommands.Add(add);
        add.SetAction(async (p, ct) => await Execute(p.GetValue(data), p.GetValue(json), p.GetValue(noColor), async app =>
        {
            var session = new Session { Model = p.GetValue(model)!, AccountId = p.GetValue(account) ?? "", Connection = new() { Provider = p.GetValue(provider), Endpoint = p.GetValue(endpoint)!, MaxOutputTokens = p.GetValue(output) } };
            await app.ConfigureModel(session, ct);
            app.Store.PutProfile(new() { Name = p.GetValue(name)!, Model = session.Model, AccountId = session.AccountId, Connection = session.Connection, Mode = p.GetValue(mode) });
            app.Write("workers", new { message = "Saved worker profile " + p.GetValue(name) + ". Enable it with --workers " + p.GetValue(name) }); return 0;
        }));
    }
}

internal sealed partial class CliApp
{
    public WorkerCoordinator Agents { get; private set; } = null!;
    private readonly SemaphoreSlim tokenRefresh = new(1, 1);
    internal Func<Session, IModelClient>? ModelFactoryOverride { get; set; }
    public IModelClient ModelClient(Session session)
    {
        if (ModelFactoryOverride is not null) return ModelFactoryOverride(session);
        if (ModelOverride is not null) return ModelOverride;
        return session.Connection.Provider == ModelProvider.LmStudio
            ? new LmStudioModelClient(http, session.Connection)
            : new ChatGptModelClient(http, async token => { await tokenRefresh.WaitAsync(token); try { return await Auth.AccessTokenAsync(session.AccountId, token); } finally { tokenRefresh.Release(); } });
    }
    public async Task ConfigureModel(Session session, CancellationToken ct)
    {
        if (!Enum.IsDefined(session.Connection.Provider)) throw new UserFacingException("Use provider chatgpt or lmstudio.");
        if (session.Connection.MaxOutputTokens is < 256 or > 32768) throw new UserFacingException("Output budget must be 256..32768 tokens.");
        if (session.Connection.Provider == ModelProvider.LmStudio)
        {
            var model = (await new LmStudioModelClient(http, session.Connection).ModelsAsync(ct)).SingleOrDefault(m => m.Id == session.Model && m.Loaded) ?? throw new UserFacingException("Selected LM Studio model is not loaded. Run smith models --provider lmstudio.");
            if (!model.ToolUse) throw new UserFacingException("Local agent models must advertise trained tool use.");
            session.Connection.LoadedContextTokens = model.ContextTokens; session.AccountId = "";
        }
        else
        {
            var account = Vault.Active(session.AccountId.Length == 0 ? null : session.AccountId); session.AccountId = account.Id;
            if (!(await Auth.ModelsAsync(account.Id, ct)).Any(m => m.Id == session.Model)) throw new UserFacingException("Model is not in the selected ChatGPT account catalog.");
        }
    }
    public async Task ShowModels(ModelProvider provider, string endpoint, CancellationToken ct)
    {
        if (provider == ModelProvider.LmStudio) Write("models", (await new LmStudioModelClient(http, new() { Provider = provider, Endpoint = endpoint }).ModelsAsync(ct)).Select(m => new { m.Id, name = m.Name + (m.Loaded ? " · loaded" : " · not loaded"), m.ContextTokens, m.ToolUse }));
        else if (provider == ModelProvider.ChatGpt) Write("models", await Auth.ModelsAsync(Vault.Active().Id, ct));
        else throw new UserFacingException("Unknown provider.");
    }
    public object Header(Session s, bool resumed = false) => new { s.Id, s.Workspace, s.Model, s.AccountId, provider = s.Connection.Provider.ToString(), endpoint = s.Connection.Provider == ModelProvider.LmStudio ? s.Connection.Endpoint : null, s.Connection.LoadedContextTokens, mode = s.Mode.ToString(), workflow = s.Workflow.ToString(), history = resumed ? s.History : null };
    private async Task<bool> AgentCommand(string name, string args, Session session)
    {
        if (name == "/agents")
        {
            if (args.Length > 0) throw new UserFacingException("Usage: /agents");
            var records = Store.Workers(session.Id);
            Write("agents", new { agents = records.Select(WorkerCoordinator.Summary), message = records.Count == 0 ? "No delegated tasks." : string.Join('\n', records.Select(w => $"#{w.Number} · {w.Profile.Name} · {w.State} · {Activity.Clean(w.Task, 120)}")) }); return true;
        }
        if (name == "/agent")
        {
            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is < 1 or > 2 || parts.Length == 2 && parts[1] != "cancel") throw new UserFacingException("Usage: /agent <id|#number> [cancel]");
            var worker = Agents.Find(session, parts[0]);
            if (parts.Length == 2) { await Agents.CancelAsync(worker.Id); worker = Agents.Find(session, worker.Id); }
            Write("agent.detail", new { agent = WorkerCoordinator.Summary(worker), report = worker.Report, message = $"Agent #{worker.Number} · {worker.Profile.Name} · {worker.State}\n{Activity.Clean(worker.Report ?? worker.Reason ?? "No report yet.", 8000)}" }); return true;
        }
        return false;
    }
}
