using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Core;

namespace AgentSmith.Tools;

/// <summary>Deterministic presentation of retained evidence; never runs inference.</summary>
public static class AudioViews
{
    public static readonly string[] Components = ["transcription", "classification", "diarization"];
    public static JsonObject Outcomes(JsonObject doc) => new(Components.Select(c => new KeyValuePair<string, JsonNode?>(c, doc[c]?["status"]?.DeepClone())));
    public static string Time(double seconds)
    {
        var tenths = (long)Math.Round(Math.Max(0, seconds) * 10, MidpointRounding.AwayFromZero);
        return $"{tenths / 600:00}:{tenths % 600 / 10:00}.{tenths % 10}";
    }
    private static double N(JsonNode? n) => n?.GetValue<double>() ?? 0;
    private static IEnumerable<JsonNode> Rows(JsonNode? node) => node is JsonArray a ? a.OfType<JsonNode>() : [];
    public sealed record Interval(string Label, double Start, double End, double Peak);
    public static IReadOnlyList<Interval> Timeline(JsonObject doc)
    {
        var done = new List<Interval>(); var active = new Dictionary<string, Interval>();
        foreach (var window in Rows(doc["classification"]?["windows"]))
        {
            var next = new Dictionary<string, Interval>();
            foreach (var label in Rows(window["labels"]).Where(l => N(l["score"]) >= .20))
            {
                var name = label["label"]!.ToString(); var start = N(window["startSeconds"]); var end = N(window["endSeconds"]); var score = N(label["score"]);
                next[name] = active.Remove(name, out var old) && start <= old.End + .001
                    ? old with { End = end, Peak = Math.Max(old.Peak, score) } : new(name, start, end, score);
                if (old is not null && start > old.End + .001) done.Add(old);
            }
            done.AddRange(active.Values); active = next;
        }
        done.AddRange(active.Values);
        return done.OrderBy(i => i.Start).ThenBy(i => i.Label, StringComparer.Ordinal).ToArray();
    }
    public static string Render(JsonObject doc, string view)
    {
        if (view is not ("summary" or "transcript" or "speakers" or "timeline" or "scores" or "metadata"))
            throw new UserFacingException("Audio view must be summary, transcript, speakers, timeline, scores or metadata.");
        var text = new StringBuilder();
        if (view == "summary")
        {
            text.AppendLine($"Audio: {doc["path"]} · {N(doc["durationSeconds"]):0.0} s · {doc["model"]}");
            foreach (var c in Components)
                if (doc[c] is { } component) text.AppendLine($"{c}: {component["status"]}" + (component["error"] is { } error ? " · " + error : ""));
            if (doc["transcription"] is { } transcript)
                text.AppendLine(transcript["noSpeech"]?.GetValue<bool>() == true ? "No speech detected." : $"Transcript: {transcript["language"]}; {Rows(transcript["segments"]).Count()} segments. Use transcript view for text.");
            if (doc["diarization"]?["status"]?.ToString() == "succeeded") text.AppendLine($"Estimated speakers: {doc["diarization"]!["speakerCount"]}. Labels are local to this analysis.");
            else if (doc["transcription"] is not null) text.AppendLine("Speaker separation unavailable; this does not establish a single speaker.");
            if (doc["classification"]?["status"]?.ToString() == "succeeded")
            {
                text.AppendLine("Sound labels (mean model scores, not certainty; display threshold 0.05):");
                var labels = Rows(doc["classification"]!["aggregateScores"]).Where(l => N(l["score"]) >= .05).OrderByDescending(l => N(l["score"])).Take(5).ToArray();
                foreach (var l in labels) text.AppendLine($"{l["label"]}: {N(l["score"]):0.000}");
                if (labels.Length == 0) text.AppendLine("No label met the display threshold.");
                var all = Timeline(doc); var top = all.OrderByDescending(i => i.Peak).ThenBy(i => i.Start).Take(10).OrderBy(i => i.Start);
                foreach (var i in top) text.AppendLine($"{Time(i.Start)}–{Time(i.End)} {i.Label} · peak {i.Peak:0.000}");
                text.AppendLine($"Approximate windows; display threshold 0.20. {Math.Max(0, all.Count - 10)} additional intervals in timeline view. Omitted labels do not establish absence.");
            }
            if (!Components.Any(c => doc[c] is not null)) text.AppendLine(doc["analysis"]?.ToString() ?? "Historical audio summary unavailable.");
        }
        else if (view == "transcript")
        {
            var t = doc["transcription"];
            if (t?["status"]?.ToString() != "succeeded") text.AppendLine("Structured transcript unavailable. " + t?["error"]);
            else if (t["noSpeech"]?.GetValue<bool>() == true) text.AppendLine("No speech detected.");
            else foreach (var s in Rows(t["speakerSegments"] ?? t["segments"]))
                text.AppendLine($"[{Time(N(s["startSeconds"]))}–{Time(N(s["endSeconds"]))}] {(s["speaker"] is { } speaker ? speaker + ": " : "")}{s["text"]}");
        }
        else if (view == "speakers")
        {
            var d = doc["diarization"];
            if (d?["status"]?.ToString() != "succeeded") text.AppendLine("Speaker separation unavailable. " + d?["error"]);
            else
            {
                text.AppendLine($"Estimated speakers: {d["speakerCount"]}. Anonymous labels apply only to this analysis.");
                foreach (var t in Rows(d["turns"])) text.AppendLine($"{Time(N(t["startSeconds"]))}–{Time(N(t["endSeconds"]))} {t["speaker"]}");
            }
        }
        else if (view == "timeline")
        {
            text.AppendLine("Approximate sound windows; scores are not certainty. Display threshold 0.20; omission does not prove absence.");
            if (doc["classification"]?["windows"] is null) text.AppendLine("Structured sound timeline unavailable.");
            else foreach (var i in Timeline(doc)) text.AppendLine($"{Time(i.Start)}–{Time(i.End)} {i.Label} · peak {i.Peak:0.000}");
        }
        else if (view == "scores")
        {
            text.AppendLine("Unfiltered retained model scores, not calibrated certainty.");
            if (doc["classification"] is not { } c) text.AppendLine("Structured scores unavailable.");
            else
            {
                foreach (var l in Rows(c["aggregateScores"])) text.AppendLine($"Mean {l["label"]}: {N(l["score"]).ToString("G", CultureInfo.InvariantCulture)}");
                foreach (var w in Rows(c["windows"])) foreach (var l in Rows(w["labels"])) text.AppendLine($"{Time(N(w["startSeconds"]))}–{Time(N(w["endSeconds"]))} {l["label"]}: {N(l["score"]).ToString("G", CultureInfo.InvariantCulture)}");
            }
        }
        else
        {
            var copy = (JsonObject)doc.DeepClone();
            foreach (var key in Components.Concat(new[] { "analysis", "modelManifest" })) copy.Remove(key);
            foreach (var c in Components) if (doc[c] is JsonObject component)
            {
                var metadata = (JsonObject)component.DeepClone();
                foreach (var key in new[] { "segments", "speakerSegments", "turns", "classMap", "aggregateScores", "windows" }) metadata.Remove(key);
                copy[c] = metadata;
            }
            text.AppendLine(copy.ToJsonString(new() { WriteIndented = true }));
        }
        return text.ToString();
    }
}
