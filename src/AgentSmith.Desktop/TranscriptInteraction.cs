using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentSmith.Application;

namespace AgentSmith.Desktop;

public sealed class TranscriptRow : INotifyPropertyChanged
{
    public TranscriptLine Line { get; }
    public TranscriptRow(TranscriptLine line, string disclosure) { Line = line; TranslationDisclosure = disclosure; }
    public string Id => Line.Id;
    public double Start => Line.Start;
    public double End => Line.End;
    public string SpeakerId => Line.SpeakerId;
    public string Speaker => Line.Speaker;
    public string Text => Line.Text;
    public string Track => Line.Track;
    public string DisplayTime => "[" + TimeLabel(Start) + "]";
    public string Details => $"{TimeLabel(Start)} – {TimeLabel(End)} · {End - Start:0.000} s · {(Track == "mic" ? "Microphone" : Track == "call" ? "Call output" : "Audio")}";
    public string TranslationDisclosure { get; }
    public string TranslationIdentity { get; init; } = "";
    public static string TimeLabel(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"hh\:mm\:ss\.fff" : @"mm\:ss\.fff");
    private bool playing;
    public bool IsPlaying { get => playing; set { if (playing == value) return; playing = value; PropertyChanged?.Invoke(this, new(nameof(IsPlaying))); } }
    private string translation = "";
    public string Translation { get => translation; set { translation = value; PropertyChanged?.Invoke(this, new(nameof(Translation))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public static bool Contains(TranscriptLine line, double position, int track) => position >= line.Start && position < line.End &&
        (track == 0 || string.IsNullOrEmpty(line.Track) || line.Track == (track == 1 ? "call" : "mic"));
}

public sealed class TranscriptMarkers : FrameworkElement
{
    public TranscriptLine[] Rows { get; set; } = [];
    public double Duration { get; set; }
    public double Position { get; set; }
    public int Track { get; set; }
    public Action<TranscriptLine>? Selected { get; set; }
    public TranscriptMarkers() { Cursor = Cursors.Hand; ToolTip = "Transcript passages: call above, microphone below. Click to seek."; }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Duration <= 0 || ActualWidth <= 0) return;
        foreach (var row in Rows)
        {
            var x = Math.Clamp(row.Start / Duration, 0, 1) * ActualWidth;
            var end = Math.Clamp(row.End / Duration, 0, 1) * ActualWidth;
            var brush = (Brush)FindResource(TranscriptRow.Contains(row, Position, Track) ? "Recorder.Icon" : "Recorder.Border");
            var y = row.Track == "mic" ? 10 : 2;
            dc.DrawRoundedRectangle(brush, null, new Rect(x, y, Math.Max(1, end - x), 4), 2, 2);
            dc.DrawRectangle((Brush)FindResource("Recorder.Icon"), null, new Rect(x, y - 1, 2, 6));
        }
    }
    private TranscriptLine? Hit(Point point) => Duration <= 0 || ActualWidth <= 0 ? null : Rows
        .Where(r => (r.Track == "mic") == (point.Y >= 9))
        .OrderBy(r => Math.Abs(r.Start / Duration * ActualWidth - point.X)).FirstOrDefault();
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Hit(e.GetPosition(this)) is { } row) ToolTip = $"{TranscriptRow.TimeLabel(row.Start)} – {TranscriptRow.TimeLabel(row.End)} · {row.Speaker}\n{row.Text}";
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    { base.OnMouseLeftButtonDown(e); if (Hit(e.GetPosition(this)) is { } row) { Selected?.Invoke(row); e.Handled = true; } }
}

public partial class MainWindow
{
    private TranscriptRow[] transcriptRows = [];
    private readonly System.Windows.Threading.DispatcherTimer playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private string? followedSegment;
    private static bool InsideTranscriptButton(DependencyObject source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is System.Windows.Controls.Primitives.ButtonBase) return true;
        return false;
    }
    private void UpdateTranscriptPosition()
    {
        if (TimelineMarkers is null || PlaybackTrack is null || FollowPlayback is null) return;
        var position = PlaybackPosition.Value;
        foreach (var row in transcriptRows) row.IsPlaying = TranscriptRow.Contains(row.Line, position, PlaybackTrack.SelectedIndex);
        var first = transcriptRows.FirstOrDefault(r => r.IsPlaying);
        if (first?.Id != followedSegment)
        {
            followedSegment = first?.Id;
            if (first is not null && playback?.Playing == true && FollowPlayback.IsChecked == true && TranscriptList.Items.Contains(first)) TranscriptList.ScrollIntoView(first);
        }
        TimelineMarkers.Position = position; TimelineMarkers.Track = PlaybackTrack.SelectedIndex; TimelineMarkers.InvalidateVisual();
    }
    private async void TranslateClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (busy || Selected is not { } call || sender is not FrameworkElement { DataContext: TranscriptRow row }) return;
        var id = row.Id;
        var result = "";
        row.Translation = "Translating…";
        await Guard(async () =>
        {
            try { await Run(async ct => result = await recorder.Translate(call.Capture.Id, id, ct)); }
            catch (Exception ex) { result = ex is OperationCanceledException ? "Translation cancelled." : "Translation failed: " + ex.Message; ErrorSettingsButton.Visibility = Visibility.Visible; }
            if (Selected?.Capture.Id == call.Capture.Id && transcriptRows.FirstOrDefault(r => r.Id == id) is { } current)
            { current.Translation = result; TranscriptList.SelectedItem = current; }
        });
    }
}
