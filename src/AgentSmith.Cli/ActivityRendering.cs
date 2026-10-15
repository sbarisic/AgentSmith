using System.Text;
using System.Text.Json;

namespace AgentSmith.Cli;

public sealed partial class TerminalRenderer
{
    private readonly Dictionary<string, (string Status, DateTimeOffset At)> processStatus = [];
    private readonly Dictionary<(string Id, string Stream), string> partialLines = [];
    private readonly Dictionary<(string Id, string Stream), TerminalControlFilter> outputFilters = [];
    private readonly Dictionary<string, long> displayedCursors = [];
    private readonly Dictionary<string, string> taskStatus = [];
    private string progressPlan = "";
    private void PlanProgress(JsonElement data)
    {
        if (!data.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
        { MessageText("Plan revision " + Text(data, "revision"), "accent"); return; }
        var planId = Text(data, "planId");
        if (progressPlan != planId) { taskStatus.Clear(); progressPlan = planId; }
        var completed = steps.EnumerateArray().Count(s => Text(s, "status").Equals("Completed", StringComparison.OrdinalIgnoreCase));
        MessageText($"Plan · {completed}/{steps.GetArrayLength()} completed · revision {Text(data, "revision")}", "accent");
        foreach (var step in steps.EnumerateArray()) Progress(step);
        if (!Verbose) MessageText("Full steps and verification: /tasks", "dim");
    }
    private void Progress(JsonElement step)
    {
        var id = Text(step, "id"); var status = Text(step, "status"); var description = Text(step, "description");
        if (!Verbose && taskStatus.TryGetValue(id, out var old) && old == status + description) return;
        taskStatus[id] = status + description;
        var title = description.Length == 0 ? id : description;
        if (!Verbose) title = AgentSmith.Core.Activity.Clean(title, Math.Max(16, width - status.Length - 10));
        var style = status.ToLowerInvariant() switch
        {
            "completed" => "success", "in progress" => "info", "blocked" => "warning",
            "failed" => "error", _ => "activity"
        };
        MessageText("[" + status + "] " + title, style);
    }
    public static string BoundedPreview(string text, bool verbose = false)
    {
        text = Safe(text).Replace("\r\n", "\n");
        var lines = text.Split('\n'); var limit = verbose ? 32768 : 4096; var lineLimit = verbose ? 200 : 20;
        if (lines.Length <= lineLimit && Encoding.UTF8.GetByteCount(text) <= limit) return text;
        static string Clip(string s, int bytes)
        {
            var b = new StringBuilder(); var count = 0;
            foreach (var rune in s.EnumerateRunes()) { if (count + rune.Utf8SequenceLength > bytes) break; b.Append(rune.ToString()); count += rune.Utf8SequenceLength; }
            return b.ToString();
        }
        var head = Clip(string.Join('\n', lines.Take(lineLimit / 2)), (limit - 150) / 2);
        // Clip the tail from its end, retaining valid surrogate pairs.
        var tailText = string.Join('\n', lines.TakeLast(lineLimit / 2));
        var tailRunes = tailText.EnumerateRunes().ToArray(); var tail = new StringBuilder(); var used = 0;
        for (var i = tailRunes.Length - 1; i >= 0 && used + tailRunes[i].Utf8SequenceLength <= (limit - 150) / 2; i--) { tail.Insert(0, tailRunes[i].ToString()); used += tailRunes[i].Utf8SequenceLength; }
        var omitted = Math.Max(0, Encoding.UTF8.GetByteCount(text) - Encoding.UTF8.GetByteCount(head) - used);
        return head + $"\n… {omitted:N0} bytes omitted; read the retained artifact for details …\n" + tail;
    }
    private void Preview(string text) => MessageText(BoundedPreview(text, Verbose));
    private void ContentBlock(string title, string text, string? source = null, string sourceLabel = "Source", bool bounded = false)
    {
        const string prefix = "  │   │ ";
        var blockWidth = Math.Min(width, 120);
        Wrapped("┌─ " + title, "dim", "  │   ", blockWidth);
        if (!string.IsNullOrWhiteSpace(source))
        {
            Wrapped(sourceLabel + ": " + source, "dim", prefix, blockWidth);
            Line("  │   ├─", "dim");
        }
        // Fetched text is literal evidence, not Markdown from the assistant. Preserve code indentation
        // and keep a guide on every wrapped line, including empty lines and omission notices.
        var available = blockWidth - prefix.Length - 2;
        var preview = (bounded ? text : BoundedPreview(text, Verbose)).Replace("\t", "    ");
        foreach (var line in preview.Split('\n'))
            foreach (var part in Chunks(line, available))
            {
                Paint("  │", guideStyle); Paint("   │ ", "dim");
                Paint(part.PadRight(available), "content"); output.WriteLine();
            }
        Line("  │   └─ End content", "dim");
    }
    private void InspectionResult(JsonElement data)
    {
        switch (Text(data, "kind"))
        {
            case "audio-analysis":
                MessageText("Audio · " + Text(data, "path") + " · " + Text(data, "model"), "info");
                MessageText("Start " + Number(data, "startSeconds").ToString("0.0") + "s · duration " + (data.TryGetProperty("durationSeconds", out var duration) && duration.ValueKind == JsonValueKind.Number ? duration.GetDouble().ToString("0.0") + "s" : "unavailable"), "dim");
                ContentBlock("Audio analysis", Verbose ? Text(data, "analysis") : Text(data, "completion") is { Length: > 0 } completion ? completion : Text(data, "summary") is { Length: > 0 } summary ? summary : "Analysis retained; see the assistant response or artifact.");
                if (Flag(data, "partial")) MessageText("Selected clip only; the rest of the file was not analyzed.", "dim");
                if (Flag(data, "truncated")) MessageText("Audio model response reached its output limit.", "status-warning");
                MessageText("Retained analysis: /artifact " + Text(data, "artifactId"), "dim"); break;
            case "web-image":
            case "workspace-image":
                var imageSource = Text(data, "path");
                if (Uri.TryCreate(Text(data, "url"), UriKind.Absolute, out var imageUrl)) imageSource = imageUrl.GetLeftPart(UriPartial.Path);
                MessageText(imageSource + " · " + Text(data, "width") + " × " + Text(data, "height") + " · " + Text(data, "bytes") + " bytes", "activity");
                MessageText("Image attachment: " + Text(data, "attachmentId"), "dim"); break;
            case "workspace-map":
                MessageText("Workspace map · " + Text(data, "totalEntries") + " entries · " + Text(data, "path"), "status-neutral");
                if (data.TryGetProperty("entries", out var entries))
                {
                    var listing = ShortenListing(entries.EnumerateArray().Select(e => Text(e, "path").Replace('\\', '/') + (Text(e, "kind") == "directory" ? "/" : "")).ToArray(), absoluteOnly: false);
                    ContentBlock("Workspace entries", string.Join('\n', listing.Lines), listing.Directory, "Directory");
                }
                MapLimits(data);
                MessageText("Retained map: /artifact " + Text(data, "artifactId"), "dim"); break;
            case "http-response":
                MessageText("HTTP " + Text(data, "status") + " · " + Text(data, "method") + " " + Text(data, "url"), "status-neutral");
                ContentBlock("Response body", Text(data, "excerpt"));
                foreach (var field in new[] { "truncated", "incomplete", "unsupportedContent", "unsupportedEncoding", "redacted" })
                    if (Flag(data, field)) MessageText("HTTP response: " + field, "status-warning");
                MessageText("Response: /artifact " + Text(data, "artifactId"), "dim"); break;
        }
    }
    private void WebResult(JsonElement data)
    {
        if (data.TryGetProperty("results", out var results))
        {
            MessageText($"Web search · {results.GetArrayLength()} results", "status-neutral");
            foreach (var result in results.EnumerateArray())
            {
                MessageText(Text(result, "title"), "accent");
                MessageText(Text(result, "url"), "info");
                MessageText(Text(result, "snippet"), "dim");
            }
        }
        else
        {
            var title = Text(data, "title"); var url = Text(data, "url");
            if (Uri.TryCreate(url, UriKind.Absolute, out var source) && (title.Length == 0 || title == source.Host))
                title = source.Segments.LastOrDefault()?.Trim('/') is { Length: > 0 } file ? file : source.Host;
            ContentBlock("Fetched content" + (title.Length > 0 ? " · " + title : ""), Text(data, "text"), url);
            MessageText("Retained page: /artifact " + Text(data, "artifactId"), "dim");
            if (Flag(data, "truncated")) MessageText("Retained page text was truncated.", "status-warning");
        }
    }
    private void DiffPreview(string text)
    {
        foreach (var line in BoundedPreview(text, Verbose).Split('\n'))
            MessageText(line, TerminalTheme.DiffStyle(line));
    }
    private void ProcessOutput(JsonElement data)
    {
        var id = Text(data, "processId"); var state = Text(data, "state"); var label = Text(data, "alias");
        if (label.Length == 0) label = id;
        var status = state + ":" + Text(data, "exitCode") + ":" + Text(data, "reason") + ":" + Text(data, "truncated");
        var lines = new List<(string Stream, string Text)>();
        if (data.TryGetProperty("output", out var chunks)) foreach (var chunk in chunks.EnumerateArray())
        {
            if (chunk.TryGetProperty("cursor", out var chunkCursor) && chunkCursor.TryGetInt64(out var sequence))
            {
                if (sequence <= displayedCursors.GetValueOrDefault(id)) continue;
                displayedCursors[id] = sequence;
            }
            var stream = Text(chunk, "stream"); var key = (id, stream);
            if (!outputFilters.TryGetValue(key, out var filter)) outputFilters[key] = filter = new();
            var text = partialLines.GetValueOrDefault(key, "") + filter.Append(Text(chunk, "text"));
            var parts = text.Split('\n');
            foreach (var line in parts.SkipLast(1)) lines.Add((stream, line.TrimEnd('\r')));
            var remaining = parts[^1];
            if (remaining.Length > 4096) { lines.Add((stream, remaining + " [partial]")); remaining = ""; }
            partialLines[key] = remaining;
        }
        if (state is "Exited" or "Stopped" or "Failed" or "TimedOut" or "Cancelled" or "Interrupted" && !Flag(data, "hasMoreOutput"))
        {
            foreach (var key in partialLines.Keys.Where(k => k.Id == id).ToArray())
            { if (partialLines[key].Length > 0) lines.Add((key.Stream, partialLines[key] + " [partial]")); partialLines.Remove(key); }
            foreach (var key in outputFilters.Keys.Where(k => k.Id == id).ToArray()) outputFilters.Remove(key);
        }
        var now = clock.GetUtcNow();
        if (!Verbose && lines.Count == 0 && processStatus.TryGetValue(id, out var old) && old.Status == status && now - old.At < TimeSpan.FromSeconds(30)) return;
        processStatus[id] = (status, now);
        var duration = data.TryGetProperty("durationMilliseconds", out var ms) ? $" · process {TerminalDuration.Format(TimeSpan.FromMilliseconds(ms.GetDouble()))}" : "";
        var statusStyle = state switch
        {
            "Running" or "Prepared" => "status-running",
            "Failed" => "status-error",
            "Exited" when Text(data, "exitCode").Length > 0 && Text(data, "exitCode") != "0" => "status-error",
            "Exited" when Text(data, "exitCode") == "0" && Flag(data, "terminationConfirmed") => "status-success",
            "Stopped" or "Interrupted" or "TimedOut" or "Cancelled" or "Exited" => "status-warning",
            _ => "status-neutral"
        };
        if (lines.Count > 0)
            ProcessBlocks(lines, label);
        StatusLine(state.Length > 0 ? state : "Process", "Process " + label + duration + (Text(data, "exitCode").Length > 0 ? " · exit " + Text(data, "exitCode") : ""), statusStyle);
        if (state is "Exited" or "Stopped" && !Flag(data, "terminationConfirmed")) MessageText("Process tree termination is unconfirmed.", "status-warning");
        if (Text(data, "reason").Length > 0) MessageText(Text(data, "reason"), "status-warning");
        if (Flag(data, "truncated")) MessageText("Retained output is truncated.", "status-warning");
        if (Flag(data, "hasMoreOutput")) MessageText("More retained output: cursor " + Text(data, "cursor"), "dim");
        if (lines.Count > 0 || Flag(data, "hasMoreOutput") || Flag(data, "truncated")) MessageText("Retained log: /artifact process:" + label, "dim");
    }
}
