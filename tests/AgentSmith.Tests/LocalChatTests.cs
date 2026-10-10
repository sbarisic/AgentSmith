using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.LmStudio;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using Xunit;

namespace AgentSmith.Tests;

public sealed class LocalChatTests
{
    private static string Chunk(JsonObject delta, string? finish = null) => "data: " + Json.Serialize(new { choices = new[] { new { index = 0, delta, finish_reason = finish } } }) + "\n\n";
    private static JsonObject Call(int index, string? id = null, string? name = null, string? arguments = null) => new()
    {
        ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = index, ["id"] = id, ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments } })
    };
    private static string ToolStream => Chunk(new() { ["reasoning_content"] = "private reasoning" }) +
        Chunk(Call(0, "first", "workspace_", "{\"path\":")) + Chunk(Call(1, "second", "workspace_list", "{\"path\":\".\"}")) +
        Chunk(Call(0, name: "list", arguments: "\".\"}")) + Chunk(new(), "tool_calls") +
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":123,\"completion_tokens\":45,\"total_tokens\":168}}\n\ndata: [DONE]\n\n";
    private static async Task<List<ModelEvent>> Read(string body)
    {
        using var stream = new FragmentedStream(Encoding.UTF8.GetBytes(body));
        var events = new List<ModelEvent>(); await foreach (var ev in LocalChatModelClient.ParseStreamAsync(stream, default)) events.Add(ev); return events;
    }
    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
    }
    private sealed class Server(string stream) : HttpMessageHandler
    {
        public string? Body; public List<string> Paths { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Null(request.Headers.Authorization); Paths.Add(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"bonsai2\",\"meta\":{\"n_ctx\":32768}}]}") };
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
        }
    }
    [Theory]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://127.0.0.1:8080/v1")]
    [InlineData("http://127.0.0.1:8080/v1/")]
    public void BaseUrlNormalizesWithoutDuplicatingV1(string input) => Assert.Equal("http://127.0.0.1:8080/v1/", LocalChatModelClient.Endpoint(input).AbsoluteUri);
    [Theory]
    [InlineData("http://example.com/v1")]
    [InlineData("http://local:secret@localhost/v1")]
    [InlineData("http://localhost/v1?key=secret")]
    [InlineData("http://localhost/v1/chat/completions")]
    public void EndpointsRejectRemoteCredentialsAndUnexpectedPaths(string input) => Assert.Throws<UserFacingException>(() => LocalChatModelClient.Endpoint(input));
    [Fact]
    public async Task FragmentedParallelCallsAndReasoningRoundTripWithOriginalIds()
    {
        var complete = Assert.Single(await Read(ToolStream)); Assert.Equal("completed", complete.Type);
        Assert.Equal(123, JsonNode.Parse(complete.Usage!)!["input_tokens"]!.GetValue<int>());
        var input = complete.Output!.Concat(new JsonObject[] { new() { ["type"] = "function_call_output", ["call_id"] = "first", ["output"] = "one" }, new() { ["type"] = "function_call_output", ["call_id"] = "second", ["output"] = "two" } }).ToArray();
        var body = LocalChatModelClient.BuildRequest(new("bonsai2", "instructions", input, [new("workspace_list", "list", new() { ["type"] = "object" })]), 8192);
        var messages = body["messages"]!.AsArray(); Assert.Equal(4, messages.Count);
        Assert.Equal("private reasoning", messages[1]!["reasoning_content"]!.ToString());
        Assert.Equal("workspace_list", messages[1]!["tool_calls"]![0]!["function"]!["name"]!.ToString());
        Assert.Equal("first", messages[2]!["tool_call_id"]!.ToString()); Assert.Equal("second", messages[3]!["tool_call_id"]!.ToString());
        Assert.NotNull(body["tools"]![0]!["function"]); Assert.Null(body["input"]); Assert.Null(body["include"]); Assert.Equal(8192, body["max_tokens"]!.GetValue<int>());
    }
    [Fact]
    public async Task InterruptedOrMalformedCallsNeverDispatchThroughRuntime()
    {
        foreach (var response in new[] { ToolStream.Replace("data: [DONE]\n\n", ""), Chunk(Call(0, "bad", "workspace_list", "{")) + Chunk(new(), "tool_calls") + "data: [DONE]\n\n" })
        {
            using var directory = new TestDirectory(); using var server = new Server(response); using var http = new HttpClient(server);
            var store = new SessionStore(directory.Path); var tools = new CountingTools();
            var session = new Session { Workspace = directory.Path, Model = "bonsai2", Connection = new() { Provider = ModelProvider.Local, Endpoint = "http://localhost:8080/v1" } };
            var run = await new AgentRunner(store, new LocalChatModelClient(http, session.Connection), tools).RunAsync(session, "read", null, _ => { }, default);
            Assert.Equal(RunState.Failed, run.State); Assert.Empty(tools.Calls); Assert.Empty(run.Calls);
        }
    }
    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    [InlineData("unexpected")]
    public async Task UnsuccessfulFinishCannotComplete(string finish) => await Assert.ThrowsAsync<UserFacingException>(() => Read(Chunk(new() { ["content"] = "partial" }, finish) + "data: [DONE]\n\n"));
    [Fact]
    public async Task ReasoningOnlyAndDuplicateCallIdsCannotComplete()
    {
        await Assert.ThrowsAsync<UserFacingException>(() => Read(Chunk(new() { ["reasoning_content"] = "thinking" }, "stop") + "data: [DONE]\n\n"));
        await Assert.ThrowsAsync<UserFacingException>(() => Read(ToolStream.Replace("second", "first")));
    }
    [Fact]
    public async Task TextUsageAndToolFreeCompactionRequestsUseChatProtocol()
    {
        using var server = new Server(Chunk(new() { ["content"] = "Résumé ✓" }, "stop") + "data: [DONE]\n\n"); using var http = new HttpClient(server);
        var client = new LocalChatModelClient(http, new() { Endpoint = "http://localhost:8080/v1" });
        Assert.Equal(32768, Assert.Single(await client.ModelsAsync(default)).ContextTokens);
        var events = new List<ModelEvent>(); await foreach (var ev in client.StreamAsync(new("bonsai2", "Summarize", [new() { ["role"] = "user", ["content"] = "history" }], []), default)) events.Add(ev);
        Assert.Equal("Résumé ✓", events[0].Text); Assert.Equal("completed", events[1].Type);
        Assert.Equal(new[] { "/v1/models", "/props", "/v1/chat/completions" }, server.Paths); Assert.Null(JsonNode.Parse(server.Body!)!["tools"]);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "must-not-send");
        await Assert.ThrowsAsync<UserFacingException>(() => client.ModelsAsync(default));
        await Assert.ThrowsAsync<UserFacingException>(async () => { await foreach (var _ in client.StreamAsync(new("bonsai2", "", [], []), default)) { } });
        Assert.Equal(3, server.Paths.Count);
    }
    [Fact]
    public async Task CancellationAndOversizedEventsDoNotComplete()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ToolStream)); using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await foreach (var _ in LocalChatModelClient.ParseStreamAsync(stream, cts.Token)) { } });
        await Assert.ThrowsAsync<UserFacingException>(() => Read("data: " + new string('x', 2 * 1024 * 1024 + 1)));
    }
    [Fact]
    public void ProviderIdentityRoundTripsWithoutChangingOldValues()
    {
        Assert.Equal(0, (int)ModelProvider.ChatGpt); Assert.Equal(1, (int)ModelProvider.LmStudio);
        using var directory = new TestDirectory(); var store = new SessionStore(directory.Path);
        var session = new Session { Connection = new() { Provider = ModelProvider.Local, Endpoint = "http://localhost:8080/v1" } };
        store.Save(session, new("test", session.Id, null, new { }));
        Assert.Equal(ModelProvider.Local, store.Get(session.Id)!.Connection.Provider);
        Assert.Equal("http://localhost:8080/v1", store.Get(session.Id)!.Connection.Endpoint);
    }
}
