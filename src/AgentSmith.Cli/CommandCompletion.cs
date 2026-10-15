namespace AgentSmith.Cli;

/// <summary>Local, finite completions only. Choosing a value edits input; it never dispatches a command.</summary>
internal static class CommandCompletion
{
    internal sealed record Suggestion(string Value, string Description, int Start, int Length);
    internal sealed record Command(string Name, string Description);
    private sealed record Choice(string Value, string Description);
    private sealed record Argument(string Command, int Position, string? Previous, Choice[] Choices);

    // Explicit insertable names, not help syntax, placeholders, provider models, or saved IDs.
    // Related commands (/work and /workflow work, /status and /context) remain discoverable.
    internal static IReadOnlyList<Command> Catalog { get; } = Array.AsReadOnly<Command>([
        new("/help", "Show command help and keyboard controls"),
        new("/mode", "Show or select workflow and permissions"),
        new("/work", "Return to the Work workflow"),
        new("/plan", "Start, inspect, revise, or review a saved plan"),
        new("/document", "Start a documentation task"),
        new("/review", "Start a read-only audit of changes, a commit, or a path"),
        new("/memorize", "Inspect sources and refresh repository-local development memories"),
        new("/workflow", "Show workflow status or return to Work"),
        new("/status", "Show current state and execution limits"),
        new("/usage", "Show cloud and local token usage"),
        new("/tasks", "Show saved plan steps"),
        new("/reports", "List Document, Review and Memorize reports"),
        new("/plans", "Find saved plans in this workspace directory"),
        new("/report", "Read or export a saved report"),
        new("/artifacts", "List retained logs and verification reports"),
        new("/diagnostics", "Inspect structured build errors and test failures"),
        new("/artifact", "Read or search a retained artifact"),
        new("/exit", "Exit; saved work can be resumed"),
        new("/tools", "List tools and availability"),
        new("/changes", "Inspect checkpointed file changes"),
        new("/undo", "Undo a matching checkpointed file change"),
        new("/processes", "List owned processes"),
        new("/stop", "Stop an owned process tree"),
        new("/agents", "List delegated workers"),
        new("/agent", "Inspect, cancel, or send steering to a worker"),
        new("/recover", "Inspect uncertain operations or abandon unfinished work"),
        new("/steer", "Redirect this run at the next safe boundary"),
        new("/steering", "Inspect steering delivery or remove a pending message"),
        new("/cancel", "Stop the active run and hold queued prompts"),
        new("/queue", "Inspect, remove, clear, or release queued follow-ups"),
        new("/answer", "Address a pending question in redirected input"),
        new("/paste", "Enter multiline literal text; /end submits it"),
        new("/attach", "Attach an image to the draft"),
        new("/model", "Show or change the next prompt's model while idle"),
        new("/models", "List available models"),
        new("/display", "Choose compact or verbose tool output"),
        new("/details", "Inspect bounded call or process details"),
        new("/markdown", "Toggle formatted Markdown"),
        new("/context", "Inspect context usage or set its budget"),
        new("/compact", "Compact saved context without resuming work"),
        new("/limits", "Show or change execution limits"),
        new("/delegation", "Show or change proactive/manual delegation"),
        new("/worker-limits", "Show or change parallel worker limits"),
        new("/worker-budget", "Show or change the budget for new workers"),
        new("/web-search", "Select native, basic, or disabled web search"),
        new("/audio", "Inspect local Whisper and YAMNet readiness"),
        new("/memory", "Browse repository/global memories, manage shared memory, or toggle retrieval")
    ]);

    private static readonly Argument[] Arguments =
    [
        new("/audio", 1, null, [new("status", "Inspect local audio installation")]),
        new("/help", 1, null, [new("workflows", "Workflows and permissions"), new("session", "Conversation and saved results"), new("tools", "Tools, workers, and recovery"), new("queue", "Input, cancellation, and follow-ups"), new("settings", "Display, context, and memory"), new("all", "All command help")]),
        new("/mode", 1, null, [new("work", "Work workflow"), new("plan", "Plan workflow"), new("planning", "Alias for plan"), new("document", "Document workflow"), new("documentation", "Alias for document"), new("review", "Read-only Review workflow"), new("memorize", "Source-read-only repository memory workflow"), new("inspect", "Inspect permissions"), new("edit", "Edit permissions"), new("execute", "Execute permissions")]),
        new("/workflow", 1, null, [new("work", "Return to Work")]),
        new("/plan", 1, null, [new("new", "Start a new plan from the following task"), new("show", "Read the active plan"), new("read", "Read a directory plan by ID"), new("revise", "Revise the plan with the following note"), new("implement", "Open the plan approval picker; does not approve it"), new("leave", "Leave Plan without approving it")]),
        new("/review", 1, null, [new("--base", "Audit changes against a base ref"), new("--commit", "Audit a commit"), new("--path", "Audit a workspace path")]),
        new("/memorize", 1, null, [new("status", "Inspect the saved Memorize report without starting a scan"), new("--full", "Request a bounded full rescan"), new("--path", "Refresh a relative workspace scope")]),
        new("/mode", 2, "memorize", [new("status", "Inspect the saved Memorize report"), new("--full", "Request a bounded full rescan"), new("--path", "Refresh a relative workspace scope")]),
        new("/report", 1, null, [new("export", "Export a report to a new Markdown file")]),
        new("/mode", 2, "review", [new("--base", "Audit changes against a base ref"), new("--commit", "Audit a commit"), new("--path", "Audit a workspace path")]),
        new("/diagnostics", 2, null, [new("errors", "Build errors"), new("warnings", "Build warnings"), new("failed", "Failed tests"), new("skipped", "Skipped tests"), new("passed", "Passed tests"), new("all", "All diagnostics")]),
        new("/artifact", 2, null, [new("read", "Read at the following cursor"), new("search", "Search for the following text")]),
        new("/agent", 2, null, [new("cancel", "Cancel this worker"), new("message", "Send the following steering text")]),
        new("/recover", 1, null, [new("abandon", "Explicitly abandon unfinished work")]),
        new("/steering", 1, null, [new("remove", "Remove a pending steering message by ID")]),
        new("/queue", 1, null, [new("remove", "Remove a queued prompt by ID"), new("clear", "Remove pending queued prompts"), new("continue", "Explicitly release the queue hold")]),
        new("/display", 1, null, [new("compact", "Compact tool output"), new("verbose", "Detailed tool output")]),
        new("/markdown", 1, null, [new("on", "Enable formatted Markdown"), new("off", "Disable formatted Markdown")]),
        new("/context", 1, null, [new("budget", "Set the context budget while idle")]),
        new("/context", 2, "budget", [new("balanced", "Balanced context budget"), new("max", "Maximum available context budget")]),
        new("/delegation", 1, null, [new("proactive", "Delegate useful independent tasks proactively"), new("manual", "Delegate only with explicit opt-in")]),
        new("/web-search", 1, null, [new("native", "ChatGPT native search"), new("basic", "DuckDuckGo fallback"), new("off", "Disable search")]),
        new("/memory", 1, null, [new("list", "List global memory entries"), new("search", "Search global memory by text"), new("show", "Read a global memory by ID"), new("current", "Read this workspace's repository memories"), new("global", "Read the global shared library"), new("repository", "Explicitly select a registered repository ID"), new("repositories", "Discover repository registrations; metadata only"), new("add", "Create a global memory from text"), new("edit", "Edit a global memory by ID"), new("forget", "Remove a global memory from retrieval"), new("on", "Enable automatic memory use"), new("off", "Disable automatic memory use")]),
        new("/memory", 2, "current", [new("list", "List repository memories"), new("search", "Search repository memories by text"), new("show", "Read a repository memory by ID or topic")]),
        new("/memory", 2, "global", [new("list", "List global memories"), new("search", "Search global memories by text"), new("show", "Read a global memory by ID")])
    ];

    internal static IReadOnlyList<Suggestion> GetSuggestions(string text, int position)
    {
        if (position < 0 || position > text.Length || text.Contains('\n') || text.Contains('\r')) return [];
        var first = 0;
        while (first < text.Length && char.IsWhiteSpace(text[first])) first++;
        if (first == text.Length || text[first] != '/' || position <= first) return [];

        var start = position;
        while (start > first && !char.IsWhiteSpace(text[start - 1])) start--;
        var end = position;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        var prefix = text[start..position];
        var before = text[first..start].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (before.Length == 0)
            return Catalog.Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(c => new Suggestion(c.Name, c.Description, start, end - start)).ToArray();

        var argument = Arguments.FirstOrDefault(a => a.Command.Equals(before[0], StringComparison.OrdinalIgnoreCase)
            && a.Position == before.Length
            && (a.Previous is null || a.Previous.Equals(before[^1], StringComparison.OrdinalIgnoreCase)));
        return argument is null ? [] : argument.Choices
            .Where(c => c.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(c => new Suggestion(c.Value, c.Description, start, end - start)).ToArray();
    }
}
