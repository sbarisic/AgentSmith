using AgentSmith.Core;
using AgentSmith.Tools;

namespace AgentSmith.Cli;

internal static class ArtifactCommands
{
    internal static string ResolveId(ToolExecutionContext context, string value)
    {
        if (!value.StartsWith("process:", StringComparison.OrdinalIgnoreCase)) return value;
        var id = Activity.ProcessId(context.Session, value[8..]);
        if (!context.Session.Processes.Any(p => p.Id == id)) throw new UserFacingException("Unknown process in this conversation.");
        ArtifactAccess.Backfill(context);
        return context.Session.Artifacts.FirstOrDefault(a => a.ProcessId == id && a.Kind == "process-log")?.Id
            ?? throw new UserFacingException("No retained log is registered for this process.");
    }
}
