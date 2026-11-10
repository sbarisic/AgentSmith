using System.Windows;
using System.Windows.Controls;
using AgentSmith.Application;
using AgentSmith.Core;

namespace AgentSmith.Desktop;

public partial class MainWindow
{
    private RecorderAgents agents = null!;
    private CancellationTokenSource? agentCancellation;
    private long agentRevision;
    private bool loadingAgent, signingIn;
    private int agentOutputTokens = 16384;
    private Func<ModelProvider, string, string, CancellationToken, Task<IReadOnlyList<RecorderModel>>> discoverModels = null!;
    private sealed record ProviderChoice(ModelProvider Provider, string Label);
    private ModelProvider DraftProvider => AgentProvider.SelectedValue is ModelProvider provider ? provider : ModelProvider.ChatGpt;

    private void InitializeAgentSettings()
    {
        agents = new(recorder.DataDirectory); discoverModels = agents.Models;
        loadingAgent = true;
        try
        {
            AgentProvider.ItemsSource = new[] { new ProviderChoice(ModelProvider.ChatGpt, "ChatGPT"), new ProviderChoice(ModelProvider.LmStudio, "LM Studio"), new ProviderChoice(ModelProvider.Local, "Local") };
            ChatModelSelection? selected = null;
            try { selected = recorder.SummaryAgent(); } catch (UserFacingException) { }
            AgentAccount.ItemsSource = agents.Accounts();
            AgentProvider.SelectedValue = selected?.Provider ?? ModelProvider.ChatGpt;
            AgentAccount.SelectedValue = string.IsNullOrEmpty(selected?.AccountId) ? agents.ActiveAccountId() : selected.AccountId;
            AgentEndpoint.Text = selected?.Endpoint ?? RecordingModels.DefaultEndpoint(DraftProvider);
            AgentModel.Text = selected?.Model ?? "";
            agentOutputTokens = selected?.MaxOutputTokens ?? 16384;
            UpdateAgentFields();
        }
        catch (Exception ex) { AgentProvider.SelectedValue = ModelProvider.ChatGpt; AgentFeedback.Text = "Could not load account settings: " + ex.Message; UpdateAgentFields(); }
        finally { loadingAgent = false; }
    }
    private void UpdateAgentFields()
    {
        if (AgentAccountPanel is null) return;
        AgentAccountPanel.Visibility = DraftProvider == ModelProvider.ChatGpt ? Visibility.Visible : Visibility.Collapsed;
        AgentEndpointPanel.Visibility = DraftProvider == ModelProvider.ChatGpt ? Visibility.Collapsed : Visibility.Visible;
        AgentReauthenticate.IsEnabled = AgentAccount.SelectedItem is RecorderAccount;
    }
    private void InvalidateAgentRequest(bool clearModel = true)
    {
        agentRevision++; agentCancellation?.Cancel();
        if (clearModel) { AgentModel.ItemsSource = null; AgentModel.Text = ""; }
        AgentFeedback.Text = "Unsaved changes. Refresh models or enter a model ID, then Save changes.";
        UpdateAgentFields();
    }
    private void AgentProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingAgent || AgentEndpoint is null) return;
        InvalidateAgentRequest();
        loadingAgent = true;
        AgentEndpoint.Text = RecordingModels.DefaultEndpoint(DraftProvider);
        agentOutputTokens = DraftProvider == ModelProvider.ChatGpt ? 16384 : 8192;
        loadingAgent = false;
    }
    private void AgentConnectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!loadingAgent && AgentModel is not null) InvalidateAgentRequest(); }
    private void AgentEndpointChanged(object sender, TextChangedEventArgs e)
    { if (!loadingAgent && AgentModel is not null) InvalidateAgentRequest(); }
    private ChatModelSelection? DraftAgent()
    {
        // Audio-only installations can save device settings before choosing any summary agent.
        if (string.IsNullOrWhiteSpace(AgentModel.Text) && recorder.Preferences.SummaryAgent is null)
        {
            try { _ = recorder.SummaryAgent(); }
            catch (UserFacingException) { return null; }
        }
        var selected = new ChatModelSelection(AgentModel.Text.Trim(), DraftProvider,
            DraftProvider == ModelProvider.ChatGpt ? RecordingModels.DefaultEndpoint(ModelProvider.ChatGpt) : AgentEndpoint.Text.Trim(),
            DraftProvider == ModelProvider.ChatGpt ? (string?)AgentAccount.SelectedValue ?? "" : "", agentOutputTokens);
        RecorderAgents.Validate(selected);
        if (AgentModel.ItemsSource is IEnumerable<RecorderModel> models && models.Any(m => m.Id == selected.Model && !m.Available))
            throw new UserFacingException("Load this model in LM Studio, then refresh models before saving.");
        return selected;
    }
    private async void AgentRefreshClick(object sender, RoutedEventArgs e) => await RefreshAgentModels();
    private async Task RefreshAgentModels()
    {
        if (busy || closing || signingIn) return;
        agentCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        agentCancellation = cancellation; var revision = ++agentRevision;
        var provider = DraftProvider; var endpoint = AgentEndpoint.Text.Trim(); var account = (string?)AgentAccount.SelectedValue ?? "";
        var modelId = AgentModel.Text;
        AgentFeedback.Text = "Connecting and loading models… No transcript is sent."; CancelAgentRequest.Visibility = Visibility.Visible;
        try
        {
            var models = await discoverModels(provider, endpoint, account, cancellation.Token);
            if (revision != agentRevision || cancellation.IsCancellationRequested || closing) return;
            AgentModel.ItemsSource = models; AgentModel.Text = modelId;
            AgentFeedback.Text = models.Count == 0 ? "Connected, but no models were returned. Load a model on the server and refresh."
                : $"Connected · {models.Count(m => m.Available)} available models. Choose a model and Save changes.";
        }
        catch (Exception ex)
        {
            if (revision != agentRevision || closing) return;
            AgentFeedback.Text = ex is OperationCanceledException ? "Connection check cancelled or timed out. Saved settings are unchanged."
                : "Could not load models: " + ex.Message + " You can still enter a model ID and save.";
        }
        finally
        {
            if (ReferenceEquals(agentCancellation, cancellation)) { agentCancellation = null; CancelAgentRequest.Visibility = Visibility.Collapsed; }
        }
    }
    private async void AgentSignInClick(object sender, RoutedEventArgs e) => await SignInAgent(null);
    private async void AgentReauthenticateClick(object sender, RoutedEventArgs e)
    { if (AgentAccount.SelectedValue is string id) await SignInAgent(id); }
    private async Task SignInAgent(string? accountId)
    {
        if (busy || closing || signingIn) return;
        InvalidateAgentRequest(false);
        using var cancellation = new CancellationTokenSource(); agentCancellation = cancellation;
        var revision = ++agentRevision;
        signingIn = true; SetControls(); AgentConnectionFields.IsEnabled = false; CancelAgentRequest.Visibility = Visibility.Visible;
        AgentFeedback.Text = "Continue in your browser. Sign-in waits until you finish or select Cancel.";
        try
        {
            var account = await agents.SignIn(accountId, _ => { }, cancellation.Token);
            if (closing || revision != agentRevision || cancellation.IsCancellationRequested) return;
            loadingAgent = true;
            AgentAccount.ItemsSource = agents.Accounts(); AgentAccount.SelectedValue = account.Id;
            AgentModel.ItemsSource = null; AgentModel.Text = "";
            loadingAgent = false;
            AgentFeedback.Text = "Signed in. Refresh models, select one, and Save changes to use it for summaries.";
        }
        catch (Exception ex) { if (!closing) AgentFeedback.Text = ex is OperationCanceledException ? "Sign-in cancelled. Saved selection unchanged." : "Sign-in failed: " + ex.Message; }
        finally
        {
            loadingAgent = signingIn = false; AgentConnectionFields.IsEnabled = true;
            if (ReferenceEquals(agentCancellation, cancellation)) { agentCancellation = null; CancelAgentRequest.Visibility = Visibility.Collapsed; }
            UpdateAgentFields(); SetControls();
        }
    }
    private void CancelAgentClick(object sender, RoutedEventArgs e) => agentCancellation?.Cancel();
    private bool SummaryAgentChanged()
    {
        if (Selected?.Analysis?.SummaryModel is not { } identity) return false;
        try { return identity != CallRecorder.AgentIdentity(recorder.SummaryAgent()); }
        catch (UserFacingException) { return false; }
    }
    private void UpdateSummaryAgentPresentation()
    {
        SavedAgentLabel.Text = recorder.ModelDescription();
        var completed = Selected?.Analysis?.CompletedSummaryModel ?? Selected?.Analysis?.SummaryModel;
        var generated = completed is null ? "" : "Displayed summary agent: " + DescribeIdentity(completed) + "\n";
        var hasSummary = Selected?.Analysis is { } job && System.IO.File.Exists(recorder.Analysis.Store.FileFor(job.Id, "summary.md"));
        SummaryDisclosure.Text = (hasSummary ? generated : "") + "Selected agent: " + SavedAgentLabel.Text + "\nSummaries send transcript evidence to this provider. Audio stays local.";
    }
    private static string DescribeIdentity(string identity)
    {
        var parts = identity.Split('|');
        return parts.Length >= 3 ? parts[0] + " / " + parts[2] : identity;
    }
}
