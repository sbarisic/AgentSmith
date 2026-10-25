using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AgentSmith.Cli;
using AgentSmith.Core;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class SimpleRecordingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "smith-simple-recording-" + Guid.NewGuid().ToString("N"));
    private readonly CliApp app;
    private readonly Session session;
    public SimpleRecordingTests()
    {
        app = new(root, true, true, new StringReader("")); session = new() { Workspace = root };
        app.Store.Save(session, new("created", session.Id, null, new { }));
        app.Registry.Recordings.StartService = (_, _) => throw new Exception("Offline test must not start the service");
        app.Registry.Recordings.Summarize = (_, _, _) => throw new Exception("Cached test must not call a provider");
    }
    private (string Capture, string Job) Saved(string state = "Completed", string captureState = "Completed")
    {
        var capture = new CaptureManifest { Id = Guid.NewGuid().ToString("N"), State = captureState, Frames = 16000, SampleRate = 16000, Device = new("endpoint", "Test headphones", true) };
        var dir = Path.Combine(root, "audio", "recordings", capture.Id); Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "manifest.json"); File.WriteAllText(path, JsonSerializer.Serialize(capture));
        using var analysis = new RecordingAnalysis(root);
        var job = new RecordingAnalysisJob { SourceKind = "capture", Source = capture.Id, SourceHash = RecordingAnalysisStore.Hash(path), Frames = 16000, State = state, Stage = "Done",
            Chunks = [new() { CoreEnd = 16000, Components = new() { ["transcription"] = "Succeeded", ["diarization"] = "Succeeded" } }] };
        Directory.CreateDirectory(analysis.Store.DirectoryFor(job.Id)); analysis.Store.Save(job);
        File.WriteAllText(analysis.Store.FileFor(job.Id, "summary.md"), "A retained summary [c000000s00000 00:00.0–00:01.0]");
        File.WriteAllText(analysis.Store.FileFor(job.Id, "transcript.jsonl"), "{\"id\":\"c000000s00000\",\"text\":\"test speech\",\"startSeconds\":0,\"endSeconds\":1}\n");
        File.WriteAllText(analysis.Store.FileFor(job.Id, "coverage.json"), "{}");
        foreach (var name in new[] { "speakers.jsonl", "sounds.jsonl" }) File.WriteAllText(analysis.Store.FileFor(job.Id, name), "");
        return (capture.Id, job.Id);
    }
    private ToolCall Call(string id = "", string? callId = null) => new() { Id = callId ?? Guid.NewGuid().ToString("N"), Name = "audio_analyze", Arguments = Json.Serialize(new { captureId = id, sounds = false, language = "auto", resume = false, retryFailed = false, retryUncertain = false }) };
    private async Task<ToolOutput> Execute(ToolCall call)
    {
        var ctx = app.Context(session); call.Prepared = app.Registry.Prepare(ctx, call); return await app.Registry.ExecuteAsync(ctx, call);
    }
    [Fact]
    public async Task SelectedRecordingReusesOfflineAndAttachesOnceAcrossCallsAndReload()
    {
        var saved = Saved(); RecordingCoordinator.Select(app.Context(session), saved.Capture);
        var call = Call(); var first = await Execute(call); var second = await Execute(call);
        Assert.Equal(first.Content, second.Content); Assert.True(JsonNode.Parse(first.Content)!["reused"]!.GetValue<bool>());
        await Execute(Call()); Assert.Single(session.Artifacts);
        var reloaded = app.Store.Get(session.Id)!; Assert.Equal(saved.Capture, reloaded.SelectedCaptureId); Assert.Equal(saved.Job, reloaded.SelectedAnalysisId);
        Assert.Single(reloaded.AnalysisArtifacts);
    }
    [Fact]
    public void CaptureSelectionQueuedDuringWorkSurvivesReloadWithoutReplacingRunState()
    {
        var saved = Saved(); var run = new Run { State = RunState.Completed }; session.Runs.Add(run);
        app.Store.Save(session, new("run.outcome", session.Id, run.Id, new { }));
        app.Store.QueueCaptureSelection(session.Id, saved.Capture);
        Assert.Null(app.Store.Get(session.Id)!.SelectedCaptureId);
        var reloaded = app.Store.Get(session.Id)!; app.SetConversation(reloaded);
        Assert.Equal(saved.Capture, app.Store.Get(session.Id)!.SelectedCaptureId);
        Assert.Equal(RunState.Completed, Assert.Single(app.Store.Get(session.Id)!.Runs).State);
        Assert.Null(app.Store.PendingCaptureSelection(session.Id));
    }
    [Fact]
    public async Task ExternalRecordingRequiresSelectionAndAnswerGrantsOnlyThatRecording()
    {
        var saved = Saved(); Saved(); var result = await Execute(Call(saved.Capture));
        Assert.NotNull(result.Question); Assert.Empty(session.Artifacts); Assert.Null(session.SelectedCaptureId);
        session.Runs.Add(new() { Question = result.Question }); result.Question.Answer = result.Question.Choices[0];
        result = await Execute(Call()); Assert.Null(result.Question); Assert.Equal(saved.Capture, session.SelectedCaptureId); Assert.Single(session.Artifacts);
    }
    [Fact]
    public async Task InterruptedRecordingRequiresSeparateConsent()
    {
        var saved = Saved("CompletedWithGaps", "Interrupted"); RecordingCoordinator.Select(app.Context(session), saved.Capture);
        var result = await Execute(Call()); Assert.NotNull(result.Question); Assert.Contains("Missing audio", result.Question.Text); Assert.Empty(session.Artifacts);
        session.Runs.Add(new() { Question = result.Question }); result.Question.Answer = result.Question.Choices[0];
        Assert.Null((await Execute(Call())).Question); Assert.Single(session.Artifacts);
    }
    [Fact]
    public async Task DeletedSelectionNeverFallsBackToAnotherRecording()
    {
        var saved = Saved(); Saved(); RecordingCoordinator.Select(app.Context(session), saved.Capture);
        Directory.Delete(Path.Combine(root, "audio", "recordings", saved.Capture), true);
        Assert.Contains("deleted", (await Execute(Call())).Content); Assert.Empty(session.Artifacts);
    }
    [Fact]
    public async Task ChangedManifestRejectsKnownOperationWithoutReplaying()
    {
        var saved = Saved(); RecordingCoordinator.Select(app.Context(session), saved.Capture);
        session.AnalysisOperations["known"] = saved.Job;
        File.AppendAllText(Path.Combine(root, "audio", "recordings", saved.Capture, "manifest.json"), " ");
        Assert.Contains("changed", (await Execute(Call(callId: "known"))).Content); Assert.Empty(session.Artifacts);
    }
    [Fact]
    public async Task PausedJobNeedsExplicitResumeAndCrashRecoveryDoesNotRunInference()
    {
        var saved = Saved("Paused"); RecordingCoordinator.Select(app.Context(session), saved.Capture);
        Assert.Contains("resume", (await Execute(Call())).Content, StringComparison.OrdinalIgnoreCase);
        session.AnalysisOperations["crashed"] = saved.Job;
        var recovered = app.Registry.Recover(app.Context(session), Call(callId: "crashed"));
        Assert.Contains("without replay", recovered!.Content); Assert.Single(session.Artifacts);
    }
    [Fact]
    public async Task MetadataDoesNotGrantEvidenceAndWorkersCannotAnalyze()
    {
        Saved(); var list = new ToolCall { Name = "audio_recordings", Arguments = "{\"page\":1}" };
        var result = await Execute(list); Assert.Contains("Test headphones", result.Content); Assert.DoesNotContain("test speech", result.Content); Assert.Empty(session.Artifacts);
        session.ParentConversationId = "parent";
        Assert.Throws<UserFacingException>(() => app.Registry.Prepare(app.Context(session), Call()));
        await Assert.ThrowsAsync<UserFacingException>(() => app.Registry.Recordings.Execute(app.Context(session), Call()));
    }
    [Fact]
    public async Task SlashSummaryAndSelectedViewsWorkWithoutServiceOrExtraModelTurn()
    {
        var saved = Saved(); Assert.True(await app.SimpleRecordingCommand(["summarize", saved.Capture], session));
        Assert.Single(session.Artifacts); Assert.Empty(session.Runs);
        Assert.True(await app.SimpleRecordingCommand(["transcript"], session)); Assert.True(await app.SimpleRecordingCommand(["coverage"], session));
        using var analysis = new RecordingAnalysis(root); analysis.Store.Delete(saved.Job);
        Assert.True(await app.SimpleRecordingCommand(["transcript"], session));
    }
    [Fact]
    public async Task SummaryOnlyUsesRetainedEvidenceWithoutInferenceAndConcurrentCallsShareResult()
    {
        var saved = Saved(); RecordingCoordinator.Select(app.Context(session), saved.Capture);
        using var analysis = new RecordingAnalysis(root); File.Delete(analysis.Store.FileFor(saved.Job, "summary.md"));
        var requests = 0;
        app.Registry.Recordings.Summarize = async (_, _, ct) => { Interlocked.Increment(ref requests); await Task.Delay(100, ct); return "{\"items\":[{\"text\":\"Test speech\",\"segments\":[\"c000000s00000\"]}]}"; };
        var other = new Session { Workspace = root, SelectedCaptureId = saved.Capture }; app.Store.Save(other, new("created", other.Id, null, new { }));
        var both = await Task.WhenAll(app.Registry.Recordings.Analyze(app.Context(session), saved.Capture, "first"), app.Registry.Recordings.Analyze(app.Context(other), saved.Capture, "second"));
        Assert.Equal(1, requests); Assert.All(both, r => Assert.Contains("Test speech", r.Content)); Assert.Single(session.Artifacts); Assert.Single(other.Artifacts);
    }
    private sealed class Model(string arguments) : IModelClient
    {
        public int Requests;
        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield(); ct.ThrowIfCancellationRequested();
            if (++Requests == 1) yield return new("completed", Output: [new() { ["type"] = "function_call", ["call_id"] = "analysis", ["name"] = "audio_analyze", ["arguments"] = arguments }]);
            else yield return new("completed", Output: [new() { ["role"] = "assistant", ["content"] = "Retained test speech [00:00–00:01]." }]);
        }
    }
    [Fact]
    public async Task NaturalLanguageRunAnalyzesSelectionAndFinishesWithoutManualAttachment()
    {
        var saved = Saved(); RecordingCoordinator.Select(app.Context(session), saved.Capture);
        var model = new Model(Call().Arguments); app.ModelOverride = model;
        var run = await app.Run(session, "Summarize that recording.", null, default);
        Assert.Equal(RunState.Completed, run.State); Assert.Equal(2, model.Requests); Assert.Single(session.Artifacts);
        Assert.Equal("audio_analyze", Assert.Single(run.Calls).Name);
    }
    [Fact]
    public async Task CaptureStartStopAndNaturalLanguageSummaryShareSelectionThroughRealTransport()
    {
        await using var host = new AgentSmith.ProcessFixture.RecordingTestHost(root);
        await app.HandleCommand("/audio capture start", session);
        var selected = session.SelectedCaptureId; Assert.NotNull(selected); Assert.Empty(session.Artifacts);
        await app.HandleCommand("/audio capture stop", session); Assert.Equal(selected, session.SelectedCaptureId);
        using var analysis = new RecordingAnalysis(root); var saved = Saved();
        var job = analysis.Store.Read(saved.Job); job.Source = selected!;
        var manifestPath = Path.Combine(root, "audio", "recordings", selected!, "manifest.json");
        var manifest = JsonSerializer.Deserialize<CaptureManifest>(File.ReadAllText(manifestPath))!;
        job.SourceHash = RecordingAnalysisStore.Hash(manifestPath); job.Frames = (long)(manifest.DurationSeconds * 16000);
        job.Chunks[0].CoreEnd = job.Frames; analysis.Store.Save(job);
        var model = new Model(Call().Arguments); app.ModelOverride = model;
        Assert.Equal(RunState.Completed, (await app.Run(session, "Summarize that recording.", null, default)).State);
        Assert.Equal(selected, session.SelectedCaptureId); Assert.Equal(saved.Job, session.SelectedAnalysisId); Assert.Single(session.Artifacts);
    }
    [Fact]
    public void NormalToolDisplayOmitsRawEvidenceButExplicitDetailRemainsAvailable()
    {
        var writer = new StringWriter(); var renderer = new TerminalRenderer(writer, false);
        renderer.Render("tool.completed", new { name = "audio_read", content = Json.Serialize(new { artifactId = "artifact", view = "coverage", text = "SECRET_RAW_EVIDENCE" }) });
        Assert.DoesNotContain("SECRET_RAW_EVIDENCE", writer.ToString()); Assert.Contains("Audio evidence read", writer.ToString());
        renderer.Render("artifact.result", new { content = Json.Serialize(new { artifactId = "artifact", text = "SECRET_RAW_EVIDENCE" }) });
        Assert.Contains("SECRET_RAW_EVIDENCE", writer.ToString());
        Assert.Equal("[01:00.0–01:02.0]", RecordingCoordinator.Display("[c000000s00000 01:00.0–01:02.0]"));
    }
    private sealed class Input : TextReader
    {
        public readonly Channel<string?> Lines = Channel.CreateUnbounded<string?>();
        public override Task<string?> ReadLineAsync() => Lines.Reader.ReadAsync().AsTask();
    }
    [Fact]
    public async Task ConsoleSignalClearsWholeDraftWithoutDeletingHistoryAndKeepsEof()
    {
        var reader = new Input(); var input = new ConsoleInput(false, reader);
        input.AcceptPrompt(UserPrompt.Text("sent"), false); input.PasteText("pasted\nblock");
        input.AddImage(new() { Id = "image", Width = 1, Height = 1 });
        var read = input.ReadLineAsync(default); await Task.Delay(30); input.SignalControlC();
        Assert.Equal("", await read.WaitAsync(TimeSpan.FromSeconds(2))); Assert.Equal("", input.Draft); Assert.False(input.HasBlocks); Assert.Equal(1, input.HistoryCount);
        reader.Lines.Writer.TryWrite(null); Assert.Null(await input.ReadLineAsync(default)); Assert.True(input.EndOfInput);
    }
    [Fact]
    public async Task QuestionTextFallbackIgnoresControlC()
    {
        var reader = new Input(); var input = new ConsoleInput(false, reader) { AllowIdleClear = false };
        input.Restore("answer draft"); var read = input.ReadLineAsync(default); await Task.Delay(30); input.SignalControlC(); await Task.Delay(60);
        Assert.False(read.IsCompleted); Assert.Equal("answer draft", input.Draft);
        reader.Lines.Writer.TryWrite("answer"); Assert.Equal("answer", await read);
    }
    public void Dispose() { app.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
}
