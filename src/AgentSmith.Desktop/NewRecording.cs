using System.Windows;
using System.Windows.Controls;
using AgentSmith.Tools;

namespace AgentSmith.Desktop;

public partial class MainWindow
{
    private bool newRecordingDraft;
    private string? draftOutputId, draftMicrophoneId;
    private void BeginNewRecording()
    {
        newRecordingDraft = true;
        LibraryList.SelectedItem = null;
        TitleInput.Clear(); RefreshDefaultTitle(); Elapsed.Text = "00:00:00";
        draftOutputId = recorder.Preferences.OutputId;
        draftMicrophoneId = recorder.Preferences.MicrophoneId;
        RefreshDraftDevices(AgentSmith.Application.CallRecorder.Devices(), reset: true);
        Navigate("Home"); WorkspaceTabs.SelectedItem = RecordingTab;
        SetControls(); TitleInput.Focus(); TitleInput.SelectAll();
    }
    private void RefreshDraftDevices(CaptureDevice[] devices, bool reset = false)
    {
        if (!reset)
        {
            draftOutputId = (DraftOutput.SelectedItem as CaptureDevice)?.Id ?? draftOutputId ?? recorder.Preferences.OutputId;
            draftMicrophoneId = (DraftMicrophone.SelectedItem as CaptureDevice)?.Id ?? draftMicrophoneId ?? recorder.Preferences.MicrophoneId;
        }
        void Fill(ComboBox box, string kind, string? id)
        {
            var items = devices.Where(d => d.Kind == kind).ToArray();
            box.ItemsSource = items;
            box.SelectedItem = id is null ? items.FirstOrDefault(d => d.CommunicationsDefault) ?? items.FirstOrDefault(d => d.Default) : items.FirstOrDefault(d => d.Id == id);
            box.ToolTip = box.SelectedItem is null ? "Selected device unavailable. Reconnect it or choose another device." : ((CaptureDevice)box.SelectedItem).Name;
        }
        Fill(DraftOutput, "output", draftOutputId); Fill(DraftMicrophone, "microphone", draftMicrophoneId);
    }
}
