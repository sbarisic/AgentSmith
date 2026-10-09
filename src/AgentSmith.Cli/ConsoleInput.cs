using System.Text;

namespace AgentSmith.Cli;

/// <summary>Single console owner. No outstanding key reads survive a focus transition.</summary>
internal sealed class ConsoleInput(bool interactive, TextReader? reader = null)
{
    private readonly object gate = new();
    private string draft = "";
    private int position;
    private bool visible;
    private bool drawn;
    private Task<string?>? redirectedRead;
    public bool EndOfInput { get; private set; }
    public bool Interactive => interactive;
    public string Draft => draft;
    public string Status { get; set; } = "";
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
        var status = Status.Length == 0 ? "Enter send · /help commands" : Status + " · Esc cancel · Enter queue";
        Console.WriteLine(status[..Math.Min(status.Length, width)]);
        var prefix = Status.Length == 0 ? "you › " : "next › ";
        var room = Math.Max(1, width - prefix.Length);
        var start = Math.Max(0, position - room + 1);
        var shown = draft[start..]; if (shown.Length > room) shown = shown[..room];
        Console.Write(prefix + TerminalRenderer.Safe(shown));
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
            switch (key.Key)
            {
                case ConsoleKey.Enter: var text = draft; draft = ""; position = 0; Draw(); return (true, text, false);
                case ConsoleKey.Backspace: if (position > 0) { var n = position > 1 && char.IsLowSurrogate(draft[position - 1]) ? 2 : 1; draft = draft.Remove(position - n, n); position -= n; } break;
                case ConsoleKey.Delete: if (position < draft.Length) draft = draft.Remove(position, char.IsHighSurrogate(draft[position]) && position + 1 < draft.Length ? 2 : 1); break;
                case ConsoleKey.LeftArrow: if (position > 0) position -= position > 1 && char.IsLowSurrogate(draft[position - 1]) ? 2 : 1; break;
                case ConsoleKey.RightArrow: if (position < draft.Length) position += char.IsHighSurrogate(draft[position]) && position + 1 < draft.Length ? 2 : 1; break;
                case ConsoleKey.Home: position = 0; break;
                case ConsoleKey.End: position = draft.Length; break;
                case ConsoleKey.D when key.Modifiers.HasFlag(ConsoleModifiers.Control): EndOfInput = true; return (true, null, false);
                default: if (!char.IsControl(key.KeyChar)) { draft = draft.Insert(position, key.KeyChar.ToString()); position++; } break;
            }
            Draw(); return (false, null, false);
        }
    }
    public void Refresh() { lock (gate) { Erase(); Draw(); } }
    public void Restore(string text) { lock (gate) { Erase(); draft = text; position = text.Length; Draw(); } }
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
