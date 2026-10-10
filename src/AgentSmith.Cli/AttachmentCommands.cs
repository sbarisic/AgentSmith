using AgentSmith.Core;

namespace AgentSmith.Cli;

internal sealed partial class CliApp
{
    private UserPrompt? nextPrompt;
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
                if (text.Trim().Equals("/exit", StringComparison.OrdinalIgnoreCase)) return null;
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
        currentConversation = session.Id;
        input.Notice = message => Write("paste", new { message });
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
