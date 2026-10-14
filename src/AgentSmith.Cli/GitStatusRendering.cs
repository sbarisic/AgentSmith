using System.Text.Json;

namespace AgentSmith.Cli;

public sealed partial class TerminalRenderer
{
    private void GitStatus(JsonElement data)
    {
        // This is presentation only. Keep porcelain results intact for the model and saved history.
        if (!data.TryGetProperty("exitCode", out var exit) || exit.ValueKind != JsonValueKind.Number || !exit.TryGetInt32(out var code) || code != 0)
        { Operation(data); return; }
        var content = Safe(Text(data, "content"));
        if (string.IsNullOrWhiteSpace(content))
        { MessageText(Flag(data, "truncated") ? "Git status output is incomplete." : "Working tree clean within the workspace.", "dim"); return; }
        MessageText("Git changes · repository-relative paths", "accent");
        var untracked = false;
        var lines = content.Split('\n').Select(line =>
        {
            if (line.Length < 4 || line[2] != ' ') return line;
            var status = line[..2]; var path = line[3..];
            if (status == "??") { untracked = true; return "Untracked · " + path; }
            if (status == "!!") return "Ignored · " + path;
            if (status is "DD" or "AU" or "UD" or "UA" or "DU" or "AA" or "UU")
                return "Conflict (" + status + ") · " + path;
            string? Label(char value) => value switch
            {
                ' ' => "", 'M' => "Modified", 'A' => "Added", 'D' => "Deleted", 'R' => "Renamed",
                'C' => "Copied", 'T' => "Type changed", _ => null
            };
            var staged = Label(status[0]); var unstaged = Label(status[1]);
            if (staged is null || unstaged is null || staged.Length + unstaged.Length == 0) return line;
            var labels = new List<string>();
            if (staged.Length > 0) labels.Add(staged + " (staged)");
            if (unstaged.Length > 0) labels.Add(unstaged + " (unstaged)");
            return string.Join("; ", labels) + " · " + path;
        }).ToArray();
        foreach (var line in BoundedPreview(string.Join('\n', lines), Verbose).Split('\n'))
        {
            var style = line switch
            {
                _ when line.StartsWith("Conflict (", StringComparison.Ordinal) => "warning",
                _ when line.StartsWith("Deleted (", StringComparison.Ordinal) => "error",
                _ when line.StartsWith("Added (", StringComparison.Ordinal) => "success",
                _ when line.StartsWith("Untracked ·", StringComparison.Ordinal) || line.StartsWith("Renamed (", StringComparison.Ordinal) => "info",
                _ when line.StartsWith("Ignored ·", StringComparison.Ordinal) || line.StartsWith("… ", StringComparison.Ordinal) => "dim",
                _ => "activity"
            };
            MessageText(line, style);
        }
        if (untracked) MessageText("Untracked = new files not yet added to Git; not an error.", "dim");
        if (Flag(data, "truncated")) MessageText("Git status output was truncated; more changes may exist.", "warning");
    }
}
