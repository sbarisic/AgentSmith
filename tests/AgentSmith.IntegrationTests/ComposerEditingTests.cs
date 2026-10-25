using System.Text.Json.Nodes;
using AgentSmith.Cli;
using AgentSmith.Core;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class ComposerEditingTests
{
    [Fact]
    public void DecisionCommandsAcceptEmptyReturnAndRestoreThePriorDraft()
    {
        var input = new ConsoleInput(false); input.Restore("prior draft");
        using (input.SuspendDraft())
        {
            var enter = input.HandleKey(new('\r', ConsoleKey.Enter, false, false, false));
            Assert.True(enter.Ready); Assert.Equal("", enter.Text); Assert.Null(input.SubmittedPrompt);
            input.Restore("/status");
        }
        Assert.Equal("prior draft", input.Draft); Assert.False(input.DecisionCommands);
        Assert.False(input.HandleKey(new('\u0004', ConsoleKey.D, false, false, true)).Ready);
        Assert.False(input.EndOfInput);
    }
    private static ConsoleKeyInfo Key(ConsoleKey key, char character = '\0', bool control = false) => new(character, key, false, false, control);
    private static void Press(ConsoleInput input, ConsoleKey key) => input.HandleKey(Key(key));

    [Fact]
    public void CompletionNeedsSecondEnterAndNeverApprovesOrSubmits()
    {
        var input = new ConsoleInput(false); input.Restore("/ex");
        Assert.True(input.SuggestionCount > 0);
        var complete = input.HandleKey(Key(ConsoleKey.Enter));
        Assert.False(complete.Ready); Assert.False(complete.Cancel); Assert.Null(input.SubmittedPrompt);
        Assert.Equal("/exit", input.Draft); Assert.Equal(0, input.SuggestionCount);
        var submit = input.HandleKey(Key(ConsoleKey.Enter));
        Assert.True(submit.Ready); Assert.Equal("/exit", submit.Text);
    }

    [Fact]
    public void NavigationIsBoundedEscapeDismissesAndControlCCancelsImmediately()
    {
        var input = new ConsoleInput(false); input.Restore("/");
        var count = input.SuggestionCount; Assert.True(count > 5);
        Press(input, ConsoleKey.UpArrow); Assert.Equal(0, input.SelectedSuggestion);
        for (var i = 0; i < count + 5; i++) Press(input, ConsoleKey.DownArrow);
        Assert.Equal(count - 1, input.SelectedSuggestion);
        Assert.True(input.HandleKey(Key(ConsoleKey.C, '\u0003', true)).Cancel);
        Assert.Equal("/", input.Draft); Assert.True(input.SuggestionCount > 0);
        Assert.False(input.HandleKey(Key(ConsoleKey.Escape)).Cancel); Assert.Equal(0, input.SuggestionCount);
        Assert.True(input.HandleKey(Key(ConsoleKey.Escape)).Cancel);
        input.HandleKey(Key(ConsoleKey.H, 'h')); Assert.True(input.SuggestionCount > 0);
    }

    [Fact]
    public void CompletionAtCursorPreservesSuffixAndArgumentRequiresSecondEnter()
    {
        var input = new ConsoleInput(false); input.Restore("/display v tail");
        Press(input, ConsoleKey.Home);
        for (var i = 0; i < 10; i++) Press(input, ConsoleKey.RightArrow);
        Assert.Equal(1, input.SuggestionCount);
        Assert.False(input.HandleKey(Key(ConsoleKey.Tab)).Ready);
        Assert.Equal("/display verbose tail", input.Draft);
        Assert.True(input.HandleKey(Key(ConsoleKey.Enter)).Ready);
    }

    [Fact]
    public void LiteralPasteFocusedBlocksAndDecisionInputsSuppressCompletionAndHistory()
    {
        var input = new ConsoleInput(false); input.AcceptPrompt(UserPrompt.Text("old"), false);
        input.PasteText("/ex"); input.Refresh(); Assert.Equal(0, input.SuggestionCount);
        Assert.True(input.HandleKey(Key(ConsoleKey.Enter)).Ready); Assert.True(input.SubmittedLiteral);
        input.ClearDraft(); input.PasteText("multi\nline"); Press(input, ConsoleKey.Tab);
        Press(input, ConsoleKey.UpArrow); Assert.Equal("multi\nline", input.Compose().PlainText);
        Assert.False(input.HandleKey(Key(ConsoleKey.Enter)).Ready);
        input.Restore("/ex"); input.EditingEnabled = false; input.Refresh();
        Assert.Equal(0, input.SuggestionCount); Press(input, ConsoleKey.UpArrow); Assert.Equal("/ex", input.Draft);
        input.EditingEnabled = true; input.ResumeConfirmation = true; input.Refresh();
        Assert.Equal(0, input.SuggestionCount); Press(input, ConsoleKey.UpArrow); Assert.Equal("/ex", input.Draft);
    }

    [Fact]
    public void HistoryIsBoundedSeededOnceClonedAndConversationScoped()
    {
        var session = new Session();
        session.History.Add(new JsonObject { ["role"] = "assistant", ["content"] = "not a prompt" });
        for (var i = 0; i < 105; i++) session.History.Add(UserPrompt.Text("prompt " + i).HistoryItem());
        var input = new ConsoleInput(false); input.SetHistory(session); Assert.Equal(100, input.HistoryCount);
        input.AcceptPrompt(UserPrompt.Text("/exit"), false); input.AcceptPrompt(UserPrompt.Text(" "), false);
        Assert.Equal(100, input.HistoryCount); input.SetHistory(session); Assert.Equal(100, input.HistoryCount);
        input.Restore("draft"); Press(input, ConsoleKey.UpArrow); Assert.Equal("prompt 104", input.Draft);
        for (var i = 0; i < 110; i++) Press(input, ConsoleKey.UpArrow);
        Assert.Equal("prompt 5", input.Draft);
        input.SetHistory(new Session()); Assert.Equal(0, input.HistoryCount); Assert.Equal("", input.Draft);
        var prompt = UserPrompt.Text("original"); input.AcceptPrompt(prompt, false); prompt.Parts.Clear();
        Press(input, ConsoleKey.UpArrow); Assert.Equal("original", input.Draft);
        input.HandleKey(Key(ConsoleKey.X, 'x')); Press(input, ConsoleKey.DownArrow); Press(input, ConsoleKey.UpArrow);
        Assert.Equal("original", input.Draft);
    }

    [Fact]
    public void HistoryRestoresCursorRichDraftAndLiteralState()
    {
        var input = new ConsoleInput(false); input.SetHistory(new Session());
        var rich = new UserPrompt { Parts = [new("text", "before"), new("image", "saved"), new("text", "after")] };
        input.AcceptPrompt(rich, true);
        input.PasteText("draft\ntext"); input.AddImage(new() { Id = "draft-image" });
        Press(input, ConsoleKey.Home); var draft = input.Draft;
        Press(input, ConsoleKey.UpArrow); Assert.Equal(rich.Parts, input.Compose().Parts);
        Press(input, ConsoleKey.UpArrow); Assert.Equal(rich.Parts, input.Compose().Parts);
        Press(input, ConsoleKey.DownArrow); Assert.Equal(draft, input.Draft);
        input.HandleKey(Key(ConsoleKey.X, 'x')); Assert.StartsWith("x", input.Draft);
        var sent = input.HandleKey(Key(ConsoleKey.Enter)); Assert.True(sent.Ready); Assert.True(input.SubmittedLiteral);
        Assert.Contains(input.SubmittedPrompt!.Parts, p => p.Kind == "image" && p.Value == "draft-image");
    }

    [Fact]
    public void UnavailableRecalledImageKeepsDraftAndRequiresExplicitRemoval()
    {
        var input = new ConsoleInput(false) { ImageAvailable = _ => false };
        input.AcceptPrompt(new() { Parts = [new("text", "caption"), new("image", "missing")] }, true);
        Press(input, ConsoleKey.UpArrow); var draft = input.Draft; string? notice = null; input.Notice = text => notice = text;
        Assert.False(input.HandleKey(Key(ConsoleKey.Enter)).Ready); Assert.Equal(draft, input.Draft);
        Assert.Contains("unavailable", notice);
        Press(input, ConsoleKey.Tab); Press(input, ConsoleKey.Delete);
        Assert.True(input.HandleKey(Key(ConsoleKey.Enter)).Ready); Assert.Equal("caption", input.SubmittedPrompt!.PlainText);
    }

    [Fact]
    public void HistoryGuardExcludesCommandsButKeepsLiteralSlashPrompts()
    {
        var input = new ConsoleInput(false);
        input.AcceptPrompt(UserPrompt.Text("/exit"), false);
        input.AcceptPrompt(UserPrompt.Text("  /ex"), false);
        input.AcceptPrompt(UserPrompt.Text(" "), true);
        Assert.Equal(0, input.HistoryCount);
        input.AcceptPrompt(UserPrompt.Text("  /ex"), true);
        Assert.Equal(1, input.HistoryCount);
        Press(input, ConsoleKey.UpArrow);
        Assert.Equal("  /ex", input.Draft); Assert.Equal(0, input.SuggestionCount);
        Assert.True(input.HandleKey(Key(ConsoleKey.Enter)).Ready);
        Assert.True(input.SubmittedLiteral);
    }

    [Fact]
    public void ImagePromptsAreLiteralEvenWhenCallerDoesNotSetFlagAndRejectionRestoreKeepsParts()
    {
        var rich = new UserPrompt { Parts = [new("text", "/ex"), new("image", "saved")] };
        var input = new ConsoleInput(false); input.AcceptPrompt(rich, false);
        Assert.Equal(1, input.HistoryCount);
        Press(input, ConsoleKey.UpArrow); Assert.Equal(rich.Parts, input.Compose().Parts);
        Assert.Equal(0, input.SuggestionCount);
        Assert.True(input.HandleKey(Key(ConsoleKey.Enter)).Ready); Assert.True(input.SubmittedLiteral);
        input.RestorePrompt(rich);
        Assert.Equal(rich.Parts, input.Compose().Parts); Assert.Equal(0, input.SuggestionCount);
        Assert.Null(input.SubmittedPrompt);
        Assert.True(input.HandleKey(Key(ConsoleKey.Enter)).Ready); Assert.True(input.SubmittedLiteral);
        input.RestorePrompt(UserPrompt.Text("/ex"));
        Assert.Equal("/ex", input.Draft); Assert.Equal(0, input.SuggestionCount);
        Assert.True(input.HandleKey(Key(ConsoleKey.Enter)).Ready); Assert.True(input.SubmittedLiteral);
    }

    [Fact]
    public void VisibleSuggestionsTakePrecedenceOverHistoryUntilDismissed()
    {
        var input = new ConsoleInput(false); input.AcceptPrompt(UserPrompt.Text("older"), false);
        input.Restore("/ex"); Press(input, ConsoleKey.UpArrow); Press(input, ConsoleKey.DownArrow);
        Assert.Equal("/ex", input.Draft); Assert.True(input.SuggestionCount > 0);
        Press(input, ConsoleKey.Escape); Press(input, ConsoleKey.UpArrow);
        Assert.Equal("older", input.Draft);
        Press(input, ConsoleKey.DownArrow); Assert.Equal("/ex", input.Draft);
    }

    [Theory]
    [InlineData("one two   ", "one ")]
    [InlineData("one...", "one")]
    [InlineData("path/to_file", "path/")]
    [InlineData("path/", "path")]
    [InlineData("one e\u0301", "one ")]
    [InlineData("one 𐐀𐐁", "one ")]
    [InlineData("one 😀😀", "one ")]
    [InlineData("one \u2764\ufe0f", "one ")]
    [InlineData("one \U0001f469\u200d\U0001f4bb", "one ")]
    [InlineData("one \U0001f1fa\U0001f1f8", "one ")]
    [InlineData("   ", "")]
    [InlineData("", "")]
    public void ControlBackspaceDeletesUnicodeRuns(string before, string after)
    {
        var input = new ConsoleInput(false); input.Restore(before);
        Assert.False(input.HandleKey(Key(ConsoleKey.Backspace, '\b', true)).Ready);
        Assert.Equal(after, input.Draft);
    }

    [Theory]
    [InlineData("one e\u0301 tail")]
    [InlineData("one \u2764\ufe0f tail")]
    [InlineData("one \U0001f469\u200d\U0001f4bb tail")]
    public void WordDeletionAtInteriorCursorRemovesWholeTextElementAndPreservesSuffix(string text)
    {
        var input = new ConsoleInput(false); input.Restore(text); Press(input, ConsoleKey.Home);
        // Use scalar-safe navigation to reach a position inside a grapheme.
        for (var i = 0; i < 5; i++) Press(input, ConsoleKey.RightArrow);
        input.HandleKey(Key(ConsoleKey.Backspace, '\b', true));
        Assert.Equal("one  tail", input.Draft);
    }

    [Fact]
    public void WordDeletionPreservesSuffixAndDeletesOpaqueBlocksAtomically()
    {
        var input = new ConsoleInput(false); input.Restore("hello world tail");
        Press(input, ConsoleKey.Home); for (var i = 0; i < 11; i++) Press(input, ConsoleKey.RightArrow);
        input.HandleKey(Key(ConsoleKey.Backspace, '\b', true)); Assert.Equal("hello  tail", input.Draft);
        input.ClearDraft(); input.Restore("prefix "); input.PasteText("line\nline");
        input.HandleKey(Key(ConsoleKey.Backspace, '\u007f')); Assert.Equal("prefix ", input.Draft); Assert.False(input.HasBlocks);
        input.AddImage(new() { Id = "image" }); input.HandleKey(Key(ConsoleKey.Backspace, '\b', true));
        Assert.Equal("prefix ", input.Draft); Assert.False(input.HasBlocks);
        input.Restore("a😀"); Press(input, ConsoleKey.Backspace); Assert.Equal("a", input.Draft);
        input.Restore("words"); Press(input, ConsoleKey.Backspace); Assert.Equal("word", input.Draft);
    }

    [Fact]
    public void InitialBindingAndRepeatedBindingPreserveUnsentDraft()
    {
        var input = new ConsoleInput(false); input.Restore("unsent"); Press(input, ConsoleKey.Home);
        var session = new Session(); session.History.Add(UserPrompt.Text("older").HistoryItem());
        input.SetHistory(session); Assert.Equal("unsent", input.Draft);
        Press(input, ConsoleKey.UpArrow); Assert.Equal("older", input.Draft);
        input.SetHistory(session); Press(input, ConsoleKey.DownArrow); Assert.Equal("unsent", input.Draft);
        input.HandleKey(Key(ConsoleKey.X, 'x')); Assert.Equal("xunsent", input.Draft);
    }

    [Fact]
    public void SavedLiteralSlashAndAttachmentAreRecalledWithoutCommandCompletion()
    {
        var session = new Session();
        session.History.Add(UserPrompt.Text("/ex").HistoryItem());
        session.History.Add(new UserPrompt { Parts = [new("text", "caption"), new("image", "saved-image")] }.HistoryItem());
        var input = new ConsoleInput(false); input.SetHistory(session);
        Press(input, ConsoleKey.UpArrow);
        Assert.Contains(input.Compose().Parts, p => p.Kind == "image" && p.Value == "saved-image");
        Press(input, ConsoleKey.UpArrow); Assert.Equal("/ex", input.Draft); Assert.Equal(0, input.SuggestionCount);
        Assert.True(input.HandleKey(Key(ConsoleKey.Enter)).Ready); Assert.True(input.SubmittedLiteral);
    }

    [Fact]
    public void NoMatchLeavesNormalSubmissionAndNoHistoryDownIsANoop()
    {
        var input = new ConsoleInput(false); input.Restore("/not-a-command");
        Assert.Equal(0, input.SuggestionCount); Press(input, ConsoleKey.DownArrow);
        Assert.Equal("/not-a-command", input.Draft);
        Assert.False(input.HandleKey(Key(ConsoleKey.Tab)).Ready); Assert.Equal("/not-a-command", input.Draft);
        Assert.True(input.HandleKey(Key(ConsoleKey.Enter)).Ready);
        Assert.Equal("/not-a-command", input.SubmittedPrompt!.PlainText);
    }

    [Fact]
    public async Task RedirectedInputDoesNotInterpretKeysOrCompleteCommands()
    {
        var input = new ConsoleInput(false, new StringReader("/ex\n"));
        Assert.Equal("/ex", await input.ReadLineAsync(default)); Assert.Null(input.SubmittedPrompt);
        Assert.Null(await input.ReadLineAsync(default)); Assert.True(input.EndOfInput);
    }
}
