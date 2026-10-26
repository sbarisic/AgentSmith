using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.OpenAI;
using AgentSmithLite;
using LiteProgram = AgentSmithLite.Program;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

// Child fixture modes run before allocating a test workspace.
if (args.Length > 0)
{
    switch (args[0])
    {
        case "--echo-args": Console.WriteLine(Json.Serialize(args.Skip(1).ToArray())); return 0;
        case "--large-output": Console.Write(new string('a', 100000)); Console.Error.Write(new string('b', 100000)); return 0;
        case "--sleep": await Task.Delay(30000); return 0;
        case "--mark": await File.WriteAllTextAsync(args[1], "launched"); return 0;
        case "--spawn-child":
            using (var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { Arguments = "--sleep", UseShellExecute = false, CreateNoWindow = true })!)
            { await File.WriteAllTextAsync(args[1], child.Id.ToString()); await Task.Delay(30000); }
            return 0;
    }
}

// Dependency-free acceptance runner. Run the actual self-contained x86 executable.
var root = Path.Combine(Path.GetTempPath(), "AgentSmithLite-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var failures = 0;
var tests = new List<(string Name, Func<Task> Run)>();
var allow = new BasicTools(root, (_, _, _) => Task.FromResult(true));
var deny = new BasicTools(root, (_, _, _) => Task.FromResult(false));
var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
string ProcessArgs(string script, int timeout = 5) => Json.Serialize(new { executable = cmd, arguments = new[] { "/d", "/c", script }, workingDirectory = ".", timeoutSeconds = timeout });
void Test(string name, Func<Task> action) => tests.Add((name, action));
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
void True(bool value, string message) { if (!value) throw new Exception(message); }
JsonObject Parse(string value) => JsonNode.Parse(value)!.AsObject();
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
JsonObject Call(string id, string name, object args) => new() { ["type"] = "function_call", ["call_id"] = id, ["name"] = name, ["arguments"] = Json.Serialize(args) };
JsonObject Message(string text) => new() { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }) };
string Sse(params JsonObject[] output) => "data: " + new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["status"] = "completed", ["output"] = new JsonArray(output.Select(i => (JsonNode)i.DeepClone()).ToArray()) } }.ToJsonString() + "\n\n";
Chat MakeChat(IChatModel model, BasicTools? tools = null) => new(model, tools ?? allow, "fixture-model", root, _ => { }, _ => { });

Test("runtime is .NET 6 x86", () => { Equal(6, Environment.Version.Major); Equal(4, IntPtr.Size); return Task.CompletedTask; });
Test("streamed tool items work with empty, null or omitted final output", () => StreamingTests.StreamedTools(root));
Test("streamed items and final snapshot do not execute twice", () => StreamingTests.SnapshotNotDuplicated(root));
Test("invalid or duplicate streamed output indices are rejected", StreamingTests.RejectIndices);
Test("completed tool items never execute before successful response completion", () => StreamingTests.NoEarlyExecution(root));
Test("empty and malformed completions remain rejected", StreamingTests.RejectEmptyCompletion);
Test("fragmented CRLF stream preserves completed messages", StreamingTests.FragmentedStream);
Test("offline login exchanges pasted callback with matching PKCE and saves identity", () => OfflineLoginTests.Success(root));
Test("offline login rejects malformed and mismatched callbacks before exchange", () => OfflineLoginTests.InvalidCallbacks(root));
Test("offline login waits for explicit input and cancels without changing accounts", () => OfflineLoginTests.Cancellation(root));
Test("offline login preserves credentials when token exchange fails", () => OfflineLoginTests.FailedExchange(root));
Test("CLI options reject unknown, duplicate and missing values", async () =>
{
    await Throws<UserFacingException>(() => Task.FromResult(LiteProgram.Options.Parse(["--unknown"])));
    await Throws<UserFacingException>(() => Task.FromResult(LiteProgram.Options.Parse(["--model"])));
    await Throws<UserFacingException>(() => Task.FromResult(LiteProgram.Options.Parse(["--yes", "--yes"])));
    var parsed = LiteProgram.Options.Parse(["chat", "--model", "test", "--workspace", root, "--yes"]);
    Equal(root, parsed.Workspace); True(parsed.Yes, "--yes missing");
    Equal("auth login offline", LiteProgram.Options.Parse(["auth", "login", "offline"]).Command);
});
Test("file write/read/list and traversal protection", async () =>
{
    Equal("written", Parse(await allow.ExecuteAsync("write_file", Json.Serialize(new { path = "nested/hello.txt", content = "Hello x86 ✓" }), default))["status"]!.ToString());
    Equal("Hello x86 ✓", Parse(await allow.ExecuteAsync("read_file", "{\"path\":\"nested/hello.txt\"}", default))["content"]!.ToString());
    True(Parse(await allow.ExecuteAsync("list_files", "{\"path\":\"nested\"}", default))["entries"]!.AsArray().Count == 1, "Missing file");
    Equal("error", Parse(await allow.ExecuteAsync("write_file", "{\"path\":\"../escape.txt\",\"content\":\"no\"}", default))["status"]!.ToString());
    Equal("error", Parse(await allow.ExecuteAsync("read_file", "{\"path\":\"nested/hello.txt:stream\"}", default))["status"]!.ToString());
});
Test("file tool bounds and schema validation", async () =>
{
    await File.WriteAllTextAsync(Path.Combine(root, "long.txt"), new string('x', 20000));
    var read = Parse(await allow.ExecuteAsync("read_file", "{\"path\":\"long.txt\"}", default));
    Equal(BasicTools.OutputLimit, read["content"]!.ToString().Length); True(read["truncated"]!.GetValue<bool>(), "Not marked truncated");
    Equal("error", Parse(await allow.ExecuteAsync("read_file", "{\"path\":42}", default))["status"]!.ToString());
    Equal("error", Parse(await allow.ExecuteAsync("read_file", "{\"path\":\"long.txt\",\"extra\":1}", default))["status"]!.ToString());
    Equal("error", Parse(await allow.ExecuteAsync("delete_everything", "{}", default))["status"]!.ToString());
});
Test("denied actions do not run", async () =>
{
    Equal("denied", Parse(await deny.ExecuteAsync("write_file", "{\"path\":\"denied.txt\",\"content\":\"no\"}", default))["status"]!.ToString());
    True(!File.Exists(Path.Combine(root, "denied.txt")), "Denied file was written");
    Equal("denied", Parse(await deny.ExecuteAsync("run_process", ProcessArgs("echo no>denied-process.txt"), default))["status"]!.ToString());
    True(!File.Exists(Path.Combine(root, "denied-process.txt")), "Denied command ran");
    Equal("denied", Parse(await deny.ExecuteAsync("start_app", Json.Serialize(new { executable = cmd, arguments = new[] { "/c", "echo no>denied-app.txt" }, workingDirectory = "." }), default))["status"]!.ToString());
});
Test("process exit code, stdout, stderr and literal argument quoting", async () =>
{
    var result = Parse(await allow.ExecuteAsync("run_process", ProcessArgs("echo hello & echo problem 1>&2 & exit /b 7"), default));
    Equal("exited", result["status"]!.ToString()); True(result["exitCode"]!.GetValue<int>() == 7, result.ToJsonString());
    True(result["stdout"]!.ToString().Contains("hello"), "Missing stdout"); True(result["stderr"]!.ToString().Contains("problem"), "Missing stderr");
    var exe = Environment.ProcessPath!;
    var args = Json.Serialize(new { executable = exe, arguments = new[] { "--echo-args", "space value", "quote\"value", "trailing\\", "" }, workingDirectory = ".", timeoutSeconds = 5 });
    var quoted = Parse(await allow.ExecuteAsync("run_process", args, default));
    Equal(Json.Serialize(new[] { "space value", "quote\"value", "trailing\\", "" }), quoted["stdout"]!.ToString().Trim());
});
Test("process output bounded without pipe deadlock", async () =>
{
    var args = Json.Serialize(new { executable = Environment.ProcessPath!, arguments = new[] { "--large-output" }, workingDirectory = ".", timeoutSeconds = 10 });
    var result = Parse(await allow.ExecuteAsync("run_process", args, default));
    Equal("exited", result["status"]!.ToString()); True(result["truncated"]!.GetValue<bool>(), "Output not truncated");
    Equal(BasicTools.OutputLimit, result["stdout"]!.ToString().Length); Equal(BasicTools.OutputLimit, result["stderr"]!.ToString().Length);
});
Test("timeout kills descendant process tree", async () =>
{
    var pidFile = Path.Combine(root, "descendant.pid");
    var args = Json.Serialize(new { executable = Environment.ProcessPath!, arguments = new[] { "--spawn-child", pidFile }, workingDirectory = ".", timeoutSeconds = 2 });
    var result = Parse(await allow.ExecuteAsync("run_process", args, default));
    Equal("timed_out", result["status"]!.ToString());
    var pid = int.Parse(await File.ReadAllTextAsync(pidFile));
    try { using var process = Process.GetProcessById(pid); True(process.HasExited, "Descendant survived timeout"); } catch (ArgumentException) { }
});
Test("cancellation kills command tree", async () =>
{
    using var cancel = new CancellationTokenSource(300);
    await Throws<OperationCanceledException>(() => allow.ExecuteAsync("run_process", Json.Serialize(new { executable = Environment.ProcessPath!, arguments = new[] { "--sleep" }, workingDirectory = ".", timeoutSeconds = 30 }), cancel.Token));
});
Test("start_app launches a separate process", async () =>
{
    var marker = Path.Combine(root, "launched.txt");
    var result = Parse(await allow.ExecuteAsync("start_app", Json.Serialize(new { executable = Environment.ProcessPath!, arguments = new[] { "--mark", marker }, workingDirectory = "." }), default));
    Equal("launch_requested", result["status"]!.ToString());
    for (var i = 0; i < 100 && !File.Exists(marker); i++) await Task.Delay(30);
    True(File.Exists(marker), "App did not execute");
});
Test("DPAPI credentials round trip and separate Lite branding", () =>
{
    var vault = new CredentialVault(Path.Combine(root, "auth"), new WindowsSecretProtector());
    using var lease = vault.Acquire();
    var credentials = new Credentials { ActiveId = "fixture" }; credentials.Accounts.Add(new Account { Id = "fixture", AccessToken = "fixture-secret" });
    vault.Write(credentials); Equal("fixture-secret", vault.Read().Accounts.Single().AccessToken);
    True(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "auth", "credentials.dpapi"))).Contains("fixture-secret"), "Token saved as plaintext");
    var attempt = new OAuthAttempt("state", "nonce", "verifier", "http://127.0.0.1:1234/auth/callback", null);
    True(OAuthProtocol.AuthorizeUrl(attempt, "host", null, "AgentSmithLite").Contains("agent_name_hint=AgentSmithLite"), "Incorrect Lite brand");
    True(OAuthProtocol.AuthorizeUrl(attempt, "host", null).Contains("agent_name_hint=AgentSmith&") || OAuthProtocol.AuthorizeUrl(attempt, "host", null).EndsWith("agent_name_hint=AgentSmith"), "Full app default changed");
    return Task.CompletedTask;
});
Test("OAuth mismatched state is rejected", async () =>
{
    var attempt = new OAuthAttempt("state", "nonce", "verifier", "http://127.0.0.1:1234/auth/callback", "client");
    await Throws<UserFacingException>(() => Task.FromResult(OAuthProtocol.ValidateCallback(new Uri(attempt.RedirectUri + "?state=wrong&code=x"), attempt)));
});
Test("x86 identity validation accepts signed JWT and rejects wrong audience/expiry", async () =>
{
    using var rsa = RSA.Create(2048);
    var key = new RsaSecurityKey(rsa) { KeyId = "fixture-key" };
    var config = new OpenIdConnectConfiguration { Issuer = OAuthProtocol.Issuer }; config.SigningKeys.Add(key);
    var validator = new IdentityValidator(new StaticConfigurationManager<OpenIdConnectConfiguration>(config));
    string Token(string audience, bool expired) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = OAuthProtocol.Issuer, Audience = audience,
        Claims = new Dictionary<string, object> { ["sub"] = "fixture-subject", ["nonce"] = "fixture-nonce", ["email"] = "test@example.invalid" },
        IssuedAt = DateTime.UtcNow.AddMinutes(-10), NotBefore = DateTime.UtcNow.AddMinutes(-10),
        Expires = expired ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(5),
        SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
    });
    Equal("fixture-subject", (await validator.ValidateAsync(Token("fixture-client", false), "fixture-client", "fixture-nonce", null, default)).Subject);
    await Throws<UserFacingException>(() => validator.ValidateAsync(Token("wrong-client", false), "fixture-client", "fixture-nonce", null, default));
    await Throws<UserFacingException>(() => validator.ValidateAsync(Token("fixture-client", true), "fixture-client", "fixture-nonce", null, default));
});
Test("HTTP streaming chat -> real tool -> follow-up response", async () =>
{
    var handler = new FixtureHandler([
        Sse(Call("write-1", "write_file", new { path = "agent.txt", content = "from chat" })),
        Sse(Call("run-1", "run_process", new { executable = cmd, arguments = new[] { "/d", "/c", "type agent.txt" }, workingDirectory = ".", timeoutSeconds = 5 })),
        Sse(Message("Completed."))
    ]);
    using var http = new HttpClient(handler);
    var output = new StringBuilder();
    var chat = new Chat(new ModelClient(http, _ => Task.FromResult("fixture-token")), allow, "fixture-model", root, s => output.Append(s), _ => { });
    await chat.SendAsync("Write and read a fixture.", default);
    Equal("from chat", await File.ReadAllTextAsync(Path.Combine(root, "agent.txt")));
    Equal("Completed.", output.ToString()); Equal(3, handler.Requests.Count);
    var body = handler.Requests.Last(); Equal(false, body["store"]!.GetValue<bool>()); Equal(true, body["stream"]!.GetValue<bool>());
    Equal(5, body["tools"]!.AsArray().Count);
    True(body["input"]!.AsArray().OfType<JsonObject>().Any(i => i["call_id"]?.ToString() == "run-1" && i["type"]?.ToString() == "function_call_output" && i["output"]!.ToString().Contains("from chat")), "Tool result absent from follow-up: " + body["input"]!.ToJsonString());
});
Test("incomplete stream cannot execute proposed tools", async () =>
{
    var item = new JsonObject { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = Call("incomplete", "write_file", new { path = "incomplete.txt", content = "no" }) };
    using var http = new HttpClient(new FixtureHandler(["data: " + item.ToJsonString() + "\n\n"]));
    await Throws<UserFacingException>(() => MakeChat(new ModelClient(http, _ => Task.FromResult("fixture"))).SendAsync("test", default));
    True(!File.Exists(Path.Combine(root, "incomplete.txt")), "Executed incomplete output");
});
Test("HTTP failures are not automatically retried", async () =>
{
    var handler = new FixtureHandler(["{}"]) { Status = HttpStatusCode.ServiceUnavailable };
    using var http = new HttpClient(handler);
    await Throws<UserFacingException>(() => MakeChat(new ModelClient(http, _ => Task.FromResult("fixture"))).SendAsync("test", default));
    Equal(1, handler.Requests.Count);
});
Test("duplicate call IDs reject whole batch before effects", async () =>
{
    var model = new ScriptModel([ [Call("same", "write_file", new { path = "duplicate.txt", content = "no" }), Call("same", "read_file", new { path = "duplicate.txt" })] ]);
    await Throws<UserFacingException>(() => MakeChat(model).SendAsync("test", default));
    True(!File.Exists(Path.Combine(root, "duplicate.txt")), "First duplicate executed");
});
Test("interrupted batch is closed with results without replay", async () =>
{
    using var cancel = new CancellationTokenSource();
    var tools = new BasicTools(root, (_, _, _) => { cancel.Cancel(); return Task.FromResult(true); });
    var model = new ScriptModel([[Call("cancel-1", "write_file", new { path = "cancel.txt", content = "no" }), Call("cancel-2", "write_file", new { path = "cancel2.txt", content = "no" })]]);
    var chat = MakeChat(model, tools);
    await Throws<OperationCanceledException>(() => chat.SendAsync("test", cancel.Token));
    Equal(2, chat.History.Count(i => i["type"]?.ToString() == "function_call_output"));
    True(!File.Exists(Path.Combine(root, "cancel.txt")), "Cancelled action executed");
    True(!File.Exists(Path.Combine(root, "cancel2.txt")), "Pending action executed");
});
Test("history and tool loop limits stop further work", async () =>
{
    var loops = Enumerable.Range(0, 16).Select(i => new[] { Call("loop-" + i, "read_file", new { path = "long.txt" }) }).ToArray();
    var chat = MakeChat(new ScriptModel(loops));
    await Throws<UserFacingException>(() => chat.SendAsync("test", default));
    Equal(16, chat.History.Count(i => i["type"]?.ToString() == "function_call_output"));
    chat.Clear(); Equal(0, chat.History.Count);
    chat.History.Add(Message(new string('x', Chat.MaxHistoryCharacters + 1)));
    await Throws<UserFacingException>(() => chat.SendAsync("test", default));
});

try
{
    foreach (var (name, run) in tests)
    {
        try { await run(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex); }
    }
    Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed | .NET {Environment.Version} | {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
    return failures == 0 ? 0 : 1;
}
finally
{
    // Only remove this runner's freshly created, GUID-named fixture directory.
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
}

internal sealed class FixtureHandler(string[] responses) : HttpMessageHandler
{
    internal readonly List<JsonObject> Requests = [];
    internal HttpStatusCode Status = HttpStatusCode.OK;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Headers.Authorization?.Parameter is null) throw new Exception("Missing auth header");
        Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
        return new HttpResponseMessage(Status) { Content = new StringContent(responses[Requests.Count - 1], Encoding.UTF8, "text/event-stream") };
    }
}
internal sealed class ScriptModel(JsonObject[][] responses) : IChatModel
{
    private int index;
    public Task<IReadOnlyList<JsonObject>> RespondAsync(string model, string instructions, IReadOnlyList<JsonObject> history, Action<string> text, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<JsonObject>>(responses[index++]);
}
