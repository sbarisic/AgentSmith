using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using AgentSmith.Cli;
using AgentSmith.Core;
using AgentSmith.LmStudio;
using AgentSmith.OpenAI;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.IntegrationTests;
public sealed class ClipboardTests
{
    private sealed class Clipboard(ClipboardContent? content, bool fail = false) : IClipboardService
    { public int Reads; public ClipboardContent Read(bool textOnly = false) { Reads++; return fail ? throw new UserFacingException("Clipboard busy") : content!; } }
    private static ConsoleKeyInfo Key(ConsoleKey key, char ch = '\0', bool alt = false) => new(ch, key, false, alt, false);
    [Fact]
    public async Task ResumeInitializesPasteFeedbackBeforeConfirmationAndDoesNotConsumeClipboard()
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-resume-paste-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var app = new CliApp(root, true, true, new StringReader("/attach unused.png\n\n"));
            var s = new Session { Workspace = root, Runs = [new() { State = RunState.Paused }] };
            app.Store.Save(s, new("created", s.Id, null, new { })); app.PrepareResume(s);
            var composer = app.Composer; Assert.NotNull(composer.ImportImage); Assert.NotNull(composer.Notice);
            var clipboard = new Clipboard(new(Text: "pasted")); composer.Clipboard = clipboard;
            string? notice = null; composer.Notice = message => notice = message; composer.ResumeConfirmation = true;
            composer.Restore("draft"); Assert.False(composer.HandleKey(Key(ConsoleKey.V, 'v', true)).Ready);
            Assert.Contains("continue first", notice); Assert.Equal(0, clipboard.Reads); Assert.Equal("draft", composer.Draft);
            composer.ClearDraft(); Assert.Equal("", composer.HandleKey(Key(ConsoleKey.Enter)).Text);
            Assert.Equal("", await app.ReadResumeDecision(s, default)); Assert.False(composer.ResumeConfirmation);
            Assert.Empty(app.Store.Attachments(s.Id)); Assert.Equal(RunState.Paused, s.Runs.Single().State);
            composer.HandleKey(Key(ConsoleKey.V, 'v', true)); Assert.Equal(1, clipboard.Reads); Assert.Equal("pasted", composer.Compose().PlainText);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Fact]
    public void PasteIsLiteralCollapsedAndExplicitlySubmitted()
    {
        var input = new ConsoleInput(false) { Clipboard = new Clipboard(new(Text: "/exit\n/queue clear\ntext")) };
        Assert.False(input.HandleKey(Key(ConsoleKey.V, 'v', true)).Ready); Assert.True(input.HasBlocks);
        Assert.Equal("/exit\n/queue clear\ntext", input.Compose().PlainText);
        input.HandleKey(Key(ConsoleKey.Tab)); Assert.False(input.HandleKey(Key(ConsoleKey.Enter)).Ready);
        var sent = input.HandleKey(Key(ConsoleKey.Enter)); Assert.True(sent.Ready); Assert.True(input.SubmittedLiteral);
        Assert.Equal("/exit\n/queue clear\ntext", input.SubmittedPrompt!.PlainText);
    }
    [Fact]
    public void ClipboardFailureAndOversizePreserveDraftAndImageWins()
    {
        var input = new ConsoleInput(false) { Clipboard = new Clipboard(null, true) }; input.Restore("draft");
        input.HandleKey(Key(ConsoleKey.V, 'v', true)); Assert.Equal("draft", input.Draft);
        Assert.Throws<UserFacingException>(() => input.PasteText(new string('x', 32769))); Assert.Equal("draft", input.Draft);
        input.Clipboard = new Clipboard(new(Text: "ignored", Image: [1], Width: 1, Height: 1));
        input.ImportImage = _ => new() { Id = "image", Width = 1, Height = 1 };
        input.HandleKey(Key(ConsoleKey.V, 'v', true)); Assert.Equal("draft", input.Compose().PlainText);
        Assert.Equal("image", input.Compose().Parts.Last().Value);
        input.HandleKey(Key(ConsoleKey.LeftArrow)); input.HandleKey(Key(ConsoleKey.Delete));
        Assert.False(input.HasBlocks); Assert.Equal("draft", input.Draft);
    }
    [Fact]
    public void PastedNoteCannotSelectOrApprove()
    {
        var picker = new QuestionInput(["Implement", "Revise"]); picker.Paste("Implement");
        Assert.Equal(-1, picker.Selected); Assert.Null(picker.Answer); Assert.Empty(picker.Note);
        picker.Handle(Key(ConsoleKey.Tab)); picker.Paste("note\n/exit"); Assert.Null(picker.Answer);
        picker.Handle(Key(ConsoleKey.Escape)); Assert.Null(picker.Answer);
    }
    [Fact]
    public void ImagesRemainOwnedAndQueuedAcrossRestartWithoutBase64History()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return;
        var root = Path.Combine(Path.GetTempPath(), "smith-images-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root); using var bitmap = new Bitmap(32, 16); using var png = new MemoryStream(); bitmap.Save(png, ImageFormat.Png);
            var store = new SessionStore(root); var s = new Session { Workspace = root }; s.Connection.Capabilities.Vision = true;
            store.Save(s, new("created", s.Id, null, new { }));
            var image = AttachmentFiles.Import(store, s.Id, png.ToArray(), 32, 16);
            var prompt = new UserPrompt { Parts = [new("text", "before"), new("image", image.Id), new("text", "after")] };
            store.Enqueue(s.Id, prompt); store.HoldQueue(s.Id, "restart");
            using var registry = new ToolRegistry();
            Assert.DoesNotContain(image.Id, new ContextBuilder(store, new EmptyModel(), registry).Build(s).Request.Instructions);
            var reopened = new SessionStore(root); var saved = reopened.Get(s.Id)!;
            Assert.Null(reopened.ClaimQueued(saved)); reopened.HoldQueue(s.Id, null);
            var run = reopened.ClaimQueued(saved); Assert.NotNull(run); Assert.Null(reopened.ClaimQueued(saved));
            var request = new ModelRequest("test", "", saved.History, []);
            var resolved = AttachmentFiles.Resolve(reopened, saved, request);
            Assert.DoesNotContain("base64", Json.Serialize(saved)); Assert.DoesNotContain("base64", Json.Serialize(reopened.Queue(s.Id)));
            var openai = ChatGptModelClient.BuildRequest(resolved);
            Assert.StartsWith("data:image/png;base64,", openai["input"]![0]!["content"]![1]!["image_url"]!.ToString());
            var local = LocalChatModelClient.BuildRequest(resolved, 8192);
            Assert.Equal("before", local["messages"]![1]!["content"]![0]!["text"]!.ToString());
            Assert.NotNull(local["messages"]![1]!["content"]![1]!["image_url"]!["url"]);
            var other = new Session(); other.Connection.Capabilities.Vision = true;
            Assert.Throws<UserFacingException>(() => AttachmentFiles.Resolve(reopened, other, request));
            saved.Connection.Capabilities.Vision = false; Assert.Throws<UserFacingException>(() => AttachmentFiles.Resolve(reopened, saved, request));
            File.AppendAllText(image.Path, "changed"); Assert.Throws<UserFacingException>(() => AttachmentFiles.Read(reopened, image));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private sealed class EmptyModel : IModelClient
    { public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) { await Task.CompletedTask; yield break; } }
}
