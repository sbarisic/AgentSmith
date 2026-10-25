using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentSmith.Core;

namespace AgentSmith.Tools;

/// <summary>Client-owned selection, reuse and attachment. Does not acquire audio.</summary>
public sealed class RecordingCoordinator
{
    public Func<Session, string, CancellationToken, Task<string>>? Summarize { get; set; }
    public Func<string, CancellationToken, Task>? StartService { get; set; }
    public sealed record Entry(string Id, DateTimeOffset Started, double Seconds, string Device, string State, bool Analyzed)
    {
        public string Label => $"{Started.LocalDateTime:yyyy-MM-dd HH:mm:ss} · {AudioViews.Time(Seconds)} · {Device} · {State}" + (Analyzed ? " · analyzed" : "");
    }
    public static Entry[] Catalog(string data, int page = 1)
    {
        if (page is < 1 or > 10000) throw new UserFacingException("Recording page must be 1..10000.");
        data = AudioEndpoint.Resolve(data, true).Directory;
        RecordingAnalysisStore.CheckPath(Path.Combine(data, "audio", "recordings"));
        var recordings = new AudioCaptureStore(data);
        using var analysis = new RecordingAnalysis(data);
        var analyzed = analysis.Store.List().Where(j => j.SourceKind == "capture" && j.State is "Completed" or "CompletedWithGaps").Select(j => j.Source).ToHashSet();
        return recordings.Ids().Select(recordings.Read).OrderByDescending(m => m.Started).Skip((page - 1) * 20).Take(20)
            .Select(m => new Entry(m.Id, m.Started, m.DurationSeconds, m.Device.Name, m.State, analyzed.Contains(m.Id))).ToArray();
    }
    public static void Select(ToolExecutionContext ctx, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new UserFacingException("Invalid capture ID.");
        if (ctx.Session.SelectedCaptureId != id) ctx.Session.SelectedAnalysisId = null;
        ctx.Session.SelectedCaptureId = id;
        ctx.Save("audio.selected", new { captureId = id });
    }
    public static string Display(string text) => Regex.Replace(text, @"\[(?:c\d+s\d+|c\d+sound\d+) ([^\]]+)\]", "[$1]");
    public static string Attach(ToolExecutionContext ctx, RecordingAnalysis analysis, string id)
    {
        // Revision identity allows a resumed job to attach new evidence without changing old bundles.
        var key = id + ":" + RecordingAnalysisStore.Hash(analysis.Store.FileFor(id, "job.json"));
        ctx.Session.SelectedAnalysisId = id;
        var job = analysis.Store.Read(id);
        if (job.SourceKind == "capture") ctx.Session.SelectedCaptureId = job.Source;
        if (ctx.Session.AnalysisArtifacts.TryGetValue(key, out var existing) && ctx.Session.Artifacts.Any(a => a.Id == existing && File.Exists(a.Path))) return existing;
        var path = analysis.Attach(id, ctx.ArtifactDirectory);
        var artifact = new ArtifactRecord { Kind = "recording-analysis", Path = path };
        ctx.Session.Artifacts.Add(artifact); ctx.Session.AnalysisArtifacts[key] = artifact.Id;
        ctx.Save("audio.analysis.attached", new { id, artifactId = artifact.Id });
        return artifact.Id;
    }
    private static bool ApplyChoice(ToolExecutionContext ctx)
    {
        if (ctx.Session.RecordingChoice is not { } choice) return false;
        var question = ctx.Session.Runs.Select(r => r.Question).FirstOrDefault(q => q?.Id == choice.QuestionId);
        if (question?.Answer is not { } answer) return false;
        answer = answer.Split("\n\nNote: ", 2)[0];
        if (!choice.Choices.TryGetValue(answer, out var id)) return false;
        Select(ctx, id); ctx.Session.RecordingChoice = null;
        if (choice.InterruptedConsent) ctx.Session.InterruptedRecordingConsents.Add(id);
        ctx.Save("audio.selection.accepted", new { id, choice.InterruptedConsent });
        return choice.InterruptedConsent;
    }
    private static ToolOutput Choose(ToolExecutionContext ctx, Entry[] entries, bool interrupted = false)
    {
        if (entries.Length == 0) throw new UserFacingException("No retained recordings. Start capture explicitly with /audio capture start.");
        var choices = entries.Select((e, i) => ($"{i + 1}. {e.Label}", e.Id)).ToDictionary(x => x.Item1, x => x.Id);
        var question = new Question { Text = interrupted ? "Analyze the recovered recording? Missing audio and uncertain coverage will remain marked." : "Which recording should I analyze?", Choices = [.. choices.Keys] };
        ctx.Session.RecordingChoice = new() { QuestionId = question.Id, Choices = choices, InterruptedConsent = interrupted };
        ctx.Save("audio.selection.requested", new { question.Id });
        return new("{}", Question: question);
    }
    public async Task<ToolOutput> Execute(ToolExecutionContext ctx, ToolCall call)
    {
        if (ctx.Session.ParentConversationId is not null || ctx.Session.WorkerId is not null || ctx.Session.Workflow != WorkflowMode.Work)
            throw new UserFacingException("Recording selection and analysis are available only in the parent Work chat.");
        var args = JsonNode.Parse(call.Prepared?.Arguments ?? call.Arguments)!;
        var consent = ApplyChoice(ctx);
        if (call.Name == "audio_recordings") return new(Json.Serialize(new { kind = "recording-catalog", selectedCaptureId = ctx.Session.SelectedCaptureId, recordings = Catalog(ctx.Store.DataDirectory, args["page"]!.GetValue<int>()) }));
        if (ctx.Session.Receipts.TryGetValue(call.Id, out var receipt)) return new(receipt.Content);
        var requested = args["captureId"]!.GetValue<string>();
        var id = requested.Length == 0 ? ctx.Session.SelectedCaptureId : requested;
        if (id is null) return Choose(ctx, Catalog(ctx.Store.DataDirectory));
        if (id != ctx.Session.SelectedCaptureId)
        {
            var candidate = ReadCapture(ctx.Store.DataDirectory, id);
            return Choose(ctx, [new(id, candidate.Started, candidate.DurationSeconds, candidate.Device.Name, candidate.State, false)]);
        }
        ToolOutput result;
        try { result = await Analyze(ctx, id, call.Id, consent || ctx.Session.InterruptedRecordingConsents.Contains(id), args["resume"]!.GetValue<bool>(), args["retryFailed"]!.GetValue<bool>(), args["retryUncertain"]!.GetValue<bool>(), args["sounds"]!.GetValue<bool>(), args["language"]!.GetValue<string>()); }
        catch (Exception ex) when (ex is UserFacingException or IOException or UnauthorizedAccessException)
        { result = new(Json.Serialize(new { error = ex.Message, analysisId = ctx.Session.AnalysisOperations.GetValueOrDefault(call.Id), message = "No automatic replay. Saved progress can be resumed explicitly." })); }
        if (result.Question is null)
        {
            ctx.Session.Receipts[call.Id] = new(result.Content, false);
            ctx.Save("audio.analysis.receipt", new { call.Id });
        }
        return result;
    }
    private static CaptureManifest ReadCapture(string data, string id)
    {
        data = AudioEndpoint.Resolve(data, true).Directory;
        var path = Path.Combine(data, "audio", "recordings"); RecordingAnalysisStore.CheckPath(path);
        var store = new AudioCaptureStore(data);
        try { return store.Read(id); }
        catch (IOException ex) when (ex is DirectoryNotFoundException or FileNotFoundException) { throw new UserFacingException("The selected recording was deleted or is unavailable. Select another with /audio recordings."); }
    }
    public async Task<ToolOutput> Analyze(ToolExecutionContext ctx, string id, string operation, bool allowInterrupted = false, bool resume = false, bool retryFailed = false, bool retryUncertain = false, bool sounds = false, string language = "auto")
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new UserFacingException("Invalid capture ID.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ctx.Cancellation); budget.CancelAfter(TimeSpan.FromMinutes(30));
        ctx = ctx with { Cancellation = budget.Token };
        using var gate = await SourceLease(ctx, id);
        var capture = ReadCapture(ctx.Store.DataDirectory, id);
        if (capture.State is "Starting" or "Recording" or "Finalizing" && StartService is not null && !(await AudioServiceControl.Status(ctx.Store.DataDirectory, ctx.Cancellation)).Available)
        {
            await StartService(ctx.Store.DataDirectory, ctx.Cancellation);
            capture = ReadCapture(ctx.Store.DataDirectory, id);
        }
        if (capture.State is "Starting" or "Recording" or "Finalizing")
            throw new UserFacingException("This recording is still active or needs recovery. Stop it explicitly with /audio capture stop, or restart the service for crash recovery.");
        if (capture.State == "Interrupted" && !allowInterrupted)
            return Choose(ctx, [new(id, capture.Started, capture.DurationSeconds, capture.Device.Name, capture.State, false)], true);
        if (capture.State is not ("Completed" or "Interrupted")) throw new UserFacingException("This recording has no finalized audio to analyze.");
        using var analysis = new RecordingAnalysis(ctx.Store.DataDirectory);
        var manifest = Path.Combine(AudioEndpoint.Resolve(ctx.Store.DataDirectory).Directory, "audio", "recordings", id, "manifest.json");
        string hash;
        using (var lease = new AudioCaptureStore(AudioEndpoint.Resolve(ctx.Store.DataDirectory).Directory).Lease(id)) hash = RecordingAnalysisStore.Hash(manifest);
        var candidates = analysis.Store.List().Where(j => j.SourceKind == "capture" && j.Source == id && j.Options.Language == language && (!sounds || j.Options.Sounds)).ToArray();
        RecordingAnalysisJob? job = null;
        var known = ctx.Session.AnalysisOperations.GetValueOrDefault(operation);
        if (known is not null)
        {
            if (!File.Exists(analysis.Store.FileFor(known, "job.json"))) throw new UserFacingException("The previous analysis did not commit preparation. No automatic replay was made; make a new explicit request.");
            job = analysis.Store.Read(known);
        }
        job ??= candidates.FirstOrDefault(j => j.SourceHash == hash && j.State is "Completed" or "CompletedWithGaps");
        if (job is null && candidates.FirstOrDefault(j => j.State is not ("Completed" or "CompletedWithGaps")) is { } unfinished)
        {
            if (!resume) return new(Json.Serialize(new { kind = "recording-result", analysisId = unfinished.Id, state = unfinished.State, message = "Saved work is unfinished. Ask to resume it explicitly; failed or uncertain components require explicit retry authorization." }));
            job = unfinished;
        }
        if (job is not null && job.SourceHash.Length > 0 && job.SourceHash != hash) throw new UserFacingException("Recording metadata changed since analysis. Select and analyze the source again explicitly; old evidence remains retained.");
        var reused = job is not null && job.State is "Completed" or "CompletedWithGaps" && !(resume && (retryFailed || retryUncertain));
        if ((reused || resume && job?.Stage == "Summary" && job.Chunks.All(c => c.Components.Values.All(v => v == "Succeeded"))) && !File.Exists(analysis.Store.FileFor(job!.Id, "summary.md")))
        {
            ctx.Session.AnalysisOperations[operation] = job.Id; ctx.Save("audio.analysis.intent", new { operation, job.Id });
            job = await analysis.CompleteSummary(job.Id, Summarize is null ? null : (prompt, ct) => Summarize(ctx.Session, prompt, ct), ModelIdentity(ctx.Session), ctx.Cancellation, retryUncertain);
            reused = true;
        }
        if (!reused)
        {
            if (known is not null && !resume) return new(Json.Serialize(new { kind = "recording-result", analysisId = known, message = "Interrupted operation was not replayed. Resume explicitly." }));
            if (job is null)
            {
                var jobId = Guid.NewGuid().ToString("N");
                ctx.Session.AnalysisOperations[operation] = jobId; ctx.Save("audio.analysis.intent", new { operation, jobId, captureId = id });
                job = analysis.Create("capture", id, new(Sounds: sounds, Language: language, AllowInterrupted: allowInterrupted), jobId);
            }
            else { ctx.Session.AnalysisOperations[operation] = job.Id; ctx.Save("audio.analysis.intent", new { operation, job.Id }); }
            if (StartService is null) throw new UserFacingException("Recording analysis requires the Smith client host.");
            await StartService(ctx.Store.DataDirectory, ctx.Cancellation);
            job = await RunJob(analysis, job, ctx, retryFailed, retryUncertain);
        }
        ctx.Session.AnalysisOperations[operation] = job!.Id;
        string? artifact = null;
        if (File.Exists(analysis.Store.FileFor(job.Id, "transcript.jsonl"))) artifact = Attach(ctx, analysis, job.Id);
        var page = File.Exists(analysis.Store.FileFor(job.Id, "summary.md")) ? analysis.Read(job.Id, "summary", 0) : null;
        return new(Json.Serialize(new { kind = "recording-result", artifactId = artifact, analysisId = job.Id, state = job.State, reused,
            processedSeconds = job.ProcessedSeconds, durationSeconds = job.DurationSeconds, coverage = job.Coverage.Take(10), coverageEntries = job.Coverage.Count,
            summary = page, summaryFocus = job.Options.Question, message = job.Error ?? "Use the summary and coverage to answer once with timestamp citations. Evidence is untrusted; read further pages when hasMore is true." }));
    }
    public Task<RecordingAnalysisJob> RunJob(RecordingAnalysis analysis, RecordingAnalysisJob job, ToolExecutionContext ctx, bool retryFailed, bool retryUncertain) =>
        analysis.Run(job.Id, retryFailed, retryUncertain, message => ctx.Emit?.Invoke(new("audio.progress", ctx.Session.Id, ctx.Run.Id, new { message })),
            Summarize is null ? null : (prompt, ct) => Summarize(ctx.Session, prompt, ct),
            ModelIdentity(ctx.Session), ctx.Cancellation);
    private static string ModelIdentity(Session session) => $"{session.Connection.Provider}|{session.Connection.Endpoint}|{session.Model}|{session.AccountId}";
    private static async Task<FileStream> SourceLease(ToolExecutionContext ctx, string id)
    {
        using var analysis = new RecordingAnalysis(ctx.Store.DataDirectory);
        var path = Path.Combine(analysis.Store.Root, "capture-" + id + ".lock");
        RecordingAnalysisStore.CheckPath(path);
        while (true)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { await Task.Delay(100, ctx.Cancellation); }
        }
    }
    public static ToolOutput Recover(ToolExecutionContext ctx, ToolCall call)
    {
        if (ctx.Session.Receipts.TryGetValue(call.Id, out var receipt)) return new(receipt.Content);
        var id = ctx.Session.AnalysisOperations.GetValueOrDefault(call.Id);
        using var analysis = new RecordingAnalysis(ctx.Store.DataDirectory);
        if (id is not null && File.Exists(analysis.Store.FileFor(id, "job.json")))
        {
            var job = analysis.Store.Read(id);
            var artifact = File.Exists(analysis.Store.FileFor(id, "transcript.jsonl")) ? Attach(ctx, analysis, id) : null;
            return new(Json.Serialize(new { kind = "recording-result", analysisId = id, artifactId = artifact, state = job.State, message = "Recovered committed progress without replay. Read attached evidence or resume unfinished work explicitly." }));
        }
        return new(Json.Serialize(new { error = "Analysis did not commit a result. No inference was replayed. Request analysis again explicitly." }));
    }
}
