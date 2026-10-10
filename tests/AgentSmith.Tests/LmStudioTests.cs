using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.LmStudio;
using AgentSmith.Storage;
using Xunit;

namespace AgentSmith.Tests;

public sealed class LmStudioTests
{
    private sealed class Server(string output, bool loaded = true) : HttpMessageHandler
    {
        public int Posts;
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Null(request.Headers.Authorization); Assert.Equal("127.0.0.1", request.RequestUri!.Host);
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = new StringContent(Json.Serialize(new { models = new[] { new { type = "llm", key = "bonsai", display_name = "Bonsai", capabilities = new { trained_for_tool_use = true }, loaded_instances = loaded ? new[] { new { config = new { context_length = 262144 } } } : [] } } })) };
            Posts++; Body = await request.Content!.ReadAsStringAsync(ct); return new(HttpStatusCode.OK) { Content = new StringContent(output, Encoding.UTF8, "text/event-stream") };
        }
    }
    private static ModelRequest Request(params ToolDefinition[] tools) => new("bonsai", "test", [], tools);
    private static string Completed(JsonObject item, int tokens = 10) => "data: " + Json.Serialize(new { type = "response.completed", response = new { status = "completed", output = new[] { item }, usage = new { output_tokens = tokens } } }) + "\n\n";
    [Fact]
    public async Task FlatToolsStreamAndStatelessHistoryDoNotUseChatGptEnvelope()
    {
        using var server = new Server(Completed(RuntimeTests.Call("c", "read", "{}"))); using var http = new HttpClient(server);
        var client = new LmStudioModelClient(http, new() { Provider = ModelProvider.LmStudio });
        var tool = new ToolDefinition("read", "Read", new() { ["type"] = "object" });
        var events = new List<ModelEvent>(); await foreach (var e in client.StreamAsync(Request(tool), default)) events.Add(e);
        Assert.Equal("completed", Assert.Single(events).Type);
        var body = JsonNode.Parse(server.Body!)!; Assert.Null(body["include"]); Assert.False(body["store"]!.GetValue<bool>()); Assert.Equal("function", body["tools"]![0]!["type"]!.ToString()); Assert.Equal(8192, body["max_output_tokens"]!.GetValue<int>());
        Assert.Null(LmStudioModelClient.BuildRequest(Request(), 8192)["tools"]);
    }
    [Theory]
    [InlineData("reasoning", 10)] [InlineData("message", 8192)]
    public async Task ReasoningOnlyAndExhaustedResponsesNeverComplete(string type, int tokens)
    {
        var item = type == "reasoning" ? new JsonObject { ["type"] = "reasoning" } : RuntimeTests.Message();
        using var server = new Server(Completed(item, tokens)); using var http = new HttpClient(server); var client = new LmStudioModelClient(http, new());
        await Assert.ThrowsAsync<UserFacingException>(async () => { await foreach (var e in client.StreamAsync(Request(), default)) Assert.NotEqual("completed", e.Type); });
    }
    [Fact]
    public async Task UnloadedModelAndAccidentalCredentialHeadersFailBeforeInference()
    {
        using var server = new Server("", false); using var http = new HttpClient(server); var client = new LmStudioModelClient(http, new());
        await Assert.ThrowsAsync<UserFacingException>(async () => { await foreach (var _ in client.StreamAsync(Request(), default)) { } }); Assert.Equal(0, server.Posts);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "must-not-send");
        await Assert.ThrowsAsync<UserFacingException>(() => client.ModelsAsync(default));
    }
    [Theory]
    [InlineData("http://example.com:1234")]
    [InlineData("http://user:password@localhost:1234")]
    [InlineData("http://127.0.0.1:1234/v1")]
    public void ConnectionIsLoopbackOriginOnly(string url) => Assert.Throws<UserFacingException>(() => LmStudioModelClient.Endpoint(url));
    [Fact]
    public void V4MigrationPreservesConversationAndCreatesBackup()
    {
        using var dir = new TestDirectory(); var store = new SessionStore(dir.Path); var s = new Session { Model = "existing", AccountId = "account", Mode = PermissionMode.Edit }; store.Save(s, new("created", s.Id, null, new { }));
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(dir.Path, "sessions.db")))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DROP TABLE workers; DROP TABLE worker_profiles; DROP TABLE workflow_reports; DROP TABLE workflow_revisions; DROP TABLE steering; UPDATE schema_version SET version=4; UPDATE conversations SET snapshot=json_remove(snapshot,'$.connection','$.enabledWorkers');"; cmd.ExecuteNonQuery(); }
        var migrated = new SessionStore(dir.Path); var saved = migrated.Get(s.Id)!;
        Assert.Equal(ModelProvider.ChatGpt, saved.Connection.Provider); Assert.Equal("account", saved.AccountId); Assert.Equal(PermissionMode.Edit, saved.Mode); Assert.Empty(saved.EnabledWorkers); Assert.Empty(migrated.Workers(s.Id)); Assert.NotEmpty(Directory.GetFiles(dir.Path, "sessions-v4-*.backup.db"));
    }
}
