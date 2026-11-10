using System.Windows;
using AgentSmith.Application;
using AgentSmith.Core;

namespace AgentSmith.Desktop;

public partial class MainWindow
{
    private async Task CheckAgentSettings()
    {
        // Disposable fixture only. Exercise actual controls and stale-result handling without a provider.
        Navigate("Recordings");
        if (WorkspaceTabs.Visibility != Visibility.Collapsed || LibraryOpenHint.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Expanded library navigation failed.");
        WriteSmokePreview("library.png", new Size(1264, 861));
        Navigate("Configuration");
        if (LibraryPanel.Visibility != Visibility.Collapsed || WorkspaceTabs.SelectedItem != ConfigurationTab)
            throw new InvalidOperationException("Configuration navigation failed.");
        recorder.SavePreferences(new()); AgentModel.Text = ""; SaveSettings(saveAgent: true);
        if (recorder.Preferences.SummaryAgent is not null) throw new InvalidOperationException("Saving audio settings invented an agent.");
        AgentProvider.SelectedValue = ModelProvider.Local; AgentEndpoint.Text = "http://127.0.0.1:8081/v1"; AgentModel.Text = "fixture-model";
        SaveSettings(saveAgent: true);
        AgentModel.Text = "unsaved-model"; SaveSettings();
        if (recorder.SummaryAgent().Model != "fixture-model") throw new InvalidOperationException("Draft model leaked into saved settings.");
        AgentEndpoint.Text = "https://example.test/v1"; AgentModel.Text = "invalid-draft";
        try { SaveSettings(saveAgent: true); throw new InvalidOperationException("Invalid endpoint was saved."); }
        catch (UserFacingException) { }
        if (recorder.SummaryAgent().Model != "fixture-model") throw new InvalidOperationException("Invalid draft replaced the saved agent.");
        AgentEndpoint.Text = "http://127.0.0.1:8081/v1";
        var oldDiscovery = discoverModels;
        try
        {
            var late = new TaskCompletionSource<IReadOnlyList<RecorderModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
            discoverModels = (_, _, _, _) => late.Task;
            var pending = RefreshAgentModels();
            AgentEndpoint.Text = "http://127.0.0.1:8082/v1"; AgentModel.Text = "current-draft";
            late.SetResult([new("stale", "Stale result")]); await pending;
            if (AgentModel.Items.Count != 0 || AgentModel.Text != "current-draft") throw new InvalidOperationException("Stale model catalog replaced a newer draft.");
            discoverModels = (_, _, _, _) => Task.FromException<IReadOnlyList<RecorderModel>>(new System.Net.Http.HttpRequestException("Fixture offline"));
            await RefreshAgentModels();
            if (!AgentFeedback.Text.Contains("Fixture offline")) throw new InvalidOperationException("Catalog failure was hidden.");
            AgentModel.Text = "offline-model"; SaveSettings(saveAgent: true);
            if (recorder.SummaryAgent().Model != "offline-model") throw new InvalidOperationException("Network failure prevented offline saving.");
            discoverModels = async (_, _, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return []; };
            var cancelled = RefreshAgentModels(); CancelAgentClick(this, new RoutedEventArgs()); await cancelled;
            if (CancelAgentRequest.Visibility != Visibility.Collapsed || !AgentFeedback.Text.Contains("cancelled")) throw new InvalidOperationException("Catalog cancellation did not settle.");
            discoverModels = (_, _, _, _) => Task.FromResult<IReadOnlyList<RecorderModel>>([new("fixture-model", "Fixture model"), new("unloaded-model", "Unloaded · not loaded", false)]);
            await RefreshAgentModels(); AgentModel.SelectedValue = "fixture-model";
            if (AgentModel.Text != "fixture-model") throw new InvalidOperationException("Model selection did not use the model ID.");
            SaveSettings(saveAgent: true); await RefreshModel();
            AgentFeedback.Text = "Saved for this recorder. CLI defaults are unchanged.";
        }
        finally { discoverModels = oldDiscovery; }
        Navigate("Home");
        if (WorkspaceTabs.SelectedItem != RecordingTab || LibraryPanel.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Home navigation failed.");
    }
}
