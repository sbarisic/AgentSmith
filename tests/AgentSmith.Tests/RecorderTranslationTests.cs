using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Application;
using AgentSmith.Core;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

public sealed class RecorderTranslationTests
{
    [Fact]
    public async Task TranslationUsesOnlySelectedPassageAndCachesBySourceLanguageAndAgent()
    {
        using var directory = new TestDirectory();
        var handler = new TranslationServer();
        using var recorder = new CallRecorder(directory.Path, Path.Combine(directory.Path, "smith.exe"), handler);
        recorder.SavePreferences(new() { SummaryAgent = new("fixture", ModelProvider.Local, "http://localhost:8080/v1", "", 8192), SummaryLanguage = "hr" });
        var job = new RecordingAnalysisJob();
        Directory.CreateDirectory(recorder.Analysis.Store.DirectoryFor(job.Id)); recorder.Analysis.Store.Save(job);
        var capture = Guid.NewGuid().ToString("N"); recorder.SaveMetadata(capture, new() { AnalysisId = job.Id });
        var path = recorder.Analysis.Store.FileFor(job.Id, "transcript.jsonl");
        const string evidence = """
            {"id":"one","startSeconds":1.125,"endSeconds":2.25,"speaker":"speaker","text":"Good morning"}
            {"id":"two","startSeconds":3,"endSeconds":4,"speaker":"speaker","text":"PRIVATE OTHER PASSAGE"}
            """;
        File.WriteAllText(path, evidence);
        Assert.Equal("Dobro jutro", await recorder.Translate(capture, "one", default));
        Assert.Contains("Good morning", handler.Body);
        Assert.Contains("hr", handler.Body);
        Assert.DoesNotContain("PRIVATE OTHER PASSAGE", handler.Body);
        var request = JsonNode.Parse(handler.Body)!;
        Assert.True(request["tools"] is null || request["tools"]!.AsArray().Count == 0);
        Assert.Equal("Dobro jutro", await recorder.Translate(capture, "one", default));
        Assert.Equal(1, handler.Generations);
        recorder.Preferences.SummaryLanguage = "sl";
        handler.Fail = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => recorder.Translate(capture, "one", default));
        Assert.Single(Directory.GetFiles(recorder.Analysis.Store.DirectoryFor(job.Id), "translation-*.json"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recorder.Translate(capture, "one", cancellation.Token));
        Assert.Equal(evidence, File.ReadAllText(path));
        var row = recorder.Transcript(capture)[0];
        Assert.NotEqual(CallRecorder.TranslationKey(row, "hr", "agent-a"), CallRecorder.TranslationKey(row, "hr", "agent-b"));
        Assert.NotEqual(CallRecorder.TranslationKey(row, "hr", "agent-a"), CallRecorder.TranslationKey(row with { Text = "Changed" }, "hr", "agent-a"));
    }

    private sealed class TranslationServer : HttpMessageHandler
    {
        public string Body = "";
        public int Generations;
        public bool Fail;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Fail) throw new HttpRequestException("Fixture offline");
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/models") return new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"fixture\",\"meta\":{\"n_ctx\":32768}}]}") };
            if (path != "/v1/chat/completions") return new(HttpStatusCode.NotFound);
            Body = await request.Content!.ReadAsStringAsync(ct); Generations++;
            return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Dobro jutro\"},\"finish_reason\":null}]}\n\ndata: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}
