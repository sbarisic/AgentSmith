using AgentSmith.Core;
using AgentSmith.Tools;

namespace AgentSmith.Cli;

internal sealed partial class CliApp
{
    internal static WorkflowMode ParseWorkflow(string? value, bool plan)
    {
        if (value is null) return plan ? WorkflowMode.Plan : WorkflowMode.Work;
        if (!Enum.TryParse<WorkflowMode>(value, true, out var workflow) || !Enum.IsDefined(workflow) || int.TryParse(value, out _) || plan && workflow != WorkflowMode.Plan)
            throw new UserFacingException("Use --workflow work|plan|document|review; --plan cannot conflict with --workflow.");
        return workflow;
    }
    private void RequireWorkflowIdle(Session session)
    {
        if (activeRun is not null || Unfinished(session) || Planning.Active(session)?.PendingReview is not null || session.PendingPlanPrompt is not null || session.Mutations.Any(m => m.State == "Prepared"))
            throw new UserFacingException("Resolve active work, decisions and recovery before changing workflows or exporting.");
    }
    private async Task<bool> WorkflowCommand(string name, string args, Session session)
    {
        if (name == "/reports")
        {
            if (args.Length > 0) throw new UserFacingException("Usage: /reports");
            Write("workflow.reports", new { reports = session.Reports.Select(r => new { r.Id, workflow = r.Workflow.ToString(), r.State, r.Revision, r.TargetKind, r.Target, r.Task }) }); return true;
        }
        if (name == "/report")
        {
            if (args.StartsWith("export ", StringComparison.Ordinal))
            {
                RequireWorkflowIdle(session);
                var pieces = args.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (pieces.Length != 3) throw new UserFacingException("Usage: /report export <id> <new-path.md>");
                Write("operation", new { content = (await Agents.ExportReportAsync(session, pieces[1], pieces[2].Trim('"'))).Content });
            }
            else
            {
                var report = args.Length == 0 ? WorkflowPolicy.Active(session) : session.Reports.SingleOrDefault(r => r.Id == args);
                if (report is null) throw new UserFacingException("Report not found in this conversation. Use /reports.");
                Write("workflow.report", new { report.Id, report.Revision, report.State, workflow = report.Workflow.ToString(), markdown = WorkflowTools.Markdown(report) });
            }
            return true;
        }
        if (name == "/workflow")
        {
            if (args.Length == 0) { Write("workflow.status", new { workflow = session.Workflow.ToString(), message = $"Workflow: {session.Workflow} / {session.Mode} · report {session.ActiveReportId ?? "none"}" }); return true; }
            if (args != "work") throw new UserFacingException("Use /workflow work, /document <task>, /review, or /plan new <task>.");
            RequireWorkflowIdle(session);
            if (session.Workflow == WorkflowMode.Plan) throw new UserFacingException("Use /plan leave to leave Plan explicitly.");
            await Agents.StopAllAsync(session.Id);
            if (Store.Workers(session.Id).Any(w => w.Active || w.ReservationsHeld || w.State == "RecoveryRequired")) throw new UserFacingException("Settle workers and recovery before leaving the workflow.");
            if (WorkflowPolicy.Active(session) is { State: "InProgress" } active) active.State = "Abandoned";
            session.Workflow = WorkflowMode.Work; Store.Save(session, new("workflow.changed", session.Id, null, new { workflow = "Work" }));
            Write("workflow.status", new { workflow = session.Workflow.ToString(), message = "Returned to Work; unfinished reports remain saved without a completion claim." }); return true;
        }
        if (name is not ("/document" or "/review")) return false;
        RequireWorkflowIdle(session);
        if (session.Workflow == WorkflowMode.Plan) throw new UserFacingException("Resolve or explicitly leave Plan before starting this workflow.");
        if (WorkflowPolicy.Active(session) is { State: "InProgress" }) throw new UserFacingException("Finish the current workflow or leave it explicitly with /workflow work.");
        var mode = name == "/document" ? WorkflowMode.Document : WorkflowMode.Review;
        var targetKind = "changes"; var target = ".";
        if (mode == WorkflowMode.Document && string.IsNullOrWhiteSpace(args)) throw new UserFacingException("Usage: /document <task>");
        if (mode == WorkflowMode.Review && args.Length > 0)
        {
            var pieces = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length != 2 || pieces[0] is not ("--base" or "--commit" or "--path") || pieces[1].Contains(" --", StringComparison.Ordinal)) throw new UserFacingException("Usage: /review [--base <ref> | --commit <ref> | --path <path>]");
            targetKind = pieces[0][2..]; target = pieces[1].Trim('"');
            if (targetKind == "path") WorkspaceTools.Resolve(session.Workspace, target);
        }
        await Agents.StopAllAsync(session.Id);
        if (Store.Workers(session.Id).Any(w => w.Active || w.ReservationsHeld || w.State == "RecoveryRequired")) throw new UserFacingException("Settle worker recovery before changing workflows.");
        if (mode == WorkflowMode.Review)
        {
            await Registry.Processes.StopAsync(session);
            if (session.Processes.Any(p => p.State is "Running" or "Prepared" || !p.TerminationConfirmed)) throw new UserFacingException("Process termination must be confirmed before Review.");
        }
        session.Workflow = mode;
        var task = mode == WorkflowMode.Document ? args : $"Perform a broad quality review of {targetKind}: {target}. Record evidence, coverage, defects and quality suggestions; do not edit or execute tests.";
        var reportRecord = WorkflowPolicy.Begin(session, task, targetKind, target);
        Store.Save(session, new("workflow.started", session.Id, null, new { reportRecord.Id, workflow = mode.ToString(), targetKind, target }));
        queuedPrompt = task;
        Write("workflow.status", new { workflow = session.Workflow.ToString(), message = $"{mode} / {session.Mode} · {targetKind} {target} · report {reportRecord.Id}" }); return true;
    }
}
