using System.CommandLine;
using System.Text.Json;
using AgentSmith.Core;
using AgentSmith.OpenAI;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using AgentSmith.Tools;

namespace AgentSmith.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        var dataOption = new Option<string?>("--data-dir") { Description = "Override the local state directory.", Recursive = true };
        var jsonOption = new Option<bool>("--json") { Description = "Write newline-delimited JSON events.", Recursive = true };
        var noColor = new Option<bool>("--no-color") { Description = "Disable terminal color.", Recursive = true };
        using var helpRegistry = new ToolRegistry();
        var root = new RootCommand("AgentSmith: ChatGPT-powered project assistant\n" + string.Join('\n', helpRegistry.Definitions.GroupBy(t => t.Capability).Select(g => $"{g.Key} tools: {string.Join(", ", g.Select(t => t.Name))}")));
        root.Options.Add(dataOption); root.Options.Add(jsonOption); root.Options.Add(noColor);
        var auth = new Command("auth", "Manage ChatGPT account registrations."); root.Subcommands.Add(auth);
        var login = new Command("login", "Continue with ChatGPT in the system browser.");
        var loginAccount = new Option<string?>("--account") { Description = "Reauthorize an existing account ID; omitted registers another account." };
        login.Options.Add(loginAccount); auth.Subcommands.Add(login);
        login.SetAction(async (p, ct) => await Execute(p.GetValue(dataOption), p.GetValue(jsonOption), p.GetValue(noColor), async app =>
        {
            var account = await app.Auth.LoginAsync(p.GetValue(loginAccount), message => app.Write("auth.progress", new { message }), ct);
            app.Write("auth.signed_in", new { account.Id, account.Email, planUsage = account.CanInfer });
            return 0;
        }));
        foreach (var name in new[] { "status", "accounts", "logout" })
        {
            var command = new Command(name, $"ChatGPT account {name}."); auth.Subcommands.Add(command);
            command.SetAction(async (p, ct) => await Execute(p.GetValue(dataOption), p.GetValue(jsonOption), p.GetValue(noColor), async app =>
            {
                if (name == "logout")
                {
                    var account = app.Vault.Active();
                    var revoked = await app.Auth.LogoutAsync(account.Id, ct);
                    app.Write("auth.signed_out", new { account.Id, remoteRevocationConfirmed = revoked, message = revoked ? "Signed out." : "Local tokens cleared; remote revocation was not confirmed. You can disconnect the app in ChatGPT Settings." });
                }
                else
                {
                    using var lease = app.Vault.Acquire();
                    var credentials = app.Vault.Read();
                    app.Write("auth.accounts", new { activeId = credentials.ActiveId, accounts = credentials.Accounts.Select(a => new { a.Id, a.Email, planUsage = a.CanInfer, a.ExpiresAt }) });
                }
                return 0;
            }));
        }
        var use = new Command("use", "Select a saved ChatGPT registration.");
        var accountArgument = new Argument<string>("account-id"); use.Arguments.Add(accountArgument); auth.Subcommands.Add(use);
        use.SetAction(p => Execute(p.GetValue(dataOption), p.GetValue(jsonOption), p.GetValue(noColor), app =>
        {
            using var lease = app.Vault.Acquire(); var credentials = app.Vault.Read(); var id = p.GetValue(accountArgument)!;
            if (!credentials.Accounts.Any(a => a.Id == id)) throw new UserFacingException("Unknown account ID.");
            credentials.ActiveId = id; app.Vault.Write(credentials); app.Write("auth.selected", new { id }); return Task.FromResult(0);
        }).GetAwaiter().GetResult());
        var models = new Command("models", "List models available to the selected account."); root.Subcommands.Add(models);
        models.SetAction(async (p, ct) => await Execute(p.GetValue(dataOption), p.GetValue(jsonOption), p.GetValue(noColor), async app =>
        { var account = app.Vault.Active(); app.Write("models", await app.Auth.ModelsAsync(account.Id, ct)); return 0; }));
        foreach (var name in new[] { "chat", "run" })
        {
            var command = new Command(name, name == "chat" ? "Start interactive project chat." : "Execute one request.");
            var mode = new Option<PermissionMode>("--mode") { Description = "Inspect, Edit, or Execute. Execute runs as your Windows account, not sandboxed.", DefaultValueFactory = _ => PermissionMode.Execute };
            var plan = new Option<bool>("--plan") { Description = "Investigate and prepare a plan; implementation requires an explicit review decision." };
            var workspace = new Option<string?>("--workspace") { Description = "Project directory; defaults to the current directory." };
            var model = new Option<string>("--model") { Description = "Explicit model ID from smith models.", Required = true };
            var prompt = new Option<string?>("--prompt") { Required = name == "run", Description = "User request." };
            command.Options.Add(workspace); command.Options.Add(model); command.Options.Add(prompt); command.Options.Add(mode); command.Options.Add(plan); root.Subcommands.Add(command);
            command.SetAction(async (p, ct) => await Execute(p.GetValue(dataOption), p.GetValue(jsonOption), p.GetValue(noColor), async app =>
            {
                var path = WorkspaceTools.ValidateRoot(p.GetValue(workspace) ?? Environment.CurrentDirectory);
                var account = app.Vault.Active();
                var selected = p.GetValue(model)!;
                var available = await app.Auth.ModelsAsync(account.Id, ct);
                if (!available.Any(m => m.Id == selected)) throw new UserFacingException("Selected model is not in this account's catalog. Run smith models.");
                var session = new Session { Workspace = path, AccountId = account.Id, Model = selected, Mode = p.GetValue(mode), Workflow = p.GetValue(plan) ? WorkflowMode.Plan : WorkflowMode.Work };
                app.Acquire(session.Id);
                app.Store.Save(session, new("conversation.created", session.Id, null, new { workspace = path, model = selected }));
                app.Write("conversation", new { session.Id, session.Workspace, session.Model, mode = session.Mode.ToString(), workflow = session.Workflow.ToString(), accountId = account.Id, connection = "Using ChatGPT plan" });
                return await app.Chat(session, p.GetValue(prompt), name == "chat", ct);
            }));
        }
        var resume = new Command("resume", "Inspect and explicitly continue a saved conversation.");
        var conversationArgument = new Argument<string>("conversation-id"); resume.Arguments.Add(conversationArgument); root.Subcommands.Add(resume);
        resume.SetAction(async (p, ct) => await Execute(p.GetValue(dataOption), p.GetValue(jsonOption), p.GetValue(noColor), async app =>
        {
            var id = p.GetValue(conversationArgument)!;
            app.Acquire(id);
            var session = app.Store.Get(id) ?? throw new UserFacingException("Conversation not found.");
            WorkspaceTools.ValidateRoot(session.Workspace);
            app.Vault.Active(session.AccountId);
            app.Registry.RecoverSession(app.Context(session));
            app.Write("conversation.resumed", new { session.Id, session.Workspace, session.Model, session.AccountId, mode = session.Mode.ToString(), workflow = session.Workflow.ToString(), history = session.History });
            if (Planning.Active(session)?.PendingReview is not null) return await app.Chat(session, null, true, ct);
            var last = session.Runs.LastOrDefault();
            if (last is not null && last.State is not (RunState.Completed or RunState.Cancelled or RunState.Failed))
            {
                if (last.State == RunState.AwaitingUser)
                {
                    if (!await app.Answer(session, ct)) return 3;
                }
                else
                {
                    app.Write("resume.confirmation", new { state = last.State.ToString(), last.Reason, message = "Type continue to resume, cancel to abandon, or leave blank to keep this run unchanged." });
                    var input = await app.ReadInput(ct);
                    while (input?.TrimStart().StartsWith('/') == true)
                    {
                        if (input.Trim().Equals("/exit", StringComparison.OrdinalIgnoreCase)) return 3;
                        await app.HandleCommand(input, session);
                        if (last.State == RunState.Cancelled) break;
                        input = await app.ReadInput(ct);
                    }
                    if (input == "cancel") AgentRunner.Abandon(session, app.Store);
                    else if (input == "continue") await app.Run(session, null, null, ct);
                    else if (last.State != RunState.Cancelled) return 3;
                    if (last.State == RunState.RecoveryRequired) return 5;
                    if (last.State == RunState.Paused) return 4;
                }
                if (last.State == RunState.AwaitingUser && !await app.Answer(session, ct)) return 3;
                if (last.State == RunState.Paused) return 4;
                if (last.State == RunState.RecoveryRequired) return 5;
            }
            return await app.Chat(session, null, true, ct);
        }));
        var sessions = new Command("sessions", "List saved conversations."); root.Subcommands.Add(sessions);
        sessions.SetAction(p => Execute(p.GetValue(dataOption), p.GetValue(jsonOption), p.GetValue(noColor), app =>
        { app.Write("sessions", app.Store.List().Select(s => new { s.Id, s.Workspace, s.Model, s.UpdatedAt, state = s.Runs.LastOrDefault()?.State.ToString() })); return Task.FromResult(0); }).GetAwaiter().GetResult());
        var inspect = new Command("inspect", "Read stored run evidence without executing anything.");
        var runArgument = new Argument<string>("run-id"); inspect.Arguments.Add(runArgument); root.Subcommands.Add(inspect);
        inspect.SetAction(p => Execute(p.GetValue(dataOption), p.GetValue(jsonOption), p.GetValue(noColor), app =>
        {
            var run = app.Store.List().SelectMany(s => s.Runs).SingleOrDefault(r => r.Id == p.GetValue(runArgument)) ?? throw new UserFacingException("Run not found.");
            app.Write("run.inspection", run); return Task.FromResult(0);
        }).GetAwaiter().GetResult());
        return await root.Parse(args).InvokeAsync();
    }
    private static async Task<int> Execute(string? directory, bool json, bool noColor, Func<CliApp, Task<int>> action)
    {
        CliApp? app = null;
        void Error(string message)
        {
            try
            {
                if (app is not null) app.Write("error", new { message });
                else if (json) Console.WriteLine(Json.Serialize(new { schemaVersion = 1, type = "error", payload = new { message } }));
                else Console.Error.WriteLine(message);
            }
            catch (IOException) { }
        }
        try { app = new CliApp(directory, json, noColor); return await action(app); }
        catch (OperationCanceledException) { Error("Cancelled."); return 130; }
        catch (UserFacingException ex) { Error(ex.Message); return 2; }
        catch (Exception) { Error("Operation failed. Check connectivity, local state access, and account availability. An unfinished saved run can be inspected and resumed. Secret response bodies are not logged."); return 1; }
        finally { app?.Dispose(); }
    }
}

internal sealed partial class CliApp : IDisposable
{
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
    private readonly bool json;
    private readonly bool color;
    private bool interactiveQuestions;
    private readonly TerminalRenderer terminal;
    private long sequence;
    private CancellationTokenSource? activeRun;
    private readonly ConsoleCancelEventHandler cancelHandler;
    public CredentialVault Vault { get; }
    public ChatGptAuth Auth { get; }
    public SessionStore Store { get; }
    public ToolRegistry Registry { get; } = new();
    private readonly List<IDisposable> leases = [];
    public void Acquire(string id) => leases.Add(Store.Acquire(id));
    public ToolExecutionContext Context(Session session) => new(session, session.Runs.LastOrDefault() ?? new Run(), Store, CancellationToken.None);
    public CliApp(string? directory, bool json, bool noColor)
    {
        this.json = json;
        color = !json && !noColor && !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
        var width = Console.IsOutputRedirected ? 90 : Console.WindowWidth;
        terminal = new TerminalRenderer(Console.Out, color, width);
        var root = Path.GetFullPath(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentSmith"));
        Vault = new(root, new WindowsSecretProtector());
        Auth = new(http, Vault, new IdentityValidator()); Store = new(root);
        cancelHandler = (_, e) => { if (activeRun is not null) { e.Cancel = true; activeRun.Cancel(); } };
        Console.CancelKeyPress += cancelHandler;
    }
    public void Write(string type, object payload, string? conversationId = null, string? runId = null)
    {
        if (json) Console.WriteLine(Json.Serialize(new { schemaVersion = 1, id = Guid.NewGuid().ToString("N"), sequence = ++sequence, timestamp = DateTimeOffset.UtcNow, type, conversationId, runId, payload }));
        else if (type != "question.pending" || !interactiveQuestions) terminal.Render(type, payload);
    }
    public async Task<string?> ReadInput(CancellationToken ct)
    {
        if (!json && !Console.IsInputRedirected) terminal.Prompt();
        try { return await Console.In.ReadLineAsync(ct); }
        finally { if (!json && !Console.IsInputRedirected) terminal.EndPrompt(); }
    }
    public async Task<Run> Run(Session session, string? prompt, string? answer, CancellationToken ct)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(ct); activeRun = source;
        try
        {
            var client = new ChatGptModelClient(http, token => Auth.AccessTokenAsync(session.AccountId, token));
            var runner = new AgentRunner(Store, client, Registry);
            return await runner.RunAsync(session, prompt, answer, e => Write(e.Type, e.Payload, e.ConversationId, e.RunId), source.Token);
        }
        finally { activeRun = null; }
    }
    public async Task<bool> Answer(Session session, CancellationToken ct)
    {
        while (session.Runs.Last().State == RunState.AwaitingUser)
        {
            var question = session.Runs.Last().Question!;
            interactiveQuestions = !json && QuestionPrompt.IsInteractive;
            Write("question.pending", new { question.Id, question.Text, question.Choices });
            var answer = interactiveQuestions ? await QuestionPrompt.ReadAsync(question, color, ct) : await ReadInput(ct);
            if (answer is null) return false;
            if (string.IsNullOrWhiteSpace(answer)) continue;
            var command = await HandleCommand(answer, session, answering: true);
            if (command == CommandAction.Exit) return false;
            if (command != CommandAction.Prompt) continue;
            await Run(session, null, answer, ct);
        }
        return true;
    }
    public async Task<int> Chat(Session session, string? initial, bool interactive, CancellationToken ct)
    {
        interactiveQuestions = interactive && !json && QuestionPrompt.IsInteractive;
        var prompt = initial;
        while (true)
        {
            if (Planning.Active(session)?.PendingReview is not null)
            {
                if (!interactive || !await ReviewPlan(session, ct)) return 3;
            }
            if (session.PendingPlanPrompt is not null && queuedPrompt is null)
            {
                Write("resume.confirmation", new { message = "A saved plan decision is ready to continue. Type continue explicitly, or leave it pending." });
                if (!interactive) return 3;
                var continuation = await ReadInput(ct);
                if (continuation?.TrimStart().StartsWith('/') == true)
                { if (await HandleCommand(continuation, session) == CommandAction.Exit) return 3; continue; }
                if (continuation != "continue") return 3;
                queuedPrompt = session.PendingPlanPrompt;
            }
            if (queuedPrompt is not null) { prompt = queuedPrompt; queuedPrompt = null; }
            if (prompt is null)
            {
                if (!interactive) return 0;
                prompt = await ReadInput(ct);
            }
            if (prompt is null) return 0;
            var command = await HandleCommand(prompt, session);
            if (command == CommandAction.Exit) return 0;
            if (command == CommandAction.Handled) { prompt = null; continue; }
            if (command == CommandAction.Paste)
            {
                Write("paste", new { message = "Enter lines; finish with /end on its own line. Slash-prefixed text here is sent literally." });
                var lines = new List<string>();
                string? line;
                while ((line = await ReadInput(ct)) is not null && line != "/end") lines.Add(line);
                if (line is null) return 0; // EOF does not submit an unfinished paste.
                prompt = string.Join('\n', lines);
            }
            if (string.IsNullOrWhiteSpace(prompt)) { prompt = null; continue; }
            var run = await Run(session, prompt, null, ct); prompt = null;
            if (run.State == RunState.AwaitingUser)
            {
                if (!interactive || !await Answer(session, ct)) return 3;
                run = session.Runs.Last();
            }
            var code = run.State switch { RunState.Completed => 0, RunState.Cancelled => 130, RunState.Paused => 4, RunState.RecoveryRequired => 5, RunState.AwaitingUser or RunState.AwaitingPlan => 3, _ => 1 };
            if (!interactive || run.State is RunState.Paused or RunState.RecoveryRequired) return code;
        }
    }

    internal enum CommandAction { Prompt, Handled, Exit, Paste }

    internal async Task<CommandAction> HandleCommand(string input, Session session, bool answering = false)
    {
        var text = input.Trim();
        if (!text.StartsWith('/')) return CommandAction.Prompt;
        var parts = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        var name = parts[0].ToLowerInvariant();
        var args = parts.Length == 2 ? parts[1].Trim() : "";
        void Error(string message) => Write("error", new { message });
        if (name is not ("/help" or "/status" or "/context" or "/paste" or "/exit" or "/markdown" or "/tools" or "/mode" or "/changes" or "/undo" or "/processes" or "/stop" or "/recover" or "/plan" or "/tasks" or "/artifacts" or "/artifact"))
        { Error($"Unknown command: {TerminalRenderer.Safe(parts[0])[..Math.Min(80, TerminalRenderer.Safe(parts[0]).Length)]}. Type /help for available commands. Use /paste for literal slash-prefixed text."); return CommandAction.Handled; }
        if (name is not ("/markdown" or "/mode" or "/undo" or "/stop" or "/recover" or "/plan" or "/artifact") && args.Length > 0)
        { Error($"{name} does not accept arguments. Type /help for usage."); return CommandAction.Handled; }
        try { switch (name)
        {
            case "/plan": await PlanCommand(session, args); break;
            case "/tasks": Write("plan.display", new { plan = Planning.Active(session) }); break;
            case "/artifacts": Write("artifact.result", new { content = ArtifactAccess.Execute(Context(session), "artifact_list", new System.Text.Json.Nodes.JsonObject()).Content }); break;
            case "/artifact":
                var artifactParts = args.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (artifactParts.Length == 0 || artifactParts.Length > 1 && (artifactParts.Length != 3 || artifactParts[1] != "search")) throw new UserFacingException("Usage: /artifact <id> [search <text>]");
                var artifactArgs = new System.Text.Json.Nodes.JsonObject { ["artifactId"] = artifactParts[0], ["cursor"] = 0, ["maxCharacters"] = 4000 };
                if (artifactParts.Length == 3) artifactArgs["query"] = artifactParts[2];
                Write("artifact.result", new { content = ArtifactAccess.Execute(Context(session), artifactParts.Length == 3 ? "artifact_search" : "artifact_read", artifactArgs).Content }); break;
            case "/tools":
                Write("tools", Registry.Definitions.Select(t => new { t.Name, t.Description, capability = t.Capability.ToString(), enabled = t.Enabled(session), t.HasEffects, effect = t.Effect.ToString() })); break;
            case "/mode":
                if (args.Length > 0)
                {
                    if (!Enum.TryParse<PermissionMode>(args, true, out var next) || !Enum.IsDefined(next) || int.TryParse(args, out _)) { Error("Usage: /mode inspect|edit|execute"); break; }
                    if (session.Mode == PermissionMode.Execute && next != PermissionMode.Execute) await Registry.Processes.StopAsync(session);
                    if (session.Workflow == WorkflowMode.Work && Planning.Active(session) is { State: "Implementing" } approved && next > approved.Approvals.Last().Mode)
                    { await Registry.Processes.StopAsync(session); session.Workflow = WorkflowMode.Plan; approved.State = "Draft"; Planning.Submit(session); }
                    session.Mode = next; Store.Save(session, new("mode.changed", session.Id, null, new { mode = next.ToString() }));
                }
                Write("mode", new { message = $"Mode: {session.Mode}." + (session.Mode == PermissionMode.Execute ? " Processes run as your Windows account; working directories are not a sandbox." : "") }); break;
            case "/changes":
                Write("changes", session.Mutations.Select(m => new { m.Id, m.Kind, m.State, m.UndoOf, diff = FileOperations.Diff(m) })); break;
            case "/undo":
                if (session.Workflow == WorkflowMode.Plan) throw new UserFacingException("Undo is disabled during Plan workflow.");
                Write("operation", new { content = FileOperations.Undo(Context(session), args).Content }); break;
            case "/processes":
                Write("processes", session.Processes); break;
            case "/stop":
                var stop = new ToolCall { Name = "process_stop", Id = "cli-" + Guid.NewGuid().ToString("N"), Arguments = Json.Serialize(new { processId = args }) };
                var stopContext = Context(session);
                stop.Prepared = Registry.Prepare(stopContext, stop); stopContext.Save("process.stop_requested", new { processId = args });
                Write("operation", new { content = (await Registry.ExecuteAsync(stopContext, stop)).Content }); break;
            case "/recover":
                Registry.RecoverSession(Context(session));
                if (args == "abandon")
                {
                    foreach (var m in session.Mutations.Where(m => m.State == "Prepared")) m.State = "Abandoned";
                    if (session.Runs.LastOrDefault() is { State: not (RunState.Completed or RunState.Cancelled) }) AgentRunner.Abandon(session, Store);
                    else Store.Save(session, new("recovery.abandoned", session.Id, null, new { }));
                    Write("recovery", new { message = "Uncertain operations acknowledged and abandoned. No effects were replayed or undone." });
                }
                else if (args.Length > 0) Error("Usage: /recover [abandon]");
                else Write("recovery", new { message = Json.Serialize(new { unresolvedCalls = session.Runs.SelectMany(r => r.Calls).Where(c => c.State == CallState.Started).Select(c => new { c.Id, c.Name }), mutations = session.Mutations.Where(m => m.State == "Prepared").Select(m => new { m.Id, m.Kind }), interruptedProcesses = session.Processes.Where(p => p.State == "Interrupted").Select(p => new { p.Id, p.Reason }) }) });
                break;
            case "/exit": return CommandAction.Exit;
            case "/help":
                Write("help", new { commands = "/help /plan new|show|revise|implement|leave /tasks /artifacts /artifact <id> [search <text>] /tools /mode inspect|edit|execute /changes /undo <checkpoint> /processes /stop <id> /recover [abandon] /status /context /markdown on|off /paste /exit; Tools execute serially." }); break;
            case "/status":
            case "/context":
                Write("status", new { session.Id, workflow = session.Workflow.ToString(), mode = session.Mode.ToString(), state = session.Runs.LastOrDefault()?.State.ToString(), context = session.Runs.LastOrDefault()?.ContextManifest }); break;
            case "/markdown":
                if (args.Length == 0) Write("display", new { message = $"Markdown display is {(terminal.MarkdownEnabled ? "on" : "off")}. Use /markdown on or /markdown off." });
                else if (args.Equals("on", StringComparison.OrdinalIgnoreCase) || args.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    terminal.MarkdownEnabled = args.Equals("on", StringComparison.OrdinalIgnoreCase);
                    Write("display", new { message = $"Markdown display {(terminal.MarkdownEnabled ? "enabled" : "disabled")}." });
                }
                else Error("Usage: /markdown on|off");
                break;
            case "/paste":
                if (answering) Error("/paste is available at the chat prompt. Use the question's note field to answer, or /exit to leave it pending.");
                else return CommandAction.Paste;
                break;
        } }
        catch (Exception ex) when (ex is UserFacingException or IOException or UnauthorizedAccessException) { Error(ex is UserFacingException ? ex.Message : "Operation could not complete. Use /recover to inspect its state."); }
        return CommandAction.Handled;
    }
    public void Dispose()
    {
        try { Registry.Dispose(); }
        finally { foreach (var lease in leases) lease.Dispose(); Console.CancelKeyPress -= cancelHandler; http.Dispose(); }
    }
}
