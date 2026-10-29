using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

public sealed class AudioInstallationTests
{
    private sealed class Handler(Dictionary<string, byte[]> downloads) : HttpMessageHandler
    {
        public int Requests;
        public bool Corrupt;
        public bool Interrupt;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            if (Interrupt) throw new OperationCanceledException("Interrupted download", ct);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Corrupt ? [1, 2, 3] : downloads[request.RequestUri!.AbsolutePath.TrimStart('/')]) });
        }
    }
    private sealed class Fixture : IDisposable
    {
        public TestDirectory Directory { get; } = new();
        public string Bundle { get; }
        public string Data { get; }
        public Handler Handler { get; }
        public HttpClient Http { get; }
        public bool FailSelfTest;
        public bool MissingDll;
        public int Commands;
        public Fixture()
        {
            Bundle = Path.Combine(Directory.Path, "bundle"); Data = Path.Combine(Directory.Path, "data");
            System.IO.Directory.CreateDirectory(Bundle);
            var files = new Dictionary<string, byte[]> {
                ["uv.zip"] = Zip(), ["python.tar.gz"] = Tar(["python/python.exe"]),
                ["yamnet.tar.gz"] = Tar(["saved_model.pb", "assets/yamnet_class_map.csv", "variables/variables.index", "variables/variables.data-00000-of-00001"]),
                ["whisper/model.bin"] = [42], ["whisper/config.json"] = [42], ["whisper/tokenizer.json"] = [42], ["whisper/vocabulary.txt"] = [42]
            };
            var manifest = new JsonObject { ["classMapSha256"] = Hash([42]), ["downloads"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject { ["path"] = f.Key, ["url"] = "https://example.test/" + f.Key, ["sha256"] = Hash(f.Value), ["size"] = f.Value.Length }).ToArray()) };
            File.WriteAllText(Path.Combine(Bundle, "downloads.json"), manifest.ToJsonString());
            File.WriteAllText(Path.Combine(Bundle, "requirements.lock"), "pinned hashes"); File.WriteAllText(Path.Combine(Bundle, "audio_helper.py"), "helper-v1");
            Handler = new(files); Http = new(Handler);
        }
        public Task Setup() => LocalAudioInstallation.Setup(Data, _ => { }, default, Bundle, Http,
            (_, args, cwd, env, _) => {
                Commands++;
                Assert.StartsWith(Path.Combine(Data, "audio"), env["UV_CACHE_DIR"]);
                if (MissingDll) throw new IOException("Missing Visual C++ x64 Redistributable");
                if (args.Contains("venv")) { var target = Path.Combine(cwd, "env", "Scripts"); System.IO.Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "python.exe"), "runtime"); }
                else { Assert.Contains("--require-hashes", args); Assert.Contains("--only-binary", args); }
                return Task.FromResult("");
            }, (_, _, _, _) => Task.FromResult(JsonNode.Parse(FailSelfTest ? """{"transcription":{"status":"failed"},"classification":{"status":"succeeded"}}""" : """{"transcription":{"status":"succeeded"},"classification":{"status":"succeeded"}}""")!.AsObject()));
        public void Dispose() { Http.Dispose(); Directory.Dispose(); }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static byte[] Zip()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true)) { using var file = zip.CreateEntry("uv.exe").Open(); file.WriteByte(42); }
        return stream.ToArray();
    }
    private static byte[] Tar(string[] names)
    {
        using var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionMode.Compress, true))
        using (var tar = new TarWriter(gzip))
            foreach (var name in names) { using var content = new MemoryStream([42]); tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = content }); }
        return stream.ToArray();
    }

    [Fact]
    public async Task FreshSetupIsReadyAndRepeatDoesNoDownloadsOrCommands()
    {
        using var f = new Fixture(); await f.Setup(); var status = LocalAudioInstallation.Status(f.Data, f.Bundle);
        Assert.True(status.Transcription); Assert.True(status.Classification); var count = f.Handler.Requests;
        Assert.True(File.Exists(Path.Combine(status.Location, "runtime", "python", "python.exe")));
        await f.Setup(); Assert.Equal(count, f.Handler.Requests); Assert.Equal(2, f.Commands);
    }

    [Theory]
    [InlineData("checksum")] [InlineData("interrupted")] [InlineData("selftest")] [InlineData("prerequisite")]
    public async Task FailedUpdatePreservesPreviousReadyGeneration(string failure)
    {
        using var f = new Fixture(); await f.Setup(); var before = LocalAudioInstallation.Status(f.Data, f.Bundle);
        File.AppendAllText(Path.Combine(f.Bundle, "audio_helper.py"), " updated");
        if (failure is "checksum" or "interrupted")
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(Path.Combine(f.Data, "audio", "downloads"))) File.Delete(file);
            f.Handler.Corrupt = failure == "checksum"; f.Handler.Interrupt = failure == "interrupted";
        }
        f.FailSelfTest = failure == "selftest"; f.MissingDll = failure == "prerequisite";
        await Assert.ThrowsAnyAsync<Exception>(() => f.Setup());
        var after = LocalAudioInstallation.Status(f.Data, f.Bundle);
        Assert.Equal(before.Location, after.Location); Assert.True(after.Transcription && after.Classification);
        Assert.True(File.Exists(Path.Combine(f.Data, "audio", "last-error.txt")));
        f.Handler.Corrupt = f.Handler.Interrupt = f.FailSelfTest = f.MissingDll = false;
        await f.Setup(); Assert.NotEqual(before.Location, LocalAudioInstallation.Status(f.Data, f.Bundle).Location);
        Assert.False(Directory.Exists(before.Location));
    }

    [Fact]
    public async Task MissingModelsDisableOnlyTheirEngine()
    {
        using var f = new Fixture(); await f.Setup(); var before = LocalAudioInstallation.Status(f.Data, f.Bundle);
        File.Delete(Path.Combine(before.Location, "models", "whisper", "model.bin"));
        var after = LocalAudioInstallation.Status(f.Data, f.Bundle);
        Assert.False(after.Ready("audio_transcribe")); Assert.True(after.Ready("audio_classify")); Assert.False(after.Ready("audio_listen"));
    }
}
