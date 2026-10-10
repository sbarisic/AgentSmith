using System.Globalization;

namespace AgentSmith.Cli;

internal static class TerminalDuration
{
    public static string Format(TimeSpan duration, string secondsFormat = "0.0")
    {
        var seconds = Math.Max(0, duration.TotalSeconds);
        if (seconds < 60) return seconds.ToString(secondsFormat, CultureInfo.InvariantCulture) + "s";
        var wholeSeconds = (long)seconds;
        return string.Create(CultureInfo.InvariantCulture, $"{wholeSeconds / 60}m {wholeSeconds % 60:00}s");
    }
}
