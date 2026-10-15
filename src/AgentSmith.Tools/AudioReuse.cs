using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Core;

namespace AgentSmith.Tools;

internal static class AudioReuse
{
    internal static string[] Required(string tool, bool speakers) => tool switch
    {
        "audio_classify" => ["classification"], "audio_diarize" => ["diarization"],
        "audio_transcribe" => speakers ? ["transcription", "diarization"] : ["transcription"],
        _ => speakers ? AudioViews.Components : ["transcription", "classification"]
    };
    internal static JsonObject Keys(string installation, string path, string hash, JsonNode args, string[] components)
    {
        string Read(string name) { var file = Path.Combine(installation, "helper", name); return File.Exists(file) ? File.ReadAllText(file) : "legacy"; }
        var version = FileOperations.Hash(Encoding.UTF8.GetBytes(Read("audio_helper.py") + Read("requirements.lock") + Read("downloads.json")));
        var keys = new JsonObject();
        foreach (var component in components)
            keys[component] = FileOperations.Hash(Encoding.UTF8.GetBytes(new JsonObject
            {
                ["path"] = Path.GetFullPath(path).ToUpperInvariant(), ["sourceHash"] = hash,
                ["start"] = args["startSeconds"]!.DeepClone(), ["duration"] = args["durationSeconds"]!.DeepClone(),
                ["normalization"] = 1, ["fingerprint"] = version, ["component"] = component,
                ["settings"] = component == "transcription" ? "words-v1;alignment-v1;language=" + (args["language"]?.ToString() ?? "auto") : component == "diarization" ? "auto;threshold=.5;on=.3;off=.5" : "yamnet-full-v1"
            }.ToJsonString()));
        return keys;
    }
    internal static (JsonObject Components, JsonObject Sources, JsonObject? Complete) Find(ToolExecutionContext ctx, JsonObject keys)
    {
        var components = new JsonObject(); var sources = new JsonObject(); JsonObject? complete = null;
        var ids = ctx.Session.Artifacts.Where(a => a.Kind == "audio-analysis").Select(a => a.Id).Concat(ctx.Session.ArtifactGrants.Select(g => g.ArtifactId)).Distinct().ToArray();
        foreach (var id in ids.Reverse())
        {
            JsonObject? doc;
            try { doc = ArtifactAccess.ReadAudio(ctx, id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UserFacingException) { continue; }
            if (doc is null) continue;
            var matches = keys.Where(k => doc["reuseKeys"]?[k.Key]?.ToString() == k.Value?.ToString() && doc[k.Key]?["status"]?.ToString() == "succeeded").Select(k => k.Key).ToArray();
            if (matches.Length == keys.Count && complete is null) complete = doc;
            foreach (var key in matches)
                if (!components.ContainsKey(key)) { components[key] = doc[key]!.DeepClone(); sources[key] = id; }
        }
        return (components, sources, complete);
    }

    internal static void Align(JsonObject result)
    {
        if (result["transcription"] is not JsonObject transcript) return;
        transcript.Remove("speakerSegments");
        if (result["diarization"]?["status"]?.ToString() != "succeeded" || result["diarization"]?["turns"] is not JsonArray turns || transcript["segments"] is not JsonArray segments) return;
        var aligned = new JsonArray();
        foreach (var segment in segments)
        {
            if (segment?["words"] is not JsonArray words) return; // Historical segments have no inferred word boundaries.
            JsonObject? group = null;
            foreach (var word in words)
            {
                var from = word!["startSeconds"]!.GetValue<double>(); var to = word["endSeconds"]!.GetValue<double>();
                var overlaps = turns.Where(t => t!["startSeconds"]!.GetValue<double>() < to && t["endSeconds"]!.GetValue<double>() > from)
                    .GroupBy(t => t!["speaker"]!.ToString()).ToArray();
                var label = "Unknown speaker";
                if (overlaps.Length > 1) label = "Overlapping/ambiguous speakers";
                else if (overlaps.Length == 1)
                {
                    var ranges = overlaps[0].Select(t => (Start: Math.Max(from, t!["startSeconds"]!.GetValue<double>()), End: Math.Min(to, t["endSeconds"]!.GetValue<double>()))).OrderBy(r => r.Start);
                    double coverage = 0, end = from;
                    foreach (var r in ranges) { coverage += Math.Max(0, r.End - Math.Max(end, r.Start)); end = Math.Max(end, r.End); }
                    if (to > from && coverage > (to - from) / 2) label = overlaps[0].Key;
                }
                if (group is null || group["speaker"]!.ToString() != label)
                {
                    group = new() { ["startSeconds"] = from, ["endSeconds"] = to, ["speaker"] = label, ["text"] = word["text"]!.ToString() };
                    aligned.Add(group);
                }
                else { group["endSeconds"] = to; group["text"] = group["text"]!.ToString() + word["text"]!.ToString(); }
            }
        }
        if (aligned.Count > 0) transcript["speakerSegments"] = aligned;
    }
}
