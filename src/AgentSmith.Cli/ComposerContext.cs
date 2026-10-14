using AgentSmith.Core;
using System.Globalization;
using System.Text.Json;

namespace AgentSmith.Cli;

internal sealed record ComposerContext(string Model, WorkflowMode Workflow, PermissionMode Permission,
    string ContextUsage = "ctx pending", string CompactContext = "ctx pending")
{
    public static ComposerContext From(Session session)
    {
        var usage = ReadContext(session.Runs.LastOrDefault()?.ContextManifest);
        return new(session.Model, session.Workflow, session.Mode, usage.Full, usage.Short);
    }

    public static string WorkerSummary(IEnumerable<WorkerRecord> workers)
    {
        // Include waiting/prepared and cancelling workers until they actually settle.
        var active = workers.Where(worker => worker.Active).OrderBy(worker => worker.Number).ToArray();
        if (active.Length == 0) return "agents 0";
        var labels = active.Take(8).Select(worker => "#" + worker.Number.ToString(CultureInfo.InvariantCulture)).ToList();
        if (active.Length > labels.Count) labels.Add("+" + (active.Length - labels.Count).ToString(CultureInfo.InvariantCulture));
        return $"agents {active.Length} [" + string.Join(", ", labels) + "]";
    }

    private static (string Full, string Short) ReadContext(string? manifest)
    {
        // Use the last prepared input, not cumulative billing totals. No model requests or
        // workspace scans on the input/render thread. New tool results may not be counted yet.
        if (string.IsNullOrWhiteSpace(manifest)) return ("ctx pending", "ctx pending");
        try
        {
            using var document = JsonDocument.Parse(manifest);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ("ctx unavailable", "ctx unavailable");
            static int? Number(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
            var budget = Number(root, "workingBudget") ?? Number(root, "limit");
            var tokens = root.TryGetProperty("unit", out var unit) && unit.ValueKind == JsonValueKind.String && unit.GetString() == "tokens";
            var measured = root.TryGetProperty("measurement", out var measurement) && measurement.ValueKind == JsonValueKind.Object;
            var used = tokens ? measured ? Number(measurement, "tokens") : null : Number(root, "characters");
            if (used is null or < 0 || budget is null or <= 0) return ("ctx unavailable", "ctx unavailable");
            var exact = tokens && measured && measurement.TryGetProperty("exact", out var exactValue) && exactValue.ValueKind == JsonValueKind.True;
            var estimate = exact ? "" : "~";
            var percent = Math.Round(100.0 * used.Value / budget.Value).ToString("0", CultureInfo.InvariantCulture);
            static string Count(int value) => value >= 1000 ? (value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k" : value.ToString(CultureInfo.InvariantCulture);
            return ($"ctx last {estimate}{Count(used.Value)}/{Count(budget.Value)} {(tokens ? "tok" : "chars")} ({percent}%)",
                $"ctx last {estimate}{percent}%{(tokens ? "" : " ch")}");
        }
        catch (JsonException) { return ("ctx unavailable", "ctx unavailable"); }
    }

    public string Line(int width)
    {
        if (width <= 0) return "";
        // Neither provider adapter currently sends a reasoning-effort override. Do not invent a level.
        var mode = $"{Workflow} / {Permission}";
        var suffix = " · reasoning: default · " + mode + " · " + ContextUsage;
        if (suffix.Length + 8 > width) suffix = $" · default · {Workflow}/{Permission} · {ContextUsage}";
        if (suffix.Length + 8 > width) suffix = $" · {Workflow}/{Permission} · {CompactContext}";
        if (suffix.Length + 4 > width) return Fit($"{Workflow}/{Permission} · {CompactContext}", width);
        var model = Activity.Clean(Model, 2048);
        // Local model IDs may be full filesystem paths; keep the useful model filename.
        if (model.Contains('\\') || model.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            model = model.Replace('\\', '/').Split('/').Last();
        if (string.IsNullOrWhiteSpace(model)) model = "Model unselected";
        return Fit(model, width - suffix.Length) + suffix;
    }

    private static string Fit(string text, int width)
    {
        if (text.Length <= width) return text;
        var length = Math.Max(0, width - 1);
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length] + "…";
    }
}
