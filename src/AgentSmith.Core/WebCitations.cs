using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentSmith.Core;

/// <summary>Display-only projection. Provider items and citation offsets remain unchanged in history.</summary>
public static class WebCitations
{
    private static readonly Regex Marker = new("\uE200[^\uE201\r\n]{0,1024}\uE201", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static string Message(JsonObject item) => item["content"] is JsonArray parts
        ? string.Concat(parts.OfType<JsonObject>().Where(p => p["type"]?.ToString() is "output_text" or "text" or "refusal").Select(Part))
        : item["content"]?.ToString() ?? "";

    private static string Part(JsonObject part)
    {
        var text = part["text"]?.ToString() ?? part["refusal"]?.ToString() ?? "";
        if (part["annotations"] is not JsonArray annotations) return text;
        var links = new SortedDictionary<int, List<string>>();
        var markers = Marker.Matches(text).ToArray();
        var seen = new HashSet<(int, string)>();
        foreach (var citation in annotations.OfType<JsonObject>().Take(64))
        {
            if (citation["type"]?.ToString() != "url_citation") continue;
            var url = citation["url"]?.ToString();
            if (url is null || url.Length > 2048 || url.Any(char.IsControl) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || MemoryPolicy.HasSecret(Uri.UnescapeDataString(url))) continue;
            var position = text.Length;
            if (citation["start_index"] is JsonValue startValue && startValue.TryGetValue<int>(out var start)
                && citation["end_index"] is JsonValue endValue && endValue.TryGetValue<int>(out var end)
                && start >= 0 && start <= end && end <= text.Length
                && (end == text.Length || !char.IsLowSurrogate(text[end]))) position = end;
            foreach (var marker in markers)
                if (position > marker.Index && position < marker.Index + marker.Length) { position = marker.Index + marker.Length; break; }
            if (!seen.Add((position, url))) continue;
            var title = new string(Activity.Clean(citation["title"]?.ToString() ?? uri.Host, 120).Where(c => !char.IsControl(c) && !char.IsSurrogate(c)
                && c is not ('[' or ']' or '*' or '`' or '\\' or '\uE200' or '\uE201')).Take(120).ToArray());
            if (string.IsNullOrWhiteSpace(title)) title = uri.Host;
            var destination = uri.AbsoluteUri.Replace("(", "%28").Replace(")", "%29");
            if (!links.TryGetValue(position, out var at)) links[position] = at = [];
            at.Add($"[{title}]({destination})");
        }
        var result = new StringBuilder(); var offset = 0;
        foreach (var (position, at) in links)
        {
            result.Append(text.AsSpan(offset, position - offset));
            result.Append(' ').AppendJoin(", ", at);
            offset = position;
        }
        result.Append(text.AsSpan(offset));
        return Marker.Replace(result.ToString(), "");
    }
}
