using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Xunit;
using static AgentSmith.Tests.RuntimeTests;

namespace AgentSmith.Tests;

public sealed class AudioTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Requests++; return send(request, ct); }
    }
    private sealed class Fixture : IDisposable
    {
        public TestDirectory Directory { get; } = new();
        public SessionStore Store { get; }
        public Session Session { get; }
        public ToolRegistry Tools { get; }
        public Fixture(Handler handler, string? key = "test-audio-key")
        {
            Store = new(Path.Combine(Directory.Path, "data"));
            Session = new() { Workspace = Directory.Path, Runs = [new()] };
            Store.Save(Session, new("test", Session.Id, null, new { }));
            Tools = new(Store, audio: new AudioTool(handler, name => name == "OPENAI_API_KEY" ? key : null));
            File.WriteAllBytes(Path.Combine(Directory.Path, "tone.wav"), Wav());
        }
        public ToolExecutionContext Context(CancellationToken ct = default) => new(Session, Session.Runs.Last(), Store, ct);
        public ToolCall Call(string path = "tone.wav", string question = "Describe this sound.", int start = 0, int duration = 0)
        {
            var call = new ToolCall { Id = Guid.NewGuid().ToString("N"), Name = "audio_listen",
                Arguments = Json.Serialize(new { path, question, startSeconds = start, durationSeconds = duration }) };
            call.Prepared = Tools.Prepare(Context(), call); return call;
        }
        public void Dispose() { Tools.Dispose(); Directory.Dispose(); }
    }
    private static HttpResponseMessage Response(string text = "A steady tone.") => new(HttpStatusCode.OK) {
        Content = new StringContent(Json.Serialize(new { model = "gpt-audio-1.5", usage = new { total_tokens = 50 },
            choices = new[] { new { finish_reason = "stop", message = new { content = text } } } }), Encoding.UTF8, "application/json") };
    private static byte[] Wav(int seconds = 2, int bits = 16)
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        var size = seconds * 8000 * bits / 8;
        w.Write("RIFF"u8); w.Write(36 + size); w.Write("WAVEfmt "u8); w.Write(16); w.Write((ushort)1);
        w.Write((ushort)1); w.Write(8000); w.Write(8000 * bits / 8); w.Write((ushort)(bits / 8)); w.Write((ushort)bits);
        w.Write("data"u8); w.Write(size);
        for (var i = 0; i < seconds * 8000; i++)
        {
            if (bits == 8) w.Write((byte)192);
            else if (bits == 16) w.Write((short)16384);
            else if (bits == 24) { w.Write((byte)0); w.Write((short)16384); }
            else w.Write(1073741824);
        }
        return ms.ToArray();
    }
    [Theory]
    [InlineData(8)] [InlineData(16)] [InlineData(24)] [InlineData(32)]
    public void WaveSlicesAtSampleBoundariesAndNormalizesPcm(int bits)
    {
        var clip = AudioClip.Prepare(Wav(bits: bits), ".WAV", 1, 1);
        Assert.Equal(1, clip.StartSeconds); Assert.Equal(1d, clip.DurationSeconds); Assert.Equal(2d, clip.SourceDurationSeconds);
        Assert.True(clip.Partial); Assert.Equal(16044, clip.Bytes.Length);
        Assert.Equal(16, BitConverter.ToInt16(clip.Bytes, 34));
        Assert.Equal(16384, BitConverter.ToInt16(clip.Bytes, 44));
    }
    [Fact]
    public void WaveDefaultIsBoundedAndMalformedChunksAndRangesFail()
    {
        var clip = AudioClip.Prepare(Wav(121), ".wav", 0, 0);
        Assert.Equal(120d, clip.DurationSeconds); Assert.True(clip.Partial);
        Assert.Throws<UserFacingException>(() => AudioClip.Prepare(Wav(), ".wav", 2, 1));
        Assert.Throws<UserFacingException>(() => AudioClip.Prepare(Wav(), ".wav", -1, 1));
        Assert.Throws<UserFacingException>(() => AudioClip.Prepare(Wav(), ".wav", 0, 121));
        var bad = Wav(); bad[4] = 0;
        Assert.Throws<UserFacingException>(() => AudioClip.Prepare(bad, ".wav", 0, 0));
        bad = Wav(); bad[16] = 255; bad[17] = 255; bad[18] = 255; bad[19] = 255;
        Assert.Throws<UserFacingException>(() => AudioClip.Prepare(bad, ".wav", 0, 0));
        bad = Wav(); bad[20] = 3;
        Assert.Throws<UserFacingException>(() => AudioClip.Prepare(bad, ".wav", 0, 0));
        bad = Wav(); bad[32] = 0;
        Assert.Throws<UserFacingException>(() => AudioClip.Prepare(bad, ".wav", 0, 0));
    }
    [Fact]
    public void Mp3RequiresWholeBoundedFileAndReportsUnknownDuration()
    {
        byte[] mp3 = [.. "ID3"u8.ToArray(), .. new byte[20]];
        var clip = AudioClip.Prepare(mp3, ".mp3", 0, 0);
        Assert.Same(mp3, clip.Bytes); Assert.Null(clip.DurationSeconds); Assert.Equal("mp3", clip.Format);
        Assert.Throws<UserFacingException>(() => AudioClip.Prepare(mp3, ".mp3", 0, 1));
        Assert.Throws<UserFacingException>(() => AudioClip.Prepare(new byte[44], ".mp3", 0, 0));
        var big = new byte[AudioTool.MaxClipBytes + 1]; "ID3"u8.CopyTo(big);
        Assert.Throws<UserFacingException>(() => AudioClip.Prepare(big, ".mp3", 0, 0));
    }
    [Fact]
    public async Task AudioIsSentToDedicatedModelAndReceiptSurvivesRestart()
    {
        var handler = new Handler(async (request, ct) => {
            Assert.Equal("https://api.openai.com/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-audio-key", request.Headers.Authorization!.Parameter);
            var data = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            Assert.Equal("gpt-audio-1.5", data["model"]!.ToString());
            Assert.Equal("text", data["modalities"]![0]!.ToString()); Assert.False(data["store"]!.GetValue<bool>());
            var audio = data["messages"]![1]!["content"]![1]!["input_audio"]!;
            Assert.Equal("wav", audio["format"]!.ToString());
            Assert.Equal(16044, Convert.FromBase64String(audio["data"]!.ToString()).Length);
            return Response();
        });
        using var f = new Fixture(handler); var call = f.Call(start: 1, duration: 1);
        var result = await f.Tools.ExecuteAsync(f.Context(), call);
        var parsed = JsonNode.Parse(result.Content)!;
        Assert.Equal("A steady tone.", parsed["analysis"]!.ToString());
        Assert.True(parsed["partial"]!.GetValue<bool>()); Assert.NotNull(parsed["sourceHash"]);
        Assert.DoesNotContain("base64", result.Content); Assert.DoesNotContain("test-audio-key", result.Content);
        var artifact = Assert.Single(f.Session.Artifacts); Assert.Equal("audio-analysis", artifact.Kind);
        Assert.Contains("A steady tone.", File.ReadAllText(artifact.Path));
        var reopened = new SessionStore(f.Store.DataDirectory); var saved = reopened.Get(f.Session.Id)!;
        var ctx = new ToolExecutionContext(saved, saved.Runs.Last(), reopened, default);
        Assert.Equal(result.Content, (await f.Tools.ExecuteAsync(ctx, call)).Content);
        Assert.Equal(result.Content, f.Tools.Recover(ctx, call)!.Content);
        Assert.Equal(1, handler.Requests);
        saved.Receipts.Clear();
        Assert.Contains("not replayed", f.Tools.Recover(ctx, call)!.Content);
        Assert.Equal(1, handler.Requests);
    }
    [Theory]
    [InlineData("../outside.wav")] [InlineData(".git/test.wav")] [InlineData("https://example.com/a.wav")] [InlineData("tone.txt")]
    public void WorkspaceBoundariesAndFormatsAreEnforced(string path)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response())); using var f = new Fixture(handler);
        Assert.ThrowsAny<Exception>(() => f.Call(path)); Assert.Equal(0, handler.Requests);
    }
    [Fact]
    public async Task MissingConfigurationRejectsBeforeSending()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response())); using var f = new Fixture(handler, key: null);
        var error = await Assert.ThrowsAsync<UserFacingException>(() => f.Tools.ExecuteAsync(f.Context(), f.Call()));
        Assert.Contains("OPENAI_API_KEY", error.Message); Assert.Equal(0, handler.Requests); Assert.Empty(f.Session.Artifacts);
    }
    [Theory]
    [InlineData(401)] [InlineData(429)] [InlineData(500)] [InlineData(302)]
    public async Task FailuresDoNotLeakResponseBodiesOrRetry(int code)
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("secret response body") }));
        using var f = new Fixture(handler);
        var error = await Assert.ThrowsAsync<UserFacingException>(() => f.Tools.ExecuteAsync(f.Context(), f.Call()));
        Assert.Contains(code.ToString(), error.Message); Assert.DoesNotContain("secret response", error.Message);
        Assert.Equal(1, handler.Requests); Assert.Empty(f.Session.Artifacts);
    }
    [Fact]
    public async Task CancellationAndOversizedResponsesAreBounded()
    {
        using var cts = new CancellationTokenSource();
        var handler = new Handler(async (_, ct) => { cts.Cancel(); await Task.Delay(10000, ct); return Response(); });
        using var f = new Fixture(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Tools.ExecuteAsync(f.Context(cts.Token), f.Call()));
        Assert.Empty(f.Session.Artifacts);
        var big = new Handler((_, _) => Task.FromResult(Response(new string('a', 140000)))); using var g = new Fixture(big);
        await Assert.ThrowsAsync<UserFacingException>(() => g.Tools.ExecuteAsync(g.Context(), g.Call())); Assert.Empty(g.Session.Artifacts);
    }
    [Theory]
    [InlineData(WorkflowMode.Work)] [InlineData(WorkflowMode.Plan)] [InlineData(WorkflowMode.Document)] [InlineData(WorkflowMode.Review)]
    public void ListeningNeedsNoVisionOrEditPermission(WorkflowMode workflow)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response())); using var f = new Fixture(handler);
        f.Session.Mode = PermissionMode.Inspect; f.Session.Workflow = workflow; f.Session.Connection.Capabilities.Vision = false;
        Assert.Contains(f.Tools.Available(f.Session), t => t.Name == "audio_listen");
        Assert.NotNull(f.Call().Prepared);
    }
}
