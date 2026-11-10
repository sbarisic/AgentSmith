using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace AgentSmith.Desktop;

public partial class MainWindow
{
    private bool scrubbing;
    private string? scrubRecordingId;
    private static string PlaybackTime(double seconds) => TranscriptRow.TimeLabel(seconds);

    private void UpdateTimelineLabels()
    {
        if (PlaybackElapsed is null) return;
        PlaybackElapsed.Text = PlaybackTime(PlaybackPosition.Value);
        PlaybackDuration.Text = PlaybackTime(Selected?.Capture.DurationSeconds ?? 0);
    }
    private Task CommitPlaybackPosition(double seconds)
    {
        EnsurePlayback();
        if (playback is not null)
        {
            playback.Seek(seconds);
            sliderUpdating = true; PlaybackPosition.Value = playback.Position; sliderUpdating = false;
        }
        UpdateTimelineLabels(); UpdateTranscriptPosition();
        return Task.CompletedTask;
    }
    private void PlaybackDragStarted(object sender, DragStartedEventArgs e)
    { scrubbing = true; scrubRecordingId = Selected?.Capture.Id; }
    private async void PlaybackDragCompleted(object sender, DragCompletedEventArgs e)
    {
        scrubbing = false;
        if (scrubRecordingId != Selected?.Capture.Id || activeId is not null) return;
        if (e.Canceled)
        {
            sliderUpdating = true; PlaybackPosition.Value = playback?.Position ?? 0; sliderUpdating = false;
            UpdateTimelineLabels();
            return;
        }
        await Guard(() => CommitPlaybackPosition(PlaybackPosition.Value));
    }
    private async void SkipBackClick(object sender, RoutedEventArgs e) => await Guard(() =>
        CommitPlaybackPosition(Math.Max(0, (playback?.Position ?? PlaybackPosition.Value) - 10)));
    private async void SkipForwardClick(object sender, RoutedEventArgs e) => await Guard(() =>
        CommitPlaybackPosition(Math.Min(Selected?.Capture.DurationSeconds ?? 0, (playback?.Position ?? PlaybackPosition.Value) + 10)));
    private void PlaybackMouseMove(object sender, MouseEventArgs e)
    {
        if (PlaybackPosition.Template.FindName("PART_Track", PlaybackPosition) is not Track track) return;
        var seconds = track.ValueFromPoint(e.GetPosition(track));
        if (double.IsFinite(seconds)) PlaybackPosition.ToolTip = "Seek to " + PlaybackTime(Math.Clamp(seconds, 0, PlaybackPosition.Maximum));
    }
    private void PlaybackPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && scrubbing && PlaybackPosition.Template.FindName("PART_Track", PlaybackPosition) is Track track)
        { track.Thumb?.CancelDrag(); e.Handled = true; }
    }
}
