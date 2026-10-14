using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using AgentSmith.Core;

namespace AgentSmith.Tools;

public sealed record DiagnosticEntry(string Id, string Severity, string? Code, string Message, string? Path, int? Line, int? Column,
    string? Test, string? Duration, string? StackTrace, int Occurrences, string[] Projects, string[] Frameworks, DiagnosticAssociation[] Associations);
public sealed record DiagnosticAssociation(string? Project, string? Framework);

public static class Diagnostics
{
    private static string? S(JsonNode? node, string key) => node?[key]?.GetValue<string>();
    private static int? N(JsonNode? node, string key) => node?[key] is JsonValue v && v.TryGetValue<int>(out var number) && number > 0 ? number : null;
    public static IReadOnlyList<DiagnosticEntry> Normalize(JsonNode report)
    {
        var entries = new List<DiagnosticEntry>();
        var diagnosticIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in report["diagnostics"]?.AsArray() ?? [])
        {
            if (item is null) continue;
            var severity = S(item, "type") ?? "unknown";
            if (severity is not ("error" or "warning")) continue;
            var signature = Json.Serialize(new { severity, code = S(item, "code"), message = S(item, "message"), path = S(item, "file"), line = N(item, "line"), column = N(item, "column") });
            var id = "D-" + CodeAnalysis.AnalysisHash.Text(signature)[..16];
            var previous = diagnosticIndexes.TryGetValue(id, out var existingIndex) ? existingIndex : -1;
            var projects = S(item, "project") is { Length: > 0 } p ? new[] { p } : [];
            var frameworks = S(item, "framework") is { Length: > 0 } f ? new[] { f } : [];
            if (previous >= 0)
            {
                var old = entries[previous];
                entries[previous] = old with { Occurrences = old.Occurrences + 1, Projects = old.Projects.Concat(projects).Distinct().ToArray(), Frameworks = old.Frameworks.Concat(frameworks).Distinct().ToArray(),
                    Associations = old.Associations.Append(new(S(item, "project"), S(item, "framework"))).Distinct().ToArray() };
            }
            else
            {
                diagnosticIndexes[id] = entries.Count;
                entries.Add(new(id, severity, S(item, "code"), S(item, "message") ?? "Message unavailable", S(item, "file"), N(item, "line"), N(item, "column"), null, null, null, 1, projects, frameworks, [new(S(item, "project"), S(item, "framework"))]));
            }
        }
        var testIndex = 0;
        foreach (var item in report["tests"]?.AsArray() ?? [])
        {
            if (item is null) continue;
            var outcome = S(item, "outcome")?.ToLowerInvariant() ?? "unknown";
            if (outcome is "notexecuted" or "skipped") outcome = "skipped";
            var id = "T-" + CodeAnalysis.AnalysisHash.Text((testIndex++).ToString(System.Globalization.CultureInfo.InvariantCulture) + item.ToJsonString())[..16];
            entries.Add(new(id, outcome, null, S(item, "message") ?? "", null, null, null, S(item, "name"), S(item, "duration"), S(item, "stackTrace"), 1, [], [], []));
        }
        return entries.OrderBy(e => e.Severity switch { "error" => 0, "failed" => 1, "warning" => 2, "skipped" => 3, "passed" => 5, _ => 4 }).ToArray();
    }

    public static ToolOutput Read(ToolExecutionContext ctx, JsonNode args)
    {
        var id = args["artifactId"]!.ToString();
        var owner = ctx;
        var artifact = owner.Session.Artifacts.SingleOrDefault(a => a.Id == id);
        if (artifact is null && ctx.Session.ArtifactGrants.SingleOrDefault(g => g.ArtifactId == id) is { } grant)
        {
            if (ctx.Session.ParentConversationId != grant.SourceConversationId) throw new UserFacingException("Invalid diagnostic artifact grant.");
            var parent = ctx.Store.Get(grant.SourceConversationId) ?? throw new UserFacingException("Diagnostic source is unavailable.");
            owner = ctx with { Session = parent, Emit = null };
            artifact = parent.Artifacts.SingleOrDefault(a => a.Id == id);
        }
        if (artifact is null || artifact.Kind is not ("verification" or "build-diagnostics" or "test-report")) throw new UserFacingException("Select an owned or explicitly shared verification, build-diagnostics or test-report artifact.");
        var path = ArtifactAccess.Validate(owner, artifact.Path);
        if (!File.Exists(path) || new FileInfo(path).Length > ArtifactAccess.MaxBytes) throw new UserFacingException("Diagnostic report is missing or exceeds 20 MiB. Use artifact_read for bounded raw evidence.");
        JsonNode report;
        string text;
        try
        {
            text = File.ReadAllText(path, new UTF8Encoding(false, true));
            if (artifact.Kind == "verification") report = JsonNode.Parse(text) ?? throw new InvalidDataException();
            else if (artifact.Kind == "build-diagnostics")
            {
                var events = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToArray();
                report = new JsonObject { ["state"] = events.Any(e => S(e, "type") == "finished") && !events.Any(e => S(e, "type") == "truncated" || e["truncated"]?.GetValue<bool>() == true) ? "complete" : "incomplete",
                    ["diagnostics"] = new JsonArray(events.Where(e => S(e, "type") is "error" or "warning").ToArray()) };
            }
            else
            {
                using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = ArtifactAccess.MaxBytes });
                var xml = XDocument.Load(reader); XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
                if (xml.Root?.Name != ns + "TestRun") throw new InvalidDataException();
                var tests = xml.Descendants(ns + "UnitTestResult").Select(t => (JsonNode)new JsonObject { ["name"] = (string?)t.Attribute("testName"), ["outcome"] = (string?)t.Attribute("outcome"), ["duration"] = (string?)t.Attribute("duration"), ["message"] = t.Descendants(ns + "Message").FirstOrDefault()?.Value, ["stackTrace"] = t.Descendants(ns + "StackTrace").FirstOrDefault()?.Value }).ToArray();
                var counters = xml.Descendants(ns + "Counters").SingleOrDefault();
                var complete = int.TryParse((string?)counters?.Attribute("total"), out var total) && total == tests.Length;
                report = new JsonObject { ["state"] = !complete ? "incomplete" : total == 0 ? "no_tests" : "complete", ["tests"] = new JsonArray(tests) };
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or XmlException or InvalidDataException or InvalidOperationException or DecoderFallbackException)
        { throw new UserFacingException("Structured report is malformed or truncated. Inspect its raw artifact; no passing result can be inferred."); }
        var filter = args["filter"]!.ToString(); var query = args["query"]!.ToString(); var pathFilter = args["path"]!.ToString();
        if (filter is not ("errors" or "warnings" or "failed" or "skipped" or "passed" or "all") || query.Length > 512 || pathFilter.Length > 512) throw new UserFacingException("Use filter errors|warnings|failed|skipped|passed|all; query and path are limited to 512 characters.");
        IReadOnlyList<DiagnosticEntry> all;
        try { all = Normalize(report); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { throw new UserFacingException("Unsupported historical report fields. Inspect the raw artifact."); }
        var selected = all.Where(e => (filter == "all" || e.Severity == (filter == "errors" ? "error" : filter == "warnings" ? "warning" : filter))
            && (pathFilter.Length == 0 || (e.Path ?? "").Replace('\\', '/').Contains(pathFilter.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            && (query.Length == 0 || Json.Serialize(e).Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        var identity = CodeAnalysis.AnalysisHash.Text(id + text + filter + pathFilter + query);
        var offset = 0; var cursor = args["cursor"]!.ToString();
        if (cursor.Length > 0)
        {
            var parts = cursor.Split(':');
            if (parts.Length != 2 || parts[0] != identity || !int.TryParse(parts[1], out offset) || offset < 0 || offset > selected.Length) throw new UserFacingException("Diagnostic cursor is stale or belongs to another filter. Start with an empty cursor.");
        }
        var result = new JsonObject { ["kind"] = "diagnostics", ["artifactId"] = id, ["sourceConversationId"] = owner.Session.Id,
            ["processId"] = artifact.ProcessId, ["state"] = Clip(report["state"]?.ToString() ?? "unavailable", 40),
            ["exitCode"] = report["exitCode"] is JsonValue code && code.TryGetValue<int>(out var exit) ? exit : null,
            ["truncated"] = artifact.Truncated, ["filter"] = filter, ["totalMatches"] = selected.Length, ["reason"] = Clip(report["reason"]?.ToString() ?? "", 500),
            ["notice"] = "Historical evidence. Completeness is not success. Missing locations/project/framework are unavailable, not inferred." };
        var items = new JsonArray();
        foreach (var entry in selected.Skip(offset).Take(50))
        {
            var bounded = entry with { Message = Clip(entry.Message, 1600), StackTrace = entry.StackTrace is null ? null : Clip(entry.StackTrace, 1000),
                Test = entry.Test is null ? null : Clip(entry.Test, 300), Path = entry.Path is null ? null : Clip(entry.Path, 500),
                Projects = entry.Projects.Take(4).Select(p => Clip(p, 300)).ToArray(), Frameworks = entry.Frameworks.Take(4).Select(f => Clip(f, 80)).ToArray(),
                Associations = entry.Associations.Take(4).Select(a => new DiagnosticAssociation(a.Project is null ? null : Clip(a.Project, 300), a.Framework is null ? null : Clip(a.Framework, 80))).ToArray() };
            var item = JsonNode.Parse(Json.Serialize(bounded))!;
            item["detailTruncated"] = entry.Message != bounded.Message || entry.StackTrace != bounded.StackTrace || entry.Test != bounded.Test || entry.Path != bounded.Path
                || !entry.Projects.SequenceEqual(bounded.Projects) || !entry.Frameworks.SequenceEqual(bounded.Frameworks) || !entry.Associations.SequenceEqual(bounded.Associations);
            if (Encoding.UTF8.GetByteCount(result.ToJsonString()) + Encoding.UTF8.GetByteCount(items.ToJsonString()) + Encoding.UTF8.GetByteCount(item.ToJsonString()) > 7500) break;
            items.Add(item);
        }
        if (selected.Length > offset && items.Count == 0) throw new UserFacingException("Diagnostic entry exceeds the preview budget. Read the raw artifact for details.");
        result["items"] = items; result["cursor"] = offset + items.Count < selected.Length ? identity + ":" + (offset + items.Count) : null;
        return new(result.ToJsonString());
    }
    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
