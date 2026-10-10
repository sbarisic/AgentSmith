using AgentSmith.Core;

namespace AgentSmith.Cli;

/// <summary>Saved-state presentation only; never infers completion from assistant prose.</summary>
internal static class ResumePresentation
{
    public static object Build(Session session, PromptQueue queue)
    {
        var run = session.Runs.LastOrDefault();
        var report = session.Reports.LastOrDefault(r => run is not null && r.Current?.RunId == run.Id)
            ?? (WorkflowPolicy.IsTask(session.Workflow) ? WorkflowPolicy.Active(session) : null);
        var pendingDecision = Planning.Active(session)?.PendingReview is not null || session.PendingPlanPrompt is not null;
        var next = pendingDecision ? "A plan decision is pending. Resolve it explicitly before starting more work."
            : run?.State switch
            {
                RunState.Running => "Previous execution was interrupted. Continue only when you want to resume its remaining work.",
                RunState.Paused => PauseGuidance.For(run),
                RunState.AwaitingUser => "A saved question needs your answer; the task is not complete.",
                RunState.AwaitingPlan => "A saved plan decision needs your explicit input.",
                RunState.RecoveryRequired => "Inspect /recover before continuing; uncertain effects will not be replayed.",
                RunState.Failed => "The last run failed. Inspect /status, then send a new instruction when ready.",
                RunState.Cancelled => "The last run was cancelled. Completed edits remain; send a new instruction when ready.",
                _ when WorkflowPolicy.IsTask(session.Workflow) => "The workflow report is unfinished. Send an instruction to continue, or use /workflow work to leave it.",
                _ => "Ready for a new message. No continuation is required."
            };
        static string Bound(string? text, int length) => text is null ? "" : text.Length <= length ? text : text[..length] + "…";
        return new
        {
            runId = run?.Id, state = run?.State.ToString() ?? "No previous run",
            reason = Bound(run?.Reason, 600), durationMilliseconds = run?.ExecutionMilliseconds ?? 0,
            completedTools = run?.Calls.Count(c => c.State == CallState.Completed) ?? 0,
            workflow = session.Workflow.ToString(), mode = session.Mode.ToString(), next,
            pendingPrompts = queue.Items.Count(p => p.State == "Pending"), queueHold = queue.HoldReason,
            report = report is null ? null : new
            {
                report.Id, workflow = report.Workflow.ToString(), report.State, report.Revision,
                task = Bound(report.Task, 300), summary = Bound(report.Current?.Summary, 1600),
                coverage = Bound(report.Current?.Coverage, 500), report.ArtifactId,
                limitations = (report.Current?.Limitations ?? []).Concat(report.Baseline?.Limitations ?? []).Concat(report.Drift).Distinct().Take(3).Select(l => Bound(l, 300)).ToArray(),
                limitationCount = (report.Current?.Limitations ?? []).Concat(report.Baseline?.Limitations ?? []).Concat(report.Drift).Distinct().Count(),
                findings = report.Current?.Findings.Count ?? 0
            }
        };
    }
}
