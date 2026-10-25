using AgentSmith.Core;
using AgentSmith.OpenAI;
using AgentSmith.Runtime;
using AgentSmith.Tools;

namespace AgentSmith.Cli;

internal sealed partial class CliApp
{
    internal IModelClient? ModelOverride { get; set; }
    internal int ContextLimit { get; set; } = 120000;
    private ConsoleInput input = null!;
    private bool continuous;
    private bool decisionRequiresAddress;
    private bool queueInitialized;
    public void PrepareResume(Session session)
    {
        SetConversation(session); continuous = true; queueInitialized = true;
        Store.HoldQueue(session.Id, Store.Queue(session.Id).Items.Any(i => i.State is "Pending" or "Dispatched") ? "Restart: inspect /queue and continue explicitly" : null);
        foreach (var run in session.Runs.Where(r => r.State is RunState.Completed or RunState.Cancelled or RunState.Failed)) Store.SettleQueue(session.Id, run);
    }
    private readonly Queue<string> addressedDecisions = new();
    private bool exitRequested;
    private bool pasteActive;
    private readonly List<string> pasteLines = [];
    private string? currentConversation;
    private void CancelActive()
    {
        var source = activeRun;
        if (source is null || source.IsCancellationRequested) return;
        // Persist the hold before cancellation can expose a terminal run to observers.
        // Cancellation must still reach the runner if storing the hold fails.
        try { if (currentConversation is not null) Store.HoldQueue(currentConversation, "Cancelled"); }
        finally { source.Cancel(); }
        Write("run.cancelling", new { message = "Cancelling… Waiting for cleanup and saved outcomes." });
    }
    private void QueueMessage(Session session, string text, UserPrompt? rich = null, bool literal = false)
    {
        try
        {
            input.SetHistory(session);
            var item = rich is null ? Store.Enqueue(session.Id, text) : Store.Enqueue(session.Id, rich);
            input.AcceptPrompt(rich ?? UserPrompt.Text(text), literal);
            var position = Store.Queue(session.Id).Items.Count(i => i.State == "Pending");
            var images = rich?.Parts.Count(p => p.Kind == "image") ?? 0;
            Write("prompt.queued", new { item.Id, position, imageCount = images,
                content = images > 0 ? PromptPresentation.Content(rich!.HistoryItem()["content"], Store.Attachments(session.Id)) : null,
                message = $"Queued #{position} · {item.Id}" + (images > 0 ? $" · {images} image(s) attached" : "") + $" · {Activity.Clean(text, 100)}" });
        }
        catch (UserFacingException ex) { if (rich is null && !literal) input.Restore(text); else input.RestorePrompt(rich ?? UserPrompt.Text(text)); Write("error", new { message = ex.Message, failure = ex.Detail?.Sanitized() }); }
    }
    private async Task BusyInput(Session session, string line)
    {
        var literal = input.SubmittedLiteral;
        var rich = TakeSubmittedPrompt();
        if (pasteActive)
        {
            if (line == "/end") { QueueMessage(session, string.Join('\n', pasteLines), literal: true); pasteLines.Clear(); pasteActive = false; input.EditingEnabled = true; }
            else pasteLines.Add(line);
            return;
        }
        if (literal || rich?.Parts.Any(p => p.Kind == "image") == true) { QueueMessage(session, line, rich, literal); return; }
        if (line.Trim() == "/paste") { pasteActive = true; input.EditingEnabled = false; Write("paste", new { message = "Enter lines; /end submits a queued prompt." }); return; }
        if (!line.TrimStart().StartsWith('/')) { QueueMessage(session, line, rich); return; }
        var name = line.Trim().Split(' ', 2)[0].ToLowerInvariant();
        if (name is "/answer" or "/review")
        {
            var saved = Store.Get(session.Id);
            var valid = name == "/answer" && saved?.Runs.LastOrDefault() is { State: RunState.AwaitingUser, Question: { } q } && line.StartsWith("/answer " + q.Id + " ", StringComparison.Ordinal)
                || name == "/review" && saved is not null && Planning.Active(saved)?.PendingReview is { } review && line.StartsWith("/review " + review.Id + " ", StringComparison.Ordinal);
            if (valid) { addressedDecisions.Enqueue(line); return; }
        }

        if (name == "/cancel") { CancelActive(); return; }
        if (name == "/exit") { await RequestExit(session); return; }
        if (name is "/audio" or "/web-search" or "/model" or "/models" or "/steer" or "/steering" or "/mode" or "/workflow" or "/report" or "/reports" or "/attach" or "/limits" or "/worker-limits" or "/worker-budget" or "/delegation" or "/memory" or "/agents" or "/agent" or "/queue" or "/display" or "/details" or "/help" or "/usage" or "/status" or "/context" or "/tools" or "/diagnostics" or "/tasks" or "/plans" or "/plan" or "/processes" or "/changes")
        {
            // Queue storage is independent; other busy reads use a committed snapshot, never the runner's mutable session.
            await HandleCommand(line, Store.Get(session.Id) ?? throw new UserFacingException("Conversation is unavailable.")); return;
        }
        Write("error", new { message = "Command unavailable while running (or unknown). Use /cancel, /queue, or wait for completion. Nothing was sent to the model." });
    }
    private async Task<T> Pump<T>(Session session, Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        attention.Working();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(ct); activeRun = source;
        if (continuous && !input.Interactive) decisionRequiresAddress = true;
        var task = Task.Run(() => work(source.Token), CancellationToken.None);
        input.SetContext(session);
        input.Status = "Running"; if (continuous) input.Show();
        var elapsed = System.Diagnostics.Stopwatch.StartNew(); var lastSecond = -1;
        try
        {
            while (!task.IsCompleted)
            {
                var second = (int)elapsed.Elapsed.TotalSeconds;
                if (continuous && second != lastSecond)
                {
                    lastSecond = second;
                    input.SetContext(session);
                    var pendingSteering = Store.Steering(session.Id).Count(i => i.State == "Pending");
                    var workerSummary = ComposerContext.WorkerSummary(Store.Workers(session.Id));
                    input.Status = (source.IsCancellationRequested ? "Cancelling" : terminal.RunningStatus) + " · " + workerSummary + $" · run {TerminalDuration.Format(TimeSpan.FromSeconds(second), "0")}" + (pendingSteering > 0 ? $" · {pendingSteering} steering pending" : ""); input.Refresh();
                }
                if (continuous && !exitRequested)
                {
                    var next = await input.PollAsync(CancellationToken.None);
                    if (next.Cancel) CancelActive();
                    if (next.Ready)
                    {
                        if (next.Text is null) { exitRequested = true; Store.HoldQueue(session.Id, "End of input"); CancelActive(); }
                        else await BusyInput(session, next.Text);
                    }
                }
                else if (input.Interactive && !continuous && Console.KeyAvailable)
                {
                    var key = await input.ReadKeyAsync(CancellationToken.None); if (key.Key == ConsoleKey.Escape) CancelActive();
                }
                await Task.WhenAny(task, Task.Delay(25));
            }
            // Keys typed into the running composer belong to follow-ups, never the next picker.
            while (continuous && input.Interactive && Console.KeyAvailable && !exitRequested)
            {
                var buffered = await input.PollAsync(CancellationToken.None);
                if (buffered.Cancel) CancelActive();
                if (buffered.Ready && buffered.Text is not null) await BusyInput(session, buffered.Text);
            }
            return await task;
        }
        catch
        {
            source.Cancel();
            try { await task; } catch { /* Preserve the initiating failure after owned work has stopped. */ }
            Store.HoldQueue(session.Id, "Execution interrupted"); throw;
        }
        finally { activeRun = null; ApplyCaptureSelection(session); input.Hide(); input.Status = ""; input.SetContext(session); }
    }
    private async Task<bool> ContinuityCommand(string name, string args, Session session)
    {
        if (SteeringCommand(name, args, session)) return true;
        if (name == "/attach") { if (input.ResumeConfirmation) throw new UserFacingException("Continue the saved work first, then attach an image in the chat composer."); if (args.Length == 0) throw new UserFacingException("Usage: /attach <image path>"); AttachFile(session, args); return true; }
        if (PolicyCommand(name, args, session)) return true;
        if (name == "/audio") { if (await SimpleRecordingCommand(Program.CaptureWords(args), session)) return true; if (args.Trim() == "status") AudioStatus(); else if (args.StartsWith("analyze ", StringComparison.Ordinal) || args.StartsWith("analysis ", StringComparison.Ordinal)) await AnalysisCommand(Program.CaptureWords(args), session, CancellationToken.None); else await CaptureCommand(args, session); return true; }
        if (name == "/memory") { MemoryCommand(args, session); return true; }
        if (await AgentCommand(name, args, session)) return true;
        switch (name)
        {
            case "/diagnostics":
                var diagnosticArgs = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var page = 1;
                if (diagnosticArgs.Length is < 1 or > 3 || diagnosticArgs.Length == 3 && (!int.TryParse(diagnosticArgs[2], out page) || page is < 1 or > 100))
                    throw new UserFacingException("Usage: /diagnostics <report-id> [errors|warnings|failed|all] [page 1..100]");
                var request = new System.Text.Json.Nodes.JsonObject { ["artifactId"] = diagnosticArgs[0], ["filter"] = diagnosticArgs.Length > 1 ? diagnosticArgs[1] : "all", ["path"] = "", ["query"] = "", ["cursor"] = "" };
                System.Text.Json.Nodes.JsonNode resultPage = null!;
                for (var index = 1; index <= page; index++)
                {
                    resultPage = System.Text.Json.Nodes.JsonNode.Parse(Diagnostics.Read(Context(Store.Get(session.Id) ?? session), request).Content)!;
                    if (index < page && resultPage["cursor"] is null) throw new UserFacingException("That diagnostic page does not exist.");
                    request["cursor"] = resultPage["cursor"]?.DeepClone() ?? "";
                }
                resultPage["page"] = page;
                Write("diagnostics.result", resultPage); return true;
            case "/cancel":
                if (args.Length > 0) throw new UserFacingException("Usage: /cancel");
                CancelActive(); return true;
            case "/display":
                if (args.Length > 0 && args is not ("compact" or "verbose")) throw new UserFacingException("Usage: /display compact|verbose");
                if (args.Length > 0) terminal.Verbose = args == "verbose";
                Write("display", new { message = terminal.Verbose ? "Verbose display" : "Compact display" }); return true;
            case "/details":
                var id = args.StartsWith('#') ? Activity.ProcessId(session, args) : args;
                var call = session.Runs.SelectMany(r => r.Calls).Where(c => c.Id == id || c.Activity?.ProcessId == id || c.Prepared?.OperationId == id).OrderBy(c => c.Name == "process_start" || c.Name.StartsWith("dotnet_", StringComparison.Ordinal) ? 0 : 1).FirstOrDefault() ?? throw new UserFacingException("Unknown call or process alias.");
                var summary = call.Name.StartsWith("agent_", StringComparison.Ordinal)
                    ? Activity.Describe(session, call, Store.Workers(session.Id)) : call.Activity ?? Activity.Describe(session, call);
                var timing = call.DurationMilliseconds > 0 ? $"Call {TerminalDuration.Format(TimeSpan.FromMilliseconds(call.DurationMilliseconds), "0.000")}" : "Call time not recorded";
                var resultSummary = "";
                try
                {
                    if (System.Text.Json.Nodes.JsonNode.Parse(call.Result ?? "null") is System.Text.Json.Nodes.JsonObject result)
                        resultSummary = string.Join(" · ", new[] { "error", "state", "exitCode", "reason", "checkpointId", "artifactId", "truncated" }.Where(k => result[k] is not null).Select(k => k + ": " + Activity.Clean(result[k]!.ToString(), 250)));
                }
                catch (System.Text.Json.JsonException) { resultSummary = "Saved result is not valid JSON."; }
                Write("details", new { call.Id, summary, call.DurationMilliseconds, resultSummary, message = $"{call.Id}: {summary.Action} {summary.Target} · {summary.Options}\n{timing} · {call.State}\n{resultSummary}\nUse /artifacts for retained output; stdin and environment values are omitted." }); return true;
            case "/queue":
                var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    if (parts[0] == "remove" && parts.Length == 2)
                    { Store.RemoveQueued(session.Id, parts[1]); Write("prompt.removed", new { id = parts[1], message = "Removed queued prompt " + parts[1] }); }
                    else if (parts[0] == "clear" && parts.Length == 1)
                    {
                        var removed = Store.Queue(session.Id).Items.Where(i => i.State == "Pending").Select(i => i.Id).ToArray(); Store.RemoveQueued(session.Id, null);
                        foreach (var removedId in removed) Write("prompt.removed", new { id = removedId, message = "Removed queued prompt " + removedId });
                    }
                    else if (parts[0] == "continue" && parts.Length == 1)
                    {
                        if (activeRun is not null || Unfinished(session) || Planning.Active(session)?.PendingReview is not null || session.PendingPlanPrompt is not null) throw new UserFacingException("Resolve the active run or pending decision before continuing the queue.");
                        Store.HoldQueue(session.Id, null);
                    }
                    else throw new UserFacingException("Usage: /queue [remove <id>|clear|continue]");
                }
                var queue = Store.Queue(session.Id); var pending = queue.Items.Where(i => i.State == "Pending").ToArray();
                Write("queue.status", new { queue.HoldReason, pending = pending.Select(i => new { i.Id, i.Sequence, preview = Activity.Clean(i.Text, 100) }), message = (queue.HoldReason is null ? "Queue ready" : "Queue held: " + queue.HoldReason) + $" · {pending.Length} pending\n" + string.Join('\n', pending.Select(i => i.Id + " · " + Activity.Clean(i.Text, 100))) }); return true;
            case "/compact":
                if (args.Length > 0 || activeRun is not null || session.Runs.LastOrDefault()?.State is RunState.Running or RunState.AwaitingPlan or RunState.AwaitingUser or RunState.RecoveryRequired) throw new UserFacingException("Use /compact while idle or paused for context, with no pending decision.");
                if (session.Runs.LastOrDefault() is { State: RunState.Paused } paused && paused.PauseReason is not (PauseReason.ContextLimit or PauseReason.CompactionFailed) && !(paused.PauseReason == PauseReason.None && (paused.Reason?.Contains("context", StringComparison.OrdinalIgnoreCase) == true || paused.Reason?.Contains("compaction", StringComparison.OrdinalIgnoreCase) == true))) throw new UserFacingException("Resolve this pause before compacting.");
                var client = ModelClient(session);
                var compactTimer = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var compactRun = session.Runs.LastOrDefault() ?? new(); compactRun.BatchRequests = 0;
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(session.Execution.MaxMinutes));
                    var context = await Pump(session, token => new ContextBuilder(Store, client, Agents) { RequestBudget = session.Execution.MaxRequests }.PrepareAsync(session, compactRun, e => Write(e.Type, e.Payload), token, true), deadline.Token);
                    if (session.Runs.LastOrDefault() is { } last) last.ContextManifest = context.Manifest;
                    Store.Save(session, new("context.updated", session.Id, session.Runs.LastOrDefault()?.Id, new { context.Manifest }));
                }
                catch (ContextBudgetException ex) { Write("error", new { message = ex.Message }); }
                catch (RequestLimitException) { Write("error", new { message = "Compaction request budget reached; inspect /limits." }); }
                catch (OperationCanceledException) { Store.HoldQueue(session.Id, "Compaction cancelled"); }
                finally { if (session.Runs.LastOrDefault() is { } timedRun) { timedRun.ExecutionMilliseconds += compactTimer.Elapsed.TotalMilliseconds; Store.Save(session, new("context.compaction.timing", session.Id, timedRun.Id, new { milliseconds = compactTimer.Elapsed.TotalMilliseconds })); } }
                return true;
        }
        return false;
    }
}
