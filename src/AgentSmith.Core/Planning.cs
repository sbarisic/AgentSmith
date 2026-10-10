namespace AgentSmith.Core;

public sealed class PlanStep
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string Verification { get; set; } = "";
    public string Note { get; set; } = "";
    public string[] References { get; set; } = [];
}
public sealed class PlanRevision
{
    public int Revision { get; set; }
    public string Goal { get; set; } = "";
    public string[] Deliverables { get; set; } = [];
    public string[] Constraints { get; set; } = [];
    public string[] Questions { get; set; } = [];
    public List<PlanStep> Steps { get; set; } = [];
    public string Note { get; set; } = "";
    public string RunId { get; set; } = "";
}
public sealed class PlanApproval
{
    public int Revision { get; set; }
    public PermissionMode Mode { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class PlanReview
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Revision { get; set; }
    public string Stage { get; set; } = "Choice";
    public string Note { get; set; } = "";
    public string? Decision { get; set; }
}
public sealed class WorkPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string State { get; set; } = "Draft";
    public List<PlanRevision> Revisions { get; set; } = [];
    public List<PlanApproval> Approvals { get; set; } = [];
    public List<PlanReview> Reviews { get; set; } = [];
    public PlanRevision Current => Revisions.Last();
    public PlanReview? PendingReview => Reviews.LastOrDefault(r => r.Decision is null);
}
public sealed record ToolReceipt(string Content, bool PlanReview, bool FinishRun = false);
public sealed class ArtifactRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "";
    public string Path { get; set; } = "";
    public string? ProcessId { get; set; }
    public bool Truncated { get; set; }
}
public sealed class VerificationRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "";
    public string ProcessId { get; set; } = "";
    public string Directory { get; set; } = "";
    public string? Result { get; set; }
}

public static class Planning
{
    public static WorkPlan? Active(Session session) => session.Plans.SingleOrDefault(p => p.Id == session.ActivePlanId);
    public static bool ScopeChanged(PlanRevision before, PlanRevision after) => before.Goal != after.Goal || !before.Deliverables.SequenceEqual(after.Deliverables) || !before.Constraints.SequenceEqual(after.Constraints);
    public static void ValidateUpdate(Session session, int expectedRevision, PlanRevision revision)
    {
        var plan = Active(session);
        if ((plan?.Current.Revision ?? 0) != expectedRevision) throw new UserFacingException("Plan revision changed. Read the current plan before updating.");
        if (plan?.PendingReview is not null) throw new UserFacingException("Resolve the pending review before changing the plan.");
        if (string.IsNullOrWhiteSpace(revision.Goal) || revision.Steps.Count is < 1 or > 30 || revision.Steps.Any(s => string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Description) || string.IsNullOrWhiteSpace(s.Verification))) throw new UserFacingException("A goal and 1..30 identified steps with verification criteria are required.");
        if (revision.Steps.Select(s => s.Id).Distinct().Count() != revision.Steps.Count || revision.Steps.Count(s => s.Status == "In progress") > 1 || revision.Steps.Any(s => s.Status is not ("Pending" or "In progress" or "Blocked" or "Completed" or "Cancelled"))) throw new UserFacingException("Invalid step IDs or statuses; only one step can be in progress.");
        if (revision.Steps.Any(s => s.Status == "Completed" && string.IsNullOrWhiteSpace(s.Note))) throw new UserFacingException("Completed steps require evidence or an explanation of why verification does not apply.");
        if (Json.Serialize(revision).Length > 24000) throw new UserFacingException("Plan exceeds the 24,000-character limit.");
    }
    public static WorkPlan Update(ToolExecutionContext ctx, int expectedRevision, PlanRevision revision)
    {
        ValidateUpdate(ctx.Session, expectedRevision, revision);
        var plan = Active(ctx.Session);
        var scopeChanged = plan?.Approvals.LastOrDefault() is { } approval && ScopeChanged(plan.Revisions.Single(r => r.Revision == approval.Revision), revision);
        if (plan is null) { plan = new(); ctx.Session.Plans.Add(plan); ctx.Session.ActivePlanId = plan.Id; }
        revision.Revision = expectedRevision + 1; revision.RunId = ctx.Run.Id;
        plan.Revisions.Add(revision);
        if (scopeChanged) { ctx.Session.Workflow = WorkflowMode.Plan; plan.State = "Draft"; }
        if (ctx.Session.Workflow == WorkflowMode.Work && plan.Approvals.Count == 0) plan.State = "Tracking";
        if (ctx.Session.Workflow == WorkflowMode.Work && revision.Steps.All(s => s.Status is "Completed" or "Cancelled")) plan.State = "Completed";
        return plan;
    }
    public static WorkPlan ValidateSubmit(Session session)
    {
        var plan = Active(session) ?? throw new UserFacingException("No active plan.");
        if (session.Workflow != WorkflowMode.Plan) throw new UserFacingException("plan_submit requires Plan workflow. In Work, use plan_update for task tracking; the user can enter Plan with /plan new or review a finished draft with /plan implement.");
        if (plan.Current.Questions.Length != 0) throw new UserFacingException("Resolve the recorded questions before submitting the plan.");
        return plan;
    }
    public static PlanReview Submit(Session session)
    {
        var plan = ValidateSubmit(session);
        if (plan.PendingReview is { } existing) return existing;
        var review = new PlanReview { Revision = plan.Current.Revision }; plan.Reviews.Add(review); plan.State = "Review"; return review;
    }
    public static string Decide(Session session, string reviewId, string decision, string note, PermissionMode? mode = null)
    {
        var plan = Active(session) ?? throw new UserFacingException("No active plan.");
        var review = plan.PendingReview ?? throw new UserFacingException("No pending plan review.");
        if (review.Id != reviewId || review.Revision != plan.Current.Revision) throw new UserFacingException("The displayed plan revision is stale.");
        if (decision == "implement")
        {
            if (mode is not (PermissionMode.Edit or PermissionMode.Execute) || plan.Current.Questions.Length > 0) throw new UserFacingException("Implementation requires an explicit Edit or Execute selection and no unresolved questions.");
            plan.Approvals.Add(new() { Revision = review.Revision, Mode = mode.Value });
            session.Mode = mode.Value; session.Workflow = WorkflowMode.Work; plan.State = "Implementing";
        }
        else if (decision is "revise" or "keep planning") { session.Workflow = WorkflowMode.Plan; plan.State = "Draft"; }
        else throw new UserFacingException("Choose implement edit, implement execute, revise <note>, or keep planning.");
        review.Decision = decision; review.Note = note;
        return decision == "implement" ? $"Implement approved plan {plan.Id}, revision {review.Revision}, in {mode} mode. Refresh relevant file evidence before editing. Update task progress and verify actual outcomes." : $"Continue planning. User chose {decision}. Their note: {note}";
    }
}
