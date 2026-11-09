using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using AgentSmith.Application;

namespace AgentSmith.Desktop;

internal static class SummaryDocument
{
    internal static FlowDocument Create(string markdown, IReadOnlyList<TranscriptLine> transcript, Action<double> seek)
    {
        var document = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 17,
            PagePadding = new Thickness(0), LineHeight = 26, TextAlignment = TextAlignment.Left };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "TextFillColorPrimaryBrush");
        var lines = markdown.Replace("\r", "").Split('\n');
        var singlePoint = lines.Count(l => Regex.IsMatch(l.Trim(), @"^(?:[-*]|\d+\.)\s+")) == 1;
        System.Windows.Documents.List? list = null;
        foreach (var source in lines)
        {
            var line = source.Trim();
            if (line.Length == 0) continue;
            // Export metadata belongs in Details & export, not the reading surface.
            if (line == "# Recording analysis" || Regex.IsMatch(line, @"^Analysis: [a-fA-F0-9]{32}$") ||
                line.StartsWith("Transcribed ", StringComparison.Ordinal) && line.Contains("Consult coverage.json", StringComparison.Ordinal)) continue;
            if (line.StartsWith("Generated interpretation, not independently verified fact.", StringComparison.Ordinal)) continue;
            var heading = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
            var bullet = Regex.Match(line, @"^(?:[-*]|\d+\.)\s+(.+)$");
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            if (heading.Success)
            {
                line = heading.Groups[2].Value;
                if (line == "Recording analysis") line = "Summary";
                paragraph.FontSize = heading.Groups[1].Length == 1 ? 20 : 16;
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.Margin = new Thickness(0, 4, 0, 12);
            }
            else if (bullet.Success) line = bullet.Groups[1].Value;
            var cursor = 0;
            foreach (Match match in Regex.Matches(line, @"\[[^\]\r\n]+\]|\*\*[^*]+\*\*"))
            {
                paragraph.Inlines.Add(line[cursor..match.Index]);
                if (match.Value.StartsWith("**", StringComparison.Ordinal))
                    paragraph.Inlines.Add(new Bold(new Run(match.Value[2..^2])));
                else
                {
                    var id = match.Value[1..^1].Split(' ', 2)[0];
                    var row = transcript.FirstOrDefault(r => r.Id == id);
                    if (row is null) paragraph.Inlines.Add(match.Value);
                    else
                    {
                        var label = TimeSpan.FromSeconds(row.Start).ToString(row.Start < 3600 ? @"mm\:ss" : @"hh\:mm\:ss");
                        var link = new Hyperlink(new Run(label)) { ToolTip = "Play source · " + row.Speaker + " · " + row.Time + "–" + TimeSpan.FromSeconds(row.End).ToString(@"hh\:mm\:ss") };
                        link.SetResourceReference(Hyperlink.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
                        link.Click += (_, _) => seek(row.Start);
                        paragraph.Inlines.Add(link);
                    }
                }
                cursor = match.Index + match.Length;
            }
            paragraph.Inlines.Add(line[cursor..]);
            if (bullet.Success && !heading.Success && !singlePoint)
            {
                if (list is null)
                {
                    list = new System.Windows.Documents.List { MarkerStyle = TextMarkerStyle.Disc, Padding = new Thickness(20, 0, 0, 0), Margin = new Thickness(0) };
                    document.Blocks.Add(list);
                }
                list.ListItems.Add(new ListItem(paragraph));
            }
            else { list = null; document.Blocks.Add(paragraph); }
        }
        return document;
    }
}
