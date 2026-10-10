using System.Text;
using System.Text.Json.Nodes;

namespace AgentSmith.Core;

public enum PauseReason { None, RequestLimit, TimeLimit, ContextLimit, CompactionFailed, NoProgress, Network }
public sealed class ExecutionPolicy
{
    public int MaxRequests { get; set; } = 150;
    public int MaxMinutes { get; set; } = 60;
    public void Validate()
    {
        if (MaxRequests is < 1 or > 10000 || MaxMinutes is < 1 or > 1440)
            throw new UserFacingException("Limits require 1..10000 requests and 1..1440 minutes.");
    }
}
public sealed class ModelCapabilities
{
    public int? ContextTokens { get; set; }
    public bool Vision { get; set; }
    public int ConcurrentRequests { get; set; } = 1;
    public string Source { get; set; } = "unknown";
    public string Confidence { get; set; } = "unknown";
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class ContextPolicy
{
    public string Budget { get; set; } = "balanced";
    public double TokensPerByte { get; set; } = 0.5;
    public void Validate()
    {
        if (Budget is not ("balanced" or "max") && (!int.TryParse(Budget, out var n) || n < 1024))
            throw new UserFacingException("Context budget must be balanced, max, or at least 1024 tokens.");
    }
    public int? WorkingTokens(ModelConnection connection)
    {
        Validate();
        var capacity = connection.Capabilities.ContextTokens ?? connection.LoadedContextTokens;
        if (capacity is null) return int.TryParse(Budget, out var configured) ? configured : null;
        var usable = capacity.Value - connection.MaxOutputTokens - Math.Max(1024, (int)Math.Ceiling(capacity.Value * 0.05));
        if (usable < 1024) throw new UserFacingException("Model context leaves insufficient input space after output and safety reserves.");
        var budget = Budget == "max" ? usable : Budget == "balanced" ? Math.Min(128000, usable) : int.Parse(Budget);
        if (budget > usable) throw new UserFacingException("Requested input budget exceeds the model's usable context.");
        return budget;
    }
    public TokenMeasurement Estimate(ModelRequest request)
    {
        var copy = new JsonArray(request.Input.Select(i => (JsonNode)i.DeepClone()).ToArray());
        var images = 0;
        foreach (var item in copy.OfType<JsonObject>())
            if (item["content"] is JsonArray parts)
                foreach (var part in parts.OfType<JsonObject>().Where(p => p["type"]?.ToString() == "input_image"))
                { images++; part["image_url"] = "[image]"; }
        var bytes = Encoding.UTF8.GetByteCount(request.Instructions + copy.ToJsonString() + Json.Serialize(request.Tools));
        return new((int)Math.Ceiling(bytes * Math.Clamp(TokensPerByte, 0.5, 2)) + images * 4096, "estimated UTF-8 + image allowance", false);
    }
    public void Observe(ModelRequest request, string? usage)
    {
        if (usage is null) return;
        try
        {
            var tokens = JsonNode.Parse(usage)?["input_tokens"]?.GetValue<int>();
            if (tokens > 0)
            {
                var bytes = Encoding.UTF8.GetByteCount(request.Instructions + Json.Serialize(request.Input) + Json.Serialize(request.Tools));
                TokensPerByte = Math.Max(TokensPerByte, Math.Min(2, tokens.Value * 1.2 / Math.Max(1, bytes)));
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
    }
}
public sealed record TokenMeasurement(int Tokens, string Method, bool Exact);
public sealed class RequestLimitException() : Exception("Model request budget reached. Explicit continuation grants another bounded batch.");

public static class PauseGuidance
{
    public static string For(Run run) => run.PauseReason switch
    {
        PauseReason.RequestLimit => "Request budget reached. Inspect /limits or change /limits <requests> <minutes>, then type continue for another bounded batch.",
        PauseReason.TimeLimit => "Time budget reached. Inspect /limits, then type continue for another bounded batch.",
        PauseReason.NoProgress => "Repeated unchanged tool rounds detected. Inspect the saved work before continuing or abandoning it.",
        PauseReason.ContextLimit or PauseReason.CompactionFailed => "Context preparation paused. Inspect /context; use /compact to retry, then type continue. Saved edits remain intact.",
        _ => "Work paused. Inspect /status for the reason, then type continue explicitly to retry."
    } + " Queued follow-ups remain held.";
}

public static class ModelCatalog
{
    public const string Version = "2026-10-09";
    public static ModelCapabilities Published(string model) => model == "gpt-6.1-sol"
        ? new() { ContextTokens = 1050000, Vision = true, Confidence = "documented; account limit unverified", Source = "https://developers.openai.com/api/docs/models/gpt-6.1-sol (catalog " + Version + ")" }
        : new();
}
