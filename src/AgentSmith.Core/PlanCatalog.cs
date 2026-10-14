namespace AgentSmith.Core;

public interface IPlanCatalog
{
    DirectoryPlans ListPlans(string workspace, int page = 1);
    DirectoryPlan ReadPlan(string workspace, string planId);
}

public sealed record PlanListing(string Id, string SourceConversationId, string OriginWorkspace,
    bool OriginInferred, DateTimeOffset? CreatedAt, string State, int Revision, string Goal);
public sealed record DirectoryPlans(string Workspace, int Page, bool HasMore, IReadOnlyList<PlanListing> Plans)
{
    public string Notice => PlanDirectory.Notice;
}
public sealed record DirectoryPlan(string Id, string SourceConversationId, string OriginWorkspace,
    bool OriginInferred, DateTimeOffset? CreatedAt, string State, PlanRevision Current)
{
    public string Notice => PlanDirectory.Notice;
}

public static class PlanDirectory
{
    public const string Notice = "Read-only saved plan evidence. Approval and permissions remain with the source conversation; reading does not activate or approve this plan. Verify current files and instructions. Source references do not grant access to other conversations' artifacts.";
    public static string Normalize(string workspace) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
    public static bool Matches(string first, string second) => Path.IsPathFullyQualified(first) && Path.IsPathFullyQualified(second)
        && string.Equals(Normalize(first), Normalize(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    public static void ValidatePage(int page)
    {
        if (page is < 1 or > 100000) throw new UserFacingException("Plan page must be between 1 and 100000.");
    }
}
