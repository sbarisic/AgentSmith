using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Core;

namespace AgentSmith.OpenAI;

public sealed class ChatGptModelClient(HttpClient http, Func<CancellationToken, Task<string>> accessToken) : IModelClient
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> countUnavailable = new();
    public async Task<TokenMeasurement?> CountTokensAsync(ModelRequest request, CancellationToken ct)
    {
        if (countUnavailable.ContainsKey(request.Model)) return null;
        var body = BuildRequest(request); body.Remove("stream"); body.Remove("store"); body.Remove("include"); body.Remove("max_output_tokens");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var message = new HttpRequestMessage(HttpMethod.Post, OAuthProtocol.Resource + "/responses/input_tokens");
        message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(timeout.Token));
            using var response = await http.SendAsync(message, timeout.Token);
            if (!response.IsSuccessStatusCode) { countUnavailable[request.Model] = true; return null; }
            var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            return data?["input_tokens"]?.GetValue<int>() is { } count ? new(count, "provider input-token count", true) : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { countUnavailable[request.Model] = true; return null; }
    }
    public static JsonObject BuildRequest(ModelRequest request)
    {
        var functions = new JsonArray(request.Tools.Where(t => !request.NativeWebSearch || t.Name != "web_search").Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["parameters"] = t.Parameters.DeepClone(),
            ["strict"] = t.Name != "agent_spawn"
        }).ToArray());
        var tools = new JsonArray();
        if (functions.Count > 0) tools.Add(new JsonObject { ["type"] = "namespace", ["name"] = "agentsmith", ["description"] = "AgentSmith tools enabled for this conversation", ["tools"] = functions });
        var include = new JsonArray("reasoning.encrypted_content");
        if (request.NativeWebSearch)
        {
            tools.Add(new JsonObject { ["type"] = "web_search" });
            include.Add("web_search_call.action.sources");
        }
        return new JsonObject
        {
            ["model"] = request.Model,
            ["instructions"] = request.Instructions,
            ["stream"] = true,
            ["store"] = false,
            ["input"] = new JsonArray(request.Input.Select(i => (JsonNode)i.DeepClone()).ToArray()),
            ["include"] = include,
            ["tools"] = tools
        };
    }
    public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, OAuthProtocol.Resource + "/responses");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(cancellationToken));
        message.Content = new StringContent(BuildRequest(request).ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var advice = request.NativeWebSearch && (int)response.StatusCode is 400 or 403 or 422
                ? " This request included native web search, whose availability depends on model/account policy. Use /web-search basic or /web-search off while idle if search is unavailable. No fallback was attempted."
                : " Check account access and usage limits.";
            throw new UserFacingException($"ChatGPT inference failed (HTTP {(int)response.StatusCode})." + advice);
        }
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var ev in ParseStreamAsync(stream, cancellationToken, nativeWebSearch: request.NativeWebSearch)) yield return ev;
    }
    public static async IAsyncEnumerable<ModelEvent> ParseStreamAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct, string provider = "ChatGPT", bool nativeWebSearch = false)
    {
        // Read chunks rather than ReadLineAsync: a malicious or broken server cannot allocate an unbounded line.
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var buffer = new char[4096];
        var pending = new StringBuilder();
        var data = new StringBuilder();
        var finishedItems = new SortedDictionary<int, JsonObject>();
        var displayedMessages = new HashSet<int>();
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
                // Native annotations arrive with the completed message. Avoid printing opaque markers first.
                if (type == "response.output_text.delta" && !nativeWebSearch) yield return new("text", item["delta"]?.GetValue<string>());
                if (nativeWebSearch && type is "response.web_search_call.in_progress" or "response.web_search_call.searching" or "response.web_search_call.completed")
                    yield return new("web-search", type == "response.web_search_call.completed" ? "Web search completed" : "Searching web…");
                if (type == "response.output_item.done")
                {
                    if (item["item"] is not JsonObject finished || item["output_index"] is null) throw new UserFacingException("Invalid completed output item.");
                    var index = item["output_index"]!.GetValue<int>();
                    if (index < 0 || index > 255 || !finishedItems.TryAdd(index, (JsonObject)finished.DeepClone())) throw new UserFacingException("Invalid or duplicate output index.");
                    if (nativeWebSearch && finished["type"]?.ToString() == "message")
                    {
                        displayedMessages.Add(index);
                        yield return new("text", WebCitations.Message(finished));
                    }
                }
                if (type is "response.failed" or "response.incomplete" or "error") throw new UserFacingException($"{provider} returned an unsuccessful response. Check model access, output budget, and context limits."
                    + (nativeWebSearch ? " Native web search was enabled; if unavailable, select /web-search basic or /web-search off while idle. No fallback was attempted." : ""));
                if (type == "response.completed")
                {
                    var result = item["response"]?.AsObject();
                    if (result?["status"]?.GetValue<string>() != "completed") throw new UserFacingException("Invalid response completion event.");
                    var output = result["output"] as JsonArray;
                    IReadOnlyList<JsonObject> finalItems = output is { Count: > 0 }
                        ? output.Select(i => (JsonObject)i!.DeepClone()).ToArray()
                        : finishedItems.Values.ToArray();
                    if (finalItems.Count == 0) throw new UserFacingException("Completed response contained no usable output items.");
                    if (nativeWebSearch)
                    {
                        IEnumerable<KeyValuePair<int, JsonObject>> indexed = output is { Count: > 0 } ? finalItems.Select((value, index) => new KeyValuePair<int, JsonObject>(index, value)) : finishedItems;
                        foreach (var (index, value) in indexed)
                            if (value["type"]?.ToString() == "message" && displayedMessages.Add(index))
                                yield return new("text", WebCitations.Message(value));
                    }
                    yield return new("completed", Output: finalItems, Usage: result["usage"]?.ToJsonString());
                    yield break;
                }
            }
        }
        throw new UserFacingException("The response stream disconnected before completion. No proposed tools were executed.");
    }
}
