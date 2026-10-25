using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Core;

namespace AgentSmith.Tools;

public static class ArtifactAccess
{
    public const int MaxBytes = 20 * 1024 * 1024;
    public static string Validate(ToolExecutionContext ctx, string path)
    {
        var root = Path.GetFullPath(ctx.ArtifactDirectory);
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new UserFacingException("Artifact is outside this conversation's artifact directory.");
        for (var item = new FileInfo(full) as FileSystemInfo; item is not null; item = item is FileInfo f ? f.Directory : ((DirectoryInfo)item).Parent)
        {
            try { if ((File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0) throw new UserFacingException("Artifact links and junctions are unsupported."); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
        return full;
    }
    public static ArtifactRecord Register(ToolExecutionContext ctx, string path, string kind, string? processId = null)
    {
        if (kind is not ("recording-analysis" or "audio-analysis" or "workspace-map" or "http-response" or "web-page" or "workflow-report" or "review-evidence" or "tool-result" or "process-log" or "build-diagnostics" or "test-report" or "verification" or "agent-report" or "agent-transcript")) throw new UserFacingException("Unsupported artifact kind.");
        var full = Validate(ctx, path);
        var artifact = ctx.Session.Artifacts.SingleOrDefault(a => a.Path.Equals(full, StringComparison.OrdinalIgnoreCase));
        if (artifact is not null) return artifact;
        artifact = new() { Path = full, Kind = kind, ProcessId = processId }; ctx.Session.Artifacts.Add(artifact);
        ctx.Save("artifact.registered", new { artifact.Id, artifact.Kind, artifact.ProcessId }); return artifact;
    }
    public static void Backfill(ToolExecutionContext ctx)
    {
        foreach (var process in ctx.Session.Processes)
        {
            if (process.LogPath.Length == 0) continue;
            try { Register(ctx, process.LogPath, "process-log", process.Id).Truncated = process.Truncated; }
            catch (Exception ex) when (ex is UserFacingException or ArgumentException or IOException or UnauthorizedAccessException) { }
        }
    }
    private sealed record Loaded(string State, string Text, Dictionary<string, string> Streams, bool Truncated);
    private static Loaded Load(ToolExecutionContext ctx, ArtifactRecord artifact)
    {
        var path = Validate(ctx, artifact.Path);
        if (!File.Exists(path)) return new("missing", "", [], artifact.Truncated);
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = (int)Math.Min(file.Length, MaxBytes); var bytes = new byte[length]; file.ReadExactly(bytes);
            var text = new UTF8Encoding(false, true).GetString(bytes);
            var truncated = file.Length > MaxBytes || artifact.Truncated;
            if (artifact.Kind != "process-log") return new(truncated ? "truncated" : "available", text, new() { ["text"] = text }, truncated);
            var rendered = new StringBuilder(); var streams = new Dictionary<string, StringBuilder>();
            var complete = text.LastIndexOf('\n');
            var process = ctx.Session.Processes.SingleOrDefault(p => p.Id == artifact.ProcessId);
            var partial = complete < text.Length - 1;
            var state = partial && process?.State != "Running" ? "corrupt" : "available";
            foreach (var line in (complete >= 0 ? text[..complete] : "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var value = JsonNode.Parse(line)!; var stream = value["stream"]!.GetValue<string>(); var content = value["text"]!.GetValue<string>();
                    if (stream is not ("stdout" or "stderr")) { state = "corrupt"; break; }
                    rendered.Append('[').Append(stream).Append("] ").Append(content);
                    if (!streams.TryGetValue(stream, out var buffer)) streams[stream] = buffer = new();
                    buffer.Append(content);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or NullReferenceException) { state = "corrupt"; break; }
            }
            return new(state == "corrupt" ? state : truncated ? "truncated" : state, rendered.ToString(), streams.ToDictionary(x => x.Key, x => x.Value.ToString()), truncated);
        }
        catch (DecoderFallbackException) { return new("corrupt", "", [], artifact.Truncated); }
        catch (IOException) { return new("unavailable", "", [], artifact.Truncated); }
    }
    public static ToolOutput Execute(ToolExecutionContext ctx, string name, JsonNode args)
    {
        Backfill(ctx);
        if (name == "artifact_list") return new(Json.Serialize(new { artifacts = ctx.Session.Artifacts.Take(200).Select(a => new { a.Id, a.Kind, a.ProcessId, a.Truncated }), grants = ctx.Session.ArtifactGrants, more = ctx.Session.Artifacts.Count > 200 }));
        var id = args["artifactId"]!.GetValue<string>();
        if (!ctx.Session.Artifacts.Any(a => a.Id == id) && ctx.Session.ArtifactGrants.SingleOrDefault(g => g.ArtifactId == id) is { } grant)
        {
            if (ctx.Session.ParentConversationId != grant.SourceConversationId) throw new UserFacingException("Invalid artifact grant.");
            var parent = ctx.Store.Get(grant.SourceConversationId) ?? throw new UserFacingException("Shared artifact source unavailable.");
            var parentContext = ctx with { Session = parent, Emit = null };
            if (!parent.Artifacts.Any(a => a.Id == id)) throw new UserFacingException("Shared artifact no longer registered.");
            // Do not backfill or save the parent's aggregate from a child.
            return ReadRegistered(parentContext, name, args, id);
        }
        return ReadRegistered(ctx, name, args, id);
    }
    private static ToolOutput ReadRegistered(ToolExecutionContext ctx, string name, JsonNode args, string id)
    {
        var artifact = ctx.Session.Artifacts.SingleOrDefault(a => a.Id == id) ?? throw new UserFacingException("Artifact is not registered to this conversation.");
        if (artifact.Kind == "recording-analysis") return ReadRecording(ctx, artifact, name, args);
        var loaded = Load(ctx, artifact);
        if (name == "audio_read")
        {
            if (artifact.Kind != "audio-analysis") throw new UserFacingException("Artifact is not audio evidence.");
            var view = args["view"]!.GetValue<string>();
            var rendered = AudioViews.Render(ParseAudio(loaded.Text), view);
            var cursor = args["cursor"]!.GetValue<int>(); var limit = args["maxCharacters"]!.GetValue<int>();
            if (cursor < 0 || cursor > rendered.Length || limit is < 1 or > 4000) throw new UserFacingException("Audio cursor must be within the selected view; maxCharacters is 1..4000.");
            if (cursor > 0 && cursor < rendered.Length && char.IsLowSurrogate(rendered[cursor])) throw new UserFacingException("Cursor splits a Unicode character.");
            var count = Math.Min(limit, rendered.Length - cursor);
            if (count > 0 && char.IsHighSurrogate(rendered[cursor + count - 1])) count--;
            if (count == 0 && cursor < rendered.Length) throw new UserFacingException("Increase maxCharacters to read a complete Unicode character.");
            return new(Json.Serialize(new { artifactId = id, view, state = loaded.State, loaded.Truncated,
                text = rendered.Substring(cursor, count), cursor = cursor + count, hasMore = cursor + count < rendered.Length,
                outcomes = AudioViews.Outcomes(ParseAudio(loaded.Text)) }));
        }
        if (name == "artifact_read")
        {
            var cursor = args["cursor"]!.GetValue<int>(); var limit = args["maxCharacters"]!.GetValue<int>();
            if (cursor < 0 || cursor > loaded.Text.Length || limit is < 1 or > 4000) throw new UserFacingException("Read cursor must be within retained content; maxCharacters is 1..4000.");
            if (cursor > 0 && cursor < loaded.Text.Length && char.IsLowSurrogate(loaded.Text[cursor])) throw new UserFacingException("Cursor splits a Unicode character.");
            var count = Math.Min(limit, loaded.Text.Length - cursor);
            if (count > 0 && char.IsHighSurrogate(loaded.Text[cursor + count - 1])) count--;
            if (count == 0 && cursor < loaded.Text.Length) throw new UserFacingException("Increase maxCharacters to read a complete Unicode character.");
            return new(Json.Serialize(new { artifactId = id, state = loaded.State, loaded.Truncated, text = loaded.Text.Substring(cursor, count), cursor = cursor + count, hasMore = cursor + count < loaded.Text.Length }));
        }
        var query = args["query"]!.GetValue<string>();
        if (query.Length is < 1 or > 512) throw new UserFacingException("Search text must contain 1..512 characters.");
        var matches = new List<object>(); var budget = 0; var more = false;
        foreach (var stream in loaded.Streams)
        {
            var offset = 0;
            while ((offset = stream.Value.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var start = Math.Max(0, offset - 60); var count = Math.Min(240, stream.Value.Length - start);
                var item = new { stream = stream.Key, offset, excerpt = stream.Value.Substring(start, count) };
                var size = Encoding.UTF8.GetByteCount(Json.Serialize(item));
                if (matches.Count == 100 || budget + size > 28000) { more = true; break; }
                matches.Add(item); budget += size; offset += query.Length;
            }
            if (more) break;
        }
        return new(Json.Serialize(new { artifactId = id, state = loaded.State, loaded.Truncated, matches, more }));
    }

    private static JsonObject ParseAudio(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject ?? new() { ["analysis"] = text }; }
        catch (System.Text.Json.JsonException) { return new() { ["analysis"] = text }; }
    }

    private static ToolOutput ReadRecording(ToolExecutionContext ctx, ArtifactRecord artifact, string name, JsonNode args)
    {
        var manifestPath = Validate(ctx, artifact.Path);
        var manifest = RecordingAnalysisStore.ReadJson<JsonObject>(manifestPath);
        if (manifest["version"]?.GetValue<int>() != 1 || manifest["kind"]?.ToString() != "recording-analysis") throw new UserFacingException("Unsupported recording artifact.");
        var directory = Path.GetDirectoryName(manifestPath)!;
        var allowed = new HashSet<string>(["job.json", "coverage.json", "summary.md", "transcript.jsonl", "speakers.jsonl", "sounds.jsonl"]);
        foreach (var file in manifest["files"]!.AsArray())
        {
            var filename = file!["name"]!.ToString();
            if (!allowed.Remove(filename)) throw new UserFacingException("Invalid recording artifact file.");
            var path = Validate(ctx, Path.Combine(directory, filename));
            if (RecordingAnalysisStore.Hash(path) != file["hash"]!.ToString()) throw new UserFacingException("Recording evidence changed since attachment.");
        }
        if (allowed.Overlaps(["job.json", "coverage.json", "transcript.jsonl", "speakers.jsonl", "sounds.jsonl"]) ||
            allowed.Any(filename => File.Exists(Path.Combine(directory, filename))))
            throw new UserFacingException("Recording artifact has missing or unregistered evidence files.");
        if (name is "audio_read" or "artifact_read")
        {
            var result = RecordingAnalysis.ReadView(directory, name == "audio_read" ? args["view"]!.ToString() : "summary", args["cursor"]!.GetValue<int>(), args["maxCharacters"]!.GetValue<int>());
            result["artifactId"] = artifact.Id; result["state"] = "available"; return new(result.ToJsonString());
        }
        var query = args["query"]!.ToString();
        if (query.Length is < 1 or > 512) throw new UserFacingException("Search text must contain 1..512 characters.");
        var matches = new JsonArray(); var more = false;
        foreach (var row in RecordingAnalysis.Rows(Path.Combine(directory, "transcript.jsonl")))
        {
            var text = row["text"]!.ToString(); var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            if (matches.Count >= 50) { more = true; break; }
            matches.Add(new JsonObject { ["id"] = row["id"]!.DeepClone(), ["startSeconds"] = row["startSeconds"]!.DeepClone(), ["excerpt"] = text.Substring(Math.Max(0, at - 60), Math.Min(240, text.Length - Math.Max(0, at - 60))) });
        }
        return new(new JsonObject { ["artifactId"] = artifact.Id, ["matches"] = matches, ["more"] = more }.ToJsonString());
    }

    public static bool IsAudio(ToolExecutionContext ctx, string id)
    {
        if (ctx.Session.Artifacts.Any(a => a.Id == id && a.Kind == "recording-analysis")) return true;
        var grant = ctx.Session.ArtifactGrants.SingleOrDefault(g => g.ArtifactId == id);
        if (grant is not null && ctx.Session.ParentConversationId == grant.SourceConversationId && ctx.Store.Get(grant.SourceConversationId)?.Artifacts.Any(a => a.Id == id && a.Kind == "recording-analysis") == true) return true;
        return ReadAudio(ctx, id) is not null;
    }

    internal static JsonObject? ReadAudio(ToolExecutionContext ctx, string id)
    {
        if (!ctx.Session.Artifacts.Any(a => a.Id == id))
        {
            var grant = ctx.Session.ArtifactGrants.SingleOrDefault(g => g.ArtifactId == id);
            if (grant is null || ctx.Session.ParentConversationId != grant.SourceConversationId) return null;
            var parent = ctx.Store.Get(grant.SourceConversationId);
            if (parent is null) return null;
            ctx = ctx with { Session = parent, Emit = null };
        }
        var artifact = ctx.Session.Artifacts.SingleOrDefault(a => a.Id == id && a.Kind == "audio-analysis");
        if (artifact is null) return null;
        var loaded = Load(ctx, artifact);
        return loaded.State == "available" ? ParseAudio(loaded.Text) : null;
    }
}
