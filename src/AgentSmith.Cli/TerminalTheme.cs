namespace AgentSmith.Cli;

/// <summary>White text with green accents for human-facing terminal output.</summary>
internal static class TerminalTheme
{
    public const string Reset = "\u001b[0m";
    public static string DiffStyle(string line) => line switch
    {
        _ when line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal) || line.StartsWith("diff --git ", StringComparison.Ordinal) => "diff-file",
        _ when line.StartsWith("@@", StringComparison.Ordinal) => "diff-hunk",
        _ when line.StartsWith('+') => "diff-add",
        _ when line.StartsWith('-') => "diff-remove",
        _ when line.StartsWith("… ", StringComparison.Ordinal) || line.StartsWith("\\ No newline", StringComparison.Ordinal) => "dim",
        _ => "normal"
    };
    public static string Guide(string? workflow) => workflow switch
    {
        "Work" => "workflow-work", "Plan" => "workflow-plan",
        "Document" => "workflow-document", "Review" => "workflow-review",
        _ => "dim"
    };
    public static string Style(string style) => style switch
    {
        "workflow-work" => "\u001b[92m",
        "workflow-plan" => "\u001b[96m",
        "workflow-document" => "\u001b[95m",
        "workflow-review" => "\u001b[93m",
        "accent" or "success" => "\u001b[92m",
        "bright" or "heading" => "\u001b[1;92m",
        "border" => "\u001b[32m",
        "dim" => "\u001b[90m",
        "warning" => "\u001b[33m",
        "status-neutral" => "\u001b[97;48;5;236m",
        "status-running" => "\u001b[97;48;5;17m",
        "status-success" => "\u001b[97;48;5;22m",
        "status-warning" => "\u001b[97;48;5;58m",
        "status-error" => "\u001b[97;48;5;52m",
        "diff-add" => "\u001b[92;48;5;22m",
        "diff-remove" => "\u001b[91;48;5;52m",
        "diff-file" => "\u001b[1;96m",
        "diff-hunk" => "\u001b[96m",
        "code" => "\u001b[97m",
        "content" => "\u001b[97;48;5;234m",
        "bold" => "\u001b[1;92m",
        "italic" => "\u001b[3;97m",
        "link" => "\u001b[4;92m",
        _ => "\u001b[97m"
    };
}
