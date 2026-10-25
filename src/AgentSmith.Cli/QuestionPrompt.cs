using AgentSmith.Core;

namespace AgentSmith.Cli;

public enum QuestionOutcome { Answer, Commands, Exit, Interrupted }
public sealed record QuestionResult(QuestionOutcome Outcome, string? Text = null);

/// <summary>Keyboard state is separate from terminal I/O; only an explicit submit produces an answer.</summary>
public sealed class QuestionInput(string[] choices)
{
    public int Focus { get; private set; } = choices.Length == 0 ? 1 : 0;
    public int Highlight { get; private set; }
    public int Selected { get; private set; } = -1;
    public string Note { get; private set; } = "";
    public string? Answer { get; private set; }
    public bool Closed { get; private set; }
    public void Reopen() { Closed = false; }
    public string Hint { get; private set; } = "";
    public int OptionCount => choices.Length + 1;
    public void Paste(string? text)
    {
        if (Focus != 1 || text is null) { Hint = "Paste text in the note field; images belong in the composer."; return; }
        if (Note.Length + text.Length > 8000) { Hint = "Note exceeds 8000 characters; unchanged."; return; }
        Note += text; Hint = "Pasted. Enter explicitly submits.";
    }

    public void Handle(ConsoleKeyInfo key)
    {
        if (Closed) return;
        var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (control && key.Key == ConsoleKey.C) return;
        if (key.Key == ConsoleKey.Escape || control && key.Key is ConsoleKey.D or ConsoleKey.Z)
        { Closed = true; return; }
        if (key.Key == ConsoleKey.Tab)
        {
            var first = choices.Length == 0 ? 1 : 0;
            Focus = key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? (Focus == first ? 2 : Focus - 1) : (Focus == 2 ? first : Focus + 1);
            return;
        }
        if (Focus == 0)
        {
            if (key.Key == ConsoleKey.UpArrow) Highlight = (Highlight + OptionCount - 1) % OptionCount;
            if (key.Key == ConsoleKey.DownArrow) Highlight = (Highlight + 1) % OptionCount;
            if (key.Key is ConsoleKey.Spacebar or ConsoleKey.Enter)
            { Selected = Highlight; Focus = 1; Hint = ""; }
            return;
        }
        if (key.Key == ConsoleKey.Enter)
        {
            var choice = Selected >= 0 && Selected < choices.Length ? choices[Selected] : null;
            var note = Note.Trim();
            if (choice is null && note.Length == 0) { Hint = "Choose an option or write an answer first."; return; }
            Answer = choice is null ? note : choice + (note.Length > 0 ? "\n\nNote: " + note : "");
            Closed = true;
            return;
        }
        if (Focus != 1) return;
        if (key.Key == ConsoleKey.Backspace && Note.Length > 0)
        {
            var length = Note.Length > 1 && char.IsLowSurrogate(Note[^1]) && char.IsHighSurrogate(Note[^2]) ? 2 : 1;
            Note = Note[..^length];
        }
        else if (!control && !char.IsControl(key.KeyChar) && Note.Length < 8000)
            Note += TerminalRenderer.Safe(key.KeyChar.ToString());
    }
}

public static class QuestionPrompt
{
    public static bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected
        && Environment.GetEnvironmentVariable("TERM") != "dumb" && Console.WindowHeight >= 14 && Console.WindowWidth >= 40;

    // Keep each redraw within the current viewport, including on small or resized terminals.
    public static string[] Frame(Question question, QuestionInput input, int width, int height)
    {
        width = Math.Max(12, width - 2);
        var lines = new List<string> { "  Choose an option, or write your own answer" };
        if (question.Choices.Length > 0)
        {
            var count = Math.Min(input.OptionCount, Math.Max(1, height - 10));
            var start = Math.Clamp(input.Highlight - count / 2, 0, input.OptionCount - count);
            for (var i = start; i < start + count; i++)
                lines.Add($"  {(input.Focus == 0 && i == input.Highlight ? ">" : " ")} {(input.Selected == i ? "[x]" : "[ ]")} "
                    + (i < question.Choices.Length ? question.Choices[i] : "Write my own answer") + $"  ({i + 1}/{input.OptionCount})");
        }
        lines.Add("  " + (input.Focus == 1 ? "> " : "  ") + (question.Choices.Length == 0 ? "Answer" : "Note / custom answer") + ":");
        var note = input.Note.Length == 0 ? "Type here" : input.Note;
        var noteWidth = Math.Max(1, width - 6);
        if (note.Length > noteWidth) note = "…" + note[^(noteWidth - 1)..];
        lines.Add("    " + note + (input.Focus == 1 ? "_" : ""));
        lines.Add("  " + (input.Focus == 2 ? "> " : "  ") + "[ Submit answer ]");
        lines.Add("  ↑↓ move · Space/Enter select · Tab note/submit");
        lines.Add("  Enter submits · Esc commands · Ctrl+C stays here");
        if (input.Hint.Length > 0) lines.Add("  " + input.Hint);
        return lines.Select(line => Clip(TerminalRenderer.Safe(line).Replace('\t', ' ').Replace('\n', ' '), width)).ToArray();
    }

    private static string Clip(string text, int width)
    {
        if (text.Length <= width) return text;
        var end = width - 1;
        if (char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end] + "…";
    }

    public static async Task<QuestionResult> ReadAsync(Question question, bool color, CancellationToken ct, Func<CancellationToken, Task<ConsoleKeyInfo>>? readKey = null, Func<string?>? paste = null, QuestionInput? savedState = null)
    {
        var state = savedState ?? new QuestionInput(question.Choices);
        state.Reopen();
        var previousLines = 0;
        var previousControlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        var renderer = new TerminalRenderer(Console.Out, color, Console.WindowWidth);
        renderer.Render("question.heading", new { question.Text });
        Console.Write("\u001b[?25l");
        try
        {
            while (!state.Closed)
            {
                ct.ThrowIfCancellationRequested();
                if (Console.WindowWidth < 40 || Console.WindowHeight < 14)
                {
                    Console.WriteLine("  Question remains pending. Enlarge the terminal and press Enter to return.");
                    return new(QuestionOutcome.Commands);
                }
                var lines = Frame(question, state, Console.WindowWidth, Console.WindowHeight);
                if (previousLines > 0) Console.Write($"\u001b[{Math.Min(previousLines, Math.Max(1, Console.WindowHeight - 1))}A");
                Console.Write("\r\u001b[J");
                for (var i = 0; i < lines.Length; i++)
                {
                    if (color) Console.Write(i == 0 || lines[i].StartsWith("  >", StringComparison.Ordinal) ? TerminalTheme.Style("accent") : TerminalTheme.Style("normal"));
                    Console.WriteLine(lines[i]);
                }
                if (color) Console.Write("\u001b[0m");
                previousLines = lines.Length;
                ConsoleKeyInfo key;
                if (readKey is not null) key = await readKey(ct);
                else { while (!Console.KeyAvailable) { ct.ThrowIfCancellationRequested(); await Task.Delay(25, ct); } key = Console.ReadKey(intercept: true); }
                if (key.Key == ConsoleKey.V && key.Modifiers.HasFlag(ConsoleModifiers.Alt)) state.Paste(paste?.Invoke());
                else state.Handle(key);
            }
            Console.Write($"\u001b[{previousLines}A\r\u001b[J");
            if (state.Answer is null) Console.WriteLine("  Question pending. Enter a command, or empty Enter to return.");
            else renderer.Render("answer.submitted", new { text = state.Answer });
            return new(state.Answer is null ? QuestionOutcome.Commands : QuestionOutcome.Answer, state.Answer);
        }
        finally
        {
            Console.Write("\u001b[?25h");
            Console.TreatControlCAsInput = previousControlC;
        }
    }
}
