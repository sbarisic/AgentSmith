using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSmith.Core;

namespace AgentSmith.LmStudio;

public sealed record ChatModel(string Id, int? ContextTokens, bool Vision = false, int Slots = 1);

// Local OpenAI-compatible Chat Completions transport. No account tokens or provider fallback.
public sealed class LocalChatModelClient(HttpClient http, ModelConnection connection) : IModelClient
{
    public async Task<TokenMeasurement?> CountTokensAsync(ModelRequest request, CancellationToken ct)
    {
        CheckHeaders();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var body = BuildRequest(request, request.MaxOutputTokens ?? connection.MaxOutputTokens);
            var images = 0;
            foreach (var message in body["messages"]!.AsArray())
                if (message?["content"] is JsonArray parts)
                    for (var i = 0; i < parts.Count; i++) if (parts[i]?["type"]?.ToString() == "image_url") { images++; parts[i] = new JsonObject { ["type"] = "text", ["text"] = "[image]" }; }
            body["add_generation_prompt"] = true;
            using var templated = await http.PostAsync(new Uri(Endpoint(connection.Endpoint), "../apply-template"), new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), timeout.Token);
            if (!templated.IsSuccessStatusCode)
            {
                if (templated.StatusCode is not (System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)) throw CountingFailure($"Local chat-template counting failed (HTTP {(int)templated.StatusCode}).");
                return await CountStrataAsync(body, images, timeout.Token);
            }
            var prompt = JsonNode.Parse(await templated.Content.ReadAsStringAsync(timeout.Token))?["prompt"]?.ToString();
            if (prompt is null) throw CountingFailure("Local chat-template counting returned no prompt.");
            var input = new JsonObject { ["content"] = prompt, ["add_special"] = false, ["parse_special"] = true };
            using var tokenized = await http.PostAsync(new Uri(Endpoint(connection.Endpoint), "../tokenize"), new StringContent(input.ToJsonString(), Encoding.UTF8, "application/json"), timeout.Token);
            if (!tokenized.IsSuccessStatusCode) throw CountingFailure($"Local tokenizer failed (HTTP {(int)tokenized.StatusCode}).");
            var tokens = JsonNode.Parse(await tokenized.Content.ReadAsStringAsync(timeout.Token))?["tokens"]?.AsArray().Count;
            return tokens is null ? throw CountingFailure("Local tokenizer returned no token array.") : new(tokens.Value + images * 4096, images == 0 ? "server chat template/tokenizer" : "server text tokenizer + estimated image tokens", images == 0);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw CountingFailure("Local token counting timed out after 30 seconds."); }
        catch (HttpRequestException) { throw CountingFailure("Local token counting could not reach the model server."); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { throw CountingFailure("Local token counting returned an invalid response."); }
    }

    private static UserFacingException CountingFailure(string reason) => Failures.Reject("local_token_count_failed",
        reason + " Context size is unknown; no estimate-driven compaction was started. Check the local server and continue to retry.",
        "Restore the local tokenizer endpoint, then continue. Saved tool results are retained.");

    // Strata exposes its tokenizer through the Messages counting API, not llama.cpp's
    // apply-template/tokenize routes. Convert the same chat transcript, including tools.
    private async Task<TokenMeasurement?> CountStrataAsync(JsonObject chat, int images, CancellationToken ct)
    {
        using var props = await http.GetAsync(new Uri(Endpoint(connection.Endpoint), "../props"), ct);
        if (!props.IsSuccessStatusCode)
        {
            if (props.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed) return null;
            throw CountingFailure($"Local tokenizer discovery failed (HTTP {(int)props.StatusCode}).");
        }
        var info = JsonNode.Parse(await props.Content.ReadAsStringAsync(ct));
        if (info?["build_info"]?.ToString().StartsWith("Strata ", StringComparison.OrdinalIgnoreCase) != true) return null;
        var messages = new JsonArray();
        var system = new StringBuilder();
        var transcript = chat["messages"]!.AsArray().Select(m => m!).ToList();
        // Strata's chat path pairs a parallel result batch by call ID before applying
        // the template; the Messages path pairs by position. Use the same ordering.
        for (var i = 1; i < transcript.Count; i++)
        {
            if (transcript[i]["role"]?.ToString() != "tool") continue;
            var end = i + 1;
            while (end < transcript.Count && transcript[end]["role"]?.ToString() == "tool") end++;
            var calls = transcript[i - 1]["tool_calls"] as JsonArray;
            var ids = calls?.Select(c => c!["id"]!.ToString()).ToArray() ?? [];
            var results = transcript.GetRange(i, end - i);
            var resultIds = results.Select(r => r["tool_call_id"]!.ToString()).ToArray();
            if (ids.Length == results.Count && ids.Distinct().Count() == ids.Length && resultIds.Distinct().Count() == ids.Length && ids.ToHashSet().SetEquals(resultIds))
            {
                var ordered = results.OrderBy(r => Array.IndexOf(ids, r["tool_call_id"]!.ToString())).ToArray();
                for (var j = 0; j < ordered.Length; j++) transcript[i + j] = ordered[j];
            }
            i = end - 1;
        }
        foreach (var message in transcript)
        {
            var role = message!["role"]!.ToString();
            if (ReferenceEquals(message, transcript[0])) { system.Append(message["content"]!.ToString()); continue; }
            if (role is "system" or "developer") role = "user";
            var blocks = new JsonArray();
            if (role == "tool")
                blocks.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = message["tool_call_id"]!.DeepClone(), ["content"] = message["content"]!.DeepClone() });
            else
            {
                if (message["reasoning_content"] is { } reasoning)
                    blocks.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = reasoning.DeepClone() });
                var content = message["content"];
                if (content is JsonArray parts)
                    foreach (var part in parts) blocks.Add(part!.DeepClone());
                else if (content?.ToString() is { Length: > 0 } text)
                    blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                if (message["tool_calls"] is JsonArray calls)
                    foreach (var call in calls)
                        blocks.Add(new JsonObject { ["type"] = "tool_use", ["id"] = call!["id"]!.DeepClone(),
                            ["name"] = call["function"]!["name"]!.DeepClone(), ["input"] = JsonNode.Parse(call["function"]!["arguments"]!.ToString()) });
            }
            messages.Add(new JsonObject { ["role"] = role == "tool" ? "user" : role, ["content"] = blocks });
        }
        var request = new JsonObject { ["model"] = chat["model"]!.DeepClone(), ["system"] = system.ToString(), ["messages"] = messages };
        if (chat["tools"] is JsonArray tools)
            request["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject { ["name"] = t!["function"]!["name"]!.DeepClone(),
                ["description"] = t["function"]!["description"]!.DeepClone(), ["input_schema"] = t["function"]!["parameters"]!.DeepClone() }).ToArray());
        async Task<int> Count()
        {
            using var response = await http.PostAsync(new Uri(Endpoint(connection.Endpoint), "messages/count_tokens"), new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            if (!response.IsSuccessStatusCode) throw CountingFailure($"Strata token counting failed (HTTP {(int)response.StatusCode}).");
            var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            return result?["input_tokens"] is JsonValue count && count.TryGetValue<int>(out var tokens) && tokens > 0
                ? tokens : throw CountingFailure("Strata token counting returned no valid input-token count.");
        }
        // The counting API inherits shared reasoning defaults, just like inference.
        // Its separate Anthropic on_request setting can still change the template.
        // Compare with explicit model thinking: equal counts are exact; otherwise
        // keep a server-tokenized upper bound, never falsely advertise exactness.
        var inherited = await Count();
        request["thinking"] = new JsonObject { ["type"] = "enabled" };
        var enabled = await Count();
        var sameTemplateCount = inherited == enabled;
        return new(Math.Max(inherited, enabled) + images * 4096,
            images > 0 ? "Strata text tokenizer + estimated image tokens" : sameTemplateCount ? "Strata server chat tokenizer" : "Strata server tokenizer upper bound (reasoning defaults differ)",
            images == 0 && sameTemplateCount);
    }
    public static Uri Endpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "http" || !uri.IsLoopback ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath is not ("/" or "/v1" or "/v1/"))
            throw new UserFacingException("Local Chat Completions requires a loopback HTTP URL, for example http://127.0.0.1:8080/v1.");
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/v1/");
    }

    private void CheckHeaders()
    {
        if (http.DefaultRequestHeaders.Authorization is not null || http.DefaultRequestHeaders.Contains("api-key") || http.DefaultRequestHeaders.Contains("x-api-key"))
            throw new UserFacingException("Local transport must not carry account credentials.");
    }

    public async Task<IReadOnlyList<ChatModel>> ModelsAsync(CancellationToken ct)
    {
        CheckHeaders();
        using var response = await http.GetAsync(new Uri(Endpoint(connection.Endpoint), "models"), ct);
        if (!response.IsSuccessStatusCode) throw new UserFacingException($"Local model discovery failed (HTTP {(int)response.StatusCode}). Check the server and endpoint.");
        var catalog = Parse(await response.Content.ReadAsStringAsync(ct));
        if (catalog["data"] is not JsonArray models) throw new UserFacingException("Invalid local model catalog.");
        JsonNode? props = null;
        try { using var r = await http.GetAsync(new Uri(Endpoint(connection.Endpoint), "../props"), ct); if (r.IsSuccessStatusCode) props = JsonNode.Parse(await r.Content.ReadAsStringAsync(ct)); }
        catch (HttpRequestException) { }
        return models.Select(m => new ChatModel(String(m?["id"]), m?["meta"]?["n_ctx"] is { } capacity ? Integer(capacity) : null,
            props?["modalities"]?["vision"]?.GetValue<bool>() == true, Math.Clamp(props?["total_slots"]?.GetValue<int>() ?? 1, 1, 16))).Where(m => m.Id.Length > 0).ToArray();
    }

    public static JsonObject BuildRequest(ModelRequest request, int maxTokens)
    {
        if (maxTokens is < 256 or > 32768) throw new UserFacingException("Output budget must be 256..32768 tokens.");
        var messages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = request.Instructions });
        JsonObject? assistant = null;
        JsonObject Assistant() => assistant ??= new JsonObject { ["role"] = "assistant", ["content"] = "" };
        void Flush() { if (assistant is not null) { messages.Add(assistant); assistant = null; } }
        foreach (var item in request.Input)
        {
            var type = String(item["type"]); var role = String(item["role"]);
            if (type == "function_call")
            {
                var message = Assistant();
                if (message["tool_calls"] is null) message["tool_calls"] = new JsonArray();
                message["tool_calls"]!.AsArray().Add(new JsonObject { ["id"] = String(item["call_id"]), ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = String(item["name"]), ["arguments"] = String(item["arguments"]) } });
            }
            else if (type == "function_call_output")
            {
                Flush(); messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = String(item["call_id"]), ["content"] = String(item["output"]) });
            }
            else if (type == "reasoning" && item["local_chat_reasoning"] is not null)
                Assistant()["reasoning_content"] = String(item["local_chat_reasoning"]);
            else if (role is "user" or "assistant" or "system" or "developer")
            {
                var content = item["content"] is JsonArray parts
                    ? string.Concat(parts.Select(p => String(p?["text"]))) : String(item["content"]);
                if (role == "assistant") { var message = Assistant(); message["content"] = String(message["content"]) + content; }
                else
                {
                    Flush();
                    JsonNode value = JsonValue.Create(content)!;
                    if (item["content"] is JsonArray rich && rich.Any(p => p?["type"]?.ToString() == "input_image"))
                        value = new JsonArray(rich.Select(p => (JsonNode)(p?["type"]?.ToString() == "input_image"
                            ? new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = p?["image_url"]?.ToString() ?? throw new UserFacingException("Image attachment was not resolved.") } }
                            : new JsonObject { ["type"] = "text", ["text"] = p?["text"]?.ToString() ?? "" })).ToArray());
                    messages.Add(new JsonObject { ["role"] = role, ["content"] = value });
                }
            }
            else throw new UserFacingException("Unsupported history item for local Chat Completions; provider history cannot be mixed.");
        }
        Flush();
        var body = new JsonObject { ["model"] = request.Model, ["messages"] = messages, ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true }, ["max_tokens"] = maxTokens };
        if (request.Tools.Count > 0) body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode)new JsonObject { ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone() } }).ToArray());
        return body;
    }

    public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        CheckHeaders();
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(Endpoint(connection.Endpoint), "chat/completions"))
        { Content = new StringContent(BuildRequest(request, request.MaxOutputTokens ?? connection.MaxOutputTokens).ToJsonString(), Encoding.UTF8, "application/json") };
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new UserFacingException($"Local Chat Completions failed (HTTP {(int)response.StatusCode}). Check model, server, and context limits; no fallback or retry was performed.");
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var ev in ParseStreamAsync(stream, cancellationToken)) yield return ev;
    }

    public static async IAsyncEnumerable<ModelEvent> ParseStreamAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        var text = new StringBuilder(); var reasoning = new StringBuilder();
        var calls = new SortedDictionary<int, Call>(); string? finish = null; string? usage = null;
        await foreach (var data in Events(stream, ct))
        {
            if (data == "[DONE]")
            {
                if (finish is not ("stop" or "tool_calls") || calls.Count == 0 && string.IsNullOrWhiteSpace(text.ToString()) ||
                    (finish == "tool_calls") != (calls.Count > 0))
                    throw new UserFacingException("Local response was incomplete, reasoning-only, or exhausted its output budget. No proposed calls were executed.");
                var output = new List<JsonObject>();
                if (reasoning.Length > 0) output.Add(new() { ["type"] = "reasoning", ["local_chat_reasoning"] = reasoning.ToString() });
                if (text.Length > 0) output.Add(new() { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text.ToString() }) });
                var ids = new HashSet<string>();
                foreach (var call in calls.Values)
                {
                    if (call.Id.Length is 0 or > 256 || !System.Text.RegularExpressions.Regex.IsMatch(call.Name.ToString(), "^[a-zA-Z0-9_-]{1,64}$") || !ids.Add(call.Id.ToString()) || call.Kind != "function")
                        throw new UserFacingException("Invalid local tool-call identity or type.");
                    Parse(call.Arguments.ToString()); // Arguments must be a complete JSON object before any dispatch.
                    output.Add(new() { ["type"] = "function_call", ["call_id"] = call.Id.ToString(), ["name"] = call.Name.ToString(), ["arguments"] = call.Arguments.ToString() });
                }
                ct.ThrowIfCancellationRequested();
                yield return new("completed", Output: output, Usage: usage); yield break;
            }
            var chunk = Parse(data);
            if (chunk["error"] is not null) throw new UserFacingException("Local server returned a streaming error.");
            if (chunk["usage"] is JsonObject u) usage = new JsonObject { ["input_tokens"] = u["prompt_tokens"]?.DeepClone(), ["input_tokens_details"] = u["prompt_tokens_details"]?.DeepClone(), ["output_tokens"] = u["completion_tokens"]?.DeepClone(), ["total_tokens"] = u["total_tokens"]?.DeepClone() }.ToJsonString();
            if (chunk["choices"] is not JsonArray choices) throw new UserFacingException("Missing local stream choices.");
            if (choices.Count == 0) continue; // A final usage-only chunk is valid.
            if (choices.Count != 1 || choices[0] is not JsonObject || Integer(choices[0]?["index"]) != 0 || finish is not null) throw new UserFacingException("Unexpected local stream choice or data after completion.");
            var choice = choices[0]!;
            if (choice["delta"] is not null and not JsonObject) throw new UserFacingException("Invalid local stream delta.");
            if (choice["delta"] is JsonObject delta)
            {
                var content = String(delta["content"]); text.Append(content);
                if (content.Length > 0) yield return new("text", content);
                reasoning.Append(String(delta["reasoning_content"]));
                if (delta["tool_calls"] is JsonArray pieces)
                    foreach (var piece in pieces)
                    {
                        if (piece is not JsonObject) throw new UserFacingException("Invalid local tool-call delta.");
                        var index = Integer(piece["index"]);
                        if (index is < 0 or >= 32) throw new UserFacingException("Invalid local tool-call index.");
                        if (!calls.TryGetValue(index, out var call)) calls[index] = call = new Call();
                        if (piece["function"] is not null and not JsonObject) throw new UserFacingException("Invalid local function delta.");
                        call.Id.Append(String(piece?["id"]));
                        if (piece?["type"] is not null) call.Kind = String(piece["type"]);
                        call.Name.Append(String(piece?["function"]?["name"]));
                        call.Arguments.Append(String(piece?["function"]?["arguments"]));
                    }
            }
            if (choice["finish_reason"] is not null) finish = String(choice["finish_reason"]);
        }
        throw new UserFacingException("Local response stream disconnected before completion. No proposed calls were executed.");
    }

    private sealed class Call
    {
        public StringBuilder Id { get; } = new();
        public StringBuilder Name { get; } = new();
        public StringBuilder Arguments { get; } = new();
        public string Kind { get; set; } = "";
    }
    private static string String(JsonNode? value)
    {
        if (value is null) return "";
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var result)) return result;
        throw new UserFacingException("Invalid local protocol string.");
    }
    private static int Integer(JsonNode? value)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<int>(out var result)) return result;
        throw new UserFacingException("Invalid local protocol integer.");
    }
    private static JsonObject Parse(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw new UserFacingException("Malformed local protocol JSON."); }
    }
    private static async IAsyncEnumerable<string> Events(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var buffer = new char[4096]; var line = new StringBuilder(); var data = new StringBuilder(); var total = 0;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct); if (count == 0) yield break;
            total += count; if (total > 16 * 1024 * 1024) throw new UserFacingException("Local response exceeded the stream limit.");
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] != '\n') { line.Append(buffer[i]); if (line.Length > 2 * 1024 * 1024) throw new UserFacingException("Local stream event exceeded the limit."); continue; }
                var value = line.ToString().TrimEnd('\r'); line.Clear();
                if (value.StartsWith("data:", StringComparison.Ordinal)) data.AppendLine(value[5..].TrimStart(' '));
                if (data.Length > 2 * 1024 * 1024) throw new UserFacingException("Local stream event exceeded the limit.");
                if (value.Length == 0 && data.Length > 0) { var item = data.ToString().Trim(); data.Clear(); yield return item; }
            }
        }
    }
}
