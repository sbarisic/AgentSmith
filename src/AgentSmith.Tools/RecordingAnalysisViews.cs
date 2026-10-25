using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentSmith.Core;

namespace AgentSmith.Tools;

public sealed partial class RecordingAnalysis
{
    internal sealed record Speaker(string Label, double[] Vector);
    internal static double Similarity(double[] a, double[] b) => a.Length == b.Length ? a.Zip(b).Sum(x => x.First * x.Second) : -1;
    internal static string MatchSpeaker(double[] vector, List<Speaker> speakers, HashSet<string> excluded)
    {
        var matches = speakers.Where(s => !excluded.Contains(s.Label)).Select(s => (s.Label, Score: Similarity(vector, s.Vector))).OrderByDescending(s => s.Score).ToArray();
        if (matches.Length > 0 && matches[0].Score >= .75 && (matches.Length == 1 || matches[0].Score - matches[1].Score >= .10)) return matches[0].Label;
        if (matches.Length > 0 && matches[0].Score >= .65) return "Unknown speaker";
        var label = "Speaker " + (speakers.Count + 1); speakers.Add(new(label, vector)); return label;
    }
    private void Assemble(RecordingAnalysisJob job, CancellationToken ct)
    {
        var speakers = new List<Speaker>();
        var transcriptPath = Store.FileFor(job.Id, "transcript.jsonl");
        var previous = new List<JsonObject>();
        using (var transcript = new StreamWriter(transcriptPath + ".new", false, new UTF8Encoding(false)))
        using (var turnsOutput = new StreamWriter(Store.FileFor(job.Id, "speakers.jsonl") + ".new", false, new UTF8Encoding(false)))
        using (var sounds = new StreamWriter(Store.FileFor(job.Id, "sounds.jsonl") + ".new", false, new UTF8Encoding(false)))
        {
            foreach (var chunk in job.Chunks)
            {
                ct.ThrowIfCancellationRequested();
                var path = Store.FileFor(job.Id, $"evidence-{chunk.Index:D6}.json");
                if (!File.Exists(path)) continue;
                var result = RecordingAnalysisStore.ReadJson<JsonObject>(path);
                AudioReuse.Align(result);
                var map = new Dictionary<string, string>();
                if (result["diarization"]?["embeddings"] is JsonArray embeddings)
                {
                    var turnList = result["diarization"]?["turns"]?.AsArray();
                    foreach (var e in embeddings.OfType<JsonObject>().OrderBy(e => turnList?.Where(t => t?["speaker"]?.ToString() == e["speaker"]!.ToString()).Select(t => N(t!["startSeconds"])).DefaultIfEmpty(double.MaxValue).Min()))
                    {
                        var vector = e["vector"]!.AsArray().Select(n => n!.GetValue<double>()).ToArray();
                        var local = e["speaker"]!.ToString();
                        var localTurns = turnList?.OfType<JsonObject>().Where(t => t["speaker"]!.ToString() == local).ToArray() ?? [];
                        var excluded = map.Where(entry => turnList?.OfType<JsonObject>().Where(t => t["speaker"]!.ToString() == entry.Key)
                            .Any(t => localTurns.Any(other => N(t["startSeconds"]) < N(other["endSeconds"]) && N(t["endSeconds"]) > N(other["startSeconds"]))) == true)
                            .Select(entry => entry.Value).ToHashSet();
                        var label = MatchSpeaker(vector, speakers, excluded);
                        map[local] = label;
                    }
                }
                string Label(string local) => map.GetValueOrDefault(local) ?? (local.StartsWith("Speaker ", StringComparison.Ordinal) ? $"Chunk {chunk.Index + 1} / {local}" : local);
                if (result["diarization"]?["turns"] is JsonArray turns)
                    foreach (var t in turns.OfType<JsonObject>())
                    {
                        var row = Offset(t, chunk.Start / 16000d); row["localSpeaker"] = t["speaker"]!.ToString(); row["speaker"] = Label(t["speaker"]!.ToString());
                        if (Trim(row, chunk)) turnsOutput.WriteLine(row.ToJsonString());
                    }
                var current = new List<JsonObject>();
                if (result["transcription"]?["status"]?.ToString() == "succeeded" && result["transcription"]?["segments"] is JsonArray segments)
                {
                    var number = 0;
                    foreach (var segment in segments.OfType<JsonObject>())
                    {
                        var words = segment["words"] as JsonArray;
                        // Keep both contexts until adjacent evidence has been reconciled. Midpoint cropping
                        // first can lose a word when the two decodes place it on opposite sides of a boundary.
                        var selected = words?.OfType<JsonObject>().Select(w => Offset(w, chunk.Start / 16000d)).ToArray();
                        if (words is not null && selected!.Length == 0) continue;
                        var row = Offset(segment, chunk.Start / 16000d);
                        row.Remove("words");
                        if (selected is { Length: > 0 })
                        {
                            row["text"] = string.Concat(selected.Select(w => w["text"]!.ToString())).Trim();
                            row["startSeconds"] = N(selected[0]["startSeconds"]); row["endSeconds"] = N(selected[^1]["endSeconds"]);
                            row["words"] = new JsonArray(selected.Select(w => (JsonNode)w).ToArray());
                        }
                        var begin = N(row["startSeconds"]); var end = N(row["endSeconds"]);
                        var matching = result["diarization"]?["turns"]?.AsArray().OfType<JsonObject>()
                            .Where(t => N(t["startSeconds"]) + chunk.Start / 16000d < end && N(t["endSeconds"]) + chunk.Start / 16000d > begin)
                            .Select(t => Label(t["speaker"]!.ToString())).Distinct().ToArray() ?? [];
                        row["speaker"] = matching.Length == 1 ? matching[0] : matching.Length > 1 ? "Overlapping/ambiguous speakers" : "Unknown speaker";
                        row["id"] = $"c{chunk.Index:D6}s{number++:D5}"; row["chunk"] = chunk.Index;
                        row["boundaryUncertain"] = words is null && (begin < chunk.CoreStart / 16000d || end > chunk.CoreEnd / 16000d);
                        current.Add(row);
                    }
                }
                ReconcileBoundary(previous, current);
                var merged = previous.Concat(current).OrderBy(r => N(r["startSeconds"])).ThenBy(r => N(r["endSeconds"])).ToList();
                var boundary = chunk.CoreEnd / 16000d - 5;
                var safe = Math.Min(boundary, merged.Where(r => N(r["endSeconds"]) >= boundary).Select(r => N(r["startSeconds"])).DefaultIfEmpty(double.MaxValue).Min());
                foreach (var row in merged.Where(r => N(r["startSeconds"]) < safe)) transcript.WriteLine(row.ToJsonString());
                previous = merged.Where(r => N(r["startSeconds"]) >= safe).ToList();
                if (result["classification"]?["windows"] is JsonArray windows)
                {
                    var index = 0;
                    foreach (var w in windows.OfType<JsonObject>())
                    {
                        var row = Offset(w, chunk.Start / 16000d);
                        if (!Trim(row, chunk)) continue;
                        row["id"] = $"c{chunk.Index:D6}a{index++:D5}";
                        var labels = row["labels"]!.AsArray().Where(l => N(l!["score"]) >= .20).Select(l => $"{l!["label"]}: {N(l["score"]):0.000}");
                        row["text"] = "Approximate sound-model scores, not certainty: " + string.Join(", ", labels);
                        sounds.WriteLine(row.ToJsonString());
                    }
                }
            }
            foreach (var row in previous) transcript.WriteLine(row.ToJsonString());
        }
        var evidenceChanged = new[] { "transcript.jsonl", "speakers.jsonl", "sounds.jsonl" }.Any(file =>
            !File.Exists(Store.FileFor(job.Id, file)) || RecordingAnalysisStore.Hash(Store.FileFor(job.Id, file)) != RecordingAnalysisStore.Hash(Store.FileFor(job.Id, file) + ".new"));
        if (evidenceChanged) File.Delete(Store.FileFor(job.Id, "summary.md"));
        foreach (var file in new[] { "transcript.jsonl", "speakers.jsonl", "sounds.jsonl" }) RecordingAnalysisStore.CommitFile(Store.FileFor(job.Id, file) + ".new", Store.FileFor(job.Id, file));
        RecordingAnalysisStore.WriteJson(Store.FileFor(job.Id, "coverage.json"), new { job.Frames, job.DurationSeconds, job.ProcessedSeconds, job.Coverage,
            intervals = job.Chunks.Select(c => new { c.Index, c.CoreStart, c.CoreEnd, c.Components }), speakers = speakers.Select(s => s.Label), job.Reconciliation });
    }
    internal static void ReconcileBoundary(List<JsonObject> previous, List<JsonObject> current)
    {
        if (previous.Count == 0 || current.Count == 0) return;
        var leftEnd = previous.Max(r => N(r["endSeconds"]));
        var rightStart = current.Min(r => N(r["startSeconds"]));
        var leftWords = previous.SelectMany(r => r["words"]?.AsArray().OfType<JsonObject>() ?? []).Where(w => N(w["endSeconds"]) > rightStart).ToArray();
        var cursor = 0;
        foreach (var right in current.ToArray())
        {
            if (right["words"] is not JsonArray words) { if (N(right["startSeconds"]) < leftEnd) right["boundaryUncertain"] = true; continue; }
            var removed = new JsonArray();
            foreach (var word in words.OfType<JsonObject>().ToArray())
            {
                if (N(word["startSeconds"]) >= leftEnd) break;
                var match = Array.FindIndex(leftWords, cursor, w => SameWord(w, word));
                if (match >= 0) { cursor = match + 1; removed.Add(word.DeepClone()); words.Remove(word); }
                else right["boundaryUncertain"] = true;
            }
            if (removed.Count == 0) continue;
            if (words.Count == 0)
            {
                previous[^1]["overlapEvidence"] = removed; current.Remove(right);
            }
            else
            {
                right["overlapEvidence"] = removed; right["text"] = string.Concat(words.Select(w => w!["text"]!.ToString())).Trim();
                right["startSeconds"] = words[0]!["startSeconds"]!.DeepClone(); right["endSeconds"] = words[^1]!["endSeconds"]!.DeepClone();
            }
        }
    }
    private static bool SameWord(JsonNode a, JsonNode b) => a["text"]!.ToString().Trim().Equals(b["text"]!.ToString().Trim(), StringComparison.OrdinalIgnoreCase)
        && Math.Min(N(a["endSeconds"]), N(b["endSeconds"])) > Math.Max(N(a["startSeconds"]), N(b["startSeconds"]));
    private static double N(JsonNode? n) => n?.GetValue<double>() ?? 0;
    private static bool Owns(JsonObject row, RecordingAnalysisChunk c) { var midpoint = (N(row["startSeconds"]) + N(row["endSeconds"])) * 8000; return midpoint >= c.CoreStart && midpoint < c.CoreEnd; }
    private static bool Trim(JsonObject row, RecordingAnalysisChunk c)
    {
        var start = Math.Max(N(row["startSeconds"]), c.CoreStart / 16000d); var end = Math.Min(N(row["endSeconds"]), c.CoreEnd / 16000d);
        if (end <= start) return false; row["startSeconds"] = start; row["endSeconds"] = end; return true;
    }
    private static JsonObject Offset(JsonObject row, double seconds)
    { var copy = (JsonObject)row.DeepClone(); copy["startSeconds"] = N(row["startSeconds"]) + seconds; copy["endSeconds"] = N(row["endSeconds"]) + seconds; return copy; }
    public static IEnumerable<JsonObject> Rows(string path)
    {
        RecordingAnalysisStore.CheckPath(path);
        if (!File.Exists(path)) yield break;
        foreach (var line in File.ReadLines(path))
        { if (line.Length > 2 * 1024 * 1024) throw new IOException("Analysis row too large."); yield return JsonNode.Parse(line)!.AsObject(); }
    }
    internal static IEnumerable<string> ViewLines(string directory, string view)
    {
        string FileAt(string name) { var p = Path.Combine(directory, name); RecordingAnalysisStore.CheckPath(p); return p; }
        if (view is "metadata" or "coverage" or "summary")
        {
            var path = FileAt(view == "metadata" ? "job.json" : view == "summary" ? "summary.md" : "coverage.json");
            if (!File.Exists(path)) { yield return "This view is not available yet."; yield break; }
            foreach (var line in File.ReadLines(path)) yield return line;
            yield break;
        }
        if (view is not ("transcript" or "speakers" or "sounds")) throw new UserFacingException("View: summary, transcript, speakers, sounds, coverage or metadata.");
        foreach (var row in Rows(FileAt(view + ".jsonl")))
            yield return view == "transcript" ? $"[{row["id"]} {AudioViews.Time(N(row["startSeconds"]))}–{AudioViews.Time(N(row["endSeconds"]))}] {row["speaker"]}: {row["text"]}" + (row["boundaryUncertain"]?.GetValue<bool>() == true ? " [uncertain boundary]" : "") : row.ToJsonString();
    }
    public static JsonObject ReadView(string directory, string view, int cursor = 0, int maxCharacters = 4000)
    {
        if (cursor < 0 || maxCharacters is < 1 or > 4000) throw new UserFacingException("Cursor must be nonnegative; maxCharacters 1..4000.");
        var text = new StringBuilder(); var offset = 0; var more = false;
        foreach (var line in ViewLines(directory, view))
        {
            var value = line + "\n";
            if (offset + value.Length <= cursor) { offset += value.Length; continue; }
            var start = Math.Max(0, cursor - offset);
            if (start > 0 && char.IsLowSurrogate(value[start])) throw new UserFacingException("Cursor splits a Unicode character.");
            var count = Math.Min(maxCharacters - text.Length, value.Length - start);
            if (count > 0 && char.IsHighSurrogate(value[start + count - 1])) count--;
            if (count == 0 && text.Length == 0) throw new UserFacingException("Page length is too small for this Unicode character; use maxCharacters of at least 2.");
            text.Append(value.AsSpan(start, count));
            if (count < value.Length - start || text.Length == maxCharacters) { more = true; break; }
            offset += value.Length;
        }
        return new() { ["view"] = view, ["text"] = text.ToString(), ["cursor"] = cursor + text.Length, ["hasMore"] = more };
    }
    public JsonObject Read(string id, string view, int cursor = 0) => ReadView(Store.DirectoryFor(id), view, cursor);
    public string Export(string id, string destination)
    {
        using var lease = Store.Lease(id); var job = Store.Read(id);
        destination = Path.GetFullPath(destination); RecordingAnalysisStore.CheckPath(destination);
        if (Path.Exists(destination)) throw new UserFacingException("Export destination must be new.");
        var parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) throw new UserFacingException("Export parent directory must exist.");
        var staging = Path.Combine(parent, ".smith-analysis-export-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        foreach (var name in new[] { "job.json", "coverage.json", "summary.md", "transcript.jsonl", "speakers.jsonl", "sounds.jsonl" })
        { var source = Store.FileFor(id, name); if (File.Exists(source)) File.Copy(source, Path.Combine(staging, name)); }
        File.WriteAllText(Path.Combine(staging, "index.md"), $"# Recording analysis {id}\n\nState: {job.State}. Processed {job.ProcessedSeconds:0.0}/{job.DurationSeconds:0.0} seconds.\n\n" +
            "Read coverage.json before assuming completeness. Transcript files contain model estimates, not verified facts. Anonymous speakers may be misattributed.\n\n" +
            "- [Timestamped transcript](transcript.txt)\n- [Subtitles](transcript.srt)\n- [Structured transcript](transcript.jsonl)\n- [Coverage](coverage.json)\n" +
            (File.Exists(Path.Combine(staging, "summary.md")) ? "- [Cited summary](summary.md)\n" : "\nNo completed summary is available.\n"));
        File.WriteAllLines(Path.Combine(staging, "transcript.txt"), ViewLines(Store.DirectoryFor(id), "transcript"));
        using (var srt = new StreamWriter(Path.Combine(staging, "transcript.srt")))
        {
            var index = 0;
            foreach (var row in Rows(Store.FileFor(id, "transcript.jsonl")))
            { srt.WriteLine(++index); srt.WriteLine($"{SrtTime(N(row["startSeconds"]))} --> {SrtTime(N(row["endSeconds"]))}"); srt.WriteLine($"{row["speaker"]}: {row["text"]}\n"); }
        }
        Directory.Move(staging, destination); return destination;
    }
    private static string SrtTime(double seconds) { var time = TimeSpan.FromSeconds(seconds); return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}"; }
}
