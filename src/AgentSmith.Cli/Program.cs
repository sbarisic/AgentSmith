using System.CommandLine;
using System.Text.Json;
using AgentSmith.Core;
using AgentSmith.OpenAI;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using AgentSmith.Tools;

namespace AgentSmith.Cli;

public static partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--internal-csharp-load", var request, var output]) return await NavigationHelper.Run(request, output);
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        var dataOption = new Option<string?>("--data-dir") { Description = "Override the local state directory.", Recursive = true };
        var jsonOption = new Option<bool>("--json") { Description = "Write newline-delimited JSON events.", Recursive = true };
        var noColor = new Option<bool>("--no-color") { Description = "Disable terminal color.", Recursive = true };
        using var helpRegistry = new ToolRegistry();
        var root = new RootCommand("AgentSmith: ChatGPT and local-model project assistant\n" + string.Join('\n', helpRegistry.Definitions.Concat(WorkerCoordinator.DelegationDefinitions).GroupBy(t => t.Capability).Select(g => $"{g.Key} tools: {string.Join(", ", g.Select(t => t.Name == "agent_finish" ? "agent_finish (worker only)" : t.Name))}")));
        root.Options.Add(dataOption); root.Options.Add(jsonOption); root.Options.Add(noColor);
        MemoryCommands(root, dataOption, jsonOption, noColor);
        var provider = new Option<ModelProvider>("--provider") { Description = "chatgpt, lmstudio (Responses), or local (Chat Completions).", Recursive = true, DefaultValueFactory = _ => ModelProvider.ChatGpt };
        var endpoint = new Option<string?>("--endpoint") { Description = "Loopback HTTP endpoint; local defaults to http://127.0.0.1:8080/v1, LM Studio to port 1234.", Recursive = true };
        var outputBudget = new Option<int>("--max-output-tokens") { Recursive = true, DefaultValueFactory = _ => 0, Description = "Output reserve; default ChatGPT 16384, local 8192." };
        root.Options.Add(provider); root.Options.Add(endpoint); root.Options.Add(outputBudget);
        WorkerCommands(root, dataOption, jsonOption, noColor, provider, endpoint, outputBudget);
        ModelCommands(root, dataOption, jsonOption, noColor, provider, endpoint, outputBudget);
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
        { await app.ShowModels(p.GetValue(provider), ResolveEndpoint(p.GetValue(provider), p.GetValue(endpoint)), ct); return 0; }));
        foreach (var name in new[] { "chat", "run" })
        {
            var command = new Command(name, name == "chat" ? "Start interactive project chat." : "Execute one request.");
            var mode = new Option<PermissionMode>("--mode") { Description = "Inspect, Edit, or Execute. Execute runs as your Windows account, not sandboxed.", DefaultValueFactory = _ => PermissionMode.Execute };
            var workerNames = new Option<string?>("--workers") { Description = "Comma-separated enabled worker profiles." };
            var maxWorkers = new Option<int>("--max-workers") { DefaultValueFactory = _ => 6, Description = "Maximum active workers, excluding parent (1..16; default 6)." };
            var maxLocalWorkers = new Option<int>("--max-local-workers") { DefaultValueFactory = _ => 1, Description = "Maximum local workers within the total limit (1..16; default 1)." };
            command.Options.Add(maxWorkers); command.Options.Add(maxLocalWorkers);
            var workerRequests = new Option<int>("--worker-max-requests") { DefaultValueFactory = _ => 100, Description = "Model requests per worker, including compaction (1..10000)." };
            var workerMinutes = new Option<int>("--worker-max-minutes") { DefaultValueFactory = _ => 30, Description = "Minutes per worker, bounded by the parent deadline (1..1440)." };
            command.Options.Add(workerRequests); command.Options.Add(workerMinutes);
            var contextBudget = new Option<string>("--context-budget") { DefaultValueFactory = _ => "balanced" };
            var maxSteps = new Option<int>("--max-steps") { DefaultValueFactory = _ => 150 };
            var maxMinutes = new Option<int>("--max-minutes") { DefaultValueFactory = _ => 60 };
            var delegation = new Option<string>("--delegation") { DefaultValueFactory = _ => "proactive" };
            var images = new Option<string[]>("--image") { Description = "Attach an image file; repeat up to four times.", AllowMultipleArgumentsPerToken = true };
            command.Options.Add(contextBudget); command.Options.Add(maxSteps); command.Options.Add(maxMinutes); command.Options.Add(delegation); command.Options.Add(images);
            command.Options.Add(workerNames);
            var workflow = new Option<string?>("--workflow") { Description = "work, plan, document, review or memorize. Memorize remains source-read-only until explicitly left." };
            command.Options.Add(workflow);
            var plan = new Option<bool>("--plan") { Description = "Investigate and prepare a plan; implementation requires an explicit review decision." };
            var workspace = new Option<string?>("--workspace") { Description = "Project directory; defaults to the current directory." };
            var model = new Option<string?>("--model") { Description = "Model ID; omitted uses the saved chat default or latest parent session." };
            var prompt = new Option<string?>("--prompt") { Required = name == "run", Description = "User request." };
            command.Options.Add(workspace); command.Options.Add(model); command.Options.Add(prompt); command.Options.Add(mode); command.Options.Add(plan); root.Subcommands.Add(command);
            command.SetAction(async (p, ct) => await Execute(p.GetValue(dataOption), p.GetValue(jsonOption), p.GetValue(noColor), async app =>
            {
                var path = WorkspaceTools.ValidateRoot(p.GetValue(workspace) ?? Environment.CurrentDirectory);
                var selection = app.ResolveChatModel(p.GetValue(model), p.GetResult(provider) is { Implicit: false } ? p.GetValue(provider) : null, p.GetValue(endpoint), p.GetValue(outputBudget));
                var selected = selection.Model;
                var session = new Session { Workspace = path, Model = selected, Mode = p.GetValue(mode), Workflow = CliApp.ParseWorkflow(p.GetValue(workflow), p.GetValue(plan)),
                    AccountId = selection.AccountId, Connection = selection.Connection() };
                session.EnabledWorkers = app.ResolveWorkers(p.GetValue(workerNames));
                session.WorkerPolicy = new() { MaxConcurrent = p.GetValue(maxWorkers), MaxLocalConcurrent = p.GetValue(maxLocalWorkers), MaxRequests = p.GetValue(workerRequests), MaxMinutes = p.GetValue(workerMinutes) }; session.WorkerPolicy.Validate();
                session.Execution = new() { MaxRequests = p.GetValue(maxSteps), MaxMinutes = p.GetValue(maxMinutes) }; session.Execution.Validate();
                session.ContextPolicy.Budget = p.GetValue(contextBudget)!; session.ContextPolicy.Validate();
                session.Delegation = p.GetValue(delegation)!;
                if (session.Delegation is not ("proactive" or "manual")) throw new UserFacingException("Delegation must be proactive or manual.");
                await app.ConfigureModel(session, ct);
                session.ContextPolicy.WorkingTokens(session.Connection);
                foreach (var profile in session.EnabledWorkers) await app.RefreshWorker(profile, ct);
                app.Acquire(session.Id);
                app.Store.Save(session, new("conversation.created", session.Id, null, new { workspace = path, model = selected }));
                app.SetConversation(session);
                foreach (var image in p.GetValue(images) ?? []) app.AttachFile(session, image);
                if (name == "chat") app.Store.SaveChatDefault(ChatModelSelection.From(session));
                app.Write("conversation", app.Header(session));
                return await app.Chat(session, p.GetValue(prompt), name == "chat", ct);
            }));
        }
        var resume = new Command("resume", "Open the most recently updated session, or a specific saved conversation.");
        var conversationArgument = new Argument<string?>("conversation-id") { Arity = ArgumentArity.ZeroOrOne, Description = "Saved conversation ID; omitted selects the latest parent session in the data directory." }; resume.Arguments.Add(conversationArgument); root.Subcommands.Add(resume);
        resume.SetAction(async (p, ct) => await Execute(p.GetValue(dataOption), p.GetValue(jsonOption), p.GetValue(noColor), async app =>
        {
            var id = p.GetValue(conversationArgument) ?? app.Store.List().FirstOrDefault(s => s.ParentConversationId is null)?.Id
                ?? throw new UserFacingException("No saved sessions found in this data directory. Start one with smith chat.");
            app.Acquire(id);
            var session = app.Store.Get(id) ?? throw new UserFacingException("Conversation not found.");
            WorkspaceTools.ValidateRoot(session.Workspace);
            if (session.ParentConversationId is not null) throw new UserFacingException("Resume the parent conversation; child sessions are inspectable only.");
            if (session.Connection.Provider == ModelProvider.ChatGpt) app.Vault.Active(session.AccountId);
            try
            {
                using var discovery = CancellationTokenSource.CreateLinkedTokenSource(ct); discovery.CancelAfter(TimeSpan.FromSeconds(5));
                await app.ConfigureModel(session, discovery.Token);
                foreach (var profile in session.EnabledWorkers) await app.RefreshWorker(profile, discovery.Token);
            }
            catch (Exception ex) when (ex is UserFacingException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            { ct.ThrowIfCancellationRequested(); app.Write("model.warning", new { message = "Model metadata refresh unavailable. Saved capabilities retained; local session inspection remains available." }); }
            app.Agents.RecoverChildren(session);
            app.PrepareResume(session);
            app.Registry.RecoverSession(app.Context(session));
            app.Write("conversation.resumed", app.Header(session, true));
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
                    var input = await app.ReadResumeDecision(session, ct);
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
        { app.Write("sessions", app.Store.List().Where(s => s.ParentConversationId is null).Select(s => new { s.Id, s.Workspace, s.Model, s.UpdatedAt, state = s.Runs.LastOrDefault()?.State.ToString() })); return Task.FromResult(0); }).GetAwaiter().GetResult());
        var usage = new Command("usage", "Show recorded cloud/local token usage for a conversation and its workers.");
        var usageConversation = new Argument<string>("conversation-id"); usage.Arguments.Add(usageConversation); root.Subcommands.Add(usage);
        usage.SetAction(p => Execute(p.GetValue(dataOption), p.GetValue(jsonOption), p.GetValue(noColor), app =>
        { app.Write("usage", app.Store.Usage(p.GetValue(usageConversation)!)); return Task.FromResult(0); }).GetAwaiter().GetResult());
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
        void Error(string message, FailureDetail? failure = null)
        {
            failure = failure?.Sanitized();
            if (failure is not null) message = failure.Reason;
            try
            {
                if (app is not null) app.Write("error", new { message, failure });
                else if (json) Console.WriteLine(Json.Serialize(new { schemaVersion = 1, type = "error", payload = new { message, failure } }));
                else Console.Error.WriteLine(failure is null ? message : $"{failure.Category}: {failure.Reason}\nEffect: {failure.Effect}\nNext: {failure.Next}\nCode: {failure.Code}");
            }
            catch (IOException) { }
        }
        try { app = new CliApp(directory, json, noColor); return await action(app); }
        catch (OperationCanceledException) { Error("Cancelled."); return 130; }
        catch (UserFacingException ex) { Error(ex.Message, ex.Detail); return 2; }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        { var failure = Failures.Storage(ex.SqliteErrorCode); Error(failure.Reason, failure); return 1; }
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
    private readonly InputAttention attention;
    private long sequence;
    private CancellationTokenSource? activeRun;
    private readonly ConsoleCancelEventHandler cancelHandler;
    public CredentialVault Vault { get; }
    public ChatGptAuth Auth { get; }
    public SessionStore Store { get; }
    public ToolRegistry Registry { get; }
    private readonly List<IDisposable> leases = [];
    public void Acquire(string id) => leases.Add(Store.Acquire(id));
    public ToolExecutionContext Context(Session session) => new(session, session.Runs.LastOrDefault() ?? new Run(), Store, CancellationToken.None);
    public CliApp(string? directory, bool json, bool noColor, TextReader? reader = null)
    {
        this.json = json;
        color = !json && !noColor && !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
        var width = Console.IsOutputRedirected ? 90 : Console.WindowWidth;
        terminal = new TerminalRenderer(Console.Out, color, width);
        input = new ConsoleInput(!json && !Console.IsInputRedirected && !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("TERM") != "dumb", reader, color);
        attention = InputAttention.Create(!json && reader is null && !Console.IsInputRedirected && !Console.IsOutputRedirected);
        var root = Path.GetFullPath(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentSmith"));
        Vault = new(root, new WindowsSecretProtector());
        Auth = new(http, Vault, new IdentityValidator()); Store = new(root);
        Registry = new(Store);
        Agents = new(Store, Store, Registry, ModelClient);
        cancelHandler = (_, e) => { if (activeRun is not null) { e.Cancel = true; CancelActive(); } };
        Console.CancelKeyPress += cancelHandler;
    }
    public void Write(string type, object payload, string? conversationId = null, string? runId = null)
    {
        input.Render(() => {
        if (json) Console.WriteLine(Json.Serialize(new { schemaVersion = 1, id = Guid.NewGuid().ToString("N"), sequence = ++sequence, timestamp = DateTimeOffset.UtcNow, type, conversationId, runId, payload }));
        else if (type != "question.pending" || !interactiveQuestions) { if (input.Interactive) terminal.Resize(Console.WindowWidth); terminal.Render(type, payload); }
        });
    }
    public async Task<string?> ReadInput(CancellationToken ct, string attentionKey = "composer")
    {
        if (addressedDecisions.TryDequeue(out var decision)) return decision;
        ct.ThrowIfCancellationRequested();
        attention.WaitFor(attentionKey);
        var editing = input.EditingEnabled;
        input.EditingEnabled = attentionKey == "composer" && !pasteActive;
        try { return await input.ReadLineAsync(ct); }
        finally { input.EditingEnabled = editing; }
    }
    public async Task<Run> Run(Session session, string? prompt, string? answer, CancellationToken ct, bool recordHistory = true, bool literal = false)
    {
        if (prompt is null && answer is null && session.Runs.LastOrDefault() is { State: RunState.Paused } paused
            && new ContextBuilder(Store, ModelClient(session), Agents) { Limit = ContextLimit }.RequiresExplicitRetry(session))
        {
            Write("error", new { message = "Compaction needs an explicit retry. Run /compact, then type continue after it succeeds. /context shows the saved context. No work was restarted; queued prompts remain held." });
            return paused;
        }
        if (Store.Queue(session.Id).HoldReason is "AwaitingUser" or "AwaitingPlan") Store.HoldQueue(session.Id, null);
        var rich = nextPrompt; nextPrompt = null;
        input.SetHistory(session);
        var historyPrompt = prompt is null || answer is not null ? null : rich ?? UserPrompt.Text(prompt);
        var accepted = false;
        Run run;
        try
        {
            run = await Pump(session, token =>
            {
                var client = ModelClient(session);
                return new AgentRunner(Store, client, Agents) { MaxContextCharacters = ContextLimit }.RunAsync(session, prompt, answer, e =>
                {
                    if (e.Type == "run.started" && !accepted && historyPrompt is not null)
                    { accepted = true; if (recordHistory) input.AcceptPrompt(historyPrompt, literal); }
                    Write(e.Type, e.Payload, e.ConversationId, e.RunId);
                }, token, rich);
            }, ct);
        }
        catch
        {
            if (!accepted && recordHistory && historyPrompt is not null)
            { if (rich is not null || literal) input.RestorePrompt(historyPrompt); else input.Restore(prompt!); }
            throw;
        }
        Store.SettleQueue(session.Id, run);
        if (run.State != RunState.Completed)
        {
            var reason = run.State is RunState.AwaitingUser or RunState.AwaitingPlan ? Store.Queue(session.Id).HoldReason ?? run.State.ToString() : run.State.ToString();
            Store.HoldQueue(session.Id, reason);
            Write("queue.held", new { reason, message = "Queue held: " + reason });
        }
        return run;
    }
    public async Task<bool> Answer(Session session, CancellationToken ct)
    {
        while (session.Runs.Last().State == RunState.AwaitingUser)
        {
            var question = session.Runs.Last().Question!;
            interactiveQuestions = !json && QuestionPrompt.IsInteractive;
            Write("question.pending", new { question.Id, question.Text, question.Choices });
            ct.ThrowIfCancellationRequested();
            attention.WaitFor("question:" + question.Id);
            var answer = interactiveQuestions ? await QuestionPrompt.ReadAsync(question, color, ct, input.ReadKeyAsync, PasteNote) : await ReadInput(ct, "question:" + question.Id);
            if (answer is null) return false;
            if (string.IsNullOrWhiteSpace(answer)) continue;
            if (!input.Interactive && (decisionRequiresAddress || Store.Queue(session.Id).Items.Count > 0))
            {
                var prefix = "/answer " + question.Id + " ";
                if (answer.StartsWith(prefix, StringComparison.Ordinal)) answer = answer[prefix.Length..];
                else { if (answer.StartsWith('/')) await HandleCommand(answer, session, answering: true); else Write("error", new { message = "Use /answer " + question.Id + " <text>; queued input cannot answer this question." }); continue; }
            }
            var command = interactiveQuestions ? CommandAction.Prompt : await HandleCommand(answer, session, answering: true);
            if (command == CommandAction.Exit) return false;
            if (command != CommandAction.Prompt) continue;
            await Run(session, null, answer, ct);
        }
        return true;
    }
    public async Task<int> Chat(Session session, string? initial, bool interactive, CancellationToken ct)
    {
        SetConversation(session);
        if (initial is not null && input.HasBlocks) { nextPrompt = input.Compose(initial); input.ClearDraft(); }
        continuous = interactive;
        currentConversation = session.Id;
        if (Store.Queue(session.Id).Items.Any(i => i.State is "Pending" or "Dispatched") && !queueInitialized)
        { Store.HoldQueue(session.Id, "Restart: inspect /queue and continue explicitly"); Write("queue.held", new { message = "Saved queue held after restart. Use /queue to inspect it." }); }
        queueInitialized = true;
        interactiveQuestions = interactive && !json && QuestionPrompt.IsInteractive;
        var prompt = initial;
        while (true)
        {
            if (exitRequested) return 0;
            input.SetContext(session);
            var workflowPrompt = false;
            if (Planning.Active(session)?.PendingReview is not null)
            {
                if (!interactive || !await ReviewPlan(session, ct)) return 3;
            }
            if (session.PendingPlanPrompt is not null && queuedPrompt is null)
            {
                Write("resume.confirmation", new { message = "A saved plan decision is ready to continue. Type continue explicitly, or leave it pending." });
                if (!interactive) return 3;
                var continuation = await ReadInput(ct, "plan-continuation:" + session.ActivePlanId);
                if (continuation?.TrimStart().StartsWith('/') == true)
                { if (await HandleCommand(continuation, session) == CommandAction.Exit) return 3; continue; }
                if (continuation != "continue") return 3;
                queuedPrompt = session.PendingPlanPrompt;
            }
            if (queuedPrompt is not null) { prompt = queuedPrompt; queuedPrompt = null; workflowPrompt = true; }
            var claimed = false;
            if (prompt is null && interactive && !pasteActive && Store.ClaimQueued(session) is { } queuedRun)
            {
                claimed = true;
                Write("prompt.dispatched", new { runId = queuedRun.Id, message = "Starting queued prompt · " + queuedRun.Id });
            }
            if (prompt is null && !claimed)
            {
                if (!interactive) return 0;
                prompt = await ReadInput(ct);
            }
            if (prompt is null && !claimed) { Store.HoldQueue(session.Id, "End of input"); return 0; }
            if (pasteActive && !claimed)
            { await BusyInput(session, prompt!); prompt = null; continue; }
            if (!input.Interactive && !claimed && input.HasBlocks && prompt?.TrimStart().StartsWith('/') != true)
            { nextPrompt = input.Compose(prompt); input.ClearDraft(); }
            var literalPaste = input.SubmittedLiteral;
            if (!claimed && input.SubmittedPrompt is { } submitted) { nextPrompt = submitted; input.ClearSubmission(); }
            if (nextPrompt?.Parts.Any(p => p.Kind == "image") == true && string.IsNullOrWhiteSpace(prompt)) prompt = "[Image attached]";
            var command = claimed || literalPaste || nextPrompt?.Parts.Any(p => p.Kind == "image") == true ? CommandAction.Prompt : await HandleCommand(prompt!, session);
            if (command == CommandAction.Exit) return 0;
            if (command == CommandAction.Handled) { prompt = null; nextPrompt = null; continue; }
            if (command == CommandAction.Paste)
            {
                Write("paste", new { message = "Enter lines; finish with /end on its own line. Slash-prefixed text here is sent literally." });
                var lines = new List<string>();
                string? line;
                while ((line = await ReadInput(ct, "paste")) is not null && line != "/end") lines.Add(line);
                if (line is null) return 0; // EOF does not submit an unfinished paste.
                prompt = string.Join('\n', lines); nextPrompt = UserPrompt.Text(prompt); literalPaste = true; input.ClearSubmission();
            }
            if (!claimed && string.IsNullOrWhiteSpace(prompt)) { prompt = null; continue; }
            var continuing = !claimed && !literalPaste && prompt == "continue" && session.Runs.LastOrDefault()?.State == RunState.Paused;
            if (continuing && !literalPaste) { claimed = true; prompt = null; nextPrompt = null; }
            if (!claimed && !workflowPrompt && Store.Queue(session.Id).Items.Any(i => i.State == "Pending"))
            { QueueMessage(session, prompt!, nextPrompt, literalPaste); nextPrompt = null; prompt = null; continue; }
            if (!claimed && Unfinished(session))
            { if (nextPrompt is not null) input.RestorePrompt(nextPrompt); else input.Restore(prompt!); nextPrompt = null; Write("error", new { message = "Resume or explicitly abandon unfinished work before a new prompt. Use /recover." }); prompt = null; continue; }
            if (!continuing)
            {
                var submittedContent = claimed ? session.History.Last(i => i["role"]?.ToString() == "user")["content"]
                    : nextPrompt?.HistoryItem()["content"] ?? System.Text.Json.Nodes.JsonValue.Create(prompt);
                Write("user.submitted", new { workflow = session.Workflow.ToString(), text = prompt,
                    content = PromptPresentation.Content(submittedContent, Store.Attachments(session.Id)) });
            }
            var run = await Run(session, prompt, null, ct, recordHistory: !claimed && !workflowPrompt, literal: literalPaste); prompt = null;
            if (run.State == RunState.AwaitingUser)
            {
                if (!interactive || !await Answer(session, ct)) return 3;
                run = session.Runs.Last();
            }
            var code = run.State switch { RunState.Completed => 0, RunState.Cancelled => 130, RunState.Paused => 4, RunState.RecoveryRequired => 5, RunState.AwaitingUser or RunState.AwaitingPlan => 3, _ => 1 };
            if (!interactive) return code;
            if (run.State == RunState.Paused) Write("resume.confirmation", new { message = PauseGuidance.For(run) });
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
        void Error(string message, FailureDetail? failure = null) => Write("error", new { message = failure?.Sanitized().Reason ?? message, failure = failure?.Sanitized() });
        try { if (await ModelCommand(name, args, session) || await WorkflowCommand(name, args, session) || await ContinuityCommand(name, args, session)) return CommandAction.Handled; }
        catch (UserFacingException ex) { Error(ex.Message, ex.Detail); return CommandAction.Handled; }
        if (name is not ("/work" or "/help" or "/usage" or "/status" or "/context" or "/paste" or "/exit" or "/markdown" or "/tools" or "/mode" or "/changes" or "/undo" or "/processes" or "/stop" or "/recover" or "/plan" or "/plans" or "/tasks" or "/artifacts" or "/artifact"))
        { Error($"Unknown command: {TerminalRenderer.Safe(parts[0])[..Math.Min(80, TerminalRenderer.Safe(parts[0]).Length)]}. Type /help for available commands. Use /paste for literal slash-prefixed text."); return CommandAction.Handled; }
        if (name is not ("/help" or "/markdown" or "/mode" or "/undo" or "/stop" or "/recover" or "/plan" or "/plans" or "/artifact") && args.Length > 0)
        { Error($"{name} does not accept arguments. Type /help for usage."); return CommandAction.Handled; }
        try { switch (name)
        {
            case "/work": await WorkflowModeCommand(session, "work"); break;
            case "/usage": Write("usage", Store.Usage(session.Id)); break;
            case "/plan": await PlanCommand(session, args); break;
            case "/plans":
                var planPage = 1;
                if (args.Length > 0 && !int.TryParse(args, out planPage)) throw new UserFacingException("Usage: /plans [page]. Page starts at 1.");
                Write("plan.list", Store.ListPlans(session.Workspace, planPage)); break;
            case "/tasks": Write("plan.display", new { plan = Planning.Active(session) }); break;
            case "/artifacts": Write("artifact.result", new { content = ArtifactAccess.Execute(Context(session), "artifact_list", new System.Text.Json.Nodes.JsonObject()).Content }); break;
            case "/artifact":
                var artifactParts = args.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (artifactParts.Length == 0 || artifactParts.Length > 1 && (artifactParts.Length != 3 || artifactParts[1] is not ("search" or "read"))) throw new UserFacingException("Usage: /artifact <id|process:#number> [read <cursor>|search <text>]");
                var artifactId = ArtifactCommands.ResolveId(Context(session), artifactParts[0]);
                var artifactCursor = 0;
                if (artifactParts.Length == 3 && artifactParts[1] == "read" && (!int.TryParse(artifactParts[2], out artifactCursor) || artifactCursor < 0)) throw new UserFacingException("Artifact cursor must be a nonnegative integer.");
                var searchingArtifact = artifactParts.Length == 3 && artifactParts[1] == "search";
                var artifactArgs = new System.Text.Json.Nodes.JsonObject { ["artifactId"] = artifactId, ["cursor"] = artifactCursor, ["maxCharacters"] = 4000 };
                if (searchingArtifact) artifactArgs["query"] = artifactParts[2];
                Write("artifact.result", new { content = ArtifactAccess.Execute(Context(session), searchingArtifact ? "artifact_search" : "artifact_read", artifactArgs).Content }); break;
            case "/tools":
                Write("tools", Agents.Definitions.Where(t => t.Name != "agent_finish").Select(t => new { t.Name, t.Description, capability = t.Capability.ToString(), enabled = Agents.Available(session).Any(d => d.Name == t.Name), t.HasEffects, effect = t.Effect.ToString() })); break;
            case "/mode":
                if (await WorkflowModeCommand(session, args)) break;
                if (activeRun is not null) throw new UserFacingException("Wait for the active run or use /cancel before changing permissions.");
                if (args.Length > 0)
                {
                    if (!Enum.TryParse<PermissionMode>(args, true, out var next) || !Enum.IsDefined(next) || int.TryParse(args, out _)) { Error("Usage: /mode work|plan|document|review|memorize or /mode inspect|edit|execute"); break; }
                    if (next < session.Mode) await Agents.StopAllAsync(session.Id);
                    if (session.Mode == PermissionMode.Execute && next != PermissionMode.Execute) await Registry.Processes.StopAsync(session);
                    if (session.Workflow != WorkflowMode.Plan && Planning.Active(session) is { State: "Implementing" } approved && next > approved.Approvals.Last().Mode)
                    {
                        if (WorkflowPolicy.IsTask(session.Workflow)) throw new UserFacingException("The requested permission exceeds the approved plan ceiling. Finish or leave this workflow before requesting a new plan approval.");
                        await Registry.Processes.StopAsync(session); session.Workflow = WorkflowMode.Plan; approved.State = "Draft"; Planning.Submit(session);
                    }
                    session.Mode = next; Store.Save(session, new("mode.changed", session.Id, null, new { mode = next.ToString() }));
                }
                Write("mode", new { workflow = session.Workflow.ToString(), message = $"Workflow: {session.Workflow} · Permissions: {session.Mode}." + (session.Mode == PermissionMode.Execute ? " Processes run as your Windows account; working directories are not a sandbox." : "") }); break;
            case "/changes":
                Write("changes", new[] { session }.Concat(Agents.ChildSessions(session)).SelectMany(s => s.Mutations).Select(m => new { m.Id, m.Kind, m.State, m.UndoOf, diff = FileOperations.Diff(m) })); break;
            case "/undo":
                if (session.Workflow == WorkflowMode.Plan || WorkflowPolicy.IsSourceReadOnly(session.Workflow)) throw new UserFacingException("Undo is disabled during Plan, Review and Memorize workflows.");
                Write("operation", new { content = (await Agents.UndoAsync(session, args)).Content }); break;
            case "/processes":
                Write("processes", session.Processes); break;
            case "/stop":
                var stop = new ToolCall { Name = "process_stop", Id = "cli-" + Guid.NewGuid().ToString("N"), Arguments = Json.Serialize(new { processId = args }) };
                var stopContext = Context(session);
                stop.Prepared = Registry.Prepare(stopContext, stop); stopContext.Save("process.stop_requested", new { processId = args });
                Write("operation", new { content = (await Registry.ExecuteAsync(stopContext, stop)).Content }); break;
            case "/recover":
                Registry.RecoverSession(Context(session));
                Agents.RecoverChildren(session, args == "abandon");
                if (args == "abandon")
                {
                    foreach (var m in session.Mutations.Where(m => m.State == "Prepared")) m.State = "Abandoned";
                    if (session.Runs.LastOrDefault() is { State: not (RunState.Completed or RunState.Cancelled) }) AgentRunner.Abandon(session, Store);
                    else Store.Save(session, new("recovery.abandoned", session.Id, null, new { }));
                    if (session.Runs.LastOrDefault() is { } abandoned) Store.SettleQueue(session.Id, abandoned);
                    Write("recovery", new { message = "Uncertain operations acknowledged and abandoned. No effects were replayed or undone." });
                }
                else if (args.Length > 0) Error("Usage: /recover [abandon]");
                else Write("recovery", new { message = Json.Serialize(new { workers = Store.Workers(session.Id).Where(w => w.ReservationsHeld || w.State == "Interrupted").Select(WorkerCoordinator.Summary), unresolvedCalls = session.Runs.SelectMany(r => r.Calls).Where(c => c.State == CallState.Started).Select(c => new { c.Id, c.Name }), mutations = session.Mutations.Where(m => m.State == "Prepared").Select(m => new { m.Id, m.Kind }), interruptedProcesses = session.Processes.Where(p => p.State == "Interrupted").Select(p => new { p.Id, p.Reason }) }) });
                break;
            case "/exit": return CommandAction.Exit;
            case "/help":
                Write("help", CommandHelp.Build(args)); break;
            case "/status":
            case "/context":
                var currentContext = activeRun is null ? new ContextBuilder(Store, ModelClient(session), Agents) { Limit = ContextLimit }.Build(session).Manifest : session.Runs.LastOrDefault()?.ContextManifest;
                Write("status", new { webSearch = session.WebSearch == "native" && session.Connection.Provider != ModelProvider.ChatGpt ? "basic (local provider)" : session.WebSearch, pendingSteering = Store.Steering(session.Id).Count(i => i.State == "Pending"), session.Id, session.Execution, session.Delegation, session.WorkerPolicy, remainingMinutes = Math.Max(0, session.Execution.MaxMinutes - (session.Runs.LastOrDefault() is { } currentRun ? (currentRun.State == RunState.Running && currentRun.BatchStartedAt is { } batchStart ? (DateTimeOffset.UtcNow - batchStart).TotalMinutes : currentRun.BatchExecutionMilliseconds / 60000) : 0)), remainingRequests = Math.Max(0, session.Execution.MaxRequests - (session.Runs.LastOrDefault()?.BatchRequests ?? 0)), provider = session.Connection.Provider.ToString(), session.Model, session.Connection.LoadedContextTokens, workers = Store.Workers(session.Id).Select(WorkerCoordinator.Summary), workflow = session.Workflow.ToString(), report = WorkflowPolicy.Active(session), mode = session.Mode.ToString(), state = session.Runs.LastOrDefault()?.State.ToString(), reason = session.Runs.LastOrDefault()?.Reason, completionRequirement = session.Runs.LastOrDefault()?.CompletionRequirement, context = currentContext, snapshot = ContextBuilder.Active(session) }); break;
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
        catch (Exception ex) when (ex is UserFacingException or IOException or UnauthorizedAccessException) { Error(ex is UserFacingException ? ex.Message : "Operation could not complete. Use /recover to inspect its state.", (ex as UserFacingException)?.Detail); }
        return CommandAction.Handled;
    }
    public void Dispose()
    {
        attention.Dispose();
        input.Hide();
        if (currentConversation is not null) Store.HoldQueue(currentConversation, "CLI exited");
        try { Agents.Dispose(); }
        finally { try { Registry.Dispose(); } finally { foreach (var lease in leases) lease.Dispose(); Console.CancelKeyPress -= cancelHandler; http.Dispose(); tokenRefresh.Dispose(); } }
    }
}
