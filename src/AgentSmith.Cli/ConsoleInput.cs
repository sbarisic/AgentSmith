using System.Text;
using System.Globalization;
using AgentSmith.Core;

namespace AgentSmith.Cli;

/// <summary>Single console owner. No outstanding key reads survive a focus transition.</summary>
internal sealed class ConsoleInput(bool interactive, TextReader? reader = null, bool color = false)
{
    private readonly object gate = new();
    private string draft = "";
    private int position;
    private bool visible;
    private int drawnRows;
    private int rowsBelowCursor;
    private ComposerContext? context;
    internal void SetContext(Session session) { lock (gate) context = ComposerContext.From(session); }
    private readonly ComposerHistory history = new();
    private sealed record DraftState(string Text, int Position, Block[] Blocks, bool Literal);
    private string? historyConversation;
    private DraftState? historyDraft;
    private int historyIndex = -1;
    internal bool EditingEnabled { get; set; } = true;
    internal int HistoryCount => history.Count;
    internal void SetHistory(Session session)
    {
        lock (gate)
        {
            if (!history.SetConversation(session)) return;
            historyIndex = -1; historyDraft = null;
            if (historyConversation is not null) ClearDraft();
            historyConversation = session.Id;
            suggestions = []; dismissed = null;
        }
    }
    internal void AcceptPrompt(UserPrompt prompt, bool isLiteral)
    {
        lock (gate) { history.Add(prompt, isLiteral); historyIndex = -1; historyDraft = null; }
    }
    private IReadOnlyList<CommandCompletion.Suggestion> suggestions = [];
    private int selectedSuggestion;
    private (string Text, int Position)? dismissed;
    internal int SuggestionCount => suggestions.Count;
    internal int SelectedSuggestion => selectedSuggestion;
    private Task<string?>? redirectedRead;
    public bool EndOfInput { get; private set; }
    public bool Interactive => interactive;
    internal bool ResumeConfirmation { get; set; }
    public string Draft => draft;
    public string Status { get; set; } = "";
    internal IClipboardService Clipboard { get; set; } = new WindowsClipboard();
    internal Func<ClipboardContent, AttachmentRecord>? ImportImage { get; set; }
    internal Func<string, bool>? ImageAvailable { get; set; }
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
    internal void ClearDraft() { lock (gate) { Erase(); draft = ""; position = 0; blocks.Clear(); blockFocus = -1; literal = false; historyIndex = -1; historyDraft = null; dismissed = null; ClearSubmission(); Draw(); } }
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
            literal = true; dismissed = null; historyIndex = -1; historyDraft = null; ClearSubmission(); Draw();
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
        if (drawnRows == 0) return;
        if (rowsBelowCursor > 0) Console.Write($"\u001b[{rowsBelowCursor}B");
        Console.Write("\r\u001b[2K");
        for (var row = 1; row < drawnRows; row++) Console.Write("\u001b[1A\r\u001b[2K");
        drawnRows = 0; rowsBelowCursor = 0;
    }
    private void Draw()
    {
        UpdateSuggestions();
        if (!visible) return;
        var width = Math.Max(1, Console.WindowWidth - 1);
        var contextLine = context?.Line(width) ?? "";
        var showContext = contextLine.Length > 0 && Console.WindowHeight >= 3;
        if (showContext)
        {
            if (color) Console.Write(TerminalTheme.Style("normal"));
            Console.WriteLine(contextLine);
        }
        var status = ResumeConfirmation ? "Type continue to resume · Alt+V is available in the chat composer afterward" : blockFocus >= 0 && blockFocus < blocks.Count ? blocks[blockFocus].Label + " · Enter preview · Delete remove · Tab composer"
            : suggestions.Count > 0 ? "↑/↓ select · Tab/Enter complete · Esc dismiss" : Status.Length == 0 ? "Enter send · ↑/↓ history · Ctrl+Backspace word · Alt+V paste · /help" : Status + " · Esc cancel · Enter queue · ↑/↓ history";
        if (color) Console.Write(TerminalTheme.Style("dim"));
        Console.WriteLine(status[..Math.Min(status.Length, width)]);
        var prefix = Status.Length == 0 ? "you › " : "next › ";
        prefix = prefix[..Math.Min(prefix.Length, Math.Max(0, width - 1))];
        var room = Math.Max(1, width - prefix.Length);
        var start = Math.Max(0, position - room + 1);
        var shown = draft[start..]; if (shown.Length > room) shown = shown[..room];
        if (color) Console.Write(TerminalTheme.Style("accent"));
        Console.Write(prefix);
        if (color) Console.Write(TerminalTheme.Style("normal"));
        Console.Write(TerminalRenderer.Safe(shown));
        if (color) Console.Write(TerminalTheme.Reset);
        var panelRows = Math.Min(Math.Min(5, suggestions.Count), Math.Max(0, Console.WindowHeight - (showContext ? 3 : 2) - 1));
        var first = Math.Max(0, Math.Min(selectedSuggestion - panelRows + 1, suggestions.Count - panelRows));
        for (var row = 0; row < panelRows; row++)
        {
            Console.WriteLine();
            var index = first + row; var suggestion = suggestions[index];
            var line = TerminalRenderer.Safe((index == selectedSuggestion ? "› " : "  ") + suggestion.Value + " — " + suggestion.Description);
            Console.Write(line[..Math.Min(line.Length, width)]);
        }
        if (panelRows > 0) Console.Write($"\u001b[{panelRows}A");
        Console.Write("\r"); var column = prefix.Length + position - start;
        if (column > 0) Console.Write($"\u001b[{column}C");
        drawnRows = (showContext ? 3 : 2) + panelRows; rowsBelowCursor = panelRows;
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
            if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)) return (false, null, true);
            UpdateSuggestions();
            if (key.Key == ConsoleKey.Escape)
            {
                if (suggestions.Count == 0) return (false, null, true);
                Erase(); dismissed = (draft, position); suggestions = []; Draw(); return (false, null, false);
            }
            if (suggestions.Count > 0 && key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.Tab or ConsoleKey.Enter)
            {
                Erase();
                if (key.Key == ConsoleKey.UpArrow) selectedSuggestion = Math.Max(0, selectedSuggestion - 1);
                else if (key.Key == ConsoleKey.DownArrow) selectedSuggestion = Math.Min(suggestions.Count - 1, selectedSuggestion + 1);
                else
                {
                    var choice = suggestions[selectedSuggestion]; draft = draft.Remove(choice.Start, choice.Length).Insert(choice.Start, choice.Value);
                    position = choice.Start + choice.Value.Length; dismissed = (draft, position); suggestions = [];
                }
                Draw(); return (false, null, false);
            }
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
            if (EditingEnabled && !ResumeConfirmation && key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
            {
                RecallHistory(key.Key == ConsoleKey.UpArrow); Draw(); return (false, null, false);
            }
            // Windows console hosts may deliver Ctrl+Backspace as DEL rather than Backspace.
            if (key.KeyChar == '\u007f' || key.Key == ConsoleKey.Backspace && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                DeletePreviousWord(); dismissed = null; Draw(); return (false, null, false);
            }
            var beforeEdit = (draft, position);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    if (ResumeConfirmation && string.IsNullOrEmpty(draft)) { ClearSubmission(); Draw(); return (true, "", false); }
                    var composed = Compose();
                    if (composed.Parts.Any(p => p.Kind == "image" && ImageAvailable?.Invoke(p.Value) == false))
                    {
                        Notice?.Invoke("A recalled image attachment is unavailable. Remove that image block before sending; the draft was kept.");
                        Draw(); return (false, null, false);
                    }
                    try { composed.Validate(); }
                    catch (UserFacingException ex) { Notice?.Invoke(ex.Message); Draw(); return (false, null, false); }
                    SubmittedPrompt = composed; SubmittedLiteral = literal;
                    var text = composed.PlainText; draft = ""; position = 0; blocks.Clear(); literal = false; historyIndex = -1; historyDraft = null; dismissed = null; Draw(); return (true, text, false);
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
            if (beforeEdit != (draft, position)) dismissed = null;
            Draw(); return (false, null, false);
        }
    }
    private void UpdateSuggestions()
    {
        IReadOnlyList<CommandCompletion.Suggestion> next = EditingEnabled && !ResumeConfirmation && !literal && blocks.Count == 0 && blockFocus < 0 && dismissed != (draft, position)
            ? CommandCompletion.GetSuggestions(draft, position) : [];
        if (!suggestions.SequenceEqual(next)) selectedSuggestion = 0;
        suggestions = next;
    }
    private void RecallHistory(bool older)
    {
        if (history.Count == 0) return;
        if (historyIndex < 0)
        {
            if (!older) return;
            historyDraft = new(draft, position, blocks.ToArray(), literal); historyIndex = history.Count;
        }
        historyIndex = Math.Clamp(historyIndex + (older ? -1 : 1), 0, history.Count);
        if (historyIndex == history.Count)
        {
            var saved = historyDraft!;
            draft = saved.Text; position = saved.Position; blocks.Clear(); blocks.AddRange(saved.Blocks); literal = saved.Literal;
            historyIndex = -1; historyDraft = null;
        }
        else
        {
            var entry = history[historyIndex];
            draft = ""; position = 0; blocks.Clear();
            foreach (var part in entry.Prompt.Parts)
            {
                if (part.Kind == "image") AddBlock(part, (ImageAvailable?.Invoke(part.Value) == false ? "Unavailable image · " : "Image · ") + part.Value[..Math.Min(8, part.Value.Length)]);
                else if (part.Value.Contains('\n') || part.Value.Contains('\r') || part.Value.Length > 2000)
                    AddBlock(part, "Recalled text · " + part.Value.Length + " characters");
                else { draft += part.Value; position = draft.Length; }
            }
            literal = entry.Literal;
        }
        blockFocus = -1; dismissed = null; ClearSubmission();
    }
    private int ScalarClass(int at)
    {
        if (!Rune.TryGetRuneAt(draft, at, out var rune)) return 2;
        if (Rune.IsWhiteSpace(rune)) return 0;
        var category = Rune.GetUnicodeCategory(rune);
        return Rune.IsLetterOrDigit(rune) || rune.Value == '_' || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark ? 1 : 2;
    }
    private void DeletePreviousWord()
    {
        if (position == 0) return;
        // Text elements keep combining marks, variation selectors and joined emoji together.
        // A cursor moved by ordinary scalar navigation can be inside an element: remove
        // the entire intersected element rather than leave an orphaned suffix.
        var elements = StringInfo.ParseCombiningCharacters(draft);
        var index = Array.BinarySearch(elements, position);
        var boundary = index >= 0 ? index : ~index;
        var end = boundary < elements.Length ? elements[boundary] : draft.Length;
        var first = boundary;
        while (first > 0 && ScalarClass(elements[first - 1]) == 0) first--;
        if (first > 0)
        {
            var category = ScalarClass(elements[first - 1]);
            do { first--; } while (first > 0 && ScalarClass(elements[first - 1]) == category);
        }
        var start = first < elements.Length ? elements[first] : draft.Length;
        // Expand intersections to complete opaque markers; rich parts are removed atomically.
        foreach (var block in blocks.ToArray())
        {
            var at = draft.IndexOf(block.Marker, StringComparison.Ordinal);
            if (at >= 0 && at < end && at + block.Marker.Length > start)
            { start = Math.Min(start, at); end = Math.Max(end, at + block.Marker.Length); blocks.Remove(block); }
        }
        draft = draft.Remove(start, end - start); position = start;
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
    public void Restore(string text) { lock (gate) { Erase(); blocks.Clear(); blockFocus = -1; literal = false; dismissed = null; historyIndex = -1; historyDraft = null; ClearSubmission(); draft = text; position = text.Length; Draw(); } }
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
