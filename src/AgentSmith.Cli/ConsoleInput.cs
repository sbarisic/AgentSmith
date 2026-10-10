using System.Text;
using AgentSmith.Core;

namespace AgentSmith.Cli;

/// <summary>Single console owner. No outstanding key reads survive a focus transition.</summary>
internal sealed class ConsoleInput(bool interactive, TextReader? reader = null, bool color = false)
{
    private readonly object gate = new();
    private string draft = "";
    private int position;
    private bool visible;
    private bool drawn;
    private Task<string?>? redirectedRead;
    public bool EndOfInput { get; private set; }
    public bool Interactive => interactive;
    internal bool ResumeConfirmation { get; set; }
    public string Draft => draft;
    public string Status { get; set; } = "";
    internal IClipboardService Clipboard { get; set; } = new WindowsClipboard();
    internal Func<ClipboardContent, AttachmentRecord>? ImportImage { get; set; }
    internal Action<string>? Notice { get; set; }
    private sealed record Block(string Marker, PromptPart Part, string Label);
    private readonly List<Block> blocks = [];
    private int blockFocus = -1;
    private int nextBlock;
    private bool literal;
    internal UserPrompt? SubmittedPrompt { get; private set; }
    internal bool SubmittedLiteral { get; private set; }
    internal bool HasBlocks => blocks.Count > 0;
    internal void ClearSubmission() { SubmittedPrompt = null; SubmittedLiteral = false; }
    internal void ClearDraft() { lock (gate) { Erase(); draft = ""; position = 0; blocks.Clear(); blockFocus = -1; literal = false; ClearSubmission(); Draw(); } }
    internal UserPrompt Compose(string? additionalText = null)
    {
        var result = new UserPrompt(); var remaining = draft;
        while (remaining.Length > 0)
        {
            var found = blocks.Select(b => (Block: b, Index: remaining.IndexOf(b.Marker, StringComparison.Ordinal))).Where(b => b.Index >= 0).OrderBy(b => b.Index).FirstOrDefault();
            if (found.Block is null) { result.Parts.Add(new("text", remaining)); break; }
            if (found.Index > 0) result.Parts.Add(new("text", remaining[..found.Index]));
            result.Parts.Add(found.Block.Part); remaining = remaining[(found.Index + found.Block.Marker.Length)..];
        }
        if (additionalText is not null) result.Parts.Add(new("text", additionalText));
        return result;
    }
    internal void AddImage(AttachmentRecord item)
    {
        if (Compose().Parts.Count(p => p.Kind == "image") >= 4) throw new UserFacingException("A prompt supports at most four images.");
        AddBlock(new("image", item.Id), $"Image {item.Width}×{item.Height} · {item.Bytes / 1024} KiB"); literal = true;
    }
    private void AddBlock(PromptPart part, string label)
    {
        var marker = $"[{(part.Kind == "image" ? "Image" : "Paste")} #{++nextBlock}]"; blocks.Add(new(marker, part, label));
        draft = draft.Insert(position, marker); position += marker.Length;
    }
    internal void PasteText(string text)
    {
        if (Encoding.UTF8.GetByteCount(Compose().PlainText) + Encoding.UTF8.GetByteCount(text) > 32768) throw new UserFacingException("Paste exceeds the 32 KiB text limit. Draft unchanged.");
        if (text.Contains('\n') || text.Contains('\r') || text.Length > 2000) AddBlock(new("text", text), $"Text · {text.Count(c => c == '\n') + 1} lines · {text.Length} characters");
        else { draft = draft.Insert(position, text); position += text.Length; }
        literal = true;
    }
    internal void RestorePrompt(UserPrompt value)
    {
        lock (gate)
        {
            Erase(); draft = ""; position = 0; blocks.Clear(); blockFocus = -1;
            foreach (var part in value.Parts)
                if (part.Kind == "image") AddBlock(part, "Image · " + part.Value[..Math.Min(8, part.Value.Length)]);
                else PasteText(part.Value);
            literal = true; ClearSubmission(); Draw();
        }
    }
    public void Show() { lock (gate) { visible = interactive; Draw(); } }
    public void Hide() { lock (gate) { Erase(); visible = false; } }
    public void Render(Action output)
    {
        lock (gate) { Erase(); output(); if (visible) Draw(); }
    }
    private void Erase()
    {
        if (!drawn) return;
        Console.Write("\r\u001b[2K\u001b[1A\r\u001b[2K"); drawn = false;
    }
    private void Draw()
    {
        if (!visible) return;
        var width = Math.Max(16, Console.WindowWidth - 1);
        var status = ResumeConfirmation ? "Type continue to resume · Alt+V is available in the chat composer afterward" : blockFocus >= 0 && blockFocus < blocks.Count ? blocks[blockFocus].Label + " · Enter preview · Delete remove · Tab composer"
            : Status.Length == 0 ? "Enter send · Alt+V paste · Tab pasted blocks · /help" : Status + " · Esc cancel · Enter queue · Alt+V paste";
        if (color) Console.Write(TerminalTheme.Style("dim"));
        Console.WriteLine(status[..Math.Min(status.Length, width)]);
        var prefix = Status.Length == 0 ? "you › " : "next › ";
        var room = Math.Max(1, width - prefix.Length);
        var start = Math.Max(0, position - room + 1);
        var shown = draft[start..]; if (shown.Length > room) shown = shown[..room];
        if (color) Console.Write(TerminalTheme.Style("accent"));
        Console.Write(prefix);
        if (color) Console.Write(TerminalTheme.Style("normal"));
        Console.Write(TerminalRenderer.Safe(shown));
        if (color) Console.Write(TerminalTheme.Reset);
        Console.Write("\r"); var column = prefix.Length + position - start;
        if (column > 0) Console.Write($"\u001b[{column}C"); drawn = true;
    }
    // Polling is intentional: neither Console.ReadKey nor a cancelled ReadLine may steal a later picker answer.
    public async Task<(bool Ready, string? Text, bool Cancel)> PollAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!interactive)
        {
            if (EndOfInput) return (true, null, false);
            redirectedRead ??= Task.Run(async () => await (reader ?? Console.In).ReadLineAsync());
            if (!redirectedRead.IsCompleted) return (false, null, false);
            var text = await redirectedRead; redirectedRead = null; if (text is null) EndOfInput = true;
            return (true, text, false);
        }
        if (!Console.KeyAvailable) return (false, null, false);
        lock (gate)
        {
            Erase(); var wasVisible = visible; visible = false;
            try
            {
                var result = HandleKey(Console.ReadKey(true));
                for (var n = 1; n < 256 && !result.Ready && !result.Cancel && Console.KeyAvailable; n++) result = HandleKey(Console.ReadKey(true));
                return result;
            }
            finally { visible = wasVisible; Draw(); }
        }
    }
    internal (bool Ready, string? Text, bool Cancel) HandleKey(ConsoleKeyInfo key)
    {
        lock (gate)
        {
            if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)) return (false, null, true);
            Erase();
            if (key.Key == ConsoleKey.V && key.Modifiers.HasFlag(ConsoleModifiers.Alt))
            {
                if (ResumeConfirmation)
                {
                    Notice?.Invoke("This is the saved-work confirmation. Type continue first; paste text or images in the chat composer afterward.");
                    Draw(); return (false, null, false);
                }
                try
                {
                    var paste = Clipboard.Read();
                    if (paste.Image is not null)
                    {
                        if (Compose().Parts.Count(p => p.Kind == "image") >= 4) throw new UserFacingException("Four images already attached.");
                        AddImage(ImportImage?.Invoke(paste) ?? throw new UserFacingException("No active image composer."));
                    }
                    else PasteText(paste.Text ?? "");
                }
                catch (Exception ex) when (ex is UserFacingException or IOException or UnauthorizedAccessException) { Notice?.Invoke(ex is UserFacingException ? ex.Message : "Clipboard unavailable; draft unchanged."); }
                Draw(); return (false, null, false);
            }
            if (key.Key == ConsoleKey.Tab && blocks.Count > 0) { blockFocus++; if (blockFocus >= blocks.Count) blockFocus = -1; Draw(); return (false, null, false); }
            if (blockFocus >= 0 && blockFocus < blocks.Count)
            {
                var block = blocks[blockFocus];
                if (key.Key == ConsoleKey.Delete)
                { draft = draft.Replace(block.Marker, "", StringComparison.Ordinal); position = Math.Min(position, draft.Length); blocks.RemoveAt(blockFocus); blockFocus = -1; }
                else if (key.Key == ConsoleKey.Enter) { Notice?.Invoke(block.Label + "\n" + (block.Part.Kind == "text" ? block.Part.Value : "Attachment " + block.Part.Value)); blockFocus = -1; }
                Draw(); return (false, null, false);
            }
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    if (ResumeConfirmation && string.IsNullOrEmpty(draft)) { ClearSubmission(); Draw(); return (true, "", false); }
                    var composed = Compose();
                    try { composed.Validate(); }
                    catch (UserFacingException ex) { Notice?.Invoke(ex.Message); Draw(); return (false, null, false); }
                    SubmittedPrompt = composed; SubmittedLiteral = literal;
                    var text = composed.PlainText; draft = ""; position = 0; blocks.Clear(); literal = false; Draw(); return (true, text, false);
                case ConsoleKey.Backspace:
                    if (RemoveBlockAt(position - 1)) break;
                    if (position > 0) { var n = position > 1 && char.IsLowSurrogate(draft[position - 1]) ? 2 : 1; draft = draft.Remove(position - n, n); position -= n; } break;
                case ConsoleKey.Delete:
                    if (RemoveBlockAt(position)) break;
                    if (position < draft.Length) draft = draft.Remove(position, char.IsHighSurrogate(draft[position]) && position + 1 < draft.Length ? 2 : 1); break;
                case ConsoleKey.LeftArrow: if (position > 0) position -= position > 1 && char.IsLowSurrogate(draft[position - 1]) ? 2 : 1; break;
                case ConsoleKey.RightArrow: if (position < draft.Length) position += char.IsHighSurrogate(draft[position]) && position + 1 < draft.Length ? 2 : 1; break;
                case ConsoleKey.Home: position = 0; break;
                case ConsoleKey.End: position = draft.Length; break;
                case ConsoleKey.D when key.Modifiers.HasFlag(ConsoleModifiers.Control): EndOfInput = true; return (true, null, false);
                default: if (!char.IsControl(key.KeyChar) && !key.Modifiers.HasFlag(ConsoleModifiers.Alt)) { draft = draft.Insert(position, key.KeyChar.ToString()); position++; } break;
            }
            foreach (var block in blocks)
            {
                var at = draft.IndexOf(block.Marker, StringComparison.Ordinal);
                if (at >= 0 && position > at && position < at + block.Marker.Length)
                    position = key.Key == ConsoleKey.LeftArrow ? at : at + block.Marker.Length;
            }
            Draw(); return (false, null, false);
        }
    }
    public void Refresh() { lock (gate) { Erase(); Draw(); } }
    private bool RemoveBlockAt(int index)
    {
        foreach (var block in blocks.ToArray())
        {
            var at = draft.IndexOf(block.Marker, StringComparison.Ordinal);
            if (at >= 0 && index >= at && index < at + block.Marker.Length)
            { draft = draft.Remove(at, block.Marker.Length); position = at; blocks.Remove(block); return true; }
        }
        return false;
    }
    public void Restore(string text) { lock (gate) { Erase(); blocks.Clear(); blockFocus = -1; literal = false; ClearSubmission(); draft = text; position = text.Length; Draw(); } }
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        Show();
        try { while (true) { var next = await PollAsync(ct); if (next.Ready) return next.Text; await Task.Delay(25, ct); } }
        finally { Hide(); }
    }
    public async Task<ConsoleKeyInfo> ReadKeyAsync(CancellationToken ct)
    {
        Hide();
        while (!Console.KeyAvailable) { ct.ThrowIfCancellationRequested(); await Task.Delay(25, ct); }
        return Console.ReadKey(true);
    }
}
