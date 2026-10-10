using AgentSmith.Core;

namespace AgentSmith.Cli;

internal static class CommandHelp
{
    internal sealed record Entry(string Command, string Description);
    internal sealed record Group(string Topic, string Title, Entry[] Commands);
    private static readonly Group[] Groups =
    [
        new("workflows", "Workflows and permissions", [
            new("/mode", "Show your workflow and permission level"),
            new("/mode work|plan|document|review", "Select a workflow, then type your task"),
            new("/mode inspect|edit|execute", "Set permissions; workflow restrictions still apply"),
            new("/work", "Return to Work; equivalent to /mode work"),
            new("/plan <task>", "Start planning immediately; /plan new <task> also works"),
            new("/plan show|implement|leave", "Read the plan, open its approval picker, or leave it"),
            new("/plan revise <note>", "Revise the saved plan"),
            new("/document <task>", "Start a documentation task immediately"),
            new("/review [--base <ref>|--commit <ref>|--path <path>]", "Start a read-only audit; default is current changes"),
            new("/workflow [work]", "Legacy workflow status / return-to-Work command")]),
        new("session", "Conversation and saved results", [
            new("/status", "Show current state and remaining execution limits"), new("/usage", "Show cloud and local token usage"),
            new("/tasks", "Show saved plan steps"), new("/reports", "List Document and Review reports"),
            new("/report [id]", "Read a report; defaults to the active report"),
            new("/report export <id> <new-path.md>", "Export a report to a new Markdown file"),
            new("/artifacts", "List retained logs and verification reports"),
            new("/artifact <id|process:#number> [read <cursor>|search <text>]", "Read, page or search a retained artifact; process:#15 opens that process log"), new("/exit", "Exit; saved work can be resumed with smith resume")]),
        new("tools", "Tools, workers, and recovery", [
            new("/tools", "List tools, including image_read, workspace_map, http_request and web research"), new("/changes", "Inspect checkpointed file changes"),
            new("/undo <checkpoint-id>", "Undo a file change if current contents still match"),
            new("/processes", "List owned processes"), new("/stop <process-id|#number>", "Stop a process tree"),
            new("/agents", "List delegated workers"), new("/agent <id|#number> [cancel]", "Inspect or cancel a worker"),
            new("/recover [abandon]", "Inspect uncertain operations or explicitly abandon unfinished work")]),
        new("queue", "Input, cancellation, and follow-ups", [
            new("/steer <text>", "Redirect this run at the next safe boundary; normal busy input stays queued"),
            new("/steering [remove <message-id>]", "Inspect delivery states or remove a pending message"),
            new("/agent <id|#number> message <text>", "Send steering to an active worker without expanding its assignment"),
            new("/cancel · Escape · Ctrl+C", "Stop the active run; keep edits and hold queued prompts"),
            new("/queue", "Show queued follow-ups and their hold state"), new("/queue remove <id>|clear|continue", "Remove pending items or explicitly release the queue"),
            new("/answer <question-id> <text>", "Address a pending question in redirected input"),
            new("/review <review-id> <decision>", "Address a pending plan decision in redirected input"),
            new("/paste", "Enter multiline text; /end submits it as message content"),
            new("/attach <image-path>", "Attach an image to the draft"),
            new("Alt+V · Tab · Enter · Delete", "Paste; focus draft blocks; preview a block; remove it")]),
        new("settings", "Display, context, and memory", [
            new("/display compact|verbose", "Choose tool-output detail"), new("/details <call-id|#process>", "Inspect bounded activity details"),
            new("/markdown on|off", "Toggle formatted Markdown"), new("/context", "Inspect context usage and compaction provenance"),
            new("/context budget balanced|max|<tokens>", "Set the context budget while idle"), new("/compact", "Compact saved context without resuming work"),
            new("/limits [requests minutes]", "Show or change execution limits"), new("/delegation [proactive|manual]", "Show or change delegation to enabled local and GPT profiles"),
            new("/worker-limits [total local]", "Show parallel worker limits; change while idle (1..16 each)"),
            new("/memory [list [page]|search <text>|show <id>]", "Browse the shared memory library"),
            new("/memory add <text>|edit <id> <text>|forget <id>", "Manage memory entries while idle"), new("/memory on|off", "Enable or disable automatic memory use")])
    ];

    public static object Build(string topic)
    {
        topic = topic.Trim().ToLowerInvariant();
        if (topic.Length > 0 && topic != "all" && !Groups.Any(g => g.Topic == topic))
            throw new UserFacingException("Unknown help topic. Use /help workflows|session|tools|queue|settings|all.");
        var groups = topic.Length == 0
            ? new[] { new Group("start", "Start a task", [new("/plan <task>", "Investigate and prepare a plan"), new("/document <task>", "Write or improve documentation"), new("/review", "Audit current changes without editing"), new("/mode <name>", "Work, Plan, Document, Review; or Inspect, Edit, Execute permissions")]),
                new Group("daily", "Everyday commands", [new("/status · /usage", "Inspect session state and token usage"), new("/tasks · /reports · /report <id>", "Read saved plans and reports"), new("/cancel · /queue", "Stop work or manage queued follow-ups"), new("/work · /exit", "Return to Work or exit the CLI")]) }
            : Groups.Where(g => topic == "all" || g.Topic == topic).ToArray();
        return new { topic, groups, example = "/plan Make a plan to implement player movement", note = "Workflow controls the task; permissions control allowed effects. Selecting a workflow never increases permissions or approves a plan.", topics = "More: /help workflows · /help session · /help tools · /help queue · /help settings · /help all" };
    }
}
