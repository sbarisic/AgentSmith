using System.Text;

namespace AgentSmith.Cli;

// Presentation-only VT filter. Keep an instance per output stream so a split escape
// sequence cannot leak its parameters into the transcript. No terminal actions run.
internal sealed class TerminalControlFilter
{
    private enum State { Text, Escape, Intermediate, Csi, String, StringEscape }
    private State state;
    public string Append(string value)
    {
        var output = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (state != State.Text && c is '\n' or '\r')
            { state = State.Text; output.Append(c); continue; }
            if (c == '\u001b') { state = state is State.String or State.StringEscape ? State.StringEscape : State.Escape; continue; }
            switch (state)
            {
                case State.Escape:
                    state = c switch { '[' => State.Csi, ']' or 'P' or 'X' or '^' or '_' => State.String,
                        >= '\u0020' and <= '\u002f' => State.Intermediate, _ => State.Text }; break;
                case State.Intermediate:
                    if (c is >= '\u0030' and <= '\u007e') state = State.Text;
                    break;
                case State.Csi:
                    if (c is >= '\u0040' and <= '\u007e') state = State.Text;
                    break;
                case State.String:
                    if (c is '\a' or '\u009c') state = State.Text;
                    break;
                case State.StringEscape:
                    state = c is '\\' or '\a' or '\u009c' ? State.Text : State.String; break;
                default:
                    if (c == '\u009b') state = State.Csi;
                    else if (c is '\u0090' or '\u0098' or '\u009d' or '\u009e' or '\u009f') state = State.String;
                    else if ((!char.IsControl(c) || c is '\n' or '\r' or '\t') && c is not (>= '\u202a' and <= '\u202e') and not (>= '\u2066' and <= '\u2069')) output.Append(c);
                    break;
            }
        }
        return output.ToString();
    }
    public static string Strip(string value) => new TerminalControlFilter().Append(value);
}
