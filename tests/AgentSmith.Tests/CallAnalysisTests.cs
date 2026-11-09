using System.Text.Json.Nodes;
using AgentSmith.Tools;
using NAudio.Wave;
using Xunit;

namespace AgentSmith.Tests;

public sealed class CallAnalysisTests
{
    [Fact]
    public async Task NativeTracksNormalizeToOneTimelineWithSeparateSourceOffsets()
    {
        using var directory = new TestDirectory(); var backend = new Backend(); var (analysis, job) = Prepared(directory.Path, backend); using var owned = analysis;
        var snapshot = Path.Combine(directory.Path, "snapshot"); Directory.CreateDirectory(snapshot);
        var capture = new CaptureManifest { Version = 2, Id = Guid.NewGuid().ToString("N"), State = "Completed", StartQpc = 100_000_000, TimelineSeconds = 4 };
        foreach (var item in new[] { ("call", 16000, .25), ("mic", 48000, -.5) })
        {
            var format = WaveFormat.CreateIeeeFloatWaveFormat(item.Item2, 1);
            var path = Path.Combine(snapshot, item.Item1 + "-chunk-000001.wav");
            using (var writer = new WaveFileWriter(path, format))
            {
                var samples = new byte[item.Item2 * 4 * 4];
                for (var i = 0; i < samples.Length; i += 4)
                    BitConverter.TryWriteBytes(samples.AsSpan(i, 4), (float)(item.Item3 + (item.Item1 == "mic" ? .1 * Math.Sin(2 * Math.PI * 12000 * (i / 4d) / item.Item2) : 0)));
                writer.Write(samples);
            }
            capture.Tracks.Add(new() { Id = capture.Id, TrackId = item.Item1, State = "Completed", StartQpc = capture.StartQpc,
                SampleRate = format.SampleRate, Channels = 1, BitsPerSample = 32, BlockAlign = 4, Format = format.ToString(), FloatingPoint = true,
                Frames = item.Item2 * 4, TimelineSeconds = 4,
                Anchors = [new(0, capture.StartQpc), new(item.Item2 * 3, capture.StartQpc + 30_000_000)],
                Chunks = [new(Path.GetFileName(path), 0, item.Item2 * 4, new FileInfo(path).Length, RecordingAnalysisStore.Hash(path))] });
        }
        RecordingAnalysisStore.WriteJson(Path.Combine(snapshot, "manifest.json"), capture);
        job.SourceKind = "capture"; job.Source = capture.Id; job.Prepared = false;
        await analysis.PrepareTracks(job, capture, snapshot, "", 10 * 1024 * 1024, _ => { }, default);
        Assert.Equal(4, job.DurationSeconds); Assert.Equal(8, job.TotalAudioSeconds); Assert.Equal(2, job.Chunks.Count);
        Assert.Equal(64000, job.Chunks[1].SourceOffset);
        using var input = new BinaryReader(File.OpenRead(analysis.Store.FileFor(job.Id, "normalized.f32")));
        input.BaseStream.Position = 16000 * 4; Assert.InRange(input.ReadSingle(), .249f, .251f);
        input.BaseStream.Position = (64000 + 16000) * 4; Assert.InRange(input.ReadSingle(), -.501f, -.499f);
        // The 12 kHz component must be rejected, not aliased to 4 kHz in the 16 kHz speech input.
        for (var sample = 0; sample < 100; sample++) Assert.InRange(input.ReadSingle(), -.505f, -.495f);
        var clip = Path.Combine(directory.Path, "mic.wav"); RecordingAnalysis.WriteClip(analysis.Store.FileFor(job.Id, "normalized.f32"), clip, job.Chunks[1]);
        using var wave = new WaveFileReader(clip); wave.Position = 16000 * 4; var bytes = new byte[4]; wave.ReadExactly(bytes); Assert.InRange(BitConverter.ToSingle(bytes), -.501f, -.499f);
        var result = await analysis.Run(job.Id, false, false, _ => { }, null, null, default); Assert.Equal("Completed", result.State);
        // A lost microphone with no retained frames must not prevent processing the surviving call track.
        input.Dispose(); wave.Dispose();
        capture.State = "Interrupted"; capture.Tracks[1].State = "Failed"; capture.Tracks[1].Frames = 0;
        capture.Tracks[1].Chunks.Clear(); capture.Tracks[1].Anchors.Clear();
        job.Options = job.Options with { AllowInterrupted = true }; job.Prepared = false;
        RecordingAnalysisStore.WriteJson(Path.Combine(snapshot, "manifest.json"), capture);
        await analysis.PrepareTracks(job, capture, snapshot, "", 10 * 1024 * 1024, _ => { }, default);
        Assert.Single(job.Chunks); Assert.Contains(job.Coverage, c => c.TrackId == "mic" && c.Kind == "Missing" && c.Frames == job.Frames);
    }
    private sealed class Backend : ILocalAudioBackend
    {
        public int Calls;
        public bool FailFirst;
        public AudioReadiness Status(string data) => new(true, true, data, "fake", true);
        public Task<JsonObject> Run(string installation, JsonObject request, Action<string> progress, CancellationToken cancellation)
        {
            Calls++; cancellation.ThrowIfCancellationRequested();
            if (FailFirst && Calls == 1) throw new IOException("Uncertain first track");
            return Task.FromResult(JsonNode.Parse("""
            {"transcription":{"status":"succeeded","segments":[{"startSeconds":1,"endSeconds":2,"text":"shared speech","words":[{"startSeconds":1,"endSeconds":2,"text":"shared speech"}]}]},"diarization":{"status":"succeeded","turns":[{"startSeconds":1,"endSeconds":2,"speaker":"Speaker 1"}],"embeddings":[{"speaker":"Speaker 1","seconds":4,"vector":[1,0]}]}}
            """)!.AsObject());
        }
    }
    private static (RecordingAnalysis, RecordingAnalysisJob) Prepared(string data, Backend backend)
    {
        var analysis = new RecordingAnalysis(data, backend); var installation = Path.Combine(data, "audio", "installations", Guid.NewGuid().ToString("N"));
        var helper = Path.Combine(installation, "helper"); Directory.CreateDirectory(helper);
        foreach (var file in new[] { "audio_helper.py", "downloads.json", "requirements.lock" }) File.WriteAllText(Path.Combine(helper, file), "{}");
        var job = new RecordingAnalysisJob { Version = 2, TrackCount = 2, Prepared = true, Frames = 160000,
            Installation = Path.GetFileName(installation), Fingerprint = LocalAudioInstallation.Fingerprint(helper),
            DecoderHash = RecordingAnalysisStore.Hash(Path.Combine(LocalAudioInstallation.Bundle, "recording_decode.py")), Options = new(NoSummary: true) };
        foreach (var track in new[] { "call", "mic" }) foreach (var chunk in RecordingAnalysis.PlanChunks(job.Frames, false))
        { chunk.Index = job.Chunks.Count; chunk.TrackId = track; chunk.SourceOffset = track == "mic" ? job.Frames : 0; job.Chunks.Add(chunk); }
        Directory.CreateDirectory(analysis.Store.DirectoryFor(job.Id)); var path = analysis.Store.FileFor(job.Id, "normalized.f32");
        using (var file = File.Create(path)) file.SetLength(job.Frames * 8);
        RecordingAnalysisStore.WriteJson(analysis.Store.FileFor(job.Id, "preparation.json"), new { hash = RecordingAnalysisStore.Hash(path) }); analysis.Store.Save(job);
        return (analysis, job);
    }
    [Fact]
    public async Task NormallyStoppedCaptureWarningsRemainIncompleteInAnalysisWithoutInterruptConsent()
    {
        using var directory = new TestDirectory(); var backend = new Backend();
        var (analysis, job) = Prepared(directory.Path, backend); using var owned = analysis;
        job.Coverage.Add(new(16000, 160, "DeviceDiscontinuity", "mic"));
        analysis.Store.Save(job);
        Assert.False(job.Options.AllowInterrupted);
        var result = await analysis.Run(job.Id, false, false, _ => { }, null, null, default);
        Assert.Equal("CompletedWithGaps", result.State);
        Assert.Contains(result.Coverage, c => c.TrackId == "mic" && c.Kind == "DeviceDiscontinuity");
        Assert.True(backend.Calls > 0);
        Assert.False(File.Exists(analysis.Store.FileFor(job.Id, "summary.md")));
    }
    [Fact]
    public async Task OverlapIsPreservedAndSummaryIsOnlyExplicit()
    {
        using var directory = new TestDirectory(); var backend = new Backend(); var (analysis, job) = Prepared(directory.Path, backend); using var owned = analysis;
        var summaryCalls = 0;
        Task<string> Summary(string prompt, CancellationToken ct) { summaryCalls++; return Task.FromResult("""{"items":[{"text":"A discussion.","segments":["callc000000s00000","micc000001s00000"]}]}"""); }
        var result = await analysis.Run(job.Id, false, false, _ => { }, Summary, "fixture", default);
        Assert.Equal("Completed", result.State); Assert.Equal(0, summaryCalls); Assert.Equal(2, backend.Calls);
        var rows = RecordingAnalysis.Rows(analysis.Store.FileFor(job.Id, "transcript.jsonl")).ToArray();
        Assert.Equal(2, rows.Length); Assert.Equal(2, rows.Select(r => r["id"]!.ToString()).Distinct().Count());
        Assert.Equal(new[] { "Speaker 1", "You" }, rows.Select(r => r["speaker"]!.ToString()));
        Assert.Equal(rows[0]["startSeconds"]!.ToString(), rows[1]["startSeconds"]!.ToString());
        await analysis.CompleteSummary(job.Id, Summary, "fixture", default); Assert.Equal(1, summaryCalls); Assert.Equal(2, backend.Calls);
        await analysis.CompleteSummary(job.Id, Summary, "fixture", default); Assert.Equal(1, summaryCalls);
        var saved = analysis.Store.Read(job.Id); saved.SummaryLanguage = "sl"; saved.SpeakerNames["Speaker 1"] = "Ana"; analysis.Store.Save(saved);
        await analysis.CompleteSummary(job.Id, Summary, "fixture", default); Assert.Equal(2, summaryCalls); Assert.Equal(2, backend.Calls);
    }
    [Fact]
    public async Task UncertainTrackDoesNotLoseOtherTrackAndRetryReusesCompletedEvidence()
    {
        using var directory = new TestDirectory(); var backend = new Backend { FailFirst = true }; var (analysis, job) = Prepared(directory.Path, backend); using var owned = analysis;
        Assert.Equal("Paused", (await analysis.Run(job.Id, false, false, _ => { }, null, null, default)).State);
        Assert.Equal("CompletedWithGaps", (await analysis.Run(job.Id, false, false, _ => { }, null, null, default)).State);
        Assert.Equal(2, backend.Calls);
        Assert.Equal("You", Assert.Single(RecordingAnalysis.Rows(analysis.Store.FileFor(job.Id, "transcript.jsonl")))["speaker"]!.ToString());
        await analysis.Run(job.Id, false, true, _ => { }, null, null, default); Assert.Equal(3, backend.Calls);
        Assert.Equal(2, RecordingAnalysis.Rows(analysis.Store.FileFor(job.Id, "transcript.jsonl")).Count());
    }
    [Fact]
    public async Task CoverageIsTrackSpecificAndIntentionalMuteIsNotFailure()
    {
        using var directory = new TestDirectory(); var backend = new Backend(); var (analysis, job) = Prepared(directory.Path, backend); using var owned = analysis;
        job.Coverage = [new(0, 16000, "IntentionalMute", "mic"), new(0, 32000, "Missing", "call")]; analysis.Store.Save(job);
        var result = await analysis.Run(job.Id, false, false, _ => { }, null, null, default);
        Assert.Equal("CompletedWithGaps", result.State); Assert.Equal(18, result.ProcessedSeconds);
    }
    [Theory]
    [InlineData("en")]
    [InlineData("hr")]
    [InlineData("sl")]
    public void AcceptedLanguagesAreSupportedWithoutClaimingRecognitionAccuracy(string language) => RecordingAnalysis.ValidateOptions(new(Language: language));
}
