using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentSmith.Cli;

public sealed partial class TerminalRenderer
{
    private void ResumeSummary(JsonElement data)
    {
        void Row(string text = "", string style = "normal") => Wrapped(text, style, "  │ ", maxWidth: 100);
        void Section(string title, string style = "accent") { Row(); Row(title, style); }
        static string Excerpt(string text, int max)
        {
            text = Regex.Replace(Safe(text), @"\s+", " ").Trim();
            if (text.Length <= max) return text;
            var end = text.LastIndexOf(' ', max);
            if (end < max / 2) end = max;
            if (char.IsHighSurrogate(text[end - 1])) end--;
            return text[..end] + "…";
        }

        guideStyle = TerminalTheme.Guide(data.TryGetProperty("report", out var savedReport) && savedReport.ValueKind == JsonValueKind.Object ? Text(savedReport, "workflow") : Text(data, "workflow"));
        MessageTitle("Saved state · " + Text(data, "state"));
        Row(Text(data, "next"));
        Row(Text(data, "workflow") + " / " + Text(data, "mode")
            + (Number(data, "durationMilliseconds") > 0 ? " · " + TerminalDuration.Format(TimeSpan.FromMilliseconds(Number(data, "durationMilliseconds"))) + " recorded · " + Text(data, "completedTools") + " tool calls" : ""), "dim");
        if (Text(data, "reason").Length > 0) { Section("Reason", "warning"); Row(Text(data, "reason")); }
        if (data.TryGetProperty("report", out var report) && report.ValueKind == JsonValueKind.Object)
        {
            Section(Text(report, "workflow") + " report · " + Text(report, "state") + " · revision " + Text(report, "revision"));
            Row("Task: " + Excerpt(Text(report, "task"), 180));
            Section("Summary (excerpt)");
            var summary = Excerpt(Text(report, "summary"), Verbose ? 1600 : 500);
            if (summary.Length == 0) Row("No report summary has been saved yet.", "dim");
            else foreach (var sentence in Regex.Split(summary, @"(?<=[.!?])\s+(?=[A-Z])")) Row("• " + sentence);
            if (Text(report, "coverage").Length > 0)
            {
                Section("Coverage (excerpt)");
                Row(Excerpt(Text(report, "coverage"), Verbose ? 500 : 200));
            }
            if (Number(report, "findings") > 0) Row(Text(report, "findings") + " saved findings", "dim");
            if (report.TryGetProperty("limitations", out var limits) && limits.ValueKind == JsonValueKind.Array && limits.GetArrayLength() > 0)
            {
                Section("Limitations (excerpts)", "warning");
                foreach (var limit in limits.EnumerateArray()) Row("• " + Excerpt(limit.GetString() ?? "", Verbose ? 300 : 180));
                if (Number(report, "limitationCount") > limits.GetArrayLength()) Row("Additional limitations are in the full report.", "dim");
            }
            Row();
            Row("Full report: /report " + Text(report, "id"), "accent");
        }
        if (Number(data, "pendingPrompts") > 0)
        {
            Section("Queued follow-ups: " + Text(data, "pendingPrompts") + (Text(data, "queueHold").Length > 0 ? " · held: " + Text(data, "queueHold") : ""), "warning");
            Row("Inspect /queue; /queue continue releases it after pending work and decisions are resolved.", "dim");
        }
        EndPrompt();
        guideStyle = TerminalTheme.Guide(workflow);
    }
}
