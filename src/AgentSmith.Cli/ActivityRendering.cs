using System.Text;
using System.Text.Json;

namespace AgentSmith.Cli;

public sealed partial class TerminalRenderer
{
    private readonly Dictionary<string, (string Status, DateTimeOffset At)> processStatus = [];
    private readonly Dictionary<(string Id, string Stream), string> partialLines = [];
    private readonly Dictionary<string, string> taskStatus = [];
    private void Progress(JsonElement step)
    {
        var id = Text(step, "id"); var status = Text(step, "status"); var description = Text(step, "description");
        if (!Verbose && taskStatus.TryGetValue(id, out var old) && old == status + description) return;
        taskStatus[id] = status + description;
        MessageText("[" + status + "] " + (description.Length == 0 ? id : description));
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
    private void ProcessOutput(JsonElement data)
    {
        var id = Text(data, "processId"); var state = Text(data, "state"); var label = Text(data, "alias");
        if (label.Length == 0) label = id;
        var status = state + ":" + Text(data, "exitCode") + ":" + Text(data, "reason") + ":" + Text(data, "truncated");
        var lines = new List<(string Stream, string Text)>();
        if (data.TryGetProperty("output", out var chunks)) foreach (var chunk in chunks.EnumerateArray())
        {
            var stream = Text(chunk, "stream"); var key = (id, stream);
            var text = partialLines.GetValueOrDefault(key, "") + Text(chunk, "text");
            var parts = text.Split('\n');
            foreach (var line in parts.SkipLast(1)) lines.Add((stream, line.TrimEnd('\r')));
            var remaining = parts[^1];
            if (remaining.Length > 4096) { lines.Add((stream, remaining + " [partial]")); remaining = ""; }
            partialLines[key] = remaining;
        }
        if (state is not ("Running" or "Prepared" or ""))
            foreach (var key in partialLines.Keys.Where(k => k.Id == id).ToArray())
            { if (partialLines[key].Length > 0) lines.Add((key.Stream, partialLines[key] + " [partial]")); partialLines.Remove(key); }
        var now = clock.GetUtcNow();
        if (!Verbose && lines.Count == 0 && processStatus.TryGetValue(id, out var old) && old.Status == status && now - old.At < TimeSpan.FromSeconds(30)) return;
        processStatus[id] = (status, now);
        var duration = data.TryGetProperty("durationMilliseconds", out var ms) ? $" · process {ms.GetDouble() / 1000:0.0}s" : "";
        MessageText("Process " + label + " · " + state + duration + (Text(data, "exitCode").Length > 0 ? " · exit " + Text(data, "exitCode") : ""), "dim");
        if (lines.Count > 0)
        {
            var block = new StringBuilder(); string? last = null;
            foreach (var line in lines) { if (line.Stream != last) { block.AppendLine(line.Stream); last = line.Stream; } block.AppendLine("  " + line.Text); }
            Preview(block.ToString().TrimEnd('\n', '\r'));
        }
        if (state is "Exited" or "Stopped" && !Flag(data, "terminationConfirmed")) MessageText("Process tree termination is unconfirmed.", "warning");
        if (Text(data, "reason").Length > 0) MessageText(Text(data, "reason"), "warning");
        if (Flag(data, "truncated")) MessageText("Retained output is truncated.", "warning");
        if (Flag(data, "hasMoreOutput")) MessageText("More retained output: cursor " + Text(data, "cursor"), "dim");
        if (lines.Count > 0) MessageText("Log: /artifacts · process " + label, "dim");
    }
}
