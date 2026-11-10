using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AgentSmith.Application;
using AgentSmith.Tools;

namespace AgentSmith.Desktop;

public partial class MainWindow
{
    private async Task CheckRecorderPolish()
    {
        var size = new Size(984, 681);
        var originalCall = Selected?.Capture.Id;
        var savedOutput = recorder.Preferences.OutputId; var savedMic = recorder.Preferences.MicrophoneId;
        BeginNewRecording();
        if (Selected is not null || RecordButton.Content.ToString() != "Record" || TitleInput.IsReadOnly || DraftDevices.Visibility != Visibility.Visible) throw new InvalidOperationException("New recording draft was not editable.");
        var overrideOutput = new CaptureDevice("draft-output", "Long output device name for this recording only", false);
        var overrideMic = new CaptureDevice("draft-mic", "Microphone override", false, "microphone");
        RefreshDraftDevices([overrideOutput, overrideMic]); DraftOutput.SelectedItem = overrideOutput; DraftMicrophone.SelectedItem = overrideMic;
        RefreshDraftDevices([]);
        if (DraftOutput.SelectedItem is not null || DraftMicrophone.SelectedItem is not null) throw new InvalidOperationException("Missing draft device silently fell back.");
        RefreshDraftDevices([overrideOutput, overrideMic]);
        if (DraftOutput.SelectedItem is not CaptureDevice { Id: "draft-output" } || DraftMicrophone.SelectedItem is not CaptureDevice { Id: "draft-mic" }) throw new InvalidOperationException("Draft devices did not reconnect.");
        TitleInput.Text = "Custom draft";
        RefreshLibrary();
        if (Selected is not null || TitleInput.Text != "Custom draft" || recorder.Preferences.OutputId != savedOutput || recorder.Preferences.MicrophoneId != savedMic) throw new InvalidOperationException("Draft changed global settings or was lost on refresh.");
        WriteSmokePreview("new-recording.png", size);
        RefreshLibrary(originalCall);
        if (RecordButton.Content.ToString() != "New" || !TitleInput.IsReadOnly) throw new InvalidOperationException("Selected recording did not offer New.");
        var view = (FrameworkElement)Content;
        Navigate("Home"); WriteSmokePreview("tabs-compact.png", size);
        var contentTop = ContentGrid.TransformToAncestor(view).Transform(new Point()).Y;
        foreach (var page in new[] { "Recordings", "Configuration", "Home" })
        {
            Navigate(page); view.Measure(size); view.Arrange(new Rect(size)); view.UpdateLayout();
            if (ContentGrid.TransformToAncestor(view).Transform(new Point()).Y != contentTop)
                throw new InvalidOperationException("Navigation changed the toolbar/content vertical position.");
        }
        // Focus changes only border color: the same border space exists before focus arrives.
        foreach (var button in new[] { HomeNavigation, LibraryNavigation, ConfigurationNavigation })
        {
            button.ApplyTemplate();
            if (button.Template.FindName("Chrome", button) is not Border { BorderThickness.Top: 1, BorderThickness.Bottom: 1 })
                throw new InvalidOperationException("Navigation did not reserve focus-border space.");
        }
        if (Selected is not { } call) return;
        var job = call.Analysis;
        if (job is not null)
        {
            var oldRevision = job.SummaryRevision; var oldLanguage = job.SummaryLanguage; var oldSpeakers = job.SpeakerNames;
            try
            {
                job.SummaryLanguage = recorder.Preferences.SummaryLanguage; job.SpeakerNames = recorder.Metadata(call.Capture.Id).SpeakerNames;
                job.SummaryRevision = RecordingAnalysis.SummaryRevision(job); recorder.Analysis.Store.Save(job); RefreshLibrary(call.Capture.Id);
                if (SummaryActions.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Completed summary kept an empty action row.");
                WriteSmokePreview("summary-compact.png", size);
                if (SavedAgentLabel.TransformToAncestor(view).Transform(new Point()).X <= SelectedTitle.TransformToAncestor(view).Transform(new Point()).X)
                    throw new InvalidOperationException("Summary agent did not move to the right of the title.");
            }
            finally { job.SummaryRevision = oldRevision; job.SummaryLanguage = oldLanguage; job.SpeakerNames = oldSpeakers; recorder.Analysis.Store.Save(job); RefreshLibrary(call.Capture.Id); }
        }
        WorkspaceTabs.SelectedItem = ReviewTab;
        var silent = new PlaybackSmokeCheck.SilentOutput();
        playback = new(recorder.CaptureDirectory(call.Capture.Id), recorder.ReadCapture(call.Capture.Id), silent);
        try
        {
            TimelineMarkers.Selected?.Invoke(transcript.Last());
            if (TranscriptList.SelectedItem is not TranscriptRow markerRow || markerRow.Id != transcript.Last().Id || playback.Position != transcript.Last().Start) throw new InvalidOperationException("Transcript marker did not select and seek its passage.");
            await CommitPlaybackPosition(3); UpdatePlaybackLabel();
            if (PlaybackElapsed.Text != "00:03.000" || PlaybackDuration.Text != "00:12.000") throw new InvalidOperationException("Timeline labels do not match the fixture.");
            PlaybackDragStarted(PlaybackPosition, new DragStartedEventArgs(0, 0));
            PlaybackPosition.Value = 6;
            if (playback.Position != 3 || PlaybackElapsed.Text != "00:06.000") throw new InvalidOperationException("Drag preview moved audio before release.");
            PlaybackDragCompleted(PlaybackPosition, new DragCompletedEventArgs(0, 0, true));
            if (PlaybackPosition.Value != 3) throw new InvalidOperationException("Cancelled drag did not restore playback position.");
            playback.Toggle();
            PlaybackDragStarted(PlaybackPosition, new DragStartedEventArgs(0, 0)); PlaybackPosition.Value = 5;
            PlaybackDragCompleted(PlaybackPosition, new DragCompletedEventArgs(0, 0, false));
            if (playback.Position != 5 || !playback.Playing) throw new InvalidOperationException("Seeking did not retain playing state.");
            SkipForwardClick(this, new RoutedEventArgs());
            if (!playback.AtEnd) throw new InvalidOperationException("Forward skip did not clamp to duration.");
            SkipBackClick(this, new RoutedEventArgs());
            if (Math.Abs(playback.Position - 2) > .001) throw new InvalidOperationException("Backward skip did not move ten seconds.");
            SkipBackClick(this, new RoutedEventArgs());
            if (playback.Position != 0) throw new InvalidOperationException("Backward skip went before the start.");
            await CommitPlaybackPosition(3.125); UpdatePlaybackLabel();
            if (PlaybackPosition.SmallChange != .1) throw new InvalidOperationException("Keyboard seeking is not sub-second.");
            if (Math.Abs(playback.Position - 3.125) > 0.0001 || PlaybackElapsed.Text != "00:03.125") throw new InvalidOperationException("Fractional seek lost precision.");
            if (transcriptRows.Count(r => r.IsPlaying) != 2) throw new InvalidOperationException("Overlapping passages were not both highlighted.");
            await CommitPlaybackPosition(6.5);
            if (transcriptRows.Any(r => r.IsPlaying)) throw new InvalidOperationException("Gap incorrectly highlighted a passage.");
            await CommitPlaybackPosition(4);
            if (transcriptRows.Count(r => r.IsPlaying) != 1) throw new InvalidOperationException("End boundary highlighted a finished passage.");
            PlaybackTrack.SelectedIndex = 1; UpdateTranscriptPosition();
            if (transcriptRows.Any(r => r.IsPlaying)) throw new InvalidOperationException("Muted track highlighted a passage.");
            PlaybackTrack.SelectedIndex = 0;
            await CommitPlaybackPosition(5); TranscriptList.SelectedItem = transcriptRows.FirstOrDefault(); UpdatePlaybackLabel();
            WriteSmokePreview("timeline-compact.png", size);
            WriteSmokePreview("timeline-minimum.png", new Size(884, 601));
            var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
            var original = dictionaries[0];
            try
            {
                dictionaries[0] = new ResourceDictionary { Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.Dark.xaml") };
                RecorderTheme.Apply(true); WriteSmokePreview("timeline-dark.png", new Size(1264, 861));
            }
            finally { dictionaries[0] = original; RecorderTheme.Apply(); }
        }
        finally { playback.Dispose(); playback = null; scrubbing = false; sliderUpdating = true; PlaybackPosition.Value = 0; sliderUpdating = false; SetControls(); }
    }
}
