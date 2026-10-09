using AgentSmith.Core;
using AgentSmith.OpenAI;
using AgentSmith.Runtime;

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
        currentConversation = session.Id; continuous = true; queueInitialized = true;
        Store.HoldQueue(session.Id, Store.Queue(session.Id).Items.Any(i => i.State is "Pending" or "Dispatched") ? "Restart: inspect /queue and continue explicitly" : null);
        foreach (var run in session.Runs.Where(r => r.State is RunState.Completed or RunState.Cancelled or RunState.Failed)) Store.SettleQueue(session.Id, run);
    }
    private bool exitRequested;
    private bool pasteActive;
    private readonly List<string> pasteLines = [];
    private string? currentConversation;
    private void CancelActive()
    {
        var source = activeRun;
        if (source is null || source.IsCancellationRequested) return;
        source.Cancel();
        if (currentConversation is not null) Store.HoldQueue(currentConversation, "Cancelled");
        Write("run.cancelling", new { message = "Cancelling… Waiting for cleanup and saved outcomes." });
    }
    private void QueueMessage(Session session, string text)
    {
        try
        {
            var item = Store.Enqueue(session.Id, text);
            var position = Store.Queue(session.Id).Items.Count(i => i.State == "Pending");
            Write("prompt.queued", new { item.Id, position, message = $"Queued #{position} · {item.Id} · {Activity.Clean(text, 100)}" });
        }
        catch (UserFacingException ex) { input.Restore(text); Write("error", new { message = ex.Message }); }
    }
    private async Task BusyInput(Session session, string line)
    {
        if (pasteActive)
        {
            if (line == "/end") { QueueMessage(session, string.Join('\n', pasteLines)); pasteLines.Clear(); pasteActive = false; }
            else pasteLines.Add(line);
            return;
        }
        if (line.Trim() == "/paste") { pasteActive = true; Write("paste", new { message = "Enter lines; /end submits a queued prompt." }); return; }
        if (!line.TrimStart().StartsWith('/')) { QueueMessage(session, line); return; }
        var name = line.Trim().Split(' ', 2)[0].ToLowerInvariant();
        if (name == "/cancel") { CancelActive(); return; }
        if (name == "/exit") { exitRequested = true; CancelActive(); Store.HoldQueue(session.Id, "CLI exit"); return; }
        if (name is "/agents" or "/agent" or "/queue" or "/display" or "/details" or "/help" or "/status" or "/context" or "/tools" or "/tasks" or "/processes" or "/changes")
        {
            // Queue storage is independent; other busy reads use a committed snapshot, never the runner's mutable session.
            await HandleCommand(line, Store.Get(session.Id) ?? throw new UserFacingException("Conversation is unavailable.")); return;
        }
        Write("error", new { message = "Command unavailable while running (or unknown). Use /cancel, /queue, or wait for completion. Nothing was sent to the model." });
    }
    private async Task<T> Pump<T>(Session session, Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(ct); activeRun = source;
        if (continuous && !input.Interactive) decisionRequiresAddress = true;
        var task = Task.Run(() => work(source.Token), CancellationToken.None);
        input.Status = "Running"; if (continuous) input.Show();
        var elapsed = System.Diagnostics.Stopwatch.StartNew(); var lastSecond = -1;
        try
        {
            while (!task.IsCompleted)
            {
                var second = (int)elapsed.Elapsed.TotalSeconds;
                if (continuous && second != lastSecond) { lastSecond = second; input.Status = (source.IsCancellationRequested ? "Cancelling" : "Running") + $" · {second}s"; input.Refresh(); }
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
        finally { activeRun = null; input.Hide(); input.Status = ""; }
    }
    private async Task<bool> ContinuityCommand(string name, string args, Session session)
    {
        if (await AgentCommand(name, args, session)) return true;
        switch (name)
        {
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
                var summary = call.Activity ?? Activity.Describe(session, call);
                var timing = call.DurationMilliseconds > 0 ? $"Call {call.DurationMilliseconds / 1000:0.000}s" : "Call time not recorded";
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
                if (session.Runs.LastOrDefault() is { State: RunState.Paused } paused && !(paused.Reason?.Contains("context", StringComparison.OrdinalIgnoreCase) == true || paused.Reason?.Contains("compaction", StringComparison.OrdinalIgnoreCase) == true)) throw new UserFacingException("Resolve this pause before compacting.");
                var client = ModelClient(session);
                try
                {
                    var context = await Pump(session, token => new ContextBuilder(Store, client, Agents).PrepareAsync(session, session.Runs.LastOrDefault() ?? new(), e => Write(e.Type, e.Payload), token, true), CancellationToken.None);
                    if (session.Runs.LastOrDefault() is { } last) last.ContextManifest = context.Manifest;
                    Store.Save(session, new("context.updated", session.Id, session.Runs.LastOrDefault()?.Id, new { context.Manifest }));
                }
                catch (ContextBudgetException ex) { Write("error", new { message = ex.Message }); }
                catch (OperationCanceledException) { Store.HoldQueue(session.Id, "Compaction cancelled"); }
                return true;
        }
        return false;
    }
}
