using System.Text;
using System.Text.RegularExpressions;

namespace AgentSmith.Runtime;

// A conservative stop condition, not a text deduplicator. Repeated source material
// remains intact in tool results; this guards generated answer text only.
public sealed class ResponseRepetitionGuard
{
    private readonly StringBuilder tail = new();
    private int uncheckedCharacters;
    public bool Append(string? text, bool final = false)
    {
        if (text is not null)
        {
            // Check large provider deltas in bounded pieces as well as normal token deltas.
            for (var offset = 0; offset < text.Length; offset += 256)
            {
                var length = Math.Min(256, text.Length - offset);
                tail.Append(text, offset, length); uncheckedCharacters += length;
                if (tail.Length > 32768) tail.Remove(0, tail.Length - 32768);
                if (uncheckedCharacters >= 256 && Repeating()) return true;
            }
        }
        return final && Repeating();
    }
    private bool Repeating()
    {
        uncheckedCharacters = 0;
        var words = Regex.Matches(tail.ToString(), @"[\p{L}\p{N}]+")
            .Select(m => m.Value.ToLowerInvariant()).TakeLast(1024).ToArray();
        const int window = 24;
        var phrases = new Dictionary<string, (int Count, int Last)>(StringComparer.Ordinal);
        for (var i = 0; i + window <= words.Length; i++)
        {
            var phrase = string.Join(' ', words.Skip(i).Take(window));
            if (phrase.Length < 120 || words.Skip(i).Take(window).Distinct().Count() < 10) continue;
            var previous = phrases.GetValueOrDefault(phrase, (Count: 0, Last: -window));
            if (i - previous.Last < window) continue;
            var count = previous.Count + 1;
            if (count >= 5) return true;
            phrases[phrase] = (count, i);
        }
        return false;
    }
}

public sealed class ResponseRepetitionException : Exception;
