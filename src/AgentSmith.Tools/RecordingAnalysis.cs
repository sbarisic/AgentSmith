using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Core;

namespace AgentSmith.Tools;

public sealed partial class RecordingAnalysis : IDisposable
{
    private readonly string data;
    public RecordingAnalysisStore Store { get; }
    private readonly ILocalAudioBackend backend;
    public RecordingAnalysis(string dataDirectory, ILocalAudioBackend? backend = null)
    { data = AudioEndpoint.Resolve(dataDirectory, true).Directory; Store = new(data); this.backend = backend ?? new LocalAudioBackend(); }
    public void Dispose() => (backend as IDisposable)?.Dispose();
    public static void ValidateOptions(RecordingAnalysisOptions options)
    {
        if (!AudioTool.ValidLanguage(options.Language) || options.MaxMinutes is < 1 or > 1440 || options.MaxRequests is < 1 or > 10000 ||
            !double.IsFinite(options.MaxStorageGiB) || options.MaxStorageGiB is < .01 or > 100000 || options.Question?.Length > 4096)
            throw new UserFacingException("Invalid analysis options: minutes 1..1440, requests 1..10000, storage .01..100000 GiB, supported language, question up to 4096 characters.");
    }
    public RecordingAnalysisJob Create(string kind, string source, RecordingAnalysisOptions options, string? id = null)
    {
        ValidateOptions(options);
        if (kind is not ("file" or "capture")) throw new UserFacingException("Use analyze file <path> or analyze capture <id>.");
        if (kind == "file")
        {
            source = Path.GetFullPath(source); RecordingAnalysisStore.CheckPath(source);
            if (!File.Exists(source) || Path.GetExtension(source).ToLowerInvariant() is not (".wav" or ".mp3")) throw new UserFacingException("Supply an existing WAV or MP3 file.");
        }
        else if (!Guid.TryParseExact(source, "N", out _)) throw new UserFacingException("Invalid capture ID.");
        var installation = backend.Status(data);
        if (!installation.Transcription || !installation.Diarization) throw new UserFacingException("Transcription and speaker engines are required. Run smith audio setup.");
        using var installationLease = LocalAudioInstallation.Use(installation.Location);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(installation.Location, "helper", "downloads.json")))!;
        if (manifest["recordingAnalysis"]?.GetValue<int>() != 1) throw new UserFacingException("The installed helper needs updating for recording analysis. Run smith audio setup.");
        var job = new RecordingAnalysisJob { SourceKind = kind, Source = source, Options = options,
            Installation = Path.GetFileName(installation.Location), Fingerprint = LocalAudioInstallation.Fingerprint(Path.Combine(installation.Location, "helper")),
            DecoderHash = RecordingAnalysisStore.Hash(Path.Combine(LocalAudioInstallation.Bundle, "recording_decode.py")) };
        if (id is not null) job.Id = id;
        if (job.Options.Question is { } question && MemoryPolicy.HasSecret(question)) job.Options = options with { Question = "[redacted]" };
        Store.CheckBudget(options.MaxStorageGiB);
        Directory.CreateDirectory(Store.DirectoryFor(job.Id));
        File.Copy(Path.Combine(LocalAudioInstallation.Bundle, "recording_decode.py"), Store.FileFor(job.Id, "decoder.py"));
        Store.Save(job); return job;
    }
    public async Task<RecordingAnalysisJob> Run(string id, bool retryFailed, bool retryUncertain, Action<string> progress,
        Func<string, CancellationToken, Task<string>>? summarize, string? modelIdentity, CancellationToken cancellation,
        int? maxMinutes = null, int? maxRequests = null, double? maxStorageGiB = null)
    {
        using var lease = Store.Lease(id);
        var job = Store.Read(id);
        job.Options = job.Options with { MaxMinutes = maxMinutes ?? job.Options.MaxMinutes, MaxRequests = maxRequests ?? job.Options.MaxRequests, MaxStorageGiB = maxStorageGiB ?? job.Options.MaxStorageGiB };
        ValidateOptions(job.Options);
        if (job.State == "Completed") return job;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(job.Options.MaxMinutes));
        var ct = deadline.Token; var timer = Stopwatch.StartNew();
        async Task Timed(string stage, Func<Task> work)
        {
            var elapsed = Stopwatch.StartNew();
            try { await work(); }
            finally { job.Timings[stage] = job.Timings.GetValueOrDefault(stage) + elapsed.Elapsed.TotalSeconds; }
        }
        job.State = "Running"; job.Error = null; Store.Save(job);
        try
        {
            if (job.Reconciliation != "titanet-cosine-v1;threshold=.75;margin=.10")
                throw new UserFacingException("This build cannot resume the pinned speaker reconciliation version. Use the original build.");
            var location = Path.Combine(data, "audio", "installations", job.Installation);
            if (!Directory.Exists(location) || LocalAudioInstallation.Fingerprint(Path.Combine(location, "helper")) != job.Fingerprint)
                throw new UserFacingException("The pinned audio installation is missing or changed. Restore that generation; this job will not substitute another model.");
            var decoder = Store.FileFor(id, "decoder.py");
            if (!File.Exists(decoder)) decoder = Path.Combine(LocalAudioInstallation.Bundle, "recording_decode.py");
            if (RecordingAnalysisStore.Hash(decoder) != job.DecoderHash)
                throw new UserFacingException("The pinned decoder changed. Use the original build to resume, or create a new analysis.");
            if (!job.Prepared) await Timed("preparationSeconds", () => Prepare(job, location, progress, ct));
            else
            {
                var preparation = RecordingAnalysisStore.ReadJson<JsonObject>(Store.FileFor(id, "preparation.json"));
                if (RecordingAnalysisStore.Hash(Store.FileFor(id, "normalized.f32")) != preparation["hash"]!.ToString()) throw new IOException("Prepared audio changed; refusing to reuse receipts.");
            }
            foreach (var chunk in job.Chunks)
            {
                ct.ThrowIfCancellationRequested();
                var savedPath = Store.FileFor(id, $"evidence-{chunk.Index:D6}.json");
                if (File.Exists(savedPath))
                {
                    var committed = RecordingAnalysisStore.ReadJson<JsonObject>(savedPath);
                    if (committed["sourceHash"]?.ToString() != chunk.Hash) throw new IOException("Evidence source hash mismatch.");
                    foreach (var component in chunk.Components.Keys.ToArray())
                        if (committed[component]?["status"]?.ToString() == "succeeded") chunk.Components[component] = "Succeeded";
                }
                var needed = chunk.Components.Where(c => c.Value != "Succeeded").Select(c => c.Key).ToArray();
                if (needed.Length == 0) continue;
                if (needed.Any(c => chunk.Components[c] is "Submitted" or "Uncertain") && !retryUncertain || needed.Any(c => chunk.Components[c] == "Failed") && !retryFailed)
                    continue;
                using var space = await Store.Reserve(10 * 1024 * 1024, job.Options.MaxStorageGiB, ct);
                job.Stage = "Inference"; progress($"Analysis {id} · {job.ProcessedSeconds:0}/{job.DurationSeconds:0}s · chunk {chunk.Index + 1}/{job.Chunks.Count}");
                var clip = Store.FileFor(id, "inference.wav");
                using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct); requestDeadline.CancelAfter(TimeSpan.FromMinutes(5));
                var ticks = Environment.TickCount64 + Math.Min(300000, Math.Max(1, (long)(job.Options.MaxMinutes * 60000 - timer.Elapsed.TotalMilliseconds)));
                WriteClip(Store.FileFor(id, "normalized.f32"), clip, chunk);
                var hash = RecordingAnalysisStore.Hash(clip);
                if (chunk.Hash.Length > 0 && chunk.Hash != hash) throw new IOException("Prepared chunk changed; saved evidence cannot be reused.");
                chunk.Hash = hash;
                foreach (var component in needed) chunk.Components[component] = "Submitted";
                Store.Save(job);
                var request = new JsonObject { ["protocol"] = 1, ["operation"] = job.Options.Sounds ? "audio_listen" : "audio_transcribe",
                    ["path"] = clip, ["models"] = Path.Combine(location, "models"), ["sourceHash"] = hash, ["deadlineTicks"] = ticks,
                    ["startSeconds"] = 0, ["durationSeconds"] = 120, ["language"] = job.Options.Language,
                    ["components"] = new JsonArray(needed.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()) };
                var elapsed = Stopwatch.StartNew();
                try
                {
                    var result = await backend.Run(location, request, progress, requestDeadline.Token);
                    Redact(result);
                    var path = Store.FileFor(id, $"evidence-{chunk.Index:D6}.json");
                    var existing = File.Exists(path) ? RecordingAnalysisStore.ReadJson<JsonObject>(path) : new JsonObject();
                    foreach (var component in needed)
                    {
                        existing[component] = result[component]?.DeepClone();
                        chunk.Components[component] = result[component]?["status"]?.ToString() == "succeeded" ? "Succeeded" : "Failed";
                    }
                    existing["service"] = result["service"]?.DeepClone(); existing["sourceHash"] = hash;
                    RecordingAnalysisStore.WriteJson(path, existing);
                    chunk.Seconds += elapsed.Elapsed.TotalSeconds; Store.Save(job);
                }
                catch
                {
                    foreach (var component in needed) chunk.Components[component] = "Uncertain";
                    Store.Save(job); throw;
                }
                finally { if (File.Exists(clip)) File.Delete(clip); }
            }
            ct.ThrowIfCancellationRequested(); job.Stage = "Assembly"; Store.Save(job);
            progress("Assembling transcript and reconciling speakers");
            await Timed("assemblySeconds", () => { Assemble(job, ct); return Task.CompletedTask; });
            var incomplete = job.Chunks.Any(c => c.Components.Values.Any(v => v != "Succeeded"));
            if (!job.Options.NoSummary)
            {
                job.Stage = "Summary"; Store.Save(job);
                if (summarize is null) throw new UserFacingException("Local evidence is saved. Configure a summary model and resume to generate the report.");
                await Timed("summarySeconds", () => Summarize(job, summarize, modelIdentity ?? "unknown", retryUncertain, progress, ct));
            }
            job.State = incomplete || job.Coverage.Any(c => c.Kind != "InferredSilence") ? "CompletedWithGaps" : "Completed";
            job.Stage = "Done";
            if (incomplete) job.Error = "Some components failed or are uncertain. Use explicit --retry-failed / --retry-uncertain on resume.";
        }
        catch (OperationCanceledException) { job.State = "Paused"; job.Error = cancellation.IsCancellationRequested ? "Cancelled; completed evidence retained." : "Execution budget reached; resume explicitly."; }
        catch (Exception ex) when (ex is IOException or UserFacingException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or HttpRequestException)
        { job.State = "Paused"; job.Error = ex.Message; }
        finally { job.ExecutionSeconds += timer.Elapsed.TotalSeconds; Store.Save(job); LocalAudioInstallation.Cleanup(data); }
        return job;
    }
    private async Task Prepare(RecordingAnalysisJob job, string installation, Action<string> progress, CancellationToken ct)
    {
        progress("Preparing immutable source snapshot");
        using var space = await Store.Reserve(null, job.Options.MaxStorageGiB, ct);
        var initialBytes = Store.UsedBytes();
        job.Coverage.Clear();
        var inputs = new JsonArray();
        if (job.SourceKind == "capture")
        {
            var snapshot = Path.Combine(Store.DirectoryFor(job.Id), "source");
            if (!Directory.Exists(snapshot))
            {
                var status = await AudioCaptureControl.Request(data, "status", new() { ["id"] = job.Source }, ct);
                var state = status["State"]?.ToString();
                if (state != "Completed" && !(state == "Interrupted" && job.Options.AllowInterrupted))
                    throw new UserFacingException("Capture must be Completed, or Interrupted with --allow-interrupted.");
                Store.CheckBudget(job.Options.MaxStorageGiB, status["Bytes"]?.GetValue<long>() ?? 0);
                if ((status["Bytes"]?.GetValue<long>() ?? 0) > space.Capacity) throw new UserFacingException("Capture exceeds available reserved analysis storage.");
                await AudioCaptureControl.Export(data, job.Source, snapshot, ct);
            }
            var manifestPath = Path.Combine(snapshot, "manifest.json");
            var manifest = RecordingAnalysisStore.ReadJson<CaptureManifest>(manifestPath);
            ValidateCapture(manifest, job.Source, job.Options.AllowInterrupted);
            long previous = 0;
            foreach (var chunk in manifest.Chunks)
            {
                ct.ThrowIfCancellationRequested();
                var path = Path.Combine(snapshot, chunk.File);
                if (new FileInfo(path).Length != chunk.Bytes || RecordingAnalysisStore.Hash(path) != chunk.Hash) throw new IOException("Capture chunk hash or length mismatch.");
                if (chunk.StartFrame > previous) job.Coverage.Add(new(previous * 16000 / manifest.SampleRate, (chunk.StartFrame - previous) * 16000 / manifest.SampleRate, "Missing"));
                var first = (long)Math.Round(chunk.StartFrame * 16000d / manifest.SampleRate);
                inputs.Add(new JsonObject { ["path"] = path, ["start"] = first, ["frames"] = (long)Math.Round((chunk.StartFrame + chunk.Frames) * 16000d / manifest.SampleRate) - first,
                    ["sampleRate"] = manifest.SampleRate, ["channels"] = manifest.Channels, ["bitsPerSample"] = manifest.BitsPerSample, ["floatingPoint"] = manifest.Format.Contains("float", StringComparison.OrdinalIgnoreCase) });
                previous = chunk.StartFrame + chunk.Frames;
            }
            job.Coverage.AddRange(manifest.Coverage.Select(c => new CaptureCoverage(c.StartFrame * 16000 / manifest.SampleRate, c.Frames * 16000 / manifest.SampleRate, c.Kind)));
            if (manifest.State == "Interrupted") job.Coverage.Add(new(previous * 16000 / manifest.SampleRate, 0, "InterruptedSourceUnknownTail"));
            job.SourceHash = RecordingAnalysisStore.Hash(manifestPath);
        }
        else
        {
            var snapshot = Store.FileFor(job.Id, "source" + Path.GetExtension(job.Source).ToLowerInvariant());
            if (!File.Exists(snapshot))
            {
                RecordingAnalysisStore.CheckPath(job.Source);
                using var input = new FileStream(job.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
                AudioTool.ValidateHandle(input, job.Source);
                Store.CheckBudget(job.Options.MaxStorageGiB, input.Length);
                if (input.Length > space.Capacity) throw new UserFacingException("Source exceeds available reserved analysis storage.");
                var partial = snapshot + ".partial"; RecordingAnalysisStore.CheckPath(partial);
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                { await input.CopyToAsync(output, 81920, ct); output.Flush(true); }
                File.Move(partial, snapshot, true);
            }
            job.SourceHash = RecordingAnalysisStore.Hash(snapshot); inputs.Add(new JsonObject { ["path"] = snapshot });
        }
        Store.Save(job); progress("Decoding complete source with bounded buffers");
        var normalized = Store.FileFor(job.Id, "normalized.f32");
        // These are uncommitted preparation outputs owned by this leased job, not retained evidence.
        foreach (var candidate in new[] { normalized, Path.ChangeExtension(normalized, ".partial") })
        { RecordingAnalysisStore.CheckPath(candidate); if (File.Exists(candidate)) File.Delete(candidate); }
        Store.CheckBudget(job.Options.MaxStorageGiB);
        var request = new JsonObject { ["inputs"] = inputs, ["destination"] = normalized, ["maxBytes"] = space.Capacity - Math.Max(0, Store.UsedBytes() - initialBytes) };
        var decoder = Store.FileFor(job.Id, "decoder.py");
        if (!File.Exists(decoder)) decoder = Path.Combine(LocalAudioInstallation.Bundle, "recording_decode.py");
        var outputText = await LocalAudioProcess.Run(Path.Combine(installation, "env", "Scripts", "python.exe"),
            ["-I", "-u", decoder], Store.DirectoryFor(job.Id), request.ToJsonString(), ct);
        var decoded = JsonNode.Parse(outputText)!;
        if (decoded["coverage"] is JsonArray coverage)
            foreach (var interval in coverage) job.Coverage.Add(new(interval!["start"]!.GetValue<long>(), interval["frames"]!.GetValue<long>(), interval["kind"]!.ToString()));
        RecordingAnalysisStore.WriteJson(Store.FileFor(job.Id, "preparation.json"), decoded);
        job.Frames = decoded["frames"]!.GetValue<long>();
        if (job.Frames <= 0 || new FileInfo(normalized).Length != job.Frames * 4) throw new IOException("Invalid decoded length.");
        job.Chunks = PlanChunks(job.Frames, job.Options.Sounds); job.Prepared = true; Store.Save(job);
    }
    internal static void ValidateCapture(CaptureManifest m, string id, bool interrupted)
    {
        if (m.Version != 1 || m.Id != id || m.SampleRate is <= 0 or > 192000 || m.Channels is < 1 or > 8 ||
            m.BitsPerSample is not (8 or 16 or 24 or 32 or 64) || m.BlockAlign != m.Channels * m.BitsPerSample / 8 || m.Frames <= 0 ||
            m.State != "Completed" && !(m.State == "Interrupted" && interrupted)) throw new IOException("Invalid or active capture manifest.");
        long end = 0;
        var names = new HashSet<string>();
        foreach (var c in m.Chunks)
        {
            if (!AudioCaptureStore.ValidChunk(c.File) || !names.Add(c.File) || c.StartFrame < end || c.Frames <= 0 || c.Frames > 192000L * 60 || c.StartFrame > m.Frames - c.Frames)
                throw new IOException("Invalid capture chunk ranges.");
            end = c.StartFrame + c.Frames;
        }
        if (end != m.Frames) throw new IOException("Capture frame total mismatch.");
        if (m.Coverage.Any(c => c.StartFrame < 0 || c.Frames < 0 || c.StartFrame > m.Frames - c.Frames)) throw new IOException("Invalid capture coverage.");
    }
    internal static List<RecordingAnalysisChunk> PlanChunks(long frames, bool sounds)
    {
        var chunks = new List<RecordingAnalysisChunk>();
        for (long start = 0; start < frames; start += 110 * 16000)
            chunks.Add(new() { Index = chunks.Count, CoreStart = start, CoreEnd = Math.Min(frames, start + 110 * 16000),
                Start = Math.Max(0, start - 5 * 16000), End = Math.Min(frames, start + 115 * 16000),
                Components = (sounds ? AudioViews.Components : ["transcription", "diarization"]).ToDictionary(c => c, _ => "Pending") });
        return chunks;
    }
    internal static void WriteClip(string source, string destination, RecordingAnalysisChunk chunk)
    {
        RecordingAnalysisStore.CheckPath(source); RecordingAnalysisStore.CheckPath(destination);
        using var input = File.OpenRead(source); input.Position = chunk.Start * 4;
        using var output = new BinaryWriter(new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None));
        var bytes = checked((int)((chunk.End - chunk.Start) * 4));
        output.Write("RIFF"u8); output.Write(bytes + 36); output.Write("WAVEfmt "u8); output.Write(16); output.Write((short)3); output.Write((short)1);
        output.Write(16000); output.Write(64000); output.Write((short)4); output.Write((short)32); output.Write("data"u8); output.Write(bytes);
        var buffer = new byte[65536];
        while (bytes > 0) { var count = Math.Min(bytes, buffer.Length); input.ReadExactly(buffer.AsSpan(0, count)); output.Write(buffer, 0, count); bytes -= count; }
    }
    internal static void Redact(JsonObject result)
    {
        if (result["transcription"]?["segments"] is not JsonArray segments) return;
        foreach (var segment in segments.OfType<JsonObject>())
            if (MemoryPolicy.HasSecret(segment.ToJsonString())) { segment["text"] = "[Transcript withheld: recognized credential material.]"; segment.Remove("words"); }
    }
}
