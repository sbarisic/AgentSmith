using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using AgentSmith.Application;
using AgentSmith.Core;
using AgentSmith.Tools;
using Microsoft.Win32;

namespace AgentSmith.Desktop;

public partial class MainWindow : Window
{
    private readonly CallRecorder recorder;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? workCancellation;
    private Task? work;
    private string? activeId;
    private bool ownsCapture, busy, polling, loading, closingAllowed, closing, sliderUpdating;
    private DateTimeOffset lastStatus;
    private CallPlayback? playback;
    private TranscriptLine[] transcript = [];
    private DateTimeOffset? testDeadline;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready => ready.Task;
    private SavedCall? Selected => LibraryList.SelectedItem as SavedCall;
    private sealed record LanguageChoice(string Code, string Name);
    public MainWindow(CallRecorder recorder)
    {
        this.recorder = recorder; InitializeComponent();
        loading = true;
        var languages = new[] { new LanguageChoice("auto", "Auto detect"), new LanguageChoice("en", "English"), new LanguageChoice("hr", "Croatian"), new LanguageChoice("sl", "Slovenian") };
        LanguageInput.ItemsSource = languages; LanguageInput.SelectedValue = recorder.Preferences.Language;
        if (LanguageInput.SelectedIndex < 0) { LanguageInput.SelectedIndex = 0; OtherLanguage.Text = recorder.Preferences.Language; }
        SummaryLanguage.ItemsSource = languages.Skip(1).ToArray(); SummaryLanguage.SelectedValue = recorder.Preferences.SummaryLanguage;
        StorageInput.Text = recorder.Preferences.StorageGiB.ToString(CultureInfo.InvariantCulture);
        MinutesInput.Text = recorder.Preferences.MaxMinutes.ToString(CultureInfo.InvariantCulture); RequestsInput.Text = recorder.Preferences.MaxRequests.ToString(CultureInfo.InvariantCulture);
        loading = false;
        Loaded += async (_, _) => { try { await Guard(Initialize); } finally { ready.TrySetResult(); } };
        timer.Tick += async (_, _) => await Tick();
        Closed += (_, _) => { timer.Stop(); playback?.Dispose(); recorder.Dispose(); };
    }
    private async Task Initialize()
    {
        RefreshDevices(); RefreshLibrary();
        ModelLabel.Text = await Task.Run(recorder.ModelDescription);
        var status = await recorder.Status(); activeId = status.Capture?.Id;
        if (!status.Available && recorder.Library().Any(c => c.Capture.State is "Starting" or "Recording" or "Finalizing"))
        {
            await recorder.EnsureService(CancellationToken.None); status = await recorder.Status(); activeId = status.Capture?.Id; RefreshLibrary();
        }
        StatusText.Text = activeId is not null ? "A recording is already active. Stop acts on that recording; closing this window leaves it running." :
            status.Engines.Transcription && status.Engines.Diarization ? "Ready · Select both devices, then Record. Transcription starts after Stop." :
            $"Recording is available. For transcription, run: smith audio setup --data-dir \"{recorder.DataDirectory}\"";
        SetControls(); timer.Start();
    }
    public async Task RunSmokeCheck()
    {
        // Construct/layout the real view without showing or activating a window.
        if (!File.Exists(Path.Combine(recorder.DataDirectory, "recorder-ui-fixture.json"))) throw new InvalidOperationException("Smoke checks require disposable fixture data from prepare_recorder_ui_fixture.py.");
        await Initialize(); timer.Stop();
        Measure(new Size(1280, 900)); Arrange(new Rect(0, 0, 1280, 900)); UpdateLayout();
        if (Selected is { } call)
        {
            SearchInput.Text = "Synthetic";
            if (transcript.Any(r => r.Text.Contains("Synthetic", StringComparison.Ordinal)) && TranscriptList.Items.Count == 0) throw new InvalidOperationException("Transcript search failed.");
            SearchInput.Text = "";
            if (transcript.Length > 0)
            {
                var before = recorder.Metadata(call.Capture.Id).SpeakerNames;
                recorder.RenameSpeaker(call.Capture.Id, transcript[0].SpeakerId, "Fixture speaker"); ShowSelected();
                if (transcript[0].Speaker != "Fixture speaker") throw new InvalidOperationException("Speaker rename failed.");
                var meta = recorder.Metadata(call.Capture.Id); meta.SpeakerNames = before; recorder.SaveMetadata(call.Capture.Id, meta); ShowSelected();
            }
            var export = Path.Combine(recorder.DataDirectory, "smoke-export-" + Guid.NewGuid().ToString("N"));
            if (call.Analysis is not null) recorder.Export(call.Capture.Id, export);
            var manifest = recorder.ReadCapture(call.Capture.Id);
            foreach (var track in manifest.Version == 2 ? manifest.Tracks : [manifest])
            { using var reader = new CaptureAudioReader(recorder.CaptureDirectory(call.Capture.Id), track); _ = reader.Sample(1); }
        }
        RecordingAnalysisStore.WriteJson(Path.Combine(recorder.DataDirectory, "desktop-smoke.json"), new { passed = true, windowShown = false, recordings = LibraryList.Items.Count, transcriptRows = transcript.Length, search = true, export = Selected?.Analysis is not null });
    }
    private void RefreshDevices()
    {
        var devices = CallRecorder.Devices();
        var outputId = (OutputDevice.SelectedItem as CaptureDevice)?.Id ?? recorder.Preferences.OutputId;
        var microphoneId = (MicrophoneDevice.SelectedItem as CaptureDevice)?.Id ?? recorder.Preferences.MicrophoneId;
        var outputs = devices.Where(d => d.Kind == "output").ToArray(); var microphones = devices.Where(d => d.Kind == "microphone").ToArray();
        OutputDevice.ItemsSource = outputs; MicrophoneDevice.ItemsSource = microphones;
        CaptureDevice? Select(CaptureDevice[] list, string? id) => id is not null ? list.SingleOrDefault(d => d.Id == id) : list.FirstOrDefault(d => d.CommunicationsDefault) ?? list.FirstOrDefault(d => d.Default);
        OutputDevice.SelectedItem = Select(outputs, outputId); MicrophoneDevice.SelectedItem = Select(microphones, microphoneId);
    }
    private void SaveSettings()
    {
        recorder.Preferences.OutputId = (OutputDevice.SelectedItem as CaptureDevice)?.Id;
        recorder.Preferences.MicrophoneId = (MicrophoneDevice.SelectedItem as CaptureDevice)?.Id;
        recorder.Preferences.Language = string.IsNullOrWhiteSpace(OtherLanguage.Text) ? (string?)LanguageInput.SelectedValue ?? "auto" : OtherLanguage.Text.Trim();
        recorder.Preferences.SummaryLanguage = (string?)SummaryLanguage.SelectedValue ?? "en";
        if (!double.TryParse(StorageInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var storage) ||
            !int.TryParse(MinutesInput.Text, out var minutes) || !int.TryParse(RequestsInput.Text, out var requests)) throw new UserFacingException("Enter valid storage, minute, and request limits.");
        recorder.Preferences.StorageGiB = storage; recorder.Preferences.MaxMinutes = minutes; recorder.Preferences.MaxRequests = requests; recorder.SavePreferences();
    }
    private async Task Guard(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { StatusText.Text = "Paused; completed work is saved."; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { SetControls(); }
    }
    private async Task Run(Func<CancellationToken, Task> operation)
    {
        if (busy) return;
        busy = true; SetControls(); workCancellation = new();
        var token = workCancellation.Token;
        try { work = Task.Run(() => operation(token), token); await work; }
        finally { busy = false; work = null; workCancellation.Dispose(); workCancellation = null; if (!closing) RefreshLibrary(); SetControls(); }
    }
    private void RefreshLibrary(string? selectId = null)
    {
        selectId ??= Selected?.Capture.Id;
        loading = true; var calls = recorder.Library(); LibraryList.ItemsSource = calls;
        LibraryList.SelectedItem = calls.FirstOrDefault(c => c.Capture.Id == selectId) ?? calls.FirstOrDefault(); loading = false;
        ShowSelected();
    }
    private void ShowSelected()
    {
        if (Selected is not { } call) { transcript = []; TranscriptList.ItemsSource = transcript; SetControls(); return; }
        transcript = recorder.Transcript(call.Capture.Id); FilterTranscript();
        CoverageText.Text = recorder.Coverage(call.Capture.Id); RenderSummary(recorder.Summary(call.Capture.Id));
        KeepButton.Visibility = call.Metadata.Test && !call.Metadata.Kept ? Visibility.Visible : Visibility.Collapsed;
        sliderUpdating = true; PlaybackPosition.Maximum = Math.Max(1, call.Capture.DurationSeconds); sliderUpdating = false;
        SetControls();
    }
    private void SetControls()
    {
        if (!IsInitialized) return;
        RecordButton.IsEnabled = TestButton.IsEnabled = !busy && activeId is null && !closing;
        StopButton.IsEnabled = activeId is not null && !busy && !closing;
        MuteInput.IsEnabled = activeId is not null && !closing;
        OutputDevice.IsEnabled = MicrophoneDevice.IsEnabled = !busy && activeId is null;
        var inactive = Selected is { } selected && selected.Capture.State is not ("Starting" or "Recording" or "Finalizing");
        TranscribeButton.IsEnabled = inactive && !busy && activeId is null && !closing;
        SummaryButton.IsEnabled = inactive && transcript.Length > 0 && !busy && activeId is null && !closing;
        DeleteRecordingButton.IsEnabled = DeleteAnalysisButton.IsEnabled = inactive && !busy && !closing;
        ExportButton.IsEnabled = inactive && Selected?.Analysis is not null && !busy;
        RawExportButton.IsEnabled = inactive && !busy;
        PauseButton.IsEnabled = busy && workCancellation is not null && !closing;
        PlayButton.IsEnabled = inactive && activeId is null;
        KeepButton.IsEnabled = inactive && !busy;
    }
    private async Task Start(bool test)
    {
        if (busy || activeId is not null) return;
        SaveSettings(); playback?.Dispose(); playback = null;
        busy = true; SetControls();
        try
        {
            var capture = await recorder.Start(TitleInput.Text, test, CancellationToken.None);
            activeId = capture.Id; ownsCapture = true; MuteInput.IsChecked = false;
            testDeadline = test ? DateTimeOffset.UtcNow.AddSeconds(10) : null;
            RefreshLibrary(capture.Id); StatusText.Text = test ? "Recording a 10-second test. Play it back, then Keep or Delete." : "Recording both tracks. Stop starts local transcription.";
        }
        catch
        {
            var status = await recorder.Status(); activeId = status.Capture?.Id;
            // An unconfirmed start may still have succeeded. Retain a visible Stop control.
            ownsCapture = activeId is not null && activeId == recorder.LastStartId; throw;
        }
        finally { busy = false; SetControls(); }
    }
    private async Task Stop(bool transcribe)
    {
        if (activeId is not { } id) return;
        busy = true; SetControls();
        CaptureManifest capture;
        try { capture = await recorder.Stop(id); activeId = null; ownsCapture = false; testDeadline = null; CallLevel.Value = MicLevel.Value = 0; RefreshLibrary(id); }
        finally { busy = false; SetControls(); }
        StatusText.Text = $"Recording {capture.State}. Audio is saved.";
        if (transcribe && capture.State == "Completed" && !recorder.Metadata(id).Test) await Transcribe(id, false);
    }
    private async Task Transcribe(string id, bool allowInterrupted)
    {
        SaveSettings();
        var retryFailed = RetryFailed.IsChecked == true; var retryUncertain = RetryUncertain.IsChecked == true;
        var progress = new Progress<string>(message => StatusText.Text = message);
        await Run(async ct => {
            var result = await recorder.Transcribe(id, allowInterrupted, retryFailed, retryUncertain, message => ((IProgress<string>)progress).Report(message), ct);
            await Dispatcher.InvokeAsync(() => StatusText.Text = $"Transcription {result.State} · {result.ProcessedSeconds:0}/{result.DurationSeconds * result.TrackCount:0} track-seconds. {result.Error}");
        });
    }
    private async Task Tick()
    {
        if (polling || closing) return; polling = true;
        try
        {
            if (playback is not null) { sliderUpdating = true; PlaybackPosition.Value = playback.Position; sliderUpdating = false; }
            if (activeId is { } id)
            {
                var status = await recorder.CaptureStatus(id);
                Elapsed.Text = TimeSpan.FromSeconds(status.DurationSeconds).ToString(@"hh\:mm\:ss");
                CallLevel.Value = status.Tracks.FirstOrDefault(t => t.TrackId == "call")?.Level ?? status.Level;
                MicLevel.Value = status.Tracks.FirstOrDefault(t => t.TrackId == "mic")?.Level ?? 0;
                MuteInput.IsChecked = status.Tracks.FirstOrDefault(t => t.TrackId == "mic")?.Muted ?? false;
                if (status.State is not ("Starting" or "Recording" or "Finalizing"))
                { activeId = null; ownsCapture = false; testDeadline = null; StatusText.Text = $"Recording {status.State}. {status.Error} Resume transcription explicitly."; RefreshLibrary(id); }
                else if (testDeadline is { } deadline && DateTimeOffset.UtcNow >= deadline && !busy) await Stop(false);
            }
            else if (DateTimeOffset.UtcNow - lastStatus > TimeSpan.FromSeconds(3))
            {
                lastStatus = DateTimeOffset.UtcNow; var status = await recorder.Status();
                if (status.Capture is { } capture) { activeId = capture.Id; ownsCapture = false; StatusText.Text = "Recording active in the shared service."; SetControls(); }
            }
        }
        catch (Exception ex) { StatusText.Text = "Status unavailable: " + ex.Message; }
        finally { polling = false; }
    }
    private async void RecordClick(object sender, RoutedEventArgs e) => await Guard(() => Start(false));
    private async void TestClick(object sender, RoutedEventArgs e) => await Guard(() => Start(true));
    private async void StopClick(object sender, RoutedEventArgs e) => await Guard(() => Stop(true));
    private async void RefreshClick(object sender, RoutedEventArgs e) => await Guard(async () => { RefreshDevices(); RefreshLibrary(); ModelLabel.Text = await Task.Run(recorder.ModelDescription); });
    private async void MuteClick(object sender, RoutedEventArgs e) => await Guard(async () => { if (activeId is { } id) await recorder.Mute(id, MuteInput.IsChecked == true); });
    private async void TranscribeClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (Selected is not { } call) return;
        var interrupted = call.Capture.State == "Interrupted";
        if (interrupted && MessageBox.Show(this, "This recording has missing or uncertain audio. Analyze the retained parts?", "Interrupted recording", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        await Transcribe(call.Capture.Id, interrupted);
    });
    private void PauseClick(object sender, RoutedEventArgs e) => workCancellation?.Cancel();
    private async void SummaryClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (Selected is not { } call) return;
        SaveSettings(); var retry = RetryUncertain.IsChecked == true; StatusText.Text = "Generating cited summary…";
        await Run(async ct => { var result = await recorder.Summarize(call.Capture.Id, retry, ct); await Dispatcher.InvokeAsync(() => StatusText.Text = $"Summary {result.State}. {result.Error}"); });
    });
    private void LibraryChanged(object sender, SelectionChangedEventArgs e)
    { if (loading) return; playback?.Dispose(); playback = null; sliderUpdating = true; PlaybackPosition.Value = 0; sliderUpdating = false; _ = Guard(() => { ShowSelected(); return Task.CompletedTask; }); }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (TranscriptList is not null) FilterTranscript(); }
    private void FilterTranscript() => TranscriptList.ItemsSource = transcript.Where(r => r.Text.Contains(SearchInput.Text, StringComparison.CurrentCultureIgnoreCase) || r.Speaker.Contains(SearchInput.Text, StringComparison.CurrentCultureIgnoreCase)).ToArray();
    private void TranscriptSelectionChanged(object sender, SelectionChangedEventArgs e) { if (TranscriptList.SelectedItem is TranscriptLine row) SpeakerName.Text = row.Speaker; }
    private async void RenameClick(object sender, RoutedEventArgs e) => await Guard(() =>
    { if (busy) throw new UserFacingException("Wait for processing to finish before renaming speakers."); if (Selected is { } call && TranscriptList.SelectedItem is TranscriptLine row) { recorder.RenameSpeaker(call.Capture.Id, row.SpeakerId, SpeakerName.Text); ShowSelected(); } return Task.CompletedTask; });
    private async void KeepClick(object sender, RoutedEventArgs e) => await Guard(() =>
    { if (Selected is { } call) { var meta = recorder.Metadata(call.Capture.Id); meta.Kept = true; recorder.SaveMetadata(call.Capture.Id, meta); RefreshLibrary(); } return Task.CompletedTask; });
    private string? ExportDirectory(string prefix)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a parent folder; a new export directory will be created" };
        return dialog.ShowDialog(this) == true ? Path.Combine(dialog.FolderName, prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]) : null;
    }
    private async void ExportClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    { if (Selected is { } call && ExportDirectory("transcript") is { } path) { await Run(_ => Task.Run(() => recorder.Export(call.Capture.Id, path))); StatusText.Text = "Exported to " + path; } });
    private async void RawExportClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    { if (Selected is { } call && ExportDirectory("audio") is { } path) { await Run(async ct => { await recorder.EnsureService(ct); await AudioCaptureControl.Export(recorder.DataDirectory, call.Capture.Id, path, ct); }); StatusText.Text = "Exported to " + path; } });
    private async void DeleteRecordingClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    { if (Selected is { } call && MessageBox.Show(this, "Delete the selected original audio? Saved analyses remain available in AgentSmith.", "Delete recording", MessageBoxButton.OKCancel) == MessageBoxResult.OK) { playback?.Dispose(); playback = null; await Run(_ => recorder.DeleteCapture(call.Capture.Id)); } });
    private async void DeleteAnalysisClick(object sender, RoutedEventArgs e) => await Guard(() =>
    { if (Selected is { } call && MessageBox.Show(this, "Delete this analysis? Original audio is retained.", "Delete analysis", MessageBoxButton.OKCancel) == MessageBoxResult.OK) { recorder.DeleteAnalysis(call.Capture.Id); RefreshLibrary(); } return Task.CompletedTask; });
    private void EnsurePlayback()
    {
        if (activeId is not null) throw new UserFacingException("Stop recording before playback.");
        if (Selected is not { } call) return;
        if (playback is null)
        {
            playback = new(recorder.CaptureDirectory(call.Capture.Id), recorder.ReadCapture(call.Capture.Id));
            playback.Failed += ex => Dispatcher.InvokeAsync(() => StatusText.Text = "Playback failed: " + ex.Message);
            playback.Select(PlaybackTrack.SelectedIndex);
        }
    }
    private async void PlayClick(object sender, RoutedEventArgs e) => await Guard(() => { EnsurePlayback(); playback?.Toggle(); return Task.CompletedTask; });
    private async void PlaybackSeek(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (!sliderUpdating && IsLoaded) await Guard(() => { EnsurePlayback(); playback?.Seek(e.NewValue); return Task.CompletedTask; }); }
    private void PlaybackTrackChanged(object sender, SelectionChangedEventArgs e) => playback?.Select(PlaybackTrack.SelectedIndex);
    private async Task PlayAt(double seconds) => await Guard(() => { EnsurePlayback(); playback?.Seek(seconds); if (playback?.Playing == false) playback.Toggle(); return Task.CompletedTask; });
    private async void TranscriptDoubleClick(object sender, MouseButtonEventArgs e) { if (TranscriptList.SelectedItem is TranscriptLine row) await PlayAt(row.Start); }
    private async void TranscriptKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && TranscriptList.SelectedItem is TranscriptLine row) { e.Handled = true; await PlayAt(row.Start); } }
    private void SummaryLanguageChanged(object sender, SelectionChangedEventArgs e)
    { if (!loading && recorder is not null && SummaryLanguage.SelectedValue is string code) { recorder.Preferences.SummaryLanguage = code; if (Selected is { } call) RenderSummary(recorder.Summary(call.Capture.Id)); } }
    private void RenderSummary(string text)
    {
        var document = new FlowDocument { FontFamily = FontFamily, FontSize = 14, PagePadding = new Thickness(0) };
        foreach (var line in text.Split('\n'))
        {
            var paragraph = new Paragraph(); var cursor = 0;
            foreach (Match match in Regex.Matches(line, @"\[([^\]]+)\]"))
            {
                paragraph.Inlines.Add(line[cursor..match.Index]);
                var row = transcript.FirstOrDefault(r => match.Groups[1].Value.Contains(r.Id, StringComparison.Ordinal));
                if (row is not null) { var link = new Hyperlink(new System.Windows.Documents.Run(match.Value)); link.Click += async (_, _) => await PlayAt(row.Start); paragraph.Inlines.Add(link); }
                else paragraph.Inlines.Add(match.Value);
                cursor = match.Index + match.Length;
            }
            paragraph.Inlines.Add(line[cursor..]); document.Blocks.Add(paragraph);
        }
        SummaryView.Document = document;
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closingAllowed) return; e.Cancel = true; if (closing) return;
        if (activeId is not null && ownsCapture && MessageBox.Show(this, "Stop recording and exit? Transcription will remain pending. Choose Cancel to keep recording.", "Stop and exit", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        if (work is not null && MessageBox.Show(this, "Pause processing and exit? Completed work will be saved.", "Pause and exit", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        if (busy && work is null) { StatusText.Text = "Wait for the current recording command to finish before closing."; return; }
        closing = true; SetControls();
        try
        {
            if (activeId is not null && ownsCapture) await Stop(false);
            workCancellation?.Cancel(); if (work is { } pending) { try { await pending; } catch (OperationCanceledException) { } }
            closingAllowed = true; Close();
        }
        catch (Exception ex) { closing = false; StatusText.Text = "Could not finish closing: " + ex.Message; SetControls(); }
    }
}
