using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.OpenAI;

namespace AgentSmithLite;

internal interface IChatModel
{
    Task<IReadOnlyList<JsonObject>> RespondAsync(string model, string instructions,
        IReadOnlyList<JsonObject> history, Action<string> text, CancellationToken ct);
}

internal sealed class ModelClient(HttpClient http, Func<CancellationToken, Task<string>> token) : IChatModel
{
    public async Task<IReadOnlyList<JsonObject>> RespondAsync(string model, string instructions,
        IReadOnlyList<JsonObject> history, Action<string> text, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model, ["instructions"] = instructions, ["store"] = false, ["stream"] = true,
            ["include"] = new JsonArray("reasoning.encrypted_content"),
            ["input"] = new JsonArray(history.Select(i => (JsonNode)i.DeepClone()).ToArray()),
            ["tools"] = BasicTools.Definitions()
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, OAuthProtocol.Resource + "/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(ct));
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new UserFacingException($"ChatGPT returned HTTP {(int)response.StatusCode}. Check sign-in, model access and account limits. No automatic retry was made.");
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await ReadResponseAsync(stream, text, ct);
    }

    internal static async Task<IReadOnlyList<JsonObject>> ReadResponseAsync(Stream stream, Action<string> text, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var buffer = new char[4096];
        var line = new StringBuilder();
        var data = new StringBuilder();
        var finishedItems = new SortedDictionary<int, JsonObject>();
        var total = 0;
        var displayed = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) break;
            total += count;
            if (total > 4 * 1024 * 1024) throw new UserFacingException("Response exceeded the 4 MiB limit.");
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] != '\n')
                {
                    line.Append(buffer[i]);
                    if (line.Length > 1024 * 1024) throw new UserFacingException("Response event is too large.");
                    continue;
                }
                var value = line.ToString().TrimEnd('\r'); line.Clear();
                if (value.StartsWith("data:", StringComparison.Ordinal)) data.AppendLine(value[5..].TrimStart(' '));
                if (data.Length > 1024 * 1024) throw new UserFacingException("Response event is too large.");
                if (value.Length != 0 || data.Length == 0) continue;
                var json = data.ToString().Trim(); data.Clear();
                if (json == "[DONE]") throw new UserFacingException("Response ended before completion. No proposed tools were executed.");
                var ev = JsonNode.Parse(json)?.AsObject() ?? throw new JsonException("Empty response event.");
                var type = ev["type"]?.GetValue<string>();
                if (type == "response.output_text.delta" || type == "response.refusal.delta")
                {
                    var delta = ev["delta"]?.GetValue<string>() ?? "";
                    text(delta); displayed |= delta.Length > 0;
                }
                if (type is "error" or "response.failed" or "response.incomplete")
                    throw new UserFacingException("Model response failed or was incomplete. No proposed tools were executed.");
                if (type == "response.output_item.done")
                {
                    if (ev["output_index"] is not JsonValue indexValue || !indexValue.TryGetValue<int>(out var index)
                        || index < 0 || index >= 256 || finishedItems.ContainsKey(index))
                        throw new UserFacingException("Invalid or duplicate completed output index. No proposed tools were executed.");
                    finishedItems.Add(index, CloneCompletedItem(ev["item"]));
                }
                if (type == "response.completed")
                {
                    if (ev["response"] is not JsonObject response || response["status"]?.ToString() != "completed")
                        throw new UserFacingException($"Invalid response completion: missing or non-completed status (streamed items: {finishedItems.Count}). No proposed tools were executed.");
                    var rawOutput = response["output"];
                    if (rawOutput is not null && rawOutput is not JsonArray)
                        throw new UserFacingException("Invalid response completion: output is not an array. No proposed tools were executed.");
                    var output = rawOutput as JsonArray;
                    if (output?.Count > 256) throw new UserFacingException("Too many response items.");
                    // Some streams deliver final items individually without repeating them
                    // in the completion snapshot. Preserve their order and opaque metadata.
                    // Items alone never authorize execution: successful completion is required.
                    var items = output is { Count: > 0 }
                        ? output.Select(CloneCompletedItem).ToArray()
                        : finishedItems.Values.ToArray();
                    if (items.Length == 0)
                        throw new UserFacingException("Completed response contained no output items in either the stream or final snapshot. No proposed tools were executed.");
                    if (!displayed)
                        foreach (var message in items.Where(item => item["type"]?.ToString() == "message"))
                            if (message["content"] is JsonArray parts)
                                foreach (var part in parts.OfType<JsonObject>())
                                    text(part["text"]?.GetValue<string>() ?? part["refusal"]?.GetValue<string>() ?? "");
                    return items;
                }
            }
        }
        throw new UserFacingException("Response disconnected before completion. No proposed tools were executed.");
    }

    private static JsonObject CloneCompletedItem(JsonNode? node)
    {
        if (node is not JsonObject item || item["type"] is not JsonValue type || !type.TryGetValue<string>(out var kind) || string.IsNullOrEmpty(kind))
            throw new UserFacingException("Malformed completed output item. No proposed tools were executed.");
        if (item["status"] is { } status && status.ToString() != "completed")
            throw new UserFacingException("Output item is not completed. No proposed tools were executed.");
        return (JsonObject)item.DeepClone();
    }
}
