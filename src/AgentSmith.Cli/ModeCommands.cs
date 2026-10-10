using AgentSmith.Core;

namespace AgentSmith.Cli;

internal sealed partial class CliApp
{
    private async Task<bool> WorkflowModeCommand(Session session, string args)
    {
        if (args.Length == 0)
        {
            Write("mode", new { workflow = session.Workflow.ToString(), message = $"Workflow: {session.Workflow} · Permissions: {session.Mode}\nWorkflows: /mode work|plan|document|review\nPermissions: /mode inspect|edit|execute\nSelect a workflow, then type your task; or start immediately with /plan <task>, /document <task>, or /review.\nWorkflow restrictions and approved-plan permissions still apply." });
            return true;
        }
        var parts = args.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        var choice = parts[0].ToLowerInvariant() switch { "planning" => "plan", "documentation" => "document", var value => value };
        var task = parts.Length > 1 ? parts[1].Trim() : "";
        if (choice is not ("work" or "plan" or "document" or "review")) return false;
        if (choice == "work")
        {
            if (task.Length > 0) throw new UserFacingException("Usage: /mode work or /work. Then send your task.");
            if (session.Workflow == WorkflowMode.Plan) await PlanCommand(session, "leave");
            else await WorkflowCommand("/workflow", "work", session);
            return true;
        }
        if (task.Length > 0)
        {
            if (choice == "plan") await PlanCommand(session, "new " + task);
            else await WorkflowCommand("/" + choice, task, session);
            return true;
        }

        RequireWorkflowIdle(session);
        var next = ParseWorkflow(choice, false);
        if (session.Workflow != next)
        {
            if (session.Workflow == WorkflowMode.Plan || WorkflowPolicy.Active(session) is { State: "InProgress" })
                throw new UserFacingException("Leave the current workflow explicitly with /mode work before selecting another workflow. Pending work and decisions must be settled first.");
            await Agents.StopAllAsync(session.Id);
            if (Store.Workers(session.Id).Any(w => w.Active || w.ReservationsHeld || w.State == "RecoveryRequired"))
                throw new UserFacingException("Settle worker recovery before changing workflows.");
            if (next is WorkflowMode.Plan or WorkflowMode.Review)
            {
                await Registry.Processes.StopAsync(session);
                if (session.Processes.Any(p => p.State is "Running" or "Prepared" || !p.TerminationConfirmed))
                    throw new UserFacingException("Process termination must be confirmed before this workflow.");
            }
            session.Workflow = next;
            Store.Save(session, new("workflow.changed", session.Id, null, new { workflow = next.ToString() }));
        }
        Write("mode", new { workflow = session.Workflow.ToString(), message = $"Workflow: {session.Workflow} · Permissions: {session.Mode}\nType your task to begin. No request has been sent yet." });
        return true;
    }
}
