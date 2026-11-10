using System.Windows;
using System.Windows.Documents;
using AgentSmith.Application;
using AgentSmith.Tools;

namespace AgentSmith.Desktop;

internal static class PresentationSmokeCheck
{
    internal static void Run()
    {
        if (TranscriptActions.For(null) != new TranscriptActions(true, false, false, false)) throw new InvalidOperationException("New recording action is ambiguous.");
        var job = new RecordingAnalysisJob { State = "CompletedWithGaps", Stage = "Done", Prepared = true,
            Chunks = [new() { Components = new() { ["transcription"] = "Succeeded", ["diarization"] = "Succeeded" } }],
            Coverage = [new(0, 160, "Missing")] };
        if (TranscriptActions.For(job) != new TranscriptActions(false, false, false, false)) throw new InvalidOperationException("Audio gaps offered redundant transcription.");
        job.State = "Paused"; job.Stage = "Inference"; job.Chunks[0].Components["transcription"] = "Pending";
        if (!TranscriptActions.For(job).Resume) throw new InvalidOperationException("Pending work cannot resume.");
        job.Chunks[0].Components["transcription"] = "Failed"; job.Chunks[0].Components["diarization"] = "Uncertain";
        if (TranscriptActions.For(job) != new TranscriptActions(false, false, true, true)) throw new InvalidOperationException("Retry actions were combined.");
        job.Stage = "Summary"; job.Chunks[0].Components["transcription"] = job.Chunks[0].Components["diarization"] = "Succeeded";
        if (TranscriptActions.For(job).Resume) throw new InvalidOperationException("Paused summary offered transcription resume.");

        const string report = "# Recording analysis\n\nAnalysis: 0123456789abcdef0123456789abcdef\n\nTranscribed 17 of 18 track-seconds. Consult coverage.json for details.\n\n- **A decision.** [callc000000s00000 00:01–00:04]\n\nGenerated interpretation, not independently verified fact.\n";
        double? sought = null;
        var document = SummaryDocument.Create(report, [new TranscriptLine("callc000000s00000", 1, 4, "Speaker 1", "Alex", "Evidence", "call")], s => sought = s);
        var visible = new TextRange(document.ContentStart, document.ContentEnd).Text;
        if (visible.Contains("#") || visible.Contains("Analysis:") || visible.Contains("coverage.json") || visible.Contains("callc000000s00000"))
            throw new InvalidOperationException("Summary leaked export syntax or identifiers.");
        if (document.Blocks.Count != 1 || document.FontSize < 14)
            throw new InvalidOperationException("Short summary was buried in export metadata or displayed too small.");
        var paragraph = document.Blocks.OfType<Paragraph>().Single();
        if (!paragraph.Inlines.OfType<Bold>().Any()) throw new InvalidOperationException("Summary emphasis was not rendered.");
        var citation = paragraph.Inlines.OfType<Hyperlink>().Single();
        citation.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        if (sought != 1 || citation.ToolTip?.ToString()?.Contains("Alex") != true)
            throw new InvalidOperationException("Summary citation did not seek to the exact source.");
        var longer = SummaryDocument.Create("## Decisions\n\n- First point.\n\n- Second point.", [], _ => { });
        if (longer.Blocks.Count != 2 || longer.Blocks.FirstBlock is not Paragraph { FontSize: 14 } ||
            longer.Blocks.OfType<System.Windows.Documents.List>().Single().ListItems.Count != 2)
            throw new InvalidOperationException("Longer summary lost its section heading or bullet list.");
    }
}
