using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Xunit;
using static AgentSmith.Tests.RuntimeTests;

namespace AgentSmith.Tests;

public sealed class AudioTests
{
    private sealed class Backend : ILocalAudioBackend
    {
        public bool Ready = true;
        public bool Speakers;
        public int Calls;
        public string? Snapshot;
        public Func<JsonObject, CancellationToken, Task<JsonObject>> Work = (_, _) => Task.FromResult(Result());
        public AudioReadiness Status(string dataDirectory) => new(Ready, Ready, dataDirectory, "Run smith audio setup.", Speakers);
        public async Task<JsonObject> Run(string installation, JsonObject request, Action<string> progress, CancellationToken cancellation)
        {
            Calls++; Snapshot = request["path"]!.ToString();
            Assert.True(File.Exists(Snapshot)); Assert.DoesNotContain("question", request.ToJsonString());
            progress("transcribing"); return await Work(request, cancellation);
        }
    }
    private sealed class Fixture : IDisposable
    {
        public TestDirectory Directory { get; } = new();
        public SessionStore Store { get; }
        public Session Session { get; }
        public ToolRegistry Tools { get; }
        public Backend Backend { get; } = new();
        public Fixture()
        {
            Store = new(Path.Combine(Directory.Path, "data"));
            Session = new() { Workspace = Directory.Path, Mode = PermissionMode.Inspect, Runs = [new()] };
            Store.Save(Session, new("test", Session.Id, null, new { }));
            Tools = new(Store, audio: new AudioTool(Backend));
            System.IO.Directory.CreateDirectory(Path.Combine(Store.DataDirectory, "helper"));
            File.Copy(Path.Combine(LocalAudioInstallation.Bundle, "downloads.json"), Path.Combine(Store.DataDirectory, "helper", "downloads.json"));
            File.WriteAllBytes(Path.Combine(Directory.Path, "tone.wav"), new byte[44]);
        }
        public ToolExecutionContext Context(CancellationToken ct = default) => new(Session, Session.Runs.Last(), Store, ct);
        public ToolCall Call(string path = "tone.wav", string name = "audio_listen", int start = 0, int duration = 0, string language = "auto")
        {
            var args = new JsonObject { ["path"] = path, ["startSeconds"] = start, ["durationSeconds"] = duration };
            if (name == "audio_listen") args["question"] = "What do you hear?";
            if (name == "audio_transcribe") args["language"] = language;
            var call = new ToolCall { Id = Guid.NewGuid().ToString("N"), Name = name, Arguments = args.ToJsonString() };
            call.Prepared = Tools.Prepare(Context(), call); return call;
        }
        public void Dispose() { Tools.Dispose(); Directory.Dispose(); }
    }
    private static JsonObject Result() => JsonNode.Parse("""
        {"startSeconds":1,"durationSeconds":1,"clipHash":"abc","transcription":{"status":"succeeded","language":"en","noSpeech":false,"segments":[{"startSeconds":1.1,"endSeconds":1.9,"text":"Hello world."}]},"classification":{"status":"succeeded","aggregateScores":[{"label":"Speech","score":0.87654}],"windows":[],"classMap":[]}}
        """)!.AsObject();

    [Theory]
    [InlineData("audio_listen")] [InlineData("audio_transcribe")] [InlineData("audio_classify")]
    public async Task SnapshotArtifactsAndReceiptSurviveReloadWithoutInference(string name)
    {
        using var f = new Fixture(); var call = f.Call(name: name, start: 1, duration: 1);
        f.Backend.Work = (request, _) => {
            Assert.Equal(1, request["startSeconds"]!.GetValue<int>());
            File.WriteAllBytes(Path.Combine(f.Directory.Path, "tone.wav"), new byte[100]);
            Assert.Equal(44, File.ReadAllBytes(request["path"]!.ToString()).Length);
            return Task.FromResult(Result());
        };
        var output = await f.Tools.ExecuteAsync(f.Context(), call);
        Assert.Contains("Hello world.", output.Content); Assert.Contains("0.877", output.Content);
        Assert.DoesNotContain("classMap", output.Content); Assert.Contains("not certainty", output.Content);
        var artifact = Assert.Single(f.Session.Artifacts); Assert.Contains("classMap", File.ReadAllText(artifact.Path));
        Assert.False(File.Exists(f.Backend.Snapshot)); Assert.DoesNotContain("base64", File.ReadAllText(artifact.Path));
        var saved = f.Store.Get(f.Session.Id)!; var ctx = new ToolExecutionContext(saved, saved.Runs.Last(), f.Store, default);
        f.Backend.Ready = false;
        Assert.Equal(output.Content, (await f.Tools.ExecuteAsync(ctx, call)).Content);
        Assert.Equal(output.Content, f.Tools.Recover(ctx, call)!.Content); Assert.Equal(1, f.Backend.Calls);
        saved.Receipts.Clear(); Assert.Contains("not replayed", f.Tools.Recover(ctx, call)!.Content); Assert.Equal(1, f.Backend.Calls);
    }

    [Fact]
    public async Task SpeakerEmbeddingsNeverEnterShortAudioConversationArtifacts()
    {
        using var f = new Fixture(); f.Backend.Speakers = true;
        f.Backend.Work = (_, _) =>
        {
            var result = Result();
            result["diarization"] = new JsonObject { ["status"] = "succeeded", ["turns"] = new JsonArray(), ["embeddings"] = new JsonArray(new JsonObject { ["vector"] = new JsonArray(1, 0) }) };
            return Task.FromResult(result);
        };
        await f.Tools.ExecuteAsync(f.Context(), f.Call());
        Assert.DoesNotContain("embeddings", File.ReadAllText(Assert.Single(f.Session.Artifacts).Path));
    }

    [Theory]
    [InlineData("../outside.wav")] [InlineData(".git/a.wav")] [InlineData("https://example.com/a.wav")]
    [InlineData("tone.txt")] [InlineData("bin/a.wav")]
    public void PathRestrictions(string path)
    { using var f = new Fixture(); Assert.ThrowsAny<Exception>(() => f.Call(path)); Assert.Equal(0, f.Backend.Calls); }

    [Fact]
    public void RejectsRangesAndUnsupportedLanguageBeforeInference()
    {
        using var f = new Fixture();
        Assert.Throws<UserFacingException>(() => f.Call(start: -1));
        Assert.Throws<UserFacingException>(() => f.Call(duration: 121));
        Assert.Throws<UserFacingException>(() => f.Call(name: "audio_transcribe", language: "xx"));
        Assert.NotNull(f.Call(name: "audio_transcribe", language: "hr"));
        Assert.NotNull(f.Call(name: "audio_transcribe", language: "auto"));
    }

    [Fact]
    public async Task SizeAndReadinessAreRecheckedAtDispatch()
    {
        using var f = new Fixture(); var call = f.Call();
        using (var file = File.OpenWrite(Path.Combine(f.Directory.Path, "tone.wav"))) file.SetLength(AudioTool.MaxFileBytes + 1L);
        await Assert.ThrowsAsync<UserFacingException>(() => f.Tools.ExecuteAsync(f.Context(), call));
        f.Backend.Ready = false;
        Assert.DoesNotContain(f.Tools.Available(f.Session), t => AudioTool.IsAudio(t.Name));
        var error = await Assert.ThrowsAsync<UserFacingException>(() => f.Tools.ExecuteAsync(f.Context(), call));
        Assert.Contains("smith audio setup", error.Message); Assert.Equal(0, f.Backend.Calls);
    }

    [Theory]
    [InlineData(WorkflowMode.Work)] [InlineData(WorkflowMode.Plan)] [InlineData(WorkflowMode.Document)] [InlineData(WorkflowMode.Review)]
    public void ToolsRemainReadOnlyAndDoNotRequireVision(WorkflowMode workflow)
    {
        using var f = new Fixture(); f.Session.Workflow = workflow; f.Session.Connection.Capabilities.Vision = false;
        foreach (var name in new[] { "audio_listen", "audio_transcribe", "audio_classify" })
        { Assert.Contains(f.Tools.Available(f.Session), t => t.Name == name && t.Effect == ToolEffect.ReadOnly); Assert.NotNull(f.Call(name: name).Prepared); }
    }

    [Fact]
    public async Task CancellationCleansSnapshotAndAllowsFreshRequest()
    {
        using var f = new Fixture(); using var cancel = new CancellationTokenSource();
        f.Backend.Work = async (_, ct) => { cancel.Cancel(); await Task.Delay(10000, ct); return Result(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Tools.ExecuteAsync(f.Context(cancel.Token), f.Call()));
        Assert.False(File.Exists(f.Backend.Snapshot)); Assert.Empty(f.Session.Artifacts); Assert.Empty(f.Session.Receipts);
        f.Backend.Work = (_, _) => Task.FromResult(Result());
        await f.Tools.ExecuteAsync(f.Context(), f.Call()); Assert.Single(f.Session.Receipts);
    }

    [Fact]
    public async Task ParentAndWorkerRegistriesSubmitIndependentlyToBackend()
    {
        using var parent = new Fixture(); using var worker = new Fixture(); worker.Session.ParentConversationId = parent.Session.Id;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        parent.Backend.Work = async (_, ct) => { entered.SetResult(); await release.Task.WaitAsync(ct); return Result(); };
        var first = parent.Tools.ExecuteAsync(parent.Context(), parent.Call()); await entered.Task;
        var second = worker.Tools.ExecuteAsync(worker.Context(), worker.Call());
        await second.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(first.IsCompleted);
        release.SetResult(); await first;
        Assert.Equal(1, worker.Backend.Calls);
    }

    [Fact]
    public async Task PartialFailureRetainsSuccessfulComponentAndAllFailureRetainsNothing()
    {
        using var f = new Fixture();
        f.Backend.Work = (_, _) => { var result = Result(); result["classification"] = JsonNode.Parse("""{"status":"failed","error":"missing model"}"""); return Task.FromResult(result); };
        var output = await f.Tools.ExecuteAsync(f.Context(), f.Call()); Assert.Contains("classification: failed", output.Content); Assert.Contains("Hello world", output.Content);
        using var g = new Fixture(); g.Backend.Work = (_, _) => Task.FromResult(new JsonObject());
        await Assert.ThrowsAsync<UserFacingException>(() => g.Tools.ExecuteAsync(g.Context(), g.Call())); Assert.Empty(g.Session.Artifacts);
    }

    [Fact]
    public async Task ScriptedChatUsesLocalEvidenceWithoutPaidModel()
    {
        using var f = new Fixture();
        var call = f.Call(name: "audio_transcribe");
        f.Session.Runs.Clear();
        var model = new ScriptedModel([Done(RuntimeTests.Call(call.Id, call.Name, call.Arguments))], [Done(Message("The recording says Hello world."))]);
        var run = await new AgentRunner(f.Store, model, f.Tools).RunAsync(f.Session, "Transcribe tone.wav", null, _ => { }, default);
        Assert.Equal(RunState.Completed, run.State); Assert.Equal(1, f.Backend.Calls); Assert.Single(f.Session.Artifacts);
        Assert.Contains(model.Requests.Last().Input, x => x["type"]?.ToString() == "function_call_output" && x.ToJsonString().Contains("Hello world"));
    }
    [Fact]
    public async Task ReuseSurvivesReloadAndInvalidatesOnContentLanguageAndRange()
    {
        using var f = new Fixture(); var first = f.Call(name: "audio_transcribe");
        await f.Tools.ExecuteAsync(f.Context(), first);
        var saved = f.Store.Get(f.Session.Id)!;
        var ctx = new ToolExecutionContext(saved, saved.Runs.Last(), f.Store, default);
        var next = f.Call(name: "audio_transcribe"); var output = await f.Tools.ExecuteAsync(ctx, next);
        Assert.True(JsonNode.Parse(output.Content)!["reused"]!.GetValue<bool>()); Assert.Equal(1, f.Backend.Calls); Assert.Single(saved.Artifacts);
        await f.Tools.ExecuteAsync(f.Context(), f.Call(name: "audio_transcribe", language: "en")); Assert.Equal(2, f.Backend.Calls);
        await f.Tools.ExecuteAsync(f.Context(), f.Call(name: "audio_transcribe", duration: 1)); Assert.Equal(3, f.Backend.Calls);
        File.WriteAllBytes(Path.Combine(f.Directory.Path, "tone.wav"), new byte[45]);
        await f.Tools.ExecuteAsync(f.Context(), f.Call(name: "audio_transcribe")); Assert.Equal(4, f.Backend.Calls);
    }

    [Fact]
    public async Task CombinedRequestRunsOnlyMissingComponentAndCanAssembleExistingArtifacts()
    {
        using var f = new Fixture();
        f.Backend.Work = (request, _) =>
        {
            var result = Result();
            foreach (var name in new[] { "transcription", "classification" })
                if (!request["components"]!.AsArray().Any(c => c!.ToString() == name)) result.Remove(name);
            return Task.FromResult(result);
        };
        await f.Tools.ExecuteAsync(f.Context(), f.Call(name: "audio_transcribe"));
        await f.Tools.ExecuteAsync(f.Context(), f.Call(name: "audio_classify"));
        var combined = await f.Tools.ExecuteAsync(f.Context(), f.Call());
        Assert.Equal(2, f.Backend.Calls); Assert.Equal(3, f.Session.Artifacts.Count);
        Assert.Contains("Hello world", combined.Content); Assert.Contains("Speech", combined.Content);
        await f.Tools.ExecuteAsync(f.Context(), f.Call()); Assert.Equal(2, f.Backend.Calls); Assert.Equal(3, f.Session.Artifacts.Count);
    }

    [Fact]
    public async Task QueuedCancellationDoesNotCancelRunningRequest()
    {
        using var parent = new Fixture(); using var worker = new Fixture(); using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        parent.Backend.Work = async (_, ct) => { entered.SetResult(); await release.Task.WaitAsync(ct); return Result(); };
        var first = parent.Tools.ExecuteAsync(parent.Context(), parent.Call()); await entered.Task;
        var waiting = worker.Tools.ExecuteAsync(worker.Context(cancel.Token), worker.Call()); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(first.IsCompleted); release.SetResult(); await first; Assert.Equal(0, worker.Backend.Calls);
    }

    [Fact]
    public async Task FollowupReadsRelevantEvidenceWithoutAnotherInferenceOrDirectoryListing()
    {
        using var f = new Fixture(); var first = f.Call(name: "audio_transcribe"); f.Session.Runs.Clear();
        f.Backend.Speakers = true;
        f.Backend.Work = (_, _) =>
        {
            var result = Result(); result["diarization"] = JsonNode.Parse("{\"status\":\"succeeded\",\"speakerCount\":2,\"turns\":[{\"speaker\":\"Speaker 1\",\"startSeconds\":1.1,\"endSeconds\":1.5},{\"speaker\":\"Speaker 2\",\"startSeconds\":1.5,\"endSeconds\":1.9}]}");
            return Task.FromResult(result);
        };
        var runner = new AgentRunner(f.Store, new ScriptedModel([Done(RuntimeTests.Call(first.Id, first.Name, first.Arguments))], [Done(Message("Hello world."))]), f.Tools);
        await runner.RunAsync(f.Session, "Transcribe tone.wav", null, _ => { }, default);
        var artifact = Assert.Single(f.Session.Artifacts);
        var args = new JsonObject { ["artifactId"] = artifact.Id, ["view"] = "timeline", ["cursor"] = 0, ["maxCharacters"] = 4000 };
        var speakerArgs = (JsonObject)args.DeepClone(); speakerArgs["view"] = "speakers";
        var model = new ScriptedModel([Done(RuntimeTests.Call("read-audio", "audio_read", args.ToJsonString()), RuntimeTests.Call("read-speakers", "audio_read", speakerArgs.ToJsonString()))], [Done(Message("These are retained sound windows."))]);
        var run = await new AgentRunner(f.Store, model, f.Tools).RunAsync(f.Session, "What else can you tell me about speakers and sounds?", null, _ => { }, default);
        Assert.Equal(RunState.Completed, run.State); Assert.Equal(1, f.Backend.Calls);
        Assert.DoesNotContain(run.Calls, c => c.Name == "workspace_list");
        Assert.All(run.Calls, c => Assert.DoesNotContain("classMap", c.Result!));
        Assert.Contains("Speaker 2", run.Calls.Single(c => c.Id == "read-speakers").Result!);
    }

    [Fact]
    public async Task UncontendedSlotDoesNotReportWaitingAndIndependentSessionDoesNotReuse()
    {
        using var f = new Fixture(); var events = new List<AgentEvent>();
        await f.Tools.ExecuteAsync(f.Context() with { Emit = events.Add }, f.Call());
        Assert.DoesNotContain(events, e => Json.Serialize(e).Contains("waiting for local audio slot"));
        var independent = new Session { Workspace = f.Session.Workspace, Runs = [new()] };
        await f.Tools.ExecuteAsync(new(independent, independent.Runs[0], f.Store, default), f.Call());
        Assert.Equal(2, f.Backend.Calls);
    }

}
