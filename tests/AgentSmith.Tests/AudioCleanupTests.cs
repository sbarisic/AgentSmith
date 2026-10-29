using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

public sealed class AudioCleanupTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory temporary = new();
        public string Data => temporary.Path;
        public string Current { get; }
        public RecordingAnalysisStore Store { get; }
        public Fixture()
        {
            Current = Generation();
            File.WriteAllText(Path.Combine(Data, "audio", "current"), Path.GetFileName(Current));
            Store = new(Data);
        }
        public string Generation()
        {
            var path = Path.Combine(Data, "audio", "installations", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "ready.json"), "{\"fingerprint\":\"fixture\"}");
            File.WriteAllText(Path.Combine(path, "model.bin"), "model");
            return path;
        }
        public RecordingAnalysisJob Job(string generation, string state)
        {
            var job = new RecordingAnalysisJob { Installation = Path.GetFileName(generation), State = state };
            Directory.CreateDirectory(Store.DirectoryFor(job.Id)); Store.Save(job); return job;
        }
        public void Dispose() => temporary.Dispose();
    }

    [Fact]
    public async Task RemovesUnusedAndCompletedButPreservesCurrentAndResults()
    {
        using var f = new Fixture();
        var unused = f.Generation(); var completed = f.Generation(); var job = f.Job(completed, "Completed");
        var result = f.Store.FileFor(job.Id, "summary.md"); File.WriteAllText(result, "Saved summary");
        File.WriteAllText(f.Store.FileFor(job.Id, "transcript.jsonl"), "");
        LocalAudioInstallation.Cleanup(f.Data);
        Assert.True(Directory.Exists(f.Current)); Assert.False(Directory.Exists(unused)); Assert.False(Directory.Exists(completed));
        Assert.Equal("Completed", f.Store.Read(job.Id).State); Assert.Equal("Saved summary", File.ReadAllText(result));
        using var analysis = new RecordingAnalysis(f.Data);
        Assert.Contains("Saved summary", analysis.Read(job.Id, "summary")["text"]!.ToString());
        var export = analysis.Export(job.Id, Path.Combine(f.Data, "export"));
        Assert.Equal("Saved summary", File.ReadAllText(Path.Combine(export, "summary.md")));
        Assert.Equal("Completed", (await analysis.Run(job.Id, false, false, _ => { }, null, null, default)).State);
        LocalAudioInstallation.Cleanup(f.Data); // Idempotent, including after retirement.
    }

    [Theory]
    [InlineData("Paused")] [InlineData("Running")] [InlineData("Interrupted")]
    [InlineData("CompletedWithGaps")] [InlineData("UnknownFutureState")]
    public void PreservesEveryResumableOrUnknownStateAndCleansAfterJobDeletion(string state)
    {
        using var f = new Fixture(); var old = f.Generation(); var job = f.Job(old, state);
        LocalAudioInstallation.Cleanup(f.Data); Assert.True(Directory.Exists(old));
        f.Store.Delete(job.Id); Assert.False(Directory.Exists(old)); Assert.True(Directory.Exists(f.Current));
    }

    [Fact]
    public void LeasesProtectConcurrentReadersAndCreationUntilDurableReferenceExists()
    {
        using var f = new Fixture(); var old = f.Generation();
        using (LocalAudioInstallation.Use(old))
        using (LocalAudioInstallation.Use(old))
        {
            LocalAudioInstallation.Cleanup(f.Data); Assert.True(Directory.Exists(old));
            f.Job(old, "Paused");
        }
        LocalAudioInstallation.Cleanup(f.Data); Assert.True(Directory.Exists(old));
        var unreferenced = f.Generation();
        using (LocalAudioInstallation.Use(unreferenced))
        { LocalAudioInstallation.Cleanup(f.Data); Assert.True(Directory.Exists(unreferenced)); }
        LocalAudioInstallation.Cleanup(f.Data); Assert.False(Directory.Exists(unreferenced));
        Assert.Throws<IOException>(() => LocalAudioInstallation.Use(unreferenced));
        Assert.False(Directory.Exists(unreferenced));
    }

    [Fact]
    public void DefersWhileSetupIsLocked()
    {
        using var f = new Fixture(); var old = f.Generation();
        using (var setup = new FileStream(Path.Combine(f.Data, "audio", "setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        { LocalAudioInstallation.Cleanup(f.Data); Assert.True(Directory.Exists(old)); }
        LocalAudioInstallation.Cleanup(f.Data); Assert.False(Directory.Exists(old));
    }

    [Theory]
    [InlineData("pointer")] [InlineData("metadata")] [InlineData("version")] [InlineData("missing-current")] [InlineData("bad-ready")]
    public void UncertainMetadataNeverDeletesGenerations(string invalid)
    {
        using var f = new Fixture(); var old = f.Generation(); var job = f.Job(old, "Completed");
        if (invalid == "pointer") File.WriteAllText(Path.Combine(f.Data, "audio", "current"), "../outside");
        if (invalid == "metadata") File.WriteAllText(f.Store.FileFor(job.Id, "job.json"), "broken JSON");
        if (invalid == "version") { job.Version = 2; f.Store.Save(job); }
        if (invalid == "missing-current") File.Delete(Path.Combine(f.Current, "ready.json"));
        if (invalid == "bad-ready") File.WriteAllText(Path.Combine(f.Current, "ready.json"), "{}");
        LocalAudioInstallation.Cleanup(f.Data); Assert.True(Directory.Exists(old));
    }

    [Fact]
    public void CompletedJobsReleaseTheirGenerationOnNextCleanup()
    {
        using var f = new Fixture(); var old = f.Generation(); var job = f.Job(old, "Paused");
        LocalAudioInstallation.Cleanup(f.Data); Assert.True(Directory.Exists(old));
        job.State = "Completed"; f.Store.Save(job);
        LocalAudioInstallation.Cleanup(f.Data); Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void LinkedContentsAreNeverTraversedOrDeleted()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); using var outside = new TestDirectory(); var old = f.Generation();
        var sentinel = Path.Combine(outside.Path, "keep.txt"); File.WriteAllText(sentinel, "keep");
        var link = Path.Combine(old, "models");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        { ArgumentList = { "/c", "mklink", "/J", link, outside.Path }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        process.WaitForExit(); Assert.Equal(0, process.ExitCode);
        try
        {
            LocalAudioInstallation.Cleanup(f.Data);
            Assert.True(Directory.Exists(old)); Assert.Equal("keep", File.ReadAllText(sentinel));
        }
        finally { Directory.Delete(link); }
    }
}
