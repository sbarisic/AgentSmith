using System.Text;
using System.Text.RegularExpressions;

namespace AgentSmith.Cli;

public sealed partial class MarkdownWriter
{
    // Only a candidate header and the current row are buffered. Column widths stay stable
    // after the first data row, so even very large streamed tables need no whole-table buffer.
    private string? tableCandidate;
    private string[]? tableHeaders;
    private int[]? tableWidths;
    private int[]? tableAlignment;
    private bool stackedTable;
    private bool tableHasRows;
    private static readonly Regex TableDelimiter = new(@"^:?-{3,}:?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private bool TryRenderTableLine(string source)
    {
        var row = SplitTableRow(source.Replace("\t", "    "));
        if (tableHeaders is not null)
        {
            if (row is not null && row.Length == tableHeaders.Length)
            {
                StartTable(row);
                RenderTableRow(row);
                return true;
            }
            FlushTable();
        }
        if (tableCandidate is not null)
        {
            var header = SplitTableRow(tableCandidate)!;
            if (row is not null && row.Length == header.Length && row.All(cell => TableDelimiter.IsMatch(cell)))
            {
                tableHeaders = header;
                tableAlignment = row.Select(cell => cell.EndsWith(':') ? cell.StartsWith(':') ? 0 : 1 : -1).ToArray();
                tableCandidate = null;
                return true;
            }
            var previous = tableCandidate;
            tableCandidate = null;
            RenderMarkdownLine(previous, false);
        }
        // Leave block syntax to the ordinary renderer, including opening code fences.
        var trimmed = source.TrimStart();
        if (row is null || trimmed.StartsWith('#') || trimmed.StartsWith('>') ||
            trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)) return false;
        tableCandidate = source.Replace("\t", "    ");
        return true;
    }

    private static string[]? SplitTableRow(string source)
    {
        var text = source.Trim();
        var parts = new List<string>();
        var cell = new StringBuilder();
        var codeTicks = 0;
        var separators = 0;
        var leadingPipe = text.StartsWith('|');
        var trailingPipe = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] is '|' or '\\')
            {
                cell.Append(text[++i]);
                trailingPipe = false;
                continue;
            }
            if (c == '`')
            {
                var count = 1;
                while (i + count < text.Length && text[i + count] == '`') count++;
                if (codeTicks == 0) codeTicks = count;
                else if (codeTicks == count) codeTicks = 0;
                cell.Append('`', count); i += count - 1;
            }
            else if (c == '|' && codeTicks == 0)
            {
                parts.Add(cell.ToString().Trim()); cell.Clear(); separators++;
                if (parts.Count > 17) return null;
                trailingPipe = i == text.Length - 1;
                continue;
            }
            else cell.Append(c);
            trailingPipe = false;
        }
        if (separators == 0) return null;
        parts.Add(cell.ToString().Trim());
        if (trailingPipe) parts.RemoveAt(parts.Count - 1);
        if (leadingPipe) parts.RemoveAt(0);
        return parts.Count is > 0 and <= 16 ? parts.ToArray() : null;
    }

    private void StartTable(string[] firstRow)
    {
        if (tableWidths is not null) return;
        var headers = tableHeaders!;
        // Match the ordinary renderer's four-column guide and spare terminal column.
        var available = Math.Max(4, width - 5);
        var contentBudget = available - 3 * headers.Length - 1;
        stackedTable = contentBudget < headers.Length * 10;
        tableWidths = Enumerable.Repeat(10, headers.Length).ToArray();
        if (stackedTable) return;
        var desired = headers.Select((header, i) => Math.Max(10,
            Math.Max(InlineCells(header).Sum(c => c.Width), InlineCells(firstRow[i]).Sum(c => c.Width)))).ToArray();
        var remaining = contentBudget - tableWidths.Sum();
        // Give short columns their natural width, then use the remaining space for prose.
        while (remaining > 0)
        {
            var changed = false;
            for (var i = 0; i < tableWidths.Length && remaining > 0; i++)
                if (tableWidths[i] < desired[i]) { tableWidths[i]++; remaining--; changed = true; }
            if (!changed) break;
        }
        TableRule('┌', '┬', '┐');
        WriteTableCells(headers, header: true);
        TableRule('├', '┼', '┤');
    }

    private void RenderTableRow(string[] row)
    {
        if (stackedTable)
        {
            if (tableHasRows) WriteLine([], "  │ ", "  │ ", false);
            for (var i = 0; i < row.Length; i++)
            {
                WriteLine(InlineCells(tableHeaders![i], "heading"), "  │ ", "  │ ", true);
                WriteLine(InlineCells(row[i]), "  │   ", "  │   ", true);
            }
        }
        else
        {
            if (tableHasRows) TableRule('├', '┼', '┤');
            WriteTableCells(row, header: false);
        }
        tableHasRows = true;
    }

    private void WriteTableCells(string[] row, bool header)
    {
        var columns = row.Select((text, i) => WrapTableCell(InlineCells(text, header ? "heading" : ""), tableWidths![i])).ToArray();
        var height = columns.Max(column => column.Count);
        for (var line = 0; line < height; line++)
        {
            Paint("  │ ", guideStyle); Paint("│", "dim");
            for (var i = 0; i < columns.Length; i++)
            {
                List<Cell> cells = line < columns[i].Count ? columns[i][line] : [];
                var padding = tableWidths![i] - cells.Sum(c => c.Width);
                var left = tableAlignment![i] switch { 1 => padding, 0 => padding / 2, _ => 0 };
                Paint(new string(' ', left + 1), "");
                for (var start = 0; start < cells.Count;)
                {
                    var style = cells[start].Style;
                    var run = new StringBuilder();
                    do { run.Append(cells[start++].Text); } while (start < cells.Count && cells[start].Style == style);
                    Paint(run.ToString(), style);
                }
                Paint(new string(' ', padding - left + 1), ""); Paint("│", "dim");
            }
            output.WriteLine();
        }
    }

    private static List<List<Cell>> WrapTableCell(List<Cell> cells, int available)
    {
        var lines = new List<List<Cell>>();
        var offset = 0;
        do
        {
            var end = offset; var used = 0; var space = -1;
            while (end < cells.Count && used + cells[end].Width <= available)
            { if (cells[end].Text == " ") space = end; used += cells[end].Width; end++; }
            if (end < cells.Count && space > offset) end = space;
            lines.Add(cells.GetRange(offset, end - offset));
            offset = end;
            while (offset < cells.Count && cells[offset].Text == " ") offset++;
        } while (offset < cells.Count);
        return lines;
    }

    private void TableRule(char left, char middle, char right)
    {
        Paint("  │ ", guideStyle);
        Paint(left + string.Join(middle.ToString(), tableWidths!.Select(n => new string('─', n + 2))) + right, "dim");
        output.WriteLine();
    }

    private void FlushTable()
    {
        if (tableCandidate is not null)
        {
            var previous = tableCandidate; tableCandidate = null;
            RenderMarkdownLine(previous, false);
        }
        if (tableHeaders is not null)
        {
            StartTable(tableHeaders);
            if (!stackedTable) TableRule('└', '┴', '┘');
            else if (!tableHasRows)
                foreach (var header in tableHeaders) WriteLine(InlineCells(header, "heading"), "  │ ", "  │ ", true);
        }
        tableHeaders = null; tableWidths = null; tableAlignment = null;
        stackedTable = false; tableHasRows = false;
    }
}
