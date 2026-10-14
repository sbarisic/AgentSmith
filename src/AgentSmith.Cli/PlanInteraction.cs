using AgentSmith.Core;
using AgentSmith.Runtime;

namespace AgentSmith.Cli;

internal sealed partial class CliApp
{
    private string? queuedPrompt;
    private static bool Unfinished(Session session) => session.Runs.LastOrDefault()?.State is RunState.Running or RunState.Paused or RunState.AwaitingUser or RunState.AwaitingPlan or RunState.RecoveryRequired;
    private async Task PlanCommand(Session session, string args)
    {
        var parts = args.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries); var verb = parts.FirstOrDefault()?.ToLowerInvariant() ?? "show"; var note = parts.Length > 1 ? parts[1] : "";
        if (verb is not ("new" or "show" or "read" or "revise" or "implement" or "leave")) { verb = "new"; note = args; }
        if (verb == "read")
        {
            if (string.IsNullOrWhiteSpace(note) || note.Any(char.IsWhiteSpace)) throw new UserFacingException("Usage: /plan read <id>. Use /plans to find saved plans in this workspace.");
            Write("plan.display", new { plan = Store.ReadPlan(session.Workspace, note) }); return;
        }
        if (verb is "show" or "implement" or "leave" && note.Length > 0) throw new UserFacingException("Usage: /plan <task>, /plan show, /plan read <id>, /plan revise <note>, /plan implement, or /plan leave. Use /plan new <task> for a task beginning with a reserved command word.");
        if (verb == "show") { Write("plan.display", new { plan = Planning.Active(session) }); return; }
        if (activeRun is not null) throw new UserFacingException("Wait for the active run or use /cancel before changing plans.");
        if (verb != "show" && WorkflowPolicy.IsTask(session.Workflow)) throw new UserFacingException("Finish the Document/Review workflow or leave it with /workflow work before changing plans.");
        if (verb == "leave")
        {
            if (Unfinished(session)) throw new UserFacingException("Explicitly abandon unfinished planning with /recover abandon before leaving Plan.");
            if (Planning.Active(session)?.PendingReview is { } pending) pending.Decision = "left without approval";
            session.PendingPlanPrompt = null; queuedPrompt = null;
            session.Workflow = WorkflowMode.Work; Store.Save(session, new("workflow.changed", session.Id, null, new { workflow = "Work" }));
            Write("mode", new { workflow = session.Workflow.ToString(), message = "Work workflow restored. No plan was approved or implemented." }); return;
        }
        if (verb == "implement")
        {
            if (Planning.Active(session)?.PendingReview is not null) return;
            if (Unfinished(session)) throw new UserFacingException("Finish or explicitly abandon the current run before reviewing a plan.");
            await Agents.StopAllAsync(session.Id); await Registry.Processes.StopAsync(session); session.Workflow = WorkflowMode.Plan; Planning.Submit(session);
            Store.Save(session, new("plan.submitted", session.Id, null, new { session.ActivePlanId })); return;
        }
        if (verb is not ("new" or "revise") || string.IsNullOrWhiteSpace(note)) throw new UserFacingException("Usage: /plan <task>, /plan new <task>, show, revise <note>, implement, or leave.");
        if (verb == "revise" && Planning.Active(session)?.PendingReview is { } review)
        { ResolveReview(session, review.Id, "revise", note); return; }
        RequireWorkflowIdle(session);
        if (Unfinished(session)) throw new UserFacingException("Finish or explicitly abandon the current run before changing workflows.");
        await Agents.StopAllAsync(session.Id); await Registry.Processes.StopAsync(session); session.Workflow = WorkflowMode.Plan;
        session.PendingPlanPrompt = null;
        if (verb == "new") session.ActivePlanId = null;
        else if (Planning.Active(session) is { } active) active.State = "Draft";
        Store.Save(session, new("workflow.changed", session.Id, null, new { workflow = "Plan" }));
        queuedPrompt = verb == "new" ? note : "Revise the active plan: " + note;
        Write("mode", new { workflow = session.Workflow.ToString(), message = "Plan workflow: inspection and internal task updates only." });
    }
    private void ResolveReview(Session session, string reviewId, string decision, string note, PermissionMode? mode = null)
    {
        // Validate the decision first. The cancelled tail cannot run under newly granted permissions.
        var prompt = Planning.Decide(session, reviewId, decision, note, mode);
        if (Unfinished(session))
        {
            var run = session.Runs.Last();
            foreach (var call in run.Calls.Where(c => c.State != CallState.Completed))
            {
                call.State = CallState.Completed; call.Result = Json.Serialize(new { error = "Superseded by explicit plan review; this queued invocation was not executed." });
                session.History.Add(new System.Text.Json.Nodes.JsonObject { ["type"] = "function_call_output", ["call_id"] = call.Id, ["output"] = call.Result });
            }
            run.State = RunState.Completed; run.Reason = "Plan review resolved; continuation starts a new run.";
        }
        session.PendingPlanPrompt = prompt;
        Store.Save(session, new("plan.decision", session.Id, null, new { reviewId, decision, note, mode = mode?.ToString() }));
        Write("plan.decision", new { decision, mode = mode?.ToString(), note }); queuedPrompt = prompt;
    }
    private async Task<bool> ReviewPlan(Session session, CancellationToken ct)
    {
        while (Planning.Active(session)?.PendingReview is { } review)
        {
            Write("plan.display", new { plan = Planning.Active(session) });
            var chooseMode = review.Stage == "Mode";
            var question = new Question { Id = review.Id, Text = chooseMode ? "Choose implementation permissions explicitly." : "Review this saved plan. Implementation requires your explicit selection.", Choices = chooseMode ? ["Edit", "Execute"] : ["Implement", "Revise", "Keep planning"] };
            Write("plan.review.pending", new { review.Id, review.Revision, review.Stage, question.Text, question.Choices, responses = "implement edit | implement execute | revise <note> | keep planning" });
            ct.ThrowIfCancellationRequested();
            var attentionKey = "plan-review:" + review.Id + ":" + review.Stage;
            attention.WaitFor(attentionKey);
            var answer = !json && QuestionPrompt.IsInteractive ? await QuestionPrompt.ReadAsync(question, color, ct, input.ReadKeyAsync, PasteNote) : await ReadInput(ct, attentionKey);
            if (answer is null || answer.Trim() == "/exit") return false;
            if (string.IsNullOrWhiteSpace(answer)) continue;
            if (!input.Interactive && (decisionRequiresAddress || Store.Queue(session.Id).Items.Count > 0))
            {
                var prefix = "/review " + review.Id + " ";
                if (answer.StartsWith(prefix, StringComparison.Ordinal)) answer = answer[prefix.Length..];
                else { if (answer.StartsWith('/')) await HandleCommand(answer, session); else Write("error", new { message = "Use /review " + review.Id + " <decision>; queued input cannot approve a plan." }); continue; }
            }
            if (answer.TrimStart().StartsWith('/')) { await HandleCommand(answer, session); continue; }
            var pieces = answer.Split("\n\nNote: ", 2, StringSplitOptions.None); var choice = pieces[0].Trim(); var note = pieces.Length > 1 ? pieces[1] : "";
            try
            {
                if (choice.Equals("Implement", StringComparison.OrdinalIgnoreCase))
                { review.Stage = "Mode"; review.Note = note; Store.Save(session, new("plan.review.stage", session.Id, null, new { review.Id, review.Stage })); continue; }
                var lower = choice.ToLowerInvariant();
                if (lower is "implement edit" or "implement execute" || chooseMode && lower is "edit" or "execute")
                { ResolveReview(session, review.Id, "implement", note.Length > 0 ? note : review.Note, lower.EndsWith("execute", StringComparison.Ordinal) ? PermissionMode.Execute : PermissionMode.Edit); }
                else if (lower == "revise" || lower.StartsWith("revise "))
                { if (note.Length == 0 && choice.Length > 6) note = choice[7..]; if (string.IsNullOrWhiteSpace(note)) throw new UserFacingException("Supply a revision note, for example: revise add a rollback step."); ResolveReview(session, review.Id, "revise", note); }
                else if (lower == "keep planning") ResolveReview(session, review.Id, "keep planning", note);
                else throw new UserFacingException("Choose implement edit, implement execute, revise <note>, or keep planning. Nothing was approved.");
            }
            catch (UserFacingException ex) { Write("error", new { message = ex.Message }); }
        }
        return true;
    }
}
