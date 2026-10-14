using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.OpenAI;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

public sealed class NativeWebSearchTests
{
    private static JsonObject Message() => JsonNode.Parse("""
        {"type":"message","role":"assistant","content":[{"type":"output_text","text":"An observed fact.","annotations":[{"type":"url_citation","start_index":0,"end_index":17,"url":"https://example.com/source","title":"Source"}]}]}
        """)!.AsObject();
    private static JsonObject Search() => JsonNode.Parse("""
        {"type":"web_search_call","id":"ws_1","status":"completed","action":{"type":"search","queries":["public question"],"sources":[{"type":"url","url":"https://example.com/source"}]}}
        """)!.AsObject();
    private static string Event(JsonObject item) => "data: " + item.ToJsonString() + "\n\n";

    [Fact]
    public void NativeToolIsTopLevelWithoutDuplicateFunctionAndBasicRemainsExplicit()
    {
        using var registry = new ToolRegistry();
        var request = new ModelRequest("model", "instructions", [Search(), Message()], registry.Definitions, NativeWebSearch: true);
        var body = ChatGptModelClient.BuildRequest(request);
        Assert.Single(body["tools"]!.AsArray(), t => t?["type"]?.ToString() == "web_search");
        var functions = body["tools"]![0]!["tools"]!.AsArray();
        Assert.DoesNotContain(functions, t => t?["name"]?.ToString() == "web_search");
        Assert.Contains(functions, t => t?["name"]?.ToString() == "web_read");
        Assert.Contains(body["include"]!.AsArray(), i => i?.ToString() == "web_search_call.action.sources");
        Assert.Equal(request.Input[0].ToJsonString(), body["input"]![0]!.ToJsonString());
        var basic = ChatGptModelClient.BuildRequest(request with { NativeWebSearch = false });
        Assert.DoesNotContain(basic["tools"]!.AsArray(), t => t?["type"]?.ToString() == "web_search");
        Assert.Contains(basic["tools"]![0]!["tools"]!.AsArray(), t => t?["name"]?.ToString() == "web_search");
        Assert.Empty(ChatGptModelClient.BuildRequest(new("model", "Compact history", [], []))["tools"]!.AsArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeStreamEmitsCitationsOnceAndPreservesHostedItems(bool doneEvents)
    {
        var sse = Event(new() { ["type"] = "response.web_search_call.searching" })
            + Event(new() { ["type"] = "response.output_text.delta", ["delta"] = "An observed fact." });
        if (doneEvents)
            sse += Event(new() { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = Search() })
                + Event(new() { ["type"] = "response.output_item.done", ["output_index"] = 1, ["item"] = Message() });
        sse += Event(new() { ["type"] = "response.completed", ["response"] = new JsonObject { ["status"] = "completed", ["output"] = new JsonArray(Search(), Message()) } });
        using var stream = new FragmentedStream(Encoding.UTF8.GetBytes(sse));
        var events = new List<ModelEvent>();
        await foreach (var item in ChatGptModelClient.ParseStreamAsync(stream, default, nativeWebSearch: true)) events.Add(item);
        Assert.Single(events, e => e.Type == "web-search");
        Assert.Equal("An observed fact. [Source](https://example.com/source)", Assert.Single(events, e => e.Type == "text").Text);
        Assert.Equal(Search().ToJsonString(), events.Last().Output![0].ToJsonString());
        Assert.Equal(Message().ToJsonString(), events.Last().Output![1].ToJsonString());
    }

    [Fact]
    public async Task NativeStreamEmptyEnvelopeDoesNotRepeatMessageOrDispatchSearch()
    {
        var sse = Event(new() { ["type"] = "response.output_item.done", ["output_index"] = 3, ["item"] = Message() })
            + Event(new() { ["type"] = "response.completed", ["response"] = new JsonObject { ["status"] = "completed", ["output"] = new JsonArray() } });
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse)); var events = new List<ModelEvent>();
        await foreach (var item in ChatGptModelClient.ParseStreamAsync(stream, default, nativeWebSearch: true)) events.Add(item);
        Assert.Single(events, e => e.Type == "text"); Assert.Single(events.Last().Output!);
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path);
        var session = new Session { Workspace = dir.Path };
        var model = new ScriptedModel(new ModelEvent[] { new("web-search", "Searching web…"), new("completed", Output: [Search(), Message()]) });
        var tools = new CountingTools(); var runtimeEvents = new List<AgentEvent>();
        var run = await new AgentRunner(store, model, tools).RunAsync(session, "Find a public source", null, runtimeEvents.Add, default);
        Assert.Equal(RunState.Completed, run.State); Assert.Empty(tools.Calls);
        Assert.Contains(runtimeEvents, e => e.Type == "web.search.progress");
        Assert.Contains(store.Get(session.Id)!.History, i => i["type"]?.ToString() == "web_search_call");
        Assert.Contains(ContextBuilder.Project(session, null), i => i["type"]?.ToString() == "web_search_call");
    }

    [Fact]
    public void CitationProjectionIsSafeBoundedAndDoesNotChangeHistory()
    {
        var item = Message(); var part = item["content"]![0]!;
        part["text"] = "Fact.\uE200cite\uE202turn0search0\uE201";
        var citations = part["annotations"]!.AsArray();
        citations[0]!["end_index"] = 10; // A malformed index inside an opaque marker.
        citations.Add(new JsonObject { ["type"] = "url_citation", ["url"] = "javascript:alert(1)", ["title"] = "unsafe" });
        citations.Add(new JsonObject { ["type"] = "url_citation", ["url"] = "https://user:password@example.com/" });
        citations.Add(new JsonObject { ["type"] = "url_citation", ["url"] = "https://example.com/?access_token=private-secret" });
        var original = item.ToJsonString(); var display = WebCitations.Message(item);
        Assert.Equal("Fact. [Source](https://example.com/source)", display);
        Assert.Equal(original, item.ToJsonString());
        citations[0]!["end_index"] = -1; citations[0]!["url"] = "https://example.com/a(b)";
        Assert.Contains("https://example.com/a%28b%29", WebCitations.Message(item));
        Assert.DoesNotContain("turn0search0", WebCitations.Message(item));
    }

    [Theory]
    [InlineData(ModelProvider.ChatGpt, "native", true)]
    [InlineData(ModelProvider.ChatGpt, "basic", false)]
    [InlineData(ModelProvider.ChatGpt, "off", false)]
    [InlineData(ModelProvider.LmStudio, "native", false)]
    [InlineData(ModelProvider.Local, "native", false)]
    public void ContextChoosesHostedSearchOnlyForEligibleNormalRequests(ModelProvider provider, string mode, bool hosted)
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); using var tools = new ToolRegistry();
        var session = new Session { Workspace = dir.Path, WebSearch = mode, Connection = new() { Provider = provider } };
        foreach (var workflow in Enum.GetValues<WorkflowMode>())
        {
            session.Workflow = workflow;
            var request = new ContextBuilder(store, new ScriptedModel(), tools).Build(session).Request;
            Assert.Equal(hosted, request.NativeWebSearch);
            Assert.Equal(mode != "off", request.Tools.Any(t => t.Name == "web_search"));
        }
    }

    [Fact]
    public void CitationCountAndTitleLengthAreBoundedAndRefusalsStayVisible()
    {
        var item = Message(); var citations = item["content"]![0]!["annotations"]!.AsArray(); citations.Clear();
        for (var i = 0; i < 100; i++)
            citations.Add(new JsonObject { ["type"] = "url_citation", ["url"] = "https://example.com/" + i, ["title"] = new string('x', 300) });
        var display = WebCitations.Message(item);
        Assert.Equal(64, display.Split("https://example.com/").Length - 1);
        Assert.DoesNotContain(new string('x', 121), display);
        Assert.Equal("Cannot comply.", WebCitations.Message(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "refusal", ["refusal"] = "Cannot comply." }) }));
    }

    [Theory]
    [InlineData("native")]
    [InlineData("off")]
    public async Task DisabledLocalSearchCannotBypassPrepare(string policy)
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); using var tools = new ToolRegistry();
        var session = new Session { Workspace = dir.Path, WebSearch = policy, Connection = new() { Provider = ModelProvider.ChatGpt } };
        var context = new ToolExecutionContext(session, new(), store, default);
        var call = new ToolCall { Name = "web_search", Arguments = "{\"query\":\"public topic\"}" };
        Assert.Throws<UserFacingException>(() => tools.Prepare(context, call));
        await Assert.ThrowsAsync<UserFacingException>(() => tools.ExecuteAsync(context, call));
    }

    private sealed class Rejected : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("secret-provider-body") });
        }
    }
    [Fact]
    public async Task UnavailableNativeSearchGivesExplicitFallbackWithoutRetryOrLeakingBody()
    {
        using var handler = new Rejected(); using var http = new HttpClient(handler);
        var client = new ChatGptModelClient(http, _ => Task.FromResult("private-token"));
        var error = await Assert.ThrowsAsync<UserFacingException>(async () =>
        {
            await foreach (var unused in client.StreamAsync(new("model", "instructions", [], [], NativeWebSearch: true), default)) { }
        });
        Assert.Contains("/web-search basic", error.Message); Assert.DoesNotContain("secret-provider-body", error.Message);
        Assert.Equal(1, handler.Requests);
    }
}
