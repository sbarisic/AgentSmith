namespace AgentSmith.Core;

public static class WorkflowPolicy
{
    public static bool IsTask(WorkflowMode mode) => mode is WorkflowMode.Document or WorkflowMode.Review;
    public static WorkflowReport? Active(Session s) => s.Reports.SingleOrDefault(r => r.Id == s.ActiveReportId);
    public static bool IsDocumentation(string path) => Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown" or ".rst" or ".adoc" or ".asciidoc" or ".txt";
    public static bool Enabled(Session s, ToolDefinition tool)
    {
        var ceiling = s.Mode;
        if (Planning.Active(s) is { State: "Implementing" } plan && plan.Approvals.LastOrDefault() is { } approval)
            ceiling = (PermissionMode)Math.Min((int)ceiling, (int)approval.Mode);
        if (tool.Capability > ceiling) return false;
        if (tool.Name == "csharp_load" && s.ParentConversationId is not null) return false;
        if (tool.Name.StartsWith("workflow_", StringComparison.Ordinal)) return s.ParentConversationId is null && IsTask(s.Workflow);
        if (IsTask(s.Workflow) && tool.Name is "plan_update" or "plan_submit") return false;
        return s.Workflow is not (WorkflowMode.Plan or WorkflowMode.Review)
            || tool.Capability == PermissionMode.Inspect && tool.Effect is ToolEffect.ReadOnly or ToolEffect.SessionState or ToolEffect.MemoryState;
    }
    public static void CheckFiles(Session s, IEnumerable<string> paths)
    {
        if (s.Workflow is WorkflowMode.Plan or WorkflowMode.Review || s.Mode < PermissionMode.Edit)
            throw new UserFacingException("File changes require Edit permission in Work or Document workflow.");
        if (Planning.Active(s) is { State: "Implementing" } plan && plan.Approvals.Last().Mode < PermissionMode.Edit)
            throw new UserFacingException("File changes exceed the approved plan permissions.");
        if (s.Workflow == WorkflowMode.Document && paths.Any(p => !IsDocumentation(p)))
            throw new UserFacingException("Document workflow can edit only .md, .markdown, .rst, .adoc, .asciidoc and .txt files. Use Work for source changes.");
    }
    public static WorkflowReport Begin(Session s, string task, string targetKind = "changes", string target = ".")
    {
        if (!IsTask(s.Workflow) || s.ParentConversationId is not null) throw new UserFacingException("A parent Document or Review workflow is required.");
        var report = new WorkflowReport { Workflow = s.Workflow, Task = task, TargetKind = s.Workflow == WorkflowMode.Document ? "documentation" : targetKind, Target = target };
        s.Reports.Add(report); s.ActiveReportId = report.Id; return report;
    }
}

public sealed class WorkflowReport
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public WorkflowMode Workflow { get; set; }
    public string Task { get; set; } = "";
    public string TargetKind { get; set; } = "changes";
    public string Target { get; set; } = ".";
    public string State { get; set; } = "InProgress";
    public string? ArtifactId { get; set; }
    public ReviewSnapshot? Baseline { get; set; }
    public List<string> Drift { get; set; } = [];
    public List<ReportRevision> Revisions { get; set; } = [];
    public int Revision => Revisions.LastOrDefault()?.Revision ?? 0;
    public ReportRevision? Current => Revisions.LastOrDefault();
}
public sealed class ReviewSnapshot
{
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string? Head { get; set; }
    public string? Base { get; set; }
    public string StateHash { get; set; } = "";
    public Dictionary<string, string> FileHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ArtifactIds { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
}
public sealed class ReportRevision
{
    public int Revision { get; set; }
    public string Summary { get; set; } = "";
    public string Coverage { get; set; } = "";
    public string[] Limitations { get; set; } = [];
    public string[] Evidence { get; set; } = [];
    public List<ReviewFinding> Findings { get; set; } = [];
    public string RunId { get; set; } = "";
    public string CallId { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class ReviewFinding
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Title { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string Path { get; set; } = "";
    public int Line { get; set; }
    public string[] Evidence { get; set; } = [];
    public string SuggestedAction { get; set; } = "";
    public string Status { get; set; } = "Open";
}
