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
        public int Calls;
        public string? Snapshot;
        public Func<JsonObject, CancellationToken, Task<JsonObject>> Work = (_, _) => Task.FromResult(Result());
        public AudioReadiness Status(string dataDirectory) => new(Ready, Ready, dataDirectory, "Run smith audio setup.");
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
    public async Task CancellationCleansSnapshotAndReleasesGlobalSlot()
    {
        using var f = new Fixture(); using var cancel = new CancellationTokenSource();
        f.Backend.Work = async (_, ct) => { cancel.Cancel(); await Task.Delay(10000, ct); return Result(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Tools.ExecuteAsync(f.Context(cancel.Token), f.Call()));
        Assert.False(File.Exists(f.Backend.Snapshot)); Assert.Empty(f.Session.Artifacts); Assert.Empty(f.Session.Receipts);
        f.Backend.Work = (_, _) => Task.FromResult(Result());
        await f.Tools.ExecuteAsync(f.Context(), f.Call()); Assert.Single(f.Session.Receipts);
    }

    [Fact]
    public async Task ParentAndWorkerRegistriesSerializeAudio()
    {
        using var parent = new Fixture(); using var worker = new Fixture(); worker.Session.ParentConversationId = parent.Session.Id;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        parent.Backend.Work = async (_, ct) => { entered.SetResult(); await release.Task.WaitAsync(ct); return Result(); };
        var first = parent.Tools.ExecuteAsync(parent.Context(), parent.Call()); await entered.Task;
        var second = worker.Tools.ExecuteAsync(worker.Context(), worker.Call());
        Assert.Equal(0, worker.Backend.Calls); release.SetResult(); await Task.WhenAll(first, second);
        Assert.Equal(1, worker.Backend.Calls);
    }

    [Fact]
    public async Task PartialFailureRetainsSuccessfulComponentAndAllFailureRetainsNothing()
    {
        using var f = new Fixture();
        f.Backend.Work = (_, _) => { var result = Result(); result["classification"] = JsonNode.Parse("""{"status":"failed","error":"missing model"}"""); return Task.FromResult(result); };
        var output = await f.Tools.ExecuteAsync(f.Context(), f.Call()); Assert.Contains("Classification failed", output.Content); Assert.Contains("Hello world", output.Content);
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
}
