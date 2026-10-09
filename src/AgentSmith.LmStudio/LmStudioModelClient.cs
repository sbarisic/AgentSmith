using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.OpenAI;

namespace AgentSmith.LmStudio;

public sealed record LocalModel(string Id, string Name, bool Loaded, int? ContextTokens, bool ToolUse);
public sealed class LmStudioModelClient(HttpClient http, ModelConnection connection) : IModelClient
{
    public static Uri Endpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "http" || !uri.IsLoopback || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new UserFacingException("LM Studio requires a loopback HTTP origin, for example http://127.0.0.1:1234.");
        return uri;
    }
    public async Task<IReadOnlyList<LocalModel>> ModelsAsync(CancellationToken ct)
    {
        CheckHeaders();
        using var response = await http.GetAsync(new Uri(Endpoint(connection.Endpoint), "api/v1/models"), ct);
        if (!response.IsSuccessStatusCode) throw new UserFacingException($"LM Studio model discovery failed (HTTP {(int)response.StatusCode}). Start the local server.");
        var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        return (data["models"]?.AsArray() ?? throw new UserFacingException("Invalid LM Studio model catalog.")).Where(m => m?["type"]?.ToString() == "llm").Select(m =>
        {
            var loaded = m!["loaded_instances"]?.AsArray();
            return new LocalModel(m["key"]!.ToString(), m["display_name"]?.ToString() ?? m["key"]!.ToString(), loaded?.Count > 0,
                loaded?.FirstOrDefault()?["config"]?["context_length"]?.GetValue<int>(), m["capabilities"]?["trained_for_tool_use"]?.GetValue<bool>() == true);
        }).ToArray();
    }
    private void CheckHeaders()
    {
        if (http.DefaultRequestHeaders.Authorization is not null) throw new UserFacingException("Local transport must not carry account credentials.");
    }
    public static JsonObject BuildRequest(ModelRequest request, int maxOutputTokens)
    {
        if (maxOutputTokens is < 256 or > 32768) throw new UserFacingException("Output budget must be 256..32768 tokens.");
        var body = new JsonObject
        {
            ["model"] = request.Model, ["instructions"] = request.Instructions, ["stream"] = true, ["store"] = false,
            ["max_output_tokens"] = maxOutputTokens,
            ["input"] = new JsonArray(request.Input.Select(i => (JsonNode)i.DeepClone()).ToArray())
        };
        if (request.Tools.Count > 0) body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode)new JsonObject
        { ["type"] = "function", ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone(), ["strict"] = true }).ToArray());
        return body;
    }
    public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var model = (await ModelsAsync(cancellationToken)).SingleOrDefault(m => m.Id == request.Model && m.Loaded)
            ?? throw new UserFacingException("Selected LM Studio model is not loaded. Load it in LM Studio and continue explicitly.");
        if (request.Tools.Count > 0 && !model.ToolUse) throw new UserFacingException("Selected local model does not advertise trained tool use.");
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(Endpoint(connection.Endpoint), "v1/responses"));
        message.Content = new StringContent(BuildRequest(request, connection.MaxOutputTokens).ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new UserFacingException($"LM Studio inference failed (HTTP {(int)response.StatusCode}). Check the local model/server; no fallback or retry was performed.");
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var ev in ChatGptModelClient.ParseStreamAsync(stream, cancellationToken, "LM Studio"))
        {
            if (ev.Type == "completed")
            {
                var usable = ev.Output?.Any(i => i["type"]?.ToString() == "function_call" || i["type"]?.ToString() == "message" && i["content"]?.AsArray().Any(c => c?["type"]?.ToString() == "output_text" && !string.IsNullOrWhiteSpace(c["text"]?.ToString())) == true) == true;
                var tokens = ev.Usage is null ? 0 : JsonNode.Parse(ev.Usage)?["output_tokens"]?.GetValue<int>() ?? 0;
                if (!usable || tokens >= connection.MaxOutputTokens) throw new UserFacingException($"LM Studio produced no usable answer/tool call or exhausted its output budget ({tokens}/{connection.MaxOutputTokens} output tokens). No proposed calls were executed.");
            }
            yield return ev;
        }
    }
}
