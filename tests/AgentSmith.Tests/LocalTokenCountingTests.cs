using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.LmStudio;
using Xunit;

namespace AgentSmith.Tests;

public sealed class LocalTokenCountingTests
{
    private static ModelRequest Request => new("test", "Keep the goal", [
        new() { ["role"] = "user", ["content"] = "inspect" },
        new() { ["type"] = "reasoning", ["local_chat_reasoning"] = "inspect the file" },
        RuntimeTests.Call("read1", "file_read", "{\"path\":\"hello.txt\"}"),
        new() { ["type"] = "function_call_output", ["call_id"] = "read1", ["output"] = "file contents" }
    ], [new("file_read", "Read a file", new() { ["type"] = "object" })], 8192);

    private sealed class CountingServer(bool strata, bool llama = false, int inheritedCount = 1234) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public JsonObject? CountRequest;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Null(request.Headers.Authorization);
            var path = request.RequestUri!.AbsolutePath; Paths.Add(path);
            string? response = path switch
            {
                "/apply-template" when llama => "{\"prompt\":\"rendered tools and history\"}",
                "/tokenize" when llama => "{\"tokens\":[1,2,3,4]}",
                "/props" => strata ? "{\"build_info\":\"Strata 0.1.52\"}" : "{}",
                "/v1/messages/count_tokens" => "{\"input_tokens\":1234}",
                _ => null
            };
            if (path == "/v1/messages/count_tokens")
            {
                CountRequest = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
                if (CountRequest["thinking"] is null) response = Json.Serialize(new { input_tokens = inheritedCount });
            }
            if (path == "/tokenize")
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                Assert.Equal("rendered tools and history", body["content"]!.ToString());
                Assert.True(body["parse_special"]!.GetValue<bool>());
            }
            return new(response is null ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new StringContent(response ?? "{}", Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task StrataCountingIncludesSystemSchemasReasoningCallsAndResultsWithoutInference()
    {
        using var server = new CountingServer(true); using var http = new HttpClient(server);
        var count = await new LocalChatModelClient(http, new() { Endpoint = "http://localhost:8081/v1" }).CountTokensAsync(Request, default);
        Assert.NotNull(count); Assert.True(count.Exact); Assert.Equal(1234, count.Tokens);
        var body = server.CountRequest!;
        Assert.Equal("Keep the goal", body["system"]!.ToString());
        Assert.Equal("file_read", body["tools"]![0]!["name"]!.ToString());
        Assert.Equal("object", body["tools"]![0]!["input_schema"]!["type"]!.ToString());
        Assert.Equal("inspect the file", body["messages"]![1]!["content"]![0]!["thinking"]!.ToString());
        Assert.Equal("hello.txt", body["messages"]![1]!["content"]![1]!["input"]!["path"]!.ToString());
        Assert.Equal("read1", body["messages"]![2]!["content"]![0]!["tool_use_id"]!.ToString());
        Assert.Equal("file contents", body["messages"]![2]!["content"]![0]!["content"]!.ToString());
        Assert.DoesNotContain("/v1/chat/completions", server.Paths);
    }

    [Fact]
    public async Task LlamaTemplateTokenizerStillCountsExactly()
    {
        using var server = new CountingServer(false, true); using var http = new HttpClient(server);
        var count = await new LocalChatModelClient(http, new() { Endpoint = "http://localhost:8081/v1" }).CountTokensAsync(Request, default);
        Assert.NotNull(count); Assert.True(count.Exact); Assert.Equal(4, count.Tokens);
        Assert.Equal(new[] { "/apply-template", "/tokenize" }, server.Paths);
    }

    [Theory]
    [InlineData(1200, 1234)]
    [InlineData(1300, 1300)]
    public async Task DifferentReasoningDefaultsUseAnHonestServerTokenizedUpperBound(int inherited, int expected)
    {
        using var server = new CountingServer(true, inheritedCount: inherited); using var http = new HttpClient(server);
        var count = await new LocalChatModelClient(http, new() { Endpoint = "http://localhost:8081/v1" }).CountTokensAsync(Request, default);
        Assert.NotNull(count); Assert.Equal(expected, count.Tokens); Assert.False(count.Exact);
        Assert.Contains("upper bound", count.Method);
        Assert.Null(LocalChatModelClient.BuildRequest(Request, 8192)["chat_template_kwargs"]);
        Assert.Equal(2, server.Paths.Count(p => p == "/v1/messages/count_tokens"));
    }

    [Fact]
    public async Task UnknownServerDoesNotClaimAnExactAnthropicCount()
    {
        using var server = new CountingServer(false); using var http = new HttpClient(server);
        Assert.Null(await new LocalChatModelClient(http, new() { Endpoint = "http://localhost:8081/v1" }).CountTokensAsync(Request, default));
        Assert.Null(server.CountRequest);
    }

    [Fact]
    public async Task ParallelResultsAreCountedInTheSameOrderAsChatInference()
    {
        using var server = new CountingServer(true); using var http = new HttpClient(server);
        var history = new JsonObject[] {
            new() { ["role"] = "user", ["content"] = "read both" },
            RuntimeTests.Call("a", "file_read", "{}"), RuntimeTests.Call("b", "file_read", "{}"),
            new() { ["type"] = "function_call_output", ["call_id"] = "b", ["output"] = "second" },
            new() { ["type"] = "function_call_output", ["call_id"] = "a", ["output"] = "first" }
        };
        await new LocalChatModelClient(http, new() { Endpoint = "http://localhost:8081/v1" }).CountTokensAsync(Request with { Input = history }, default);
        var messages = server.CountRequest!["messages"]!;
        Assert.Equal("first", messages[2]!["content"]![0]!["content"]!.ToString());
        Assert.Equal("second", messages[3]!["content"]![0]!["content"]!.ToString());
        Assert.Equal("b", history[3]["call_id"]!.ToString());
    }

    [Fact]
    public async Task ImageAllowanceIsNeverPresentedAsAnExactTextCount()
    {
        using var server = new CountingServer(true); using var http = new HttpClient(server);
        var request = Request with { Input = [new() { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_image", ["image_url"] = "data:image/png;base64,AA==" }) }] };
        var count = await new LocalChatModelClient(http, new() { Endpoint = "http://localhost:8081/v1" }).CountTokensAsync(request, default);
        Assert.NotNull(count); Assert.False(count.Exact); Assert.Equal(1234 + 4096, count.Tokens);
        Assert.DoesNotContain("data:image", server.CountRequest!.ToJsonString());
    }

    private sealed class FailedCounter(bool timeout) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/apply-template") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (request.RequestUri.AbsolutePath == "/props") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"build_info\":\"Strata 0.1.52\"}") });
            if (timeout) throw new TaskCanceledException();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    [Theory]
    [InlineData(false, "HTTP 503")]
    [InlineData(true, "timed out")]
    public async Task BrokenSupportedCounterDoesNotSilentlyBecomeAByteEstimate(bool timeout, string expected)
    {
        using var server = new FailedCounter(timeout); using var http = new HttpClient(server);
        var error = await Assert.ThrowsAsync<UserFacingException>(() => new LocalChatModelClient(http, new() { Endpoint = "http://localhost:8081/v1" }).CountTokensAsync(Request, default));
        Assert.Equal("local_token_count_failed", error.Detail!.Code);
        Assert.Contains(expected, error.Message);
    }

    private sealed class SlowCounter : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/apply-template") return new(HttpStatusCode.NotFound);
            if (request.RequestUri.AbsolutePath == "/props") return new(HttpStatusCode.OK) { Content = new StringContent("{\"build_info\":\"Strata 0.1.52\"}") };
            await Task.Delay(TimeSpan.FromSeconds(6), ct);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"input_tokens\":15000}") };
        }
    }

    [Fact]
    public async Task CountingLongerThanFiveSecondsStillUsesServerTokens()
    {
        using var server = new SlowCounter(); using var http = new HttpClient(server);
        var count = await new LocalChatModelClient(http, new() { Endpoint = "http://localhost:8081/v1" }).CountTokensAsync(Request, default);
        Assert.NotNull(count); Assert.True(count.Exact); Assert.Equal(15000, count.Tokens);
    }
}
