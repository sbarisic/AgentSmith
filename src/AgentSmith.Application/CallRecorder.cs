using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Tools;
using AgentSmith.Storage;
using AgentSmith.OpenAI;
using AgentSmith.Runtime;

namespace AgentSmith.Application;

public sealed class RecorderPreferences
{
    public string? OutputId { get; set; }
    public string? MicrophoneId { get; set; }
    public string Language { get; set; } = "auto";
    public string SummaryLanguage { get; set; } = "en";
    public double StorageGiB { get; set; } = 10;
    public int MaxMinutes { get; set; } = 30;
    public int MaxRequests { get; set; } = 100;
}
public sealed class CallMetadata
{
    public string Title { get; set; } = "";
    public bool Test { get; set; }
    public bool Kept { get; set; }
    public string? AnalysisId { get; set; }
    public string Language { get; set; } = "auto";
    public Dictionary<string, string> SpeakerNames { get; set; } = [];
}
public sealed record SavedCall(CaptureManifest Capture, CallMetadata Metadata, RecordingAnalysisJob? Analysis)
{
    public string Title => Metadata.Title.Length > 0 ? Metadata.Title : Capture.Started.ToLocalTime().ToString("g");
    public string Detail => $@"{Capture.Started.ToLocalTime():g} · {TimeSpan.FromSeconds(Capture.DurationSeconds):hh\:mm\:ss} · {(Capture.State is "Recording" or "Starting" or "Finalizing" ? Capture.State : Metadata.Test && !Metadata.Kept ? "Test — Keep or Delete" : Analysis?.State ?? "Transcription pending")}";
}
public sealed record TranscriptLine(string Id, double Start, double End, string SpeakerId, string Speaker, string Text, string Track)
{
    public string Time => TimeSpan.FromSeconds(Start).ToString(@"hh\:mm\:ss");
}

public sealed class CallRecorder : IDisposable
{
    public string DataDirectory { get; }
    public RecordingAnalysis Analysis { get; }
    private readonly string metadataDirectory;
    private readonly string executable;
    private readonly HttpClient http = new();
    private ChatModelSelection? displayedModel;
    public string? LastStartId { get; private set; }
    public RecorderPreferences Preferences { get; }
    public CallRecorder(string? directory, string serviceExecutable)
    {
        DataDirectory = AudioCaptureDevices.ResolveDirectory(SmithPaths.DataDirectory(directory));
        executable = Path.GetFullPath(serviceExecutable);
        metadataDirectory = Path.Combine(DataDirectory, "desktop");
        RecordingAnalysisStore.CheckPath(metadataDirectory); Directory.CreateDirectory(metadataDirectory);
        Analysis = new(DataDirectory);
        var path = Path.Combine(metadataDirectory, "preferences.json");
        Preferences = File.Exists(path) ? RecordingAnalysisStore.ReadJson<RecorderPreferences>(path) : new();
    }
    public void SavePreferences()
    {
        RecordingAnalysis.ValidateOptions(new(Language: Preferences.Language, MaxMinutes: Preferences.MaxMinutes, MaxRequests: Preferences.MaxRequests, MaxStorageGiB: Preferences.StorageGiB));
        RecordingAnalysisStore.WriteJson(Path.Combine(metadataDirectory, "preferences.json"), Preferences);
    }
    private string MetadataPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new UserFacingException("Invalid recording ID.");
        return Path.Combine(metadataDirectory, id + ".json");
    }
    public CallMetadata Metadata(string id) => File.Exists(MetadataPath(id)) ? RecordingAnalysisStore.ReadJson<CallMetadata>(MetadataPath(id)) : new();
    public void SaveMetadata(string id, CallMetadata metadata) => RecordingAnalysisStore.WriteJson(MetadataPath(id), metadata);
    public string CaptureDirectory(string id) { _ = MetadataPath(id); var path = Path.Combine(DataDirectory, "audio", "recordings", id); RecordingAnalysisStore.CheckPath(path); return path; }
    public CaptureManifest ReadCapture(string id) => RecordingAnalysisStore.ReadJson<CaptureManifest>(Path.Combine(CaptureDirectory(id), "manifest.json"));
    public IReadOnlyList<SavedCall> Library()
    {
        var root = Path.Combine(DataDirectory, "audio", "recordings");
        if (!Directory.Exists(root)) return [];
        RecordingAnalysisStore.CheckPath(root);
        return Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>().Where(id => Guid.TryParseExact(id, "N", out _) && File.Exists(Path.Combine(root, id, "manifest.json")))
            .Select(id => { var meta = Metadata(id); return new SavedCall(ReadCapture(id), meta, meta.AnalysisId is { } a && File.Exists(Analysis.Store.FileFor(a, "job.json")) ? Analysis.Store.Read(a) : null); })
            .OrderByDescending(c => c.Capture.Started).ToArray();
    }
    public Task<AudioServiceStatus> Status(CancellationToken ct = default) => AudioServiceControl.Status(DataDirectory, ct);
    public async Task EnsureService(CancellationToken ct)
    {
        if ((await Status(ct)).Available) return;
        if (!File.Exists(executable)) throw new UserFacingException("Place the matching smith.exe next to the GUI, or launch with --smith <path>.");
        await AudioServiceControl.Start(DataDirectory, executable, [], ct);
    }
    public static CaptureDevice[] Devices()
    {
        // Enumeration opens no capture streams and starts no service.
        return AudioCaptureDevices.List();
    }
    public async Task<CaptureManifest> Start(string title, bool test, CancellationToken ct)
    {
        SavePreferences();
        if (Preferences.OutputId is null || Preferences.MicrophoneId is null) throw new UserFacingException("Select both call output and microphone.");
        await EnsureService(ct);
        var id = Guid.NewGuid().ToString("N");
        LastStartId = id;
        SaveMetadata(id, new() { Title = title.Trim(), Test = test, Language = Preferences.Language });
        var request = new CaptureStart(id, Preferences.OutputId, Preferences.StorageGiB, MicrophoneId: Preferences.MicrophoneId);
        return (await AudioCaptureControl.Request(DataDirectory, "start", JsonSerializer.SerializeToNode(request)!.AsObject(), ct)).Deserialize<CaptureManifest>()!;
    }
    public async Task<CaptureManifest> CaptureStatus(string id, CancellationToken ct = default) =>
        (await AudioCaptureControl.Request(DataDirectory, "status", new() { ["id"] = id }, ct)).Deserialize<CaptureManifest>()!;
    public async Task<CaptureManifest> Stop(string id, CancellationToken ct = default)
    {
        var result = (await AudioCaptureControl.Request(DataDirectory, "stop", new() { ["id"] = id }, ct)).Deserialize<CaptureManifest>()!;
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (result.State is "Starting" or "Recording" or "Finalizing" && started.Elapsed < TimeSpan.FromSeconds(10))
        { await Task.Delay(200, ct); result = await CaptureStatus(id, ct); }
        if (result.State is "Starting" or "Recording" or "Finalizing") throw new UserFacingException("Stop could not be confirmed. Recording may still be active; inspect status or retry Stop.");
        return result;
    }
    public Task<JsonNode> Mute(string id, bool muted) => AudioCaptureControl.Request(DataDirectory, "mute", new() { ["id"] = id, ["muted"] = muted });
    public async Task<RecordingAnalysisJob> Transcribe(string id, bool allowInterrupted, bool retryFailed, bool retryUncertain, Action<string> progress, CancellationToken ct)
    {
        await EnsureService(ct);
        var meta = Metadata(id);
        var job = meta.AnalysisId is { } analysisId && File.Exists(Analysis.Store.FileFor(analysisId, "job.json")) ? Analysis.Store.Read(analysisId) : null;
        if (job is null)
        {
            job = Analysis.Create("capture", id, new(Language: meta.Language, NoSummary: true, AllowInterrupted: allowInterrupted,
                MaxMinutes: Preferences.MaxMinutes, MaxRequests: Preferences.MaxRequests, MaxStorageGiB: Preferences.StorageGiB));
            meta.AnalysisId = job.Id; SaveMetadata(id, meta);
        }
        // Transcribe/Resume never invokes a summary provider, even after a previous summary attempt.
        using (Analysis.Store.Lease(job.Id)) { job.Options = job.Options with { NoSummary = true }; Analysis.Store.Save(job); }
        return await Analysis.Run(job.Id, retryFailed, retryUncertain, progress, null, null, ct, Preferences.MaxMinutes, Preferences.MaxRequests, Preferences.StorageGiB);
    }
    public string ModelDescription()
    {
        try { var selected = RecordingModels.Resolve(new SessionStore(DataDirectory)); displayedModel = selected; return $"{selected.Provider} / {selected.Model}"; }
        catch (UserFacingException ex) { displayedModel = null; return ex.Message; }
    }
    public async Task<RecordingAnalysisJob> Summarize(string captureId, bool retryUncertain, CancellationToken ct)
    {
        var meta = Metadata(captureId); var id = meta.AnalysisId ?? throw new UserFacingException("Transcribe the recording first.");
        var store = new SessionStore(DataDirectory);
        var selection = displayedModel ?? throw new UserFacingException("Configure a default summary model with smith model, then refresh recordings and devices.");
        var summaryLanguage = Preferences.SummaryLanguage;
        var session = new Session { Model = selection.Model, AccountId = selection.AccountId, Connection = selection.Connection() };
        var vault = new CredentialVault(DataDirectory, new WindowsSecretProtector());
        var auth = new ChatGptAuth(http, vault, new IdentityValidator());
        await RecordingModels.Configure(session, http, vault, auth, ct);
        var client = new SessionModelClient(store, session, RecordingModels.Client(session, http, token => auth.AccessTokenAsync(session.AccountId, token)));
        using (Analysis.Store.Lease(id))
        {
            var job = Analysis.Store.Read(id); job.SpeakerNames = new(meta.SpeakerNames); job.SummaryLanguage = summaryLanguage;
            job.Options = job.Options with { MaxMinutes = Preferences.MaxMinutes, MaxRequests = Preferences.MaxRequests, MaxStorageGiB = Preferences.StorageGiB }; Analysis.Store.Save(job);
        }
        return await Analysis.CompleteSummary(id, (prompt, token) => RecordingModels.Summarize(session, client, prompt, summaryLanguage, token),
            $"{session.Connection.Provider}|{session.Connection.Endpoint}|{session.Model}|{session.AccountId}", ct, retryUncertain);
    }
    public TranscriptLine[] Transcript(string captureId)
    {
        var meta = Metadata(captureId);
        if (meta.AnalysisId is null) return [];
        return RecordingAnalysis.Rows(Analysis.Store.FileFor(meta.AnalysisId, "transcript.jsonl")).Select(r => {
            var speaker = r["speaker"]?.ToString() ?? "Unknown speaker";
            return new TranscriptLine(r["id"]!.ToString(), r["startSeconds"]!.GetValue<double>(), r["endSeconds"]!.GetValue<double>(), speaker,
                meta.SpeakerNames.GetValueOrDefault(speaker) ?? speaker, r["text"]!.ToString(), r["track"]?.ToString() ?? ""); }).ToArray();
    }
    public void RenameSpeaker(string captureId, string speaker, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl)) throw new UserFacingException("Speaker name must be 1–100 printable characters.");
        var meta = Metadata(captureId); meta.SpeakerNames[speaker] = name.Trim(); SaveMetadata(captureId, meta);
    }
    public string Summary(string captureId)
    {
        var meta = Metadata(captureId); if (meta.AnalysisId is null) return "Generate a summary after transcription.";
        var path = Analysis.Store.FileFor(meta.AnalysisId, "summary.md"); if (!File.Exists(path)) return "No completed summary. Click Generate summary to send transcript evidence to the configured model.";
        var job = Analysis.Store.Read(meta.AnalysisId); job.SpeakerNames = meta.SpeakerNames; job.SummaryLanguage = Preferences.SummaryLanguage;
        return (job.SummaryRevision != RecordingAnalysis.SummaryRevision(job) ? "Summary outdated: speaker names or language changed. Generate again to update.\n\n" : "") + File.ReadAllText(path);
    }
    public string Coverage(string captureId)
    {
        var meta = Metadata(captureId);
        var path = meta.AnalysisId is null ? null : Analysis.Store.FileFor(meta.AnalysisId, "coverage.json");
        return path is not null && File.Exists(path) ? JsonSerializer.Serialize(JsonNode.Parse(File.ReadAllText(path)), new JsonSerializerOptions { WriteIndented = true }) : JsonSerializer.Serialize(ReadCapture(captureId), new JsonSerializerOptions { WriteIndented = true });
    }
    public string Export(string captureId, string destination)
    {
        var meta = Metadata(captureId); if (meta.AnalysisId is null) throw new UserFacingException("Transcribe before exporting results.");
        Analysis.Export(meta.AnalysisId, destination);
        var rows = Transcript(captureId);
        File.WriteAllLines(Path.Combine(destination, "transcript.txt"), rows.Select(r => $"[{r.Time}] {r.Speaker}: {r.Text}"));
        string Time(double seconds) { var t = TimeSpan.FromSeconds(seconds); return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}"; }
        File.WriteAllLines(Path.Combine(destination, "transcript.srt"), rows.SelectMany((r, i) => new[] { (i + 1).ToString(), $"{Time(r.Start)} --> {Time(r.End)}", $"{r.Speaker}: {r.Text}", "" }));
        RecordingAnalysisStore.WriteJson(Path.Combine(destination, "speaker-names.json"), meta.SpeakerNames);
        if (File.Exists(Path.Combine(destination, "summary.md"))) File.WriteAllText(Path.Combine(destination, "summary.md"), Summary(captureId));
        return destination;
    }
    public async Task DeleteCapture(string id)
    { await EnsureService(CancellationToken.None); await AudioCaptureControl.Request(DataDirectory, "delete", new() { ["id"] = id }); File.Delete(MetadataPath(id)); }
    public void DeleteAnalysis(string id)
    { var meta = Metadata(id); if (meta.AnalysisId is not null) Analysis.Store.Delete(meta.AnalysisId); meta.AnalysisId = null; SaveMetadata(id, meta); }
    public void Dispose() { Analysis.Dispose(); http.Dispose(); }
}
