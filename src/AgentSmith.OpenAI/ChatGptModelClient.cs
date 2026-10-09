using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Core;

namespace AgentSmith.OpenAI;

public sealed class ChatGptModelClient(HttpClient http, Func<CancellationToken, Task<string>> accessToken) : IModelClient
{
    public static JsonObject BuildRequest(ModelRequest request)
    {
        var functions = new JsonArray(request.Tools.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["parameters"] = t.Parameters.DeepClone(),
            ["strict"] = true
        }).ToArray());
        return new JsonObject
        {
            ["model"] = request.Model,
            ["instructions"] = request.Instructions,
            ["stream"] = true,
            ["store"] = false,
            ["input"] = new JsonArray(request.Input.Select(i => (JsonNode)i.DeepClone()).ToArray()),
            ["include"] = new JsonArray("reasoning.encrypted_content"),
            ["tools"] = new JsonArray(new JsonObject { ["type"] = "namespace", ["name"] = "agentsmith", ["description"] = "AgentSmith tools enabled for this conversation", ["tools"] = functions })
        };
    }
    public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, OAuthProtocol.Resource + "/responses");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(cancellationToken));
        message.Content = new StringContent(BuildRequest(request).ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new UserFacingException($"ChatGPT inference failed (HTTP {(int)response.StatusCode}). Check account access and usage limits.");
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var ev in ParseStreamAsync(stream, cancellationToken)) yield return ev;
    }
    public static async IAsyncEnumerable<ModelEvent> ParseStreamAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        // Read chunks rather than ReadLineAsync: a malicious or broken server cannot allocate an unbounded line.
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var buffer = new char[4096];
        var pending = new StringBuilder();
        var data = new StringBuilder();
        var finishedItems = new SortedDictionary<int, JsonObject>();
        var total = 0;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) break;
            total += count;
            if (total > 16 * 1024 * 1024) throw new UserFacingException("Model response exceeded the stream limit.");
            for (var i = 0; i < count; i++)
            {
                var ch = buffer[i];
                if (ch != '\n')
                {
                    pending.Append(ch);
                    if (pending.Length > 2 * 1024 * 1024) throw new UserFacingException("Model stream event exceeded the limit.");
                    continue;
                }
                var line = pending.ToString().TrimEnd('\r'); pending.Clear();
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.AppendLine(line[5..].TrimStart(' '));
                    if (data.Length > 2 * 1024 * 1024) throw new UserFacingException("Model stream event exceeded the limit.");
                }
                if (line.Length != 0 || data.Length == 0) continue;
                var json = data.ToString().Trim(); data.Clear();
                if (json == "[DONE]") throw new UserFacingException("Stream ended without a completed response.");
                JsonObject item;
                try { item = JsonNode.Parse(json)!.AsObject(); }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { throw new UserFacingException("Malformed model stream event."); }
                var type = item["type"]?.GetValue<string>();
                if (type == "response.output_text.delta") yield return new("text", item["delta"]?.GetValue<string>());
                if (type == "response.output_item.done")
                {
                    if (item["item"] is not JsonObject finished || item["output_index"] is null) throw new UserFacingException("Invalid completed output item.");
                    var index = item["output_index"]!.GetValue<int>();
                    if (index < 0 || index > 255 || !finishedItems.TryAdd(index, (JsonObject)finished.DeepClone())) throw new UserFacingException("Invalid or duplicate output index.");
                }
                if (type is "response.failed" or "response.incomplete" or "error") throw new UserFacingException("ChatGPT returned an unsuccessful response. Check access, usage, and context limits.");
                if (type == "response.completed")
                {
                    var result = item["response"]?.AsObject();
                    if (result?["status"]?.GetValue<string>() != "completed") throw new UserFacingException("Invalid response completion event.");
                    var output = result["output"] as JsonArray;
                    IReadOnlyList<JsonObject> finalItems = output is { Count: > 0 }
                        ? output.Select(i => (JsonObject)i!.DeepClone()).ToArray()
                        : finishedItems.Values.ToArray();
                    if (finalItems.Count == 0) throw new UserFacingException("Completed response contained no usable output items.");
                    yield return new("completed", Output: finalItems, Usage: result["usage"]?.ToJsonString());
                    yield break;
                }
            }
        }
        throw new UserFacingException("The response stream disconnected before completion. No proposed tools were executed.");
    }
}
