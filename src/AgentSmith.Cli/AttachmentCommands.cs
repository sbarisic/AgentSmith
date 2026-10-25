using AgentSmith.Core;

namespace AgentSmith.Cli;

internal sealed partial class CliApp
{
    private UserPrompt? nextPrompt;
    private System.Text.Json.Nodes.JsonObject[] DisplayHistory(Session session)
    {
        var attachments = Store.Attachments(session.Id);
        return session.History.Select((item, index) =>
        {
            var display = item.DeepClone().AsObject();
            display["_workflow"] = session.MessageWorkflows.TryGetValue(index, out var workflow) ? workflow.ToString() : null;
            if (display["role"]?.ToString() == "user" && display["content"] is System.Text.Json.Nodes.JsonArray)
                display["content"] = PromptPresentation.Content(display["content"], attachments);
            if (display["role"]?.ToString() == "assistant" && display["content"] is System.Text.Json.Nodes.JsonArray)
                display["content"] = WebCitations.Message(display);
            return display;
        }).ToArray();
    }
    internal ConsoleInput Composer => input;
    internal async Task<string?> ReadResumeDecision(Session session, CancellationToken ct)
    {
        input.ResumeConfirmation = true;
        try
        {
            var attentionKey = "resume:" + session.Runs.LastOrDefault()?.Id;
            var text = await ReadInput(ct, attentionKey);
            while (text?.TrimStart().StartsWith('/') == true)
            {
                if (text.Trim().Equals("/exit", StringComparison.OrdinalIgnoreCase)) { await RequestExit(session); return null; }
                await HandleCommand(text, session);
                if (session.Runs.LastOrDefault()?.State == RunState.Cancelled) return null;
                text = await ReadInput(ct, attentionKey);
            }
            return text;
        }
        finally { input.ResumeConfirmation = false; input.ClearSubmission(); }
    }
    internal void SetConversation(Session session)
    {
        if (activeRun is null) ApplyCaptureSelection(session);
        currentConversation = session.Id;
        input.SetContext(session);
        input.SetHistory(session);
        input.Notice = message => Write("paste", new { message });
        input.ImageAvailable = id => Store.Attachments(session.Id).Any(a => a.Id == id && File.Exists(a.Path));
        input.ImportImage = content =>
        {
            if (!session.Connection.Capabilities.Vision) throw new UserFacingException("Selected model does not advertise image input.");
            return AttachmentFiles.Import(Store, session.Id, content.Image!, content.Width, content.Height);
        };
    }
    internal void AttachFile(Session session, string path)
    {
        SetConversation(session);
        if (input.Compose().Parts.Count(p => p.Kind == "image") >= 4) throw new UserFacingException("Four images already attached.");
        var decoded = WindowsClipboard.ReadFile(path.Trim('"'));
        var item = input.ImportImage!(decoded); input.AddImage(item); input.Refresh();
        Write("attachment.added", new { item.Id, item.Width, item.Height, item.Bytes, message = $"Image attached · {item.Width}×{item.Height}. Enter sends it with your message." });
    }
    private UserPrompt? TakeSubmittedPrompt()
    {
        var value = input.SubmittedPrompt; input.ClearSubmission(); return value;
    }
    private string? PasteNote()
    {
        try { return input.Clipboard.Read(true).Text; }
        catch (UserFacingException ex) { Write("error", new { message = ex.Message }); return null; }
    }
}
