using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

public sealed class RecordingAnalysisTests
{
    private sealed class Backend : ILocalAudioBackend
    {
        public int Calls;
        public bool Disconnect;
        public bool FailSpeakers;
        public bool ManySegments;
        public List<string[]> Components = [];
        public AudioReadiness Status(string data) => new(true, true, data, "fake", true);
        public Task<JsonObject> Run(string installation, JsonObject request, Action<string> progress, CancellationToken cancellation)
        {
            Calls++; if (Disconnect) throw new IOException("Lost response");
            Components.Add(request["components"]!.AsArray().Select(c => c!.ToString()).ToArray());
            return Task.FromResult(JsonNode.Parse("""
                {"transcription":{"status":"succeeded","segments":[{"startSeconds":1,"endSeconds":2,"text":"hello world","words":[{"startSeconds":1,"endSeconds":1.5,"text":"hello "},{"startSeconds":1.5,"endSeconds":2,"text":"world"}]}]},"diarization":{"status":"succeeded","turns":[{"startSeconds":1,"endSeconds":2,"speaker":"Speaker 1"}],"embeddings":[{"speaker":"Speaker 1","seconds":4,"vector":[1,0]}]}}
                """)!.AsObject().Also(r =>
                {
                    if (FailSpeakers) r["diarization"] = new JsonObject { ["status"] = "failed", ["error"] = "fixture" };
                    if (ManySegments) r["transcription"]!["segments"] = new JsonArray(Enumerable.Range(0, 60).Select(i => (JsonNode)new JsonObject { ["startSeconds"] = i + 1, ["endSeconds"] = i + 2, ["text"] = new string('a', 500) + i }).ToArray());
                }));
        }
    }
    private static (RecordingAnalysis Analysis, RecordingAnalysisJob Job) Prepared(string data, Backend backend, int seconds = 225, bool summary = false)
    {
        var analysis = new RecordingAnalysis(data, backend);
        var installation = Path.Combine(data, "audio", "installations", Guid.NewGuid().ToString("N")); var helper = Path.Combine(installation, "helper"); Directory.CreateDirectory(helper);
        foreach (var file in new[] { "audio_helper.py", "downloads.json", "requirements.lock" }) File.WriteAllText(Path.Combine(helper, file), "{}");
        var decoder = Path.Combine(LocalAudioInstallation.Bundle, "recording_decode.py");
        var job = new RecordingAnalysisJob { Prepared = true, Frames = seconds * 16000L, Installation = Path.GetFileName(installation), Fingerprint = LocalAudioInstallation.Fingerprint(helper),
            DecoderHash = RecordingAnalysisStore.Hash(decoder), Options = new(NoSummary: !summary), Chunks = RecordingAnalysis.PlanChunks(seconds * 16000L, false) };
        Directory.CreateDirectory(analysis.Store.DirectoryFor(job.Id));
        var normalized = analysis.Store.FileFor(job.Id, "normalized.f32"); using (var file = File.Create(normalized)) file.SetLength(job.Frames * 4);
        RecordingAnalysisStore.WriteJson(analysis.Store.FileFor(job.Id, "preparation.json"), new { hash = RecordingAnalysisStore.Hash(normalized) }); analysis.Store.Save(job);
        return (analysis, job);
    }
    [Fact]
    public void ChunkPlanCoversEveryFrameOnceAndBoundsContext()
    {
        var frames = 2 * 60 * 60 * 16000L + 17; var chunks = RecordingAnalysis.PlanChunks(frames, true);
        Assert.Equal(frames, chunks.Sum(c => c.CoreEnd - c.CoreStart));
        Assert.All(chunks, c => Assert.InRange(c.End - c.Start, 1, 120 * 16000));
        for (var i = 1; i < chunks.Count; i++) Assert.Equal(chunks[i - 1].CoreEnd, chunks[i].CoreStart);
        Assert.Equal(frames, chunks[^1].End);
    }
    [Fact]
    public async Task CompletedChunksReuseWithoutServiceAndExportRemainsReadable()
    {
        using var directory = new TestDirectory(); var backend = new Backend(); var (analysis, job) = Prepared(directory.Path, backend);
        using (analysis)
        {
            var result = await analysis.Run(job.Id, false, false, _ => { }, null, null, default);
            Assert.Equal("Completed", result.State); Assert.Equal(3, backend.Calls); Assert.Equal(225, result.ProcessedSeconds);
            await analysis.Run(job.Id, false, false, _ => { }, null, null, default); Assert.Equal(3, backend.Calls);
            Assert.Contains("hello world", analysis.Read(job.Id, "transcript")["text"]!.ToString());
            var destination = Path.Combine(directory.Path, "export"); analysis.Export(job.Id, destination); Assert.True(File.Exists(Path.Combine(destination, "transcript.srt")));
            Assert.Throws<UserFacingException>(() => analysis.Export(job.Id, destination));
        }
    }
    [Fact]
    public async Task UncertainSubmissionRequiresExplicitRetry()
    {
        using var directory = new TestDirectory(); var backend = new Backend { Disconnect = true }; var (analysis, job) = Prepared(directory.Path, backend, 10);
        using (analysis)
        {
            Assert.Equal("Paused", (await analysis.Run(job.Id, false, false, _ => { }, null, null, default)).State);
            backend.Disconnect = false;
            Assert.Equal("CompletedWithGaps", (await analysis.Run(job.Id, false, false, _ => { }, null, null, default)).State); Assert.Equal(1, backend.Calls);
            Assert.Equal("Completed", (await analysis.Run(job.Id, false, true, _ => { }, null, null, default)).State); Assert.Equal(2, backend.Calls);
        }
    }
    [Fact]
    public async Task PartialComponentsOnlyRetryTheFailure()
    {
        using var directory = new TestDirectory(); var backend = new Backend { FailSpeakers = true }; var (analysis, job) = Prepared(directory.Path, backend, 10);
        using (analysis)
        {
            Assert.Equal("CompletedWithGaps", (await analysis.Run(job.Id, false, false, _ => { }, null, null, default)).State);
            backend.FailSpeakers = false; await analysis.Run(job.Id, true, false, _ => { }, null, null, default);
            Assert.Equal(new[] { "diarization" }, backend.Components[1]);
        }
    }
    [Fact]
    public async Task ChangedPreparedAudioAndConcurrentOwnersAreRejected()
    {
        using var directory = new TestDirectory(); var backend = new Backend(); var (analysis, job) = Prepared(directory.Path, backend, 10);
        using (analysis)
        {
            using (analysis.Store.Lease(job.Id)) await Assert.ThrowsAsync<IOException>(() => analysis.Run(job.Id, false, false, _ => { }, null, null, default));
            File.WriteAllText(analysis.Store.FileFor(job.Id, "normalized.f32"), "changed");
            var result = await analysis.Run(job.Id, false, false, _ => { }, null, null, default);
            Assert.Equal("Paused", result.State); Assert.Contains("changed", result.Error); Assert.Equal(0, backend.Calls);
        }
    }
    [Fact]
    public void RepetitionIsOnlyDeduplicatedWithOverlappingWordEvidence()
    {
        JsonObject Row(double start) => JsonNode.Parse($$"""{"text":"yes","startSeconds":{{start}},"endSeconds":{{start+1}},"words":[{"text":"yes","startSeconds":{{start}},"endSeconds":{{start+1}}}]}""")!.AsObject();
        var first = new List<JsonObject> { Row(0) }; var repeated = new List<JsonObject> { Row(1) };
        RecordingAnalysis.ReconcileBoundary(first, repeated); Assert.Single(repeated);
        var duplicate = new List<JsonObject> { Row(.1) }; RecordingAnalysis.ReconcileBoundary(first, duplicate); Assert.Empty(duplicate);
    }
    [Fact]
    public void SpeakersAreRecordingLocalAndAmbiguousMatchesRemainUnknown()
    {
        var speakers = new List<RecordingAnalysis.Speaker>();
        Assert.Equal("Speaker 1", RecordingAnalysis.MatchSpeaker([1, 0], speakers, []));
        Assert.Equal("Speaker 1", RecordingAnalysis.MatchSpeaker([.99, .1], speakers, []));
        Assert.Equal("Speaker 2", RecordingAnalysis.MatchSpeaker([0, 1], speakers, []));
        Assert.Equal("Unknown speaker", RecordingAnalysis.MatchSpeaker([.71, .71], speakers, []));
        Assert.NotEqual("Speaker 1", RecordingAnalysis.MatchSpeaker([1, 0], speakers, ["Speaker 1"]));
    }
    [Fact]
    public void SummaryRejectsInventedCitations()
    {
        var item = JsonNode.Parse("""{"items":[{"text":"A decision","segments":["missing"]}]}""")!.AsObject();
        Assert.Throws<UserFacingException>(() => RecordingAnalysis.ValidateSummary(item, ["known"]));
        RecordingAnalysis.ValidateSummary(item, ["missing"]);
    }
    [Fact]
    public async Task SummaryFailureRetainsEvidenceAndExplicitRetryReusesInference()
    {
        using var directory = new TestDirectory(); var backend = new Backend(); var (analysis, job) = Prepared(directory.Path, backend, 10, true);
        using (analysis)
        {
            var result = await analysis.Run(job.Id, false, false, _ => { }, (_, _) => throw new IOException("Provider disconnected"), "fixture", default);
            Assert.Equal("Paused", result.State); Assert.Contains("hello", analysis.Read(job.Id, "transcript")["text"]!.ToString());
            Task<string> Summary(string _, CancellationToken ct) => Task.FromResult("""{"items":[{"text":"A greeting.","segments":["c000000s00000"]}]}""");
            result = await analysis.Run(job.Id, false, true, _ => { }, Summary, "fixture", default);
            Assert.Equal("Completed", result.State); Assert.Equal(1, backend.Calls); Assert.Contains("00:01", analysis.Read(job.Id, "summary")["text"]!.ToString());
        }
    }
    [Fact]
    public async Task AttachmentSurvivesJobDeletionAndRequiresConversationGrant()
    {
        using var directory = new TestDirectory(); var backend = new Backend(); var (analysis, job) = Prepared(directory.Path, backend, 10);
        using (analysis)
        {
            await analysis.Run(job.Id, false, false, _ => { }, null, null, default);
            var store = new SessionStore(directory.Path); var session = new Session { Workspace = directory.Path }; var run = new Run();
            var context = new ToolExecutionContext(session, run, store, default);
            var path = analysis.Attach(job.Id, context.ArtifactDirectory); var record = ArtifactAccess.Register(context, path, "recording-analysis");
            analysis.Store.Delete(job.Id); Assert.Empty(analysis.Store.List());
            var arguments = new JsonObject { ["artifactId"] = record.Id, ["view"] = "transcript", ["cursor"] = 0, ["maxCharacters"] = 4000 };
            Assert.Contains("hello world", ArtifactAccess.Execute(context, "audio_read", arguments).Content);
            var stranger = context with { Session = new Session { Workspace = directory.Path } };
            Assert.Throws<UserFacingException>(() => ArtifactAccess.Execute(stranger, "audio_read", arguments));
            store.Save(session, new("attached", session.Id, null, new { }));
            var child = new Session { Workspace = directory.Path, ParentConversationId = session.Id };
            Assert.Throws<UserFacingException>(() => ArtifactAccess.Execute(context with { Session = child }, "audio_read", arguments));
            child.ArtifactGrants.Add(new(session.Id, record.Id));
            Assert.Contains("hello world", ArtifactAccess.Execute(context with { Session = child }, "audio_read", arguments).Content);
            var bundle = RecordingAnalysisStore.ReadJson<JsonObject>(path);
            var files = bundle["files"]!.AsArray(); files.Remove(files.Single(f => f!["name"]!.ToString() == "transcript.jsonl"));
            RecordingAnalysisStore.WriteJson(path, bundle);
            Assert.Throws<UserFacingException>(() => ArtifactAccess.Execute(context, "audio_read", arguments));
        }
    }
    [Fact]
    public void ActiveAndMalformedCapturesCannotBeAnalyzed()
    {
        var manifest = new CaptureManifest { Id = "capture", State = "Recording", SampleRate = 16000, Channels = 1, BitsPerSample = 16,
            BlockAlign = 2, Frames = 16000, Chunks = [new("chunk-000000.wav", 0, 16000, 32044, "hash")] };
        Assert.Throws<IOException>(() => RecordingAnalysis.ValidateCapture(manifest, "capture", true));
        manifest.State = "Completed"; RecordingAnalysis.ValidateCapture(manifest, "capture", false);
        manifest.State = "Interrupted"; Assert.Throws<IOException>(() => RecordingAnalysis.ValidateCapture(manifest, "capture", false));
        RecordingAnalysis.ValidateCapture(manifest, "capture", true);
        manifest.State = "Completed"; manifest.Chunks = [new("../escape.wav", 0, 16000, 32044, "hash")];
        Assert.Throws<IOException>(() => RecordingAnalysis.ValidateCapture(manifest, "capture", false));
    }
    [Fact]
    public async Task HierarchicalSummaryBudgetResumesWithoutRepeatingSuccessfulRequests()
    {
        using var directory = new TestDirectory(); var backend = new Backend { ManySegments = true }; var (analysis, job) = Prepared(directory.Path, backend, 100, true);
        using (analysis)
        {
            var calls = 0;
            Task<string> Summary(string prompt, CancellationToken ct)
            {
                calls++;
                var evidence = JsonNode.Parse(prompt[(prompt.IndexOf("EVIDENCE:\n", StringComparison.Ordinal) + 10)..])!.AsArray();
                var ids = evidence.SelectMany(row => row!["id"] is { } id ? new[] { id.ToString() } : row["segments"]!.AsArray().Select(x => x!.ToString())).Take(8);
                return Task.FromResult(new JsonObject { ["items"] = new JsonArray(new JsonObject { ["text"] = "Supported fixture summary.", ["segments"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) }) }.ToJsonString());
            }
            var first = await analysis.Run(job.Id, false, false, _ => { }, Summary, "fixture", default, maxRequests: 1);
            Assert.Equal("Paused", first.State); Assert.Contains("budget", first.Error); Assert.Equal(1, calls);
            var final = await analysis.Run(job.Id, false, false, _ => { }, Summary, "fixture", default, maxRequests: 100);
            Assert.Equal("Completed", final.State); Assert.True(calls > 3); Assert.Equal(calls, final.SummaryRequests); Assert.Equal(1, backend.Calls);
        }
    }
    [Fact]
    public void CoverageDoesNotDoubleCountOverlappingGaps()
    {
        var job = new RecordingAnalysisJob { Frames = 100 * 16000, Chunks = RecordingAnalysis.PlanChunks(100 * 16000, false), Coverage = [new(10*16000, 10*16000, "Missing"), new(15*16000, 10*16000, "TimestampGap"), new(30*16000,10*16000,"InferredSilence")] };
        job.Chunks[0].Components["transcription"] = "Succeeded";
        Assert.Equal(85, job.ProcessedSeconds);
    }
    [Fact]
    public async Task CancelledJobKeepsItsPlanAndNoSubmissionIsReplayed()
    {
        using var directory = new TestDirectory(); var backend = new Backend(); var (analysis, job) = Prepared(directory.Path, backend, 10);
        using (analysis)
        {
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            var result = await analysis.Run(job.Id, false, false, _ => { }, null, null, cancel.Token);
            Assert.Equal("Paused", result.State); Assert.Equal(0, backend.Calls); Assert.Single(result.Chunks);
            result = await analysis.Run(job.Id, false, false, _ => { }, null, null, default); Assert.Equal("Completed", result.State);
        }
    }
    [Fact]
    public void PaginationPreservesUnicodeAndStorageBudgetDoesNotDeleteEvidence()
    {
        using var directory = new TestDirectory(); var store = new RecordingAnalysisStore(directory.Path); var job = new RecordingAnalysisJob(); Directory.CreateDirectory(store.DirectoryFor(job.Id)); store.Save(job);
        File.WriteAllText(store.FileFor(job.Id, "summary.md"), "abc😀def\n");
        var first = RecordingAnalysis.ReadView(store.DirectoryFor(job.Id), "summary", 0, 4); Assert.Equal("abc", first["text"]!.ToString());
        var second = RecordingAnalysis.ReadView(store.DirectoryFor(job.Id), "summary", first["cursor"]!.GetValue<int>(), 4); Assert.StartsWith("😀", second["text"]!.ToString());
        Assert.Throws<UserFacingException>(() => RecordingAnalysis.ReadView(store.DirectoryFor(job.Id), "summary", 3, 1));
        Assert.Throws<UserFacingException>(() => store.CheckBudget(.01, 20 * 1024 * 1024)); Assert.True(File.Exists(store.FileFor(job.Id, "summary.md")));
    }
    [Fact]
    public async Task ConcurrentStorageReservationsCannotOversubscribeTheBudget()
    {
        using var directory = new TestDirectory(); var store = new RecordingAnalysisStore(directory.Path);
        using (await store.Reserve(8 * 1024 * 1024, .01, default))
            await Assert.ThrowsAsync<UserFacingException>(() => store.Reserve(8 * 1024 * 1024, .01, default));
        using (await store.Reserve(8 * 1024 * 1024, .01, default)) { }
        File.WriteAllText(Path.Combine(store.Root, "reserve-abandoned.json"), "8000000");
        using (await store.Reserve(8 * 1024 * 1024, .01, default)) { }
        Assert.False(File.Exists(Path.Combine(store.Root, "reserve-abandoned.json")));
    }
}

internal static class AnalysisTestExtensions
{
    internal static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
