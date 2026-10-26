using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmithLite;

internal static class StreamingTests
{
    private static string Event(JsonObject item) => "data: " + item.ToJsonString() + "\n\n";
    private static JsonObject Tool(string name) => new() { ["type"] = "function_call", ["call_id"] = name, ["name"] = "write_file",
        ["arguments"] = Json.Serialize(new { path = name + ".txt", content = "streamed tool result" }), ["status"] = "completed" };
    private static JsonObject Message(string value) => new() { ["type"] = "message", ["role"] = "assistant", ["status"] = "completed",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = value }) };
    private static string Done(int index, JsonObject item) => Event(new JsonObject { ["type"] = "response.output_item.done", ["output_index"] = index, ["item"] = item.DeepClone() });
    private static string Complete(JsonArray? output) => Event(new JsonObject { ["type"] = "response.completed",
        ["response"] = new JsonObject { ["status"] = "completed", ["output"] = output } });
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Reject(string stream)
    {
        try { using var input = new MemoryStream(Encoding.UTF8.GetBytes(stream)); await ModelClient.ReadResponseAsync(input, _ => { }, default); }
        catch (UserFacingException) { return; }
        throw new Exception("Malformed or unfinished response was accepted.");
    }

    internal static async Task StreamedTools(string root)
    {
        foreach (var finalShape in new[] { "empty", "null", "omitted" })
        {
            var name = "streamed-" + finalShape;
            var reasoning = new JsonObject { ["type"] = "reasoning", ["id"] = "reasoning-fixture", ["encrypted_content"] = "opaque-fixture", ["summary"] = new JsonArray() };
            var completion = finalShape == "omitted"
                ? Event(new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["status"] = "completed" } })
                : Complete(finalShape == "empty" ? new JsonArray() : null);
            var first = Done(1, Tool(name)) + Done(0, reasoning) + completion;
            var handler = new FixtureHandler([first, Complete(new JsonArray(Message("Done.")))]);
            using var http = new HttpClient(handler);
            var approvals = 0;
            var tools = new BasicTools(root, (_, _, _) => { approvals++; return Task.FromResult(true); });
            var chat = new Chat(new ModelClient(http, _ => Task.FromResult("fixture-token")), tools, "fixture-model", root, _ => { }, _ => { });
            await chat.SendAsync("Write the fixture.", default);
            Check(File.ReadAllText(Path.Combine(root, name + ".txt")) == "streamed tool result", "Streamed tool was lost.");
            Check(approvals == 1 && handler.Requests.Count == 2, "Tool was replayed or no follow-up occurred.");
            var history = handler.Requests[1]["input"]!.AsArray();
            Check(history[1]!["type"]!.ToString() == "reasoning" && history[2]!["type"]!.ToString() == "function_call", "Output index ordering was lost.");
            Check(history[1]!["encrypted_content"]!.ToString() == "opaque-fixture", "Completed reasoning was lost.");
        }
    }

    internal static async Task SnapshotNotDuplicated(string root)
    {
        var call = Tool("snapshot-once");
        using var http = new HttpClient(new FixtureHandler([Done(0, call) + Complete(new JsonArray(call.DeepClone())), Complete(new JsonArray(Message("Done.")))]));
        var approvals = 0;
        var chat = new Chat(new ModelClient(http, _ => Task.FromResult("fixture")), new BasicTools(root, (_, _, _) => { approvals++; return Task.FromResult(true); }), "fixture", root, _ => { }, _ => { });
        await chat.SendAsync("test", default);
        Check(approvals == 1, "The streamed item and snapshot executed twice.");
        Check(chat.History.Count(i => i["type"]?.ToString() == "function_call") == 1, "Duplicate call in history.");
    }

    internal static async Task RejectIndices()
    {
        await Reject(Done(0, Tool("duplicate")) + Done(0, Tool("duplicate")) + Complete(new JsonArray()));
        await Reject(Done(-1, Tool("negative")) + Complete(new JsonArray()));
        await Reject(Done(256, Tool("oversized")) + Complete(new JsonArray()));
        await Reject(Event(new JsonObject { ["type"] = "response.output_item.done", ["item"] = Tool("missing-index") }) + Complete(new JsonArray()));
    }

    internal static async Task NoEarlyExecution(string root)
    {
        foreach (var ending in new[] { "disconnect", "done-marker", "failed", "incomplete", "bad-completion" })
        {
            var name = "no-execution-" + ending;
            var terminal = ending switch
            {
                "disconnect" => "",
                "done-marker" => "data: [DONE]\n\n",
                "bad-completion" => Event(new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["status"] = "in_progress" } }),
                _ => Event(new JsonObject { ["type"] = "response." + ending })
            };
            using var http = new HttpClient(new FixtureHandler([Done(0, Tool(name)) + terminal]));
            var tools = new BasicTools(root, (_, _, _) => Task.FromResult(true));
            var chat = new Chat(new ModelClient(http, _ => Task.FromResult("fixture")), tools, "fixture", root, _ => { }, _ => { });
            try { await chat.SendAsync("test", default); throw new Exception("Unsuccessful stream was accepted."); }
            catch (UserFacingException) { }
            Check(!File.Exists(Path.Combine(root, name + ".txt")), "Tool executed without successful completion.");
        }
    }

    internal static async Task RejectEmptyCompletion()
    {
        await Reject(Complete(new JsonArray()));
        await Reject(Event(new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["status"] = "completed", ["output"] = "invalid" } }));
        await Reject(Complete(new JsonArray(JsonValue.Create(12))));
        var unfinished = Tool("unfinished"); unfinished["status"] = "in_progress";
        await Reject(Done(0, unfinished) + Complete(new JsonArray()));
        await Reject(Complete(new JsonArray(unfinished.DeepClone())));
    }

    internal static async Task FragmentedStream()
    {
        var stream = (Done(0, Message("Hello ✓")) + Complete(new JsonArray())).Replace("\n", "\r\n", StringComparison.Ordinal);
        using var input = new ChunkedStream(Encoding.UTF8.GetBytes(stream));
        var text = new StringBuilder();
        var result = await ModelClient.ReadResponseAsync(input, s => text.Append(s), default);
        Check(result.Count == 1 && text.ToString() == "Hello ✓", "Fragmented message was lost or duplicated.");
    }

    private sealed class ChunkedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], ct);
    }
}
