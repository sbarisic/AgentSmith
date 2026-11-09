using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AgentSmith.Application;
using AgentSmith.Core;
using AgentSmith.Tools;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

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
    private string suggestedTitle = "";
    private MMDeviceEnumerator? deviceEnumerator;
    private MMDeviceNotificationClient? deviceNotifications;
    private long deviceChangeAt;
    private bool refreshingDevices, outputSelectionLost, microphoneSelectionLost;
    private CaptureDevice? lastOutputDevice, lastMicrophoneDevice;
    private CaptureManifest? activeCaptureDevices;
    private bool preparingTranscription, transcriptionPrepared;
    private Func<Action<string>, CancellationToken, Task> prepareTranscription;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready => ready.Task;
    private SavedCall? Selected => LibraryList.SelectedItem as SavedCall;
    private sealed record LanguageChoice(string Code, string Name);
    public MainWindow(CallRecorder recorder)
    {
        this.recorder = recorder; InitializeComponent();
        prepareTranscription = recorder.PrepareTranscription;
        RefreshDefaultTitle();
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
        Closed += (_, _) => { timer.Stop(); StopDeviceWatching(); playback?.Dispose(); recorder.Dispose(); };
    }
    private async Task Initialize()
    {
        StartDeviceWatching(); RefreshDevices(); RefreshLibrary();
        await RefreshModel();
        var status = await recorder.Status(); activeId = status.Capture?.Id;
        if (!status.Available && recorder.Library().Any(c => c.Capture.State is "Starting" or "Recording" or "Finalizing"))
        {
            await recorder.EnsureService(CancellationToken.None); status = await recorder.Status(); activeId = status.Capture?.Id; RefreshLibrary();
        }
        StatusText.Text = activeId is not null ? "A recording is already active. Stop acts on that recording; closing this window leaves it running." :
            "Preparing local transcription automatically…";
        SetControls(); timer.Start();
        if (activeId is null) await PrepareTranscription();
    }
    private async Task PrepareTranscription()
    {
        if (transcriptionPrepared) return;
        if (busy || activeId is not null) throw new UserFacingException("Wait for the current operation to finish before preparing transcription.");
        preparingTranscription = true;
        OperationBanner.Visibility = SetupProgress.Visibility = Visibility.Visible;
        RetrySetupButton.Visibility = Visibility.Collapsed;
        OperationTitle.Text = "Preparing local transcription";
        OperationMessage.Text = "Checking installed components. Missing or outdated components will be downloaded and installed automatically. Your recordings stay on this computer.";
        var progress = new Progress<string>(message =>
        {
            if (!preparingTranscription) return;
            OperationMessage.Text = message; StatusText.Text = message;
        });
        try
        {
            await Run(ct => prepareTranscription(message => ((IProgress<string>)progress).Report(message), ct));
            transcriptionPrepared = true; OperationBanner.Visibility = Visibility.Collapsed;
            StatusText.Text = "Ready · Local transcription is configured. Record a call or resume a saved recording.";
        }
        catch (Exception ex)
        {
            OperationTitle.Text = ex is OperationCanceledException ? "Automatic setup cancelled" : "Automatic setup could not finish";
            OperationMessage.Text = (ex is OperationCanceledException ? "Verified downloads and the previous installation are retained." : ex.Message) +
                " Your recordings are saved. Select Retry to continue.";
            StatusText.Text = OperationMessage.Text;
            RetrySetupButton.Visibility = Visibility.Visible;
            throw;
        }
        finally { preparingTranscription = false; SetupProgress.Visibility = Visibility.Collapsed; SetControls(); }
    }
    private async void RetrySetupClick(object sender, RoutedEventArgs e) => await Guard(PrepareTranscription);
    public async Task RunSmokeCheck()
    {
        // Construct/layout the real view without showing or activating a window.
        if (!File.Exists(Path.Combine(recorder.DataDirectory, "recorder-ui-fixture.json"))) throw new InvalidOperationException("Smoke checks require disposable fixture data from prepare_recorder_ui_fixture.py.");
        var preparationCalls = 0;
        prepareTranscription = (_, _) => { preparationCalls++; return Task.CompletedTask; };
        await Initialize(); timer.Stop();
        if (preparationCalls != 1 || !transcriptionPrepared) throw new InvalidOperationException("Startup did not prepare transcription automatically.");
        await PrepareTranscription();
        if (preparationCalls != 1) throw new InvalidOperationException("Ready transcription unnecessarily reran setup.");
        await CheckAutomaticSetupRecovery();
        PresentationSmokeCheck.Run();
        CheckDeviceSelectionRefresh();
        Measure(new Size(1280, 900)); Arrange(new Rect(0, 0, 1280, 900)); UpdateLayout();
        // Exercise all tab contents without opening a window or capturing a device.
        foreach (var size in new[] { new Size(1264, 861), new Size(984, 681) })
        foreach (var tab in new[] { RecordingTab, ConfigurationTab, ReviewTab })
        {
            WorkspaceTabs.SelectedItem = tab;
            var name = tab == RecordingTab ? "recording-tab" : tab == ConfigurationTab ? "configuration-tab" : "review-tab";
            WriteSmokePreview($"{name}-{size.Width}.png", size);
            if (tab.Content is not FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 }) throw new InvalidOperationException($"{tab.Name} layout failed.");
        }
        WorkspaceTabs.SelectedItem = ReviewTab; ResultsTabs.SelectedItem = TranscriptTab; UpdateLayout();
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
            PlaybackSmokeCheck.Run(recorder.CaptureDirectory(call.Capture.Id), manifest);
            foreach (var track in manifest.Version == 2 ? manifest.Tracks : [manifest])
            { using var reader = new CaptureAudioReader(recorder.CaptureDirectory(call.Capture.Id), track); _ = reader.Sample(1); }
        }
        var pausedWork = Run(ct => Task.Delay(Timeout.Infinite, ct));
        if (!PauseButton.IsEnabled || PauseButton.Visibility != Visibility.Visible || ConfigurationFields.IsEnabled) throw new InvalidOperationException("Processing controls did not enter the active state.");
        PauseClick(this, new RoutedEventArgs());
        try { await pausedWork; throw new InvalidOperationException("Processing cancellation was ignored."); } catch (OperationCanceledException) { }
        if (PauseButton.Visibility != Visibility.Collapsed || !ConfigurationFields.IsEnabled) throw new InvalidOperationException("Processing controls did not return to the idle state.");
        WorkspaceTabs.SelectedItem = RecordingTab;
        var deviceNotificationsRegistered = deviceNotifications is not null;
        StopDeviceWatching();
        var recordings = LibraryList.Items.Count; var hasRecording = Selected is not null; var hasAnalysis = Selected?.Analysis is not null;
        // Exercise real owned-service shutdown using only this disposable data directory.
        await recorder.EnsureService(CancellationToken.None);
        var ownedService = recorder.HasOwnedService;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Closed += (_, _) => closed.TrySetResult();
        Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        if (ownedService && (await AudioServiceControl.Status(recorder.DataDirectory)).Available)
            throw new InvalidOperationException("Closing left the app-owned audio service running.");
        RecordingAnalysisStore.WriteJson(Path.Combine(recorder.DataDirectory, "desktop-smoke.json"), new { passed = true, windowShown = false, tabs = 3, recordings, transcriptRows = transcript.Length, search = true, deviceNotificationsRegistered, deviceSelectionRefresh = true, automaticSetup = true, setupRecovery = true, explicitTranscriptActions = true, formattedSummary = true, playbackReplay = hasRecording, idleClose = true, ownedServiceShutdown = ownedService, export = hasAnalysis });
    }
    private async Task CheckAutomaticSetupRecovery()
    {
        transcriptionPrepared = false;
        prepareTranscription = (_, _) => Task.FromException(new IOException("Fixture: connection unavailable."));
        try { await PrepareTranscription(); throw new InvalidOperationException("Setup failure was hidden."); }
        catch (IOException) { }
        if (transcriptionPrepared || busy || OperationBanner.Visibility != Visibility.Visible || RetrySetupButton.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Setup failure did not leave a visible, retryable error.");
        WriteSmokePreview("automatic-setup-error.png", new Size(1264, 861));
        prepareTranscription = (_, ct) => Task.Delay(Timeout.Infinite, ct);
        var pending = PrepareTranscription();
        if (CancelSetupButton.Visibility != Visibility.Visible || !CancelSetupButton.IsEnabled || RecordButton.IsEnabled)
            throw new InvalidOperationException("Automatic setup did not expose cancellation or serialize foreground work.");
        PauseClick(this, new RoutedEventArgs());
        try { await pending; throw new InvalidOperationException("Setup ignored cancellation."); }
        catch (OperationCanceledException) { }
        if (transcriptionPrepared || busy || RetrySetupButton.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Cancelled setup could not be retried.");
        prepareTranscription = (_, _) => Task.CompletedTask;
        await PrepareTranscription();
        if (!transcriptionPrepared || OperationBanner.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Successful setup retry did not clear the blocker.");
    }
    private void WriteSmokePreview(string filename, Size size)
    {
        var view = (FrameworkElement)Content;
        view.Measure(size); view.Arrange(new Rect(size)); view.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var background = new System.Windows.Media.DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(Background, null, new Rect(size));
        bitmap.Render(background); bitmap.Render(view);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(recorder.DataDirectory, filename)); encoder.Save(output);
    }
    private void CheckDeviceSelectionRefresh()
    {
        var outputs = OutputDevice.ItemsSource; var microphones = MicrophoneDevice.ItemsSource;
        var output = OutputDevice.SelectedItem; var microphone = MicrophoneDevice.SelectedItem;
        var outputLost = outputSelectionLost; var microphoneLost = microphoneSelectionLost;
        var lastOutput = lastOutputDevice; var lastMicrophone = lastMicrophoneDevice;
        var savedOutputId = recorder.Preferences.OutputId; var savedMicrophoneId = recorder.Preferences.MicrophoneId;
        var captureId = activeId; var captureDevices = activeCaptureDevices; var tab = WorkspaceTabs.SelectedItem;
        try
        {
            var speaker = new CaptureDevice("fixture-output", "Speakers", true);
            var mic = new CaptureDevice("fixture-mic", "Microphone", true, "microphone");
            var headset = new CaptureDevice("fixture-headset", "USB headphones", true, CommunicationsDefault: true);
            var headsetMic = new CaptureDevice("fixture-headset-mic", "USB microphone", true, "microphone", true);
            ApplyDevices([speaker, mic]); OutputDevice.SelectedItem = speaker; MicrophoneDevice.SelectedItem = mic;
            ApplyDevices([speaker with { Default = false }, mic with { Default = false }, headset, headsetMic]);
            if ((OutputDevice.SelectedItem as CaptureDevice)?.Id != speaker.Id || (MicrophoneDevice.SelectedItem as CaptureDevice)?.Id != mic.Id)
                throw new InvalidOperationException("Connecting a new default device changed the selected endpoints.");
            OutputDevice.SelectedItem = headset; MicrophoneDevice.SelectedItem = headsetMic;
            ApplyDevices([speaker, mic]);
            if (OutputDevice.SelectedItem is not null || MicrophoneDevice.SelectedItem is not null)
                throw new InvalidOperationException("Removed devices silently switched to other endpoints.");
            if (!OutputDevicePlaceholder.Text.Contains(headset.Name) || !MicrophoneDevicePlaceholder.Text.Contains(headsetMic.Name) ||
                !RecordingOutput.Text.Contains("Unavailable: " + headset.Name) || !RecordingMicrophone.Text.Contains("Unavailable: " + headsetMic.Name))
                throw new InvalidOperationException("Disconnected device names disappeared from the selectors or recording panel.");
            SetControls();
            WorkspaceTabs.SelectedItem = ConfigurationTab;
            WriteSmokePreview("disconnected-configuration.png", new Size(1264, 861));
            if (OutputDevicePlaceholder.Visibility != Visibility.Visible || MicrophoneDevicePlaceholder.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Disconnected device placeholders are hidden.");
            WorkspaceTabs.SelectedItem = RecordingTab;
            WriteSmokePreview("disconnected-recording.png", new Size(984, 681));
            ApplyDevices([speaker, mic, headset, headsetMic]);
            if ((OutputDevice.SelectedItem as CaptureDevice)?.Id != headset.Id || (MicrophoneDevice.SelectedItem as CaptureDevice)?.Id != headsetMic.Id ||
                outputSelectionLost || microphoneSelectionLost)
                throw new InvalidOperationException("Reconnected devices were not restored automatically.");
            ApplyDevices([speaker, mic, headset with { Name = "Renamed headphones" }, headsetMic]);
            if ((OutputDevice.SelectedItem as CaptureDevice)?.Name != "Renamed headphones" || (MicrophoneDevice.SelectedItem as CaptureDevice)?.Id != headsetMic.Id)
                throw new InvalidOperationException("Device refresh lost a restored endpoint.");
            // Windows communications defaults win for calls; multimedia defaults are the fallback.
            ApplyWindowsDefaults([speaker, mic, headset, headsetMic]);
            if ((OutputDevice.SelectedItem as CaptureDevice)?.Id != headset.Id || (MicrophoneDevice.SelectedItem as CaptureDevice)?.Id != headsetMic.Id)
                throw new InvalidOperationException("Windows communications defaults were not selected.");
            ApplyWindowsDefaults([speaker, mic]);
            if ((OutputDevice.SelectedItem as CaptureDevice)?.Id != speaker.Id || (MicrophoneDevice.SelectedItem as CaptureDevice)?.Id != mic.Id)
                throw new InvalidOperationException("Windows multimedia defaults were not used as a fallback.");
            ApplyWindowsDefaults([speaker, mic, headset, headsetMic]);
            SaveSettings();
            var saved = RecordingAnalysisStore.ReadJson<RecorderPreferences>(Path.Combine(recorder.DataDirectory, "desktop", "preferences.json"));
            if (saved.OutputId != headset.Id || saved.MicrophoneId != headsetMic.Id)
                throw new InvalidOperationException("Selected Windows defaults were not saved.");
            // Simulate opening while saved devices are disconnected, then reconnect one at a time.
            refreshingDevices = true;
            OutputDevice.SelectedItem = null; MicrophoneDevice.SelectedItem = null;
            lastOutputDevice = lastMicrophoneDevice = null;
            outputSelectionLost = microphoneSelectionLost = false;
            refreshingDevices = false;
            ApplyDevices([speaker, mic]);
            if (OutputDevice.SelectedItem is not null || MicrophoneDevice.SelectedItem is not null)
                throw new InvalidOperationException("Unavailable saved devices were replaced with different defaults.");
            ApplyDevices([speaker, mic, headset]);
            if ((OutputDevice.SelectedItem as CaptureDevice)?.Id != headset.Id || MicrophoneDevice.SelectedItem is not null)
                throw new InvalidOperationException("Saved devices did not reconnect independently.");
            ApplyDevices([speaker, mic, headset, headsetMic]);
            if ((MicrophoneDevice.SelectedItem as CaptureDevice)?.Id != headsetMic.Id)
                throw new InvalidOperationException("Saved microphone was not restored after startup.");
            // A recording opened from another client must display its actual endpoints.
            activeId = "fixture-active";
            activeCaptureDevices = new CaptureManifest { Id = activeId, Version = 2, Device = speaker,
                Tracks = [new() { TrackId = "call", Device = speaker }, new() { TrackId = "mic", Device = mic }] };
            UpdateDevicePresentation();
            if (RecordingOutput.Text != "Output: " + speaker.Name || RecordingMicrophone.Text != "Mic: " + mic.Name)
                throw new InvalidOperationException("Recording panel displayed preferences instead of the active capture endpoints.");
        }
        finally
        {
            refreshingDevices = true;
            OutputDevice.ItemsSource = outputs; MicrophoneDevice.ItemsSource = microphones;
            OutputDevice.SelectedItem = output; MicrophoneDevice.SelectedItem = microphone;
            outputSelectionLost = outputLost; microphoneSelectionLost = microphoneLost;
            lastOutputDevice = lastOutput; lastMicrophoneDevice = lastMicrophone;
            recorder.Preferences.OutputId = savedOutputId; recorder.Preferences.MicrophoneId = savedMicrophoneId;
            recorder.SavePreferences();
            activeId = captureId; activeCaptureDevices = captureDevices; WorkspaceTabs.SelectedItem = tab;
            refreshingDevices = false; UpdateDeviceRefreshHint(); SetControls();
        }
    }
    private void StartDeviceWatching()
    {
        if (deviceNotifications is not null) return;
        try
        {
            deviceEnumerator = new MMDeviceEnumerator();
            deviceNotifications = deviceEnumerator.CreateNotificationClient(useSynchronizationContext: false);
            deviceNotifications.DeviceAdded += AudioDevicesChanged;
            deviceNotifications.DeviceRemoved += AudioDevicesChanged;
            deviceNotifications.DeviceStateChanged += AudioDevicesChanged;
            deviceNotifications.DefaultDeviceChanged += AudioDevicesChanged;
            deviceNotifications.PropertyValueChanged += AudioDevicesChanged;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            StopDeviceWatching();
            DeviceRefreshHint.Text = "Automatic device refresh is unavailable. Use Refresh devices.";
        }
    }
    private void StopDeviceWatching()
    {
        deviceNotifications?.Dispose(); deviceNotifications = null;
        deviceEnumerator?.Dispose(); deviceEnumerator = null;
    }
    private void AudioDevicesChanged(object? sender, EventArgs e)
    {
        // The Windows audio callback must not block, touch UI, or enumerate endpoints.
        Interlocked.Exchange(ref deviceChangeAt, Environment.TickCount64);
    }
    private void RefreshChangedDevices()
    {
        var changedAt = Interlocked.Read(ref deviceChangeAt);
        if (changedAt == 0 || Environment.TickCount64 - changedAt < 500 || busy || activeId is not null) return;
        if (Interlocked.CompareExchange(ref deviceChangeAt, 0, changedAt) != changedAt) return;
        try { RefreshDevices(); SetControls(); }
        catch (Exception ex)
        {
            DeviceRefreshHint.Text = "Could not refresh audio devices: " + ex.Message + " Retrying…";
            // Endpoint properties may briefly be unavailable during a plug/unplug transition.
            Interlocked.CompareExchange(ref deviceChangeAt, Environment.TickCount64 + 1500, 0);
        }
    }
    private void RefreshDevices() => ApplyDevices(CallRecorder.Devices());
    private void ApplyDevices(CaptureDevice[] devices)
    {
        lastOutputDevice = OutputDevice.SelectedItem as CaptureDevice ?? lastOutputDevice;
        lastMicrophoneDevice = MicrophoneDevice.SelectedItem as CaptureDevice ?? lastMicrophoneDevice;
        var outputId = lastOutputDevice?.Id ?? recorder.Preferences.OutputId;
        var microphoneId = lastMicrophoneDevice?.Id ?? recorder.Preferences.MicrophoneId;
        var outputs = devices.Where(d => d.Kind == "output").ToArray(); var microphones = devices.Where(d => d.Kind == "microphone").ToArray();
        void Update(ComboBox combo, CaptureDevice[] list, string? id, ref bool lost)
        {
            var selected = id is not null ? list.SingleOrDefault(d => d.Id == id)
                : list.FirstOrDefault(d => d.CommunicationsDefault) ?? list.FirstOrDefault(d => d.Default);
            lost = id is not null && selected is null;
            // Ignore irrelevant property notifications; do not rebuild an unchanged open menu.
            if (combo.ItemsSource is not CaptureDevice[] previous || !previous.SequenceEqual(list)) combo.ItemsSource = list;
            if (!Equals(combo.SelectedItem, selected)) combo.SelectedItem = selected;
        }
        refreshingDevices = true;
        try
        {
            Update(OutputDevice, outputs, outputId, ref outputSelectionLost);
            Update(MicrophoneDevice, microphones, microphoneId, ref microphoneSelectionLost);
        }
        finally { refreshingDevices = false; }
        UpdateDeviceRefreshHint();
    }
    private void UpdateDeviceRefreshHint()
    {
        DeviceRefreshHint.Text = outputSelectionLost || microphoneSelectionLost ? "Waiting for your selected devices to reconnect. They will be selected automatically when available, or you can choose other devices."
            : deviceNotifications is not null ? "Devices reconnect automatically. Use Windows defaults to load the current call devices, then Save changes to remember your selection."
            : "Automatic device refresh is unavailable. Use Refresh devices.";
        UpdateDevicePresentation();
    }
    private void UpdateDevicePresentation()
    {
        var output = OutputDevice.SelectedItem as CaptureDevice;
        var microphone = MicrophoneDevice.SelectedItem as CaptureDevice;
        lastOutputDevice = output ?? lastOutputDevice; lastMicrophoneDevice = microphone ?? lastMicrophoneDevice;
        string Placeholder(ComboBox combo, CaptureDevice? last, bool lost, string kind)
        {
            if (combo.SelectedItem is not null) return "";
            if (lost)
            {
                if (last is null) return $"Saved {kind} unavailable — waiting for reconnection";
                return $"Unavailable: {last.Name}";
            }
            return combo.Items.Count == 0 ? $"No {kind} devices available" : $"Choose {kind} device";
        }
        OutputDevicePlaceholder.Text = Placeholder(OutputDevice, lastOutputDevice, outputSelectionLost, "output");
        MicrophoneDevicePlaceholder.Text = Placeholder(MicrophoneDevice, lastMicrophoneDevice, microphoneSelectionLost, "microphone");
        if (activeId is not null)
        {
            // A shared-service capture may use different endpoints than this window's preferences.
            var capture = activeCaptureDevices?.Id == activeId ? activeCaptureDevices : null;
            output = capture?.Tracks.FirstOrDefault(t => t.TrackId == "call")?.Device ?? capture?.Device;
            microphone = capture?.Tracks.FirstOrDefault(t => t.TrackId == "mic")?.Device;
            RecordingOutput.Text = "Output: " + (output?.Name ?? "Checking active recording…");
            RecordingMicrophone.Text = "Mic: " + (microphone?.Name ?? (capture?.Version == 1 ? "Not recorded (output only)" : "Checking active recording…"));
        }
        else
        {
            RecordingOutput.Text = "Output: " + (output?.Name ?? OutputDevicePlaceholder.Text);
            RecordingMicrophone.Text = "Mic: " + (microphone?.Name ?? MicrophoneDevicePlaceholder.Text);
        }
        RecordingOutput.Foreground = output is null ? System.Windows.Media.Brushes.SaddleBrown : System.Windows.Media.Brushes.DarkSlateGray;
        RecordingMicrophone.Foreground = microphone is null ? System.Windows.Media.Brushes.SaddleBrown : System.Windows.Media.Brushes.DarkSlateGray;
    }
    private void SaveSettings()
    {
        recorder.Preferences.OutputId = (OutputDevice.SelectedItem as CaptureDevice)?.Id ?? recorder.Preferences.OutputId;
        recorder.Preferences.MicrophoneId = (MicrophoneDevice.SelectedItem as CaptureDevice)?.Id ?? recorder.Preferences.MicrophoneId;
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
        busy = true; workCancellation = new(); SetControls();
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
        if (Selected is not { } call)
        {
            transcript = []; TranscriptList.ItemsSource = transcript; SelectedTitle.Text = "Your recording";
            CoverageText.Clear(); SpeakerName.Clear(); RenderSummary(""); SetControls(); return;
        }
        SelectedTitle.Text = call.Title;
        transcript = recorder.Transcript(call.Capture.Id); FilterTranscript();
        CoverageText.Text = recorder.Coverage(call.Capture.Id); RenderSummary(recorder.Summary(call.Capture.Id));
        sliderUpdating = true; PlaybackPosition.Maximum = Math.Max(1, call.Capture.DurationSeconds); sliderUpdating = false;
        SetControls();
    }
    private void SetControls()
    {
        if (!IsInitialized) return;
        UpdateDevicePresentation();
        var capturing = activeId is not null;
        RecordButton.Visibility = capturing ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = capturing ? Visibility.Visible : Visibility.Collapsed;
        CancelRecordingButton.Visibility = capturing && ownsCapture && testDeadline is null ? Visibility.Visible : Visibility.Collapsed;
        CancelRecordingButton.IsEnabled = !busy && !closing;
        StopButton.Content = testDeadline is null ? "■ Stop and transcribe" : "■ Stop test";
        MuteInput.Visibility = LevelPanel.Visibility = capturing ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Visibility = busy && workCancellation is not null ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Content = preparingTranscription ? "Cancel setup" : "Pause processing";
        CancelSetupButton.Visibility = preparingTranscription ? Visibility.Visible : Visibility.Collapsed;
        CancelSetupButton.IsEnabled = busy && workCancellation is { IsCancellationRequested: false } && !closing;
        RetrySetupButton.IsEnabled = !busy && !capturing && !closing;
        TitleInput.IsEnabled = ConfigurationFields.IsEnabled = !busy && !capturing && !closing;
        RecordingHint.Text = capturing ? testDeadline is not null ? "Recording a short test. Stop, listen, then Keep or Delete."
            : ownsCapture ? "Recording both tracks. Cancel keeps the audio without starting transcription."
            : "A recording is active in the shared service. Stop will finalize it."
            : busy ? "Processing your recording. You can browse saved calls while you wait."
            : OutputDevice.SelectedItem is null || MicrophoneDevice.SelectedItem is null ? "Choose your call output and microphone in Configuration before recording."
            : "Record your call, then stop to transcribe automatically. Devices and languages are in Configuration.";
        RecordButton.IsEnabled = TestButton.IsEnabled = !busy && activeId is null && !closing;
        StopButton.IsEnabled = activeId is not null && !busy && !closing;
        MuteInput.IsEnabled = activeId is not null && !closing;
        OutputDevice.IsEnabled = MicrophoneDevice.IsEnabled = !busy && activeId is null;
        var inactive = Selected is { } selected && selected.Capture.State is not ("Starting" or "Recording" or "Finalizing");
        var actions = TranscriptActions.For(Selected?.Analysis);
        foreach (var (button, visible) in new[] { (TranscribeButton, actions.Transcribe), (ResumeTranscriptionButton, actions.Resume),
            (RetryTranscriptionButton, actions.RetryFailed), (RetryUncertainTranscriptionButton, actions.RetryUncertain) })
        {
            button.IsEnabled = inactive && !busy && activeId is null && !closing;
            button.Visibility = inactive && visible ? Visibility.Visible : Visibility.Collapsed;
        }
        var job = Selected?.Analysis;
        var hasSummary = job is not null && File.Exists(recorder.Analysis.Store.FileFor(job.Id, "summary.md"));
        var summaryPending = job is { Stage: "Summary", State: "Paused" or "Interrupted" or "Running" };
        var summaryOutdated = false;
        if (hasSummary && job is not null && Selected is { } saved)
        {
            var revision = new RecordingAnalysisJob { SummaryLanguage = (string?)SummaryLanguage.SelectedValue ?? recorder.Preferences.SummaryLanguage,
                SpeakerNames = recorder.Metadata(saved.Capture.Id).SpeakerNames };
            summaryOutdated = job.SummaryRevision != RecordingAnalysis.SummaryRevision(revision);
        }
        SummaryButton.Visibility = !hasSummary && !summaryPending ? Visibility.Visible : Visibility.Collapsed;
        ResumeSummaryButton.Visibility = summaryPending ? Visibility.Visible : Visibility.Collapsed;
        UpdateSummaryButton.Visibility = hasSummary && summaryOutdated && !summaryPending ? Visibility.Visible : Visibility.Collapsed;
        SummaryButton.IsEnabled = ResumeSummaryButton.IsEnabled = UpdateSummaryButton.IsEnabled = inactive && transcript.Length > 0 && !busy && activeId is null && !closing;
        SummaryCoverageNotice.Visibility = hasSummary && job is not null && (job.Coverage.Any(c => c.Kind is not ("InferredSilence" or "IntentionalMute")) || job.State == "CompletedWithGaps") ? Visibility.Visible : Visibility.Collapsed;
        SummaryDetails.Header = SummaryCoverageNotice.Visibility == Visibility.Visible ? "Summary details · Audio warnings" : "Summary details";
        SummaryDisclosure.Visibility = transcript.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteRecordingButton.IsEnabled = DeleteAnalysisButton.IsEnabled = inactive && !busy && !closing;
        ExportButton.IsEnabled = inactive && Selected?.Analysis is not null && !busy;
        RawExportButton.IsEnabled = inactive && !busy;
        PauseButton.IsEnabled = busy && workCancellation is { IsCancellationRequested: false } && !closing;
        PlayButton.IsEnabled = inactive && activeId is null;
        UpdatePlaybackLabel();
        PlaybackPanel.Visibility = inactive && !capturing ? Visibility.Visible : Visibility.Collapsed;
        var test = Selected is { Metadata.Test: true, Metadata.Kept: false };
        KeepButton.Visibility = DeleteTestButton.Visibility = test ? Visibility.Visible : Visibility.Collapsed;
        KeepButton.IsEnabled = DeleteTestButton.IsEnabled = inactive && !busy && !closing;
    }
    private void RefreshDefaultTitle()
    {
        if (!string.IsNullOrWhiteSpace(TitleInput.Text) && TitleInput.Text != suggestedTitle && TitleInput.Text != "New call") return;
        suggestedTitle = "New call — " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        TitleInput.Text = suggestedTitle;
    }
    private async Task Start(bool test)
    {
        if (busy || activeId is not null) return;
        if (OutputDevice.SelectedItem is null || MicrophoneDevice.SelectedItem is null)
        { WorkspaceTabs.SelectedItem = ConfigurationTab; throw new UserFacingException("Select both call output and microphone in Configuration."); }
        SaveSettings(); playback?.Dispose(); playback = null;
        busy = true; SetControls();
        try
        {
            RefreshDefaultTitle();
            var capture = await recorder.Start(TitleInput.Text, test, CancellationToken.None);
            activeId = capture.Id; activeCaptureDevices = capture; ownsCapture = true; MuteInput.IsChecked = false;
            Elapsed.Text = "00:00:00"; WorkspaceTabs.SelectedItem = RecordingTab;
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
        StatusText.Text = capture.State == "Completed" && !string.IsNullOrWhiteSpace(capture.Error)
            ? "Recording saved with audio warnings. See Details & export for coverage."
            : $"Recording {capture.State}. Audio is saved.";
        if (recorder.Metadata(id).Test) WorkspaceTabs.SelectedItem = ReviewTab;
        else if (transcribe && capture.State == "Completed") await Transcribe(id, false);
    }
    private async Task Transcribe(string id, bool allowInterrupted, bool retryFailed = false, bool retryUncertain = false)
    {
        await PrepareTranscription();
        SaveSettings();
        OperationBanner.Visibility = Visibility.Visible; RetrySetupButton.Visibility = Visibility.Collapsed;
        OperationTitle.Text = "Transcribing recording";
        OperationMessage.Text = "Preparing saved audio for local transcription…";
        var progress = new Progress<string>(message => { if (busy) { StatusText.Text = message; OperationMessage.Text = message; } });
        try
        {
            await Run(async ct => {
                var result = await recorder.Transcribe(id, allowInterrupted, retryFailed, retryUncertain, message => ((IProgress<string>)progress).Report(message), ct);
                await Dispatcher.InvokeAsync(() => {
                    StatusText.Text = $"Transcription {result.State} · {result.ProcessedSeconds:0}/{result.DurationSeconds * result.TrackCount:0} track-seconds. {result.Error}";
                    if (result.State == "Paused" || !string.IsNullOrWhiteSpace(result.Error))
                    {
                        OperationTitle.Text = "Transcription needs attention";
                        OperationMessage.Text = StatusText.Text + " Open Review for the available resume or retry action.";
                    }
                    else OperationBanner.Visibility = Visibility.Collapsed;
                });
            });
        }
        catch (Exception ex)
        {
            OperationTitle.Text = ex is OperationCanceledException ? "Transcription paused" : "Transcription could not finish";
            OperationMessage.Text = ex is OperationCanceledException ? "Completed work is saved. Select Resume transcription to continue." : ex.Message;
            throw;
        }
    }
    private async Task Tick()
    {
        if (polling || closing) return; polling = true;
        try
        {
            RefreshChangedDevices();
            if (playback is not null) { sliderUpdating = true; PlaybackPosition.Value = playback.Position; sliderUpdating = false; UpdatePlaybackLabel(); }
            if (activeId is { } id)
            {
                var status = await recorder.CaptureStatus(id);
                activeCaptureDevices = status; UpdateDevicePresentation();
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
    private async void CancelRecordingClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (busy || !ownsCapture) return;
        await Stop(false);
        StatusText.Text = "Recording cancelled. Audio is kept in the library; transcription has not started.";
    });
    private async Task RefreshModel()
    {
        ModelLabel.Text = await Task.Run(recorder.ModelDescription);
        SummaryDisclosure.Text = $"{ModelLabel.Text}\nSummaries send transcript evidence to this provider. Audio stays local.";
    }
    private async void RefreshClick(object sender, RoutedEventArgs e) => await Guard(async () => { if (!busy && activeId is null) RefreshDevices(); RefreshLibrary(); await RefreshModel(); });
    private void DeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || refreshingDevices || !IsInitialized) return;
        if (sender == OutputDevice && OutputDevice.SelectedItem is CaptureDevice) outputSelectionLost = false;
        if (sender == MicrophoneDevice && MicrophoneDevice.SelectedItem is CaptureDevice) microphoneSelectionLost = false;
        UpdateDeviceRefreshHint(); SetControls();
    }
    private async void SaveConfigurationClick(object sender, RoutedEventArgs e) => await Guard(() =>
    { SaveSettings(); StatusText.Text = "Changes saved. Your selected devices will be restored automatically when available."; return Task.CompletedTask; });
    private async void UseWindowsDefaultsClick(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        if (busy || activeId is not null) return Task.CompletedTask;
        ApplyWindowsDefaults(CallRecorder.Devices());
        SetControls();
        StatusText.Text = "Loaded Windows call devices (communications defaults, with system defaults as fallback). Select Save changes to remember them.";
        return Task.CompletedTask;
    });
    private void ApplyWindowsDefaults(CaptureDevice[] devices)
    {
        ApplyDevices(devices);
        CaptureDevice? Default(string kind) => devices.FirstOrDefault(d => d.Kind == kind && d.CommunicationsDefault)
            ?? devices.FirstOrDefault(d => d.Kind == kind && d.Default);
        OutputDevice.SelectedItem = Default("output");
        MicrophoneDevice.SelectedItem = Default("microphone");
        UpdateDeviceRefreshHint();
    }
    private async void MuteClick(object sender, RoutedEventArgs e) => await Guard(async () => { if (activeId is { } id) await recorder.Mute(id, MuteInput.IsChecked == true); });
    private async void TranscribeClick(object sender, RoutedEventArgs e) => await TranscribeSelected("start");
    private async void ResumeTranscriptionClick(object sender, RoutedEventArgs e) => await TranscribeSelected("resume");
    private async void RetryTranscriptionClick(object sender, RoutedEventArgs e) => await TranscribeSelected("failed");
    private async void RetryUncertainTranscriptionClick(object sender, RoutedEventArgs e) => await TranscribeSelected("uncertain");
    private Task TranscribeSelected(string action) => Guard(async () =>
    {
        if (Selected is not { } call) return;
        var actions = TranscriptActions.For(call.Analysis);
        if (!(action switch { "start" => actions.Transcribe, "resume" => actions.Resume, "failed" => actions.RetryFailed, "uncertain" => actions.RetryUncertain, _ => false })) return;
        var interrupted = call.Capture.State == "Interrupted";
        if (interrupted && MessageBox.Show(this, "This recording has missing or uncertain audio.\n\n" +
            (call.Capture.Error is { Length: > 0 } reason ? reason + "\n\n" : "") +
            "Analyze the retained parts?", "Interrupted recording", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        await Transcribe(call.Capture.Id, interrupted, action == "failed", action == "uncertain");
    });
    private void PauseClick(object sender, RoutedEventArgs e)
    { workCancellation?.Cancel(); PauseButton.IsEnabled = CancelSetupButton.IsEnabled = false; StatusText.Text = preparingTranscription ? "Cancelling setup; retaining verified downloads…" : "Pausing processing; saving completed work…"; }
    private async void SummaryClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (Selected is not { } call) return;
        WorkspaceTabs.SelectedItem = RecordingTab;
        SaveSettings(); var retry = sender == ResumeSummaryButton && RetryUncertain.IsChecked == true; StatusText.Text = "Generating cited summary…";
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
    private void UpdatePlaybackLabel() => PlayButton.Content = playback?.AtEnd == true ? "Replay" : playback?.Playing == true ? "Pause" : "Play";
    private async void PlayClick(object sender, RoutedEventArgs e) => await Guard(() => { EnsurePlayback(); playback?.Toggle(); return Task.CompletedTask; });
    private async void PlaybackSeek(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (!sliderUpdating && IsLoaded) await Guard(() => { EnsurePlayback(); playback?.Seek(e.NewValue); return Task.CompletedTask; }); }
    private void PlaybackTrackChanged(object sender, SelectionChangedEventArgs e) => playback?.Select(PlaybackTrack.SelectedIndex);
    private async Task PlayAt(double seconds) => await Guard(() => { EnsurePlayback(); WorkspaceTabs.SelectedItem = ReviewTab; ResultsTabs.SelectedItem = TranscriptTab; playback?.Seek(seconds); if (playback?.Playing == false) playback.Toggle(); return Task.CompletedTask; });
    private async void TranscriptDoubleClick(object sender, MouseButtonEventArgs e) { if (TranscriptList.SelectedItem is TranscriptLine row) await PlayAt(row.Start); }
    private async void TranscriptKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && TranscriptList.SelectedItem is TranscriptLine row) { e.Handled = true; await PlayAt(row.Start); } }
    private void SummaryLanguageChanged(object sender, SelectionChangedEventArgs e)
    { if (!loading && recorder is not null && SummaryLanguage.SelectedValue is string code) { recorder.Preferences.SummaryLanguage = code; if (Selected is { } call) RenderSummary(recorder.Summary(call.Capture.Id)); SetControls(); } }
    private void RenderSummary(string text)
    {
        if (Selected?.Analysis is not { } analysis || !File.Exists(recorder.Analysis.Store.FileFor(analysis.Id, "summary.md"))) text = "";
        SummaryPlaceholder.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;
        SummaryView.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        SummaryDetails.IsExpanded = string.IsNullOrWhiteSpace(text) && transcript.Length > 0;
        SummaryDetails.Visibility = transcript.Length > 0 || !string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;
        SummaryPlaceholder.Text = Selected is null ? "Your summary will appear here. Record a call or select one from the library to get started."
            : transcript.Length > 0 ? "Your transcript is ready. Click Generate summary to summarize this call."
            : activeId is not null ? "Stop recording to transcribe your call. Then generate a summary when you are ready."
            : "Transcription is pending. Open Review to transcribe or resume this recording.";
        SummaryView.Document = SummaryDocument.Create(text, transcript, seconds => _ = PlayAt(seconds));
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closingAllowed) return;
        if (!closing && !busy && work is null && !(activeId is not null && ownsCapture) && !recorder.HasOwnedService)
        {
            // Nothing needs an asynchronous stop: allow this original close request to finish.
            closing = true; closingAllowed = true; return;
        }
        e.Cancel = true; if (closing) return;
        if (activeId is not null && ownsCapture && MessageBox.Show(this, "Stop recording and exit? Transcription will remain pending. Choose Cancel to keep recording.", "Stop and exit", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        if (work is not null && MessageBox.Show(this, preparingTranscription ? "Cancel setup and exit? Verified downloads and the previous installation will be retained." : "Pause processing and exit? Completed work will be saved.", preparingTranscription ? "Cancel setup and exit" : "Pause and exit", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        if (busy && work is null) { StatusText.Text = "Wait for the current recording command to finish before closing."; return; }
        closing = true; SetControls();
        try
        {
            if (activeId is not null && ownsCapture) await Stop(false);
            workCancellation?.Cancel(); if (work is { } pending) { try { await pending; } catch (OperationCanceledException) { } }
            StatusText.Text = "Closing audio service…";
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                await recorder.ReleaseService(timeout.Token);
            // Stop/cancellation can finish synchronously. Re-enter Close only after this event returns.
            _ = Dispatcher.BeginInvoke(new Action(() => { closingAllowed = true; Close(); }));
        }
        catch (Exception ex) { closingAllowed = false; closing = false; StatusText.Text = "Could not finish closing: " + ex.Message; SetControls(); }
    }
}
