using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentSmith.Cli;

/// <summary>A bounded, line-streaming Markdown subset for terminals. Never emits model-supplied escapes.</summary>
public sealed class MarkdownWriter(TextWriter output, bool color, int width, bool enabled = true, string guideStyle = "dim")
{
    private readonly StringBuilder pending = new();
    private char fence;
    private int fenceLength;
    private bool diffFence;
    private bool longLine;
    private static readonly Regex Inline = new(@"`([^`]+)`|\*\*(.+?)\*\*|__(.+?)__|\*([^*]+)\*|\[([^\]]+)\]\(([^)]+)\)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Heading = new(@"^ {0,3}#{1,6}\s+(.+)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex List = new(@"^(\s*)([-+*]|\d+[.)])\s+(.*)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly record struct Cell(string Text, string Style, int Width);

    public void Append(string text)
    {
        foreach (var c in TerminalRenderer.Safe(text))
        {
            if (c == '\n') { RenderLine(pending.ToString(), longLine); pending.Clear(); longLine = false; }
            else
            {
                pending.Append(c);
                // Keep pathological unterminated lines bounded. Preserve their text without parsing fragments.
                if (pending.Length >= 16384 && !char.IsHighSurrogate(c))
                { RenderLine(pending.ToString(), true); pending.Clear(); longLine = true; }
            }
        }
        output.Flush();
    }

    public void Finish()
    {
        if (pending.Length > 0) RenderLine(pending.ToString(), longLine);
        pending.Clear(); longLine = false;
        if (fence != '\0') WriteLine([new("└─", "dim", 2)], "  │ ", "  │ ", false);
        fence = '\0'; fenceLength = 0; diffFence = false;
        output.Flush();
    }

    private void RenderLine(string source, bool literal)
    {
        var line = source.Replace("\t", "    ");
        if (!enabled) { WriteLine(Cells(line, ""), "  │ ", "  │ ", false); return; }
        var trim = line.TrimStart();
        var marker = trim.Length > 0 ? trim[0] : '\0';
        var count = marker is '`' or '~' ? trim.TakeWhile(c => c == marker).Count() : 0;
        if (!literal && count >= 3 && (fence == '\0' || marker == fence && count >= fenceLength && string.IsNullOrWhiteSpace(trim[count..])))
        {
            if (fence == '\0')
            {
                fence = marker; fenceLength = count;
                diffFence = trim[count..].Trim().Equals("diff", StringComparison.OrdinalIgnoreCase) || trim[count..].Trim().Equals("patch", StringComparison.OrdinalIgnoreCase);
                WriteLine(Cells("┌─ " + (trim[count..].Trim() is { Length: > 0 } language ? language : "code"), "dim"), "  │ ", "  │ ", false);
            }
            else { fence = '\0'; fenceLength = 0; diffFence = false; WriteLine(Cells("└─", "dim"), "  │ ", "  │ ", false); }
            return;
        }
        if (fence != '\0') { WriteLine(Cells(line, diffFence ? TerminalTheme.DiffStyle(line) : "code"), "  │   ", "  │   ", false); return; }
        if (literal) { WriteLine(Cells(line, ""), "  │ ", "  │ ", false); return; }
        var heading = Heading.Match(line);
        if (heading.Success) { WriteLine(InlineCells(heading.Groups[1].Value, "heading"), "  │ ", "  │ ", true); return; }
        if (trim is "---" or "***" or "___") { WriteLine(Cells(new string('─', Math.Max(1, Math.Min(40, width - 6))), "dim"), "  │ ", "  │ ", false); return; }
        var prefix = "  │ ";
        if (trim.StartsWith('>'))
        {
            line = trim;
            var depth = 0;
            while (line.StartsWith('>')) { depth++; line = line[1..].TrimStart(); }
            prefix += new string('▌', Math.Min(depth, 3)) + " ";
        }
        var list = List.Match(line);
        if (list.Success)
        {
            var indent = new string(' ', Math.Min(list.Groups[1].Length, 8));
            var bullet = list.Groups[2].Value;
            if (bullet is "-" or "+" or "*") bullet = "•";
            WriteLine(InlineCells(list.Groups[3].Value), prefix + indent + bullet + " ", prefix + indent + new string(' ', bullet.Length + 1), true);
        }
        else WriteLine(InlineCells(line), prefix, prefix, true);
    }

    private static List<Cell> Cells(string text, string style)
    {
        var cells = new List<Cell>();
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var rune = Rune.GetRuneAt(element, 0);
            var n = rune.Value;
            var wide = n is >= 0x1100 and <= 0x115f or >= 0x2e80 and <= 0xa4cf or >= 0xac00 and <= 0xd7a3
                or >= 0xf900 and <= 0xfaff or >= 0xfe10 and <= 0xfe6f or >= 0xff01 and <= 0xff60 or >= 0x1f300 and <= 0x1faff or >= 0x20000;
            cells.Add(new(element, style, wide || element.Contains('\ufe0f') ? 2 : 1));
        }
        return cells;
    }

    private static List<Cell> InlineCells(string text, string style = "")
    {
        var result = new List<Cell>(); var position = 0;
        foreach (Match match in Inline.Matches(text))
        {
            result.AddRange(Cells(text[position..match.Index], style));
            if (match.Groups[1].Success) result.AddRange(Cells(match.Groups[1].Value, "code"));
            else if (match.Groups[2].Success || match.Groups[3].Success) result.AddRange(Cells(match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value, "bold"));
            else if (match.Groups[4].Success) result.AddRange(Cells(match.Groups[4].Value, "italic"));
            else result.AddRange(Cells(match.Groups[5].Value + " (" + match.Groups[6].Value + ")", "link"));
            position = match.Index + match.Length;
        }
        result.AddRange(Cells(text[position..], style));
        return result;
    }

    private void WriteLine(List<Cell> cells, string firstPrefix, string continuation, bool wordWrap)
    {
        var offset = 0; var prefix = firstPrefix;
        do
        {
            var available = Math.Max(4, width - prefix.Length - 1);
            var end = offset; var used = 0; var space = -1;
            while (end < cells.Count && used + cells[end].Width <= available)
            { if (cells[end].Text == " ") space = end; used += cells[end].Width; end++; }
            if (wordWrap && end < cells.Count && space > offset) end = space;
            Paint(prefix, guideStyle);
            for (var i = offset; i < end;)
            {
                var style = cells[i].Style;
                var run = new StringBuilder();
                do { run.Append(cells[i++].Text); } while (i < end && cells[i].Style == style);
                Paint(run.ToString(), style);
            }
            output.WriteLine();
            offset = end;
            if (wordWrap) while (offset < cells.Count && cells[offset].Text == " ") offset++;
            prefix = continuation;
        } while (offset < cells.Count);
    }

    private void Paint(string text, string style)
    {
        if (color) output.Write(TerminalTheme.Style(style));
        output.Write(text);
        if (color) output.Write(TerminalTheme.Reset);
    }
}
