using System.Text;
using System.Text.Json;

namespace AgentSmith.Cli;

public sealed partial class TerminalRenderer
{
    // Only shorten a block consisting entirely of paths. Never replace a path prefix
    // inside prose, diagnostics or commands. The retained artifact stays unchanged.
    private static (string? Directory, string[] Lines) ShortenListing(string[] original, bool absoluteOnly = true)
    {
        var paths = original.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        if (absoluteOnly && paths.Count >= 4 && paths[0] == "FullName" && paths[1].All(c => c == '-')) paths.RemoveRange(0, 2);
        static bool Absolute(string path) => path.StartsWith('/') || path.StartsWith("\\\\", StringComparison.Ordinal)
            || path.Length > 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\';
        if (paths.Count < 2 || absoluteOnly && paths.Any(p => !Absolute(p))) return (null, original);
        var normalized = paths.Select(p => p.Replace('\\', '/')).ToArray();
        if (normalized.Any(p => !p.Contains('/'))) return (null, original);
        var comparison = paths.All(p => p.Length > 1 && p[1] == ':' || p.StartsWith("\\\\", StringComparison.Ordinal))
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var common = normalized[0][..(normalized[0].LastIndexOf('/') + 1)];
        foreach (var path in normalized)
        {
            while (common.Length > 0 && (!path.StartsWith(common, comparison) || path.Length == common.Length))
            {
                var parent = common.TrimEnd('/').LastIndexOf('/');
                common = parent < 0 ? "" : common[..(parent + 1)];
            }
        }
        if (common.Length == 0) return (null, original);
        return (common, normalized.Select(p => p[common.Length..]).ToArray());
    }

    private void MapLimits(JsonElement data)
    {
        var reasons = new List<string>();
        if (Flag(data, "depthLimited")) reasons.Add(Text(data, "depth").Length > 0 ? "depth limit " + Text(data, "depth") + " reached" : "depth limit reached");
        if (Flag(data, "entryScanLimited")) reasons.Add("10,000-entry scan limit reached");
        if (Flag(data, "sizeLimited")) reasons.Add("262,144-character path budget reached");
        if (Flag(data, "scanLimited") && !Flag(data, "entryScanLimited") && !Flag(data, "sizeLimited")) reasons.Add("scan limit reached (older result does not identify which limit)");
        if (reasons.Count > 0) MessageText("Partial scan: " + string.Join("; ", reasons) + ". Narrow the requested path to inspect deeper subtrees.", "status-warning");
        if (Number(data, "omittedEntries") > 0) MessageText(Text(data, "omittedEntries") + " scanned entries omitted from this preview; use the retained map below.", "dim");
        if (Number(data, "excluded") > 0) MessageText(Text(data, "excluded") + " entries excluded by workspace boundaries, exclusions or link checks.", "dim");
        if (data.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array && issues.GetArrayLength() > 0)
        {
            MessageText("Scan issues (coverage may be incomplete):", "warning");
            foreach (var issue in issues.EnumerateArray().Take(3)) MessageText("  " + AgentSmith.Core.Activity.Clean(issue.GetString() ?? "", 180), "warning");
            if (Number(data, "omittedIssues") > 0 || issues.GetArrayLength() > 3) MessageText("More issues in the retained map.", "dim");
        }
        else if (Number(data, "omittedIssues") > 0) MessageText("Scan issues were omitted from the preview; inspect the retained map for coverage gaps.", "warning");
    }

    private sealed record OutputGroup(string Stream, string? Directory, string[] Lines);
    private sealed record OutputRow(int Group, int Index, string Text);
    private void ProcessBlocks(List<(string Stream, string Text)> lines, string processLabel)
    {
        var groups = new List<OutputGroup>();
        for (var i = 0; i < lines.Count;)
        {
            var stream = lines[i].Stream; var start = i++;
            while (i < lines.Count && lines[i].Stream == stream) i++;
            var listing = ShortenListing(lines.GetRange(start, i - start).Select(l => l.Text).ToArray());
            groups.Add(new(stream, listing.Directory, listing.Lines));
        }
        var rows = groups.SelectMany((g, index) => g.Lines.Select(l => (Group: index, Text: l))).Select((r, index) => new OutputRow(r.Group, index, r.Text)).ToList();
        var byteLimit = Verbose ? 32768 : 4096; var lineLimit = Verbose ? 200 : 20;
        var totalBytes = rows.Sum(r => Encoding.UTF8.GetByteCount(r.Text) + 1);
        var selected = rows;
        var clipped = rows.Count > lineLimit || totalBytes > byteLimit;
        var breakAfter = -1;
        if (clipped)
        {
            selected = [];
            void Add(IEnumerable<OutputRow> items, bool tail)
            {
                var budget = byteLimit / 2;
                var part = new List<OutputRow>();
                foreach (var original in items)
                {
                    if (budget <= 1) break;
                    var row = original;
                    if (tail && selected.FirstOrDefault(r => r.Index == row.Index) is { } head)
                    {
                        if (head.Text.Length == row.Text.Length) continue;
                        row = row with { Text = row.Text[head.Text.Length..] };
                    }
                    var runes = row.Text.EnumerateRunes().ToArray();
                    var text = new StringBuilder(); var used = 0;
                    foreach (var rune in tail ? runes.Reverse() : runes.AsEnumerable())
                    {
                        if (used + rune.Utf8SequenceLength > budget - 1) break;
                        if (tail) text.Insert(0, rune.ToString()); else text.Append(rune.ToString());
                        used += rune.Utf8SequenceLength;
                    }
                    part.Add(row with { Text = text.ToString() }); budget -= used + 1;
                }
                if (tail) part.Reverse();
                selected.AddRange(part);
            }
            var headCount = Math.Min(rows.Count, lineLimit / 2);
            Add(rows.Take(headCount), false);
            breakAfter = selected.Count;
            Add(rows.TakeLast(lineLimit - headCount).Reverse(), true);
        }
        for (var i = 0; i < selected.Count;)
        {
            var start = i++; var group = selected[start].Group;
            while (i < selected.Count && selected[i].Group == group && i != breakAfter) i++;
            var source = groups[group];
            ContentBlock(source.Stream + " · process " + processLabel + (clipped ? " · excerpt" : ""),
                string.Join('\n', selected.GetRange(start, i - start).Select(r => r.Text)), source.Directory, "Directory", bounded: true);
            if (i == breakAfter && i < selected.Count) MessageText("  … middle output omitted …", "dim");
        }
        if (clipped)
        {
            var omittedBytes = Math.Max(0, totalBytes - selected.Sum(r => Encoding.UTF8.GetByteCount(r.Text) + 1));
            MessageText($"Preview omitted {rows.Count - selected.Select(r => r.Index).Distinct().Count():N0} whole lines / {omittedBytes:N0} display bytes · /artifact process:{processLabel}", "dim");
        }
    }
}
