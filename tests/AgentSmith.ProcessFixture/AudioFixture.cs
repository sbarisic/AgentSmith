using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Storage;
using AgentSmith.Tools;

namespace AgentSmith.ProcessFixture;

internal static class AudioFixture
{
    internal static async Task Run(string[] args)
    {
        var data = args[1];
        if (args[0] == "--audio-cache")
        {
            var workspace = Path.GetFullPath(args[3]); Directory.CreateDirectory(workspace);
            var input = Path.Combine(workspace, "input.wav"); File.Copy(args[2], input);
            var store = new SessionStore(Path.Combine(workspace, "state"));
            var session = new Session { Workspace = workspace, Runs = [new()] };
            store.Save(session, new("created", session.Id, null, new { }));
            using var tools = new ToolRegistry(store, audio: new AudioTool(new InstalledBackend(data)));
            async Task<JsonNode> Analyze()
            {
                var ctx = new ToolExecutionContext(session, session.Runs.Last(), store, default);
                var call = new ToolCall { Id = Guid.NewGuid().ToString("N"), Name = "audio_transcribe",
                    Arguments = """{"path":"input.wav","startSeconds":0,"durationSeconds":0,"language":"auto"}""" };
                call.Prepared = tools.Prepare(ctx, call);
                return JsonNode.Parse((await tools.ExecuteAsync(ctx, call)).Content)!;
            }
            var first = await Analyze(); Console.WriteLine(first.ToJsonString());
            await Console.In.ReadLineAsync(); // The acceptance driver stops the service at this point.
            session = store.Get(session.Id)!;
            var reused = await Analyze();
            var bytes = File.ReadAllBytes(input); bytes[^1] ^= 1; File.WriteAllBytes(input, bytes);
            string? changedError = null;
            try { await Analyze(); } catch (UserFacingException ex) { changedError = ex.Message; }
            Console.WriteLine(Json.Serialize(new { reused = reused["reused"]!.GetValue<bool>(),
                sameArtifact = first["artifactId"]!.ToString() == reused["artifactId"]!.ToString(), changedError }));
            return;
        }
        if (args[0] == "--audio-reserve")
        {
            try
            {
                using var pipe = await AudioConnection.Connect(AudioEndpoint.Resolve(data), "client", default);
                using var gate = new SemaphoreSlim(1);
                var replies = new JsonArray();
                for (var i = 0; i < int.Parse(args[2]); i++)
                {
                    var submission = new AudioSubmission(Guid.NewGuid().ToString("N"), "fake", ".wav", 44,
                        new string('0', 64), "audio_diarize", 0, 0, "auto", ["diarization"], Environment.TickCount64 + 300_000);
                    await AudioWire.Write(pipe, new("submit", Guid.NewGuid().ToString("N"), AudioWire.Data(submission)), gate, default);
                    replies.Add(AudioWire.Data(await AudioWire.Read(pipe, default)));
                }
                Console.WriteLine(Json.Serialize(new { connected = true, replies }));
                await Console.In.ReadLineAsync();
            }
            catch (Exception ex) { Console.WriteLine(Json.Serialize(new { connected = false, error = ex.Message })); }
            return;
        }
        if (args[0] == "--audio-host")
        {
            var host = new AudioServiceHost(data);
            var calls = 0;
            host.ValidateInstallation = _ => data;
            host.Infer = async (_, request, progress, ct) =>
            {
                progress("fake inference started");
                var call = Interlocked.Increment(ref calls);
                if (request["startSeconds"]!.GetValue<int>() == 99) throw new IOException("Injected helper failure");
                await Task.Delay(request["durationSeconds"]!.GetValue<int>() * 1000, ct);
                return new JsonObject { ["startSeconds"] = 0d, ["durationSeconds"] = 1d, ["call"] = call,
                    ["diarization"] = new JsonObject { ["status"] = "succeeded", ["speakerCount"] = 0d, ["turns"] = new JsonArray() } };
            };
            Console.WriteLine("ready");
            await host.Run();
            return;
        }
        if (args[0] == "--audio-client")
        {
            using var client = await AudioConnection.Open(AudioEndpoint.Resolve(data), default);
            var input = args[2]; var delay = int.Parse(args[3]);
            var cancelAfter = args.Length > 4 ? int.Parse(args[4]) : 0;
            using var cancel = new CancellationTokenSource();
            if (cancelAfter > 0) cancel.CancelAfter(cancelAfter);
            using var source = File.OpenRead(input);
            var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(source));
            var submission = new AudioSubmission(Guid.NewGuid().ToString("N"), "fake", ".wav", source.Length, hash,
                "audio_diarize", 0, delay, "auto", ["diarization"], Environment.TickCount64 + 300_000);
            var watch = Stopwatch.StartNew();
            try
            {
                var result = await client.Run(submission, input, _ => { }, cancel.Token);
                Console.WriteLine(Json.Serialize(new { success = true, seconds = watch.Elapsed.TotalSeconds, result }));
            }
            catch (Exception ex) { Console.WriteLine(Json.Serialize(new { success = false, error = ex.Message })); }
            return;
        }
        if (args[0] is "--audio-real" or "--audio-baseline" or "--audio-generation")
        {
            var status = LocalAudioInstallation.Status(data);
            var installation = args[0] == "--audio-generation"
                ? Path.Combine(Path.GetDirectoryName(status.Location)!, args[3]) : status.Location;
            var input = Path.GetFullPath(args[2]);
            var request = new JsonObject { ["protocol"] = 1, ["path"] = input, ["models"] = Path.Combine(installation, "models"),
                ["operation"] = "audio_listen", ["startSeconds"] = 0, ["durationSeconds"] = 0, ["language"] = "auto",
                ["components"] = new JsonArray("transcription", "classification", "diarization") };
            using var helper = new LocalAudioHost();
            using var backend = new LocalAudioBackend();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var timer = Stopwatch.StartNew();
            var result = args[0] != "--audio-baseline"
                ? await backend.Run(installation, request, _ => { }, timeout.Token)
                : await helper.Infer(installation, request, _ => { }, timeout.Token);
            Console.WriteLine(Json.Serialize(new { success = true, seconds = timer.Elapsed.TotalSeconds, result,
                clientWorkingSet = Environment.WorkingSet, helperProcessId = helper.ProcessId, helperPeakCommittedBytes = helper.PeakCommittedBytes }));
            if (args.Length > 3 && args[0] != "--audio-generation") await Task.Delay(int.Parse(args[3]));
        }
    }
    private sealed class InstalledBackend(string data) : ILocalAudioBackend, IDisposable
    {
        private readonly LocalAudioBackend inner = new();
        public AudioReadiness Status(string _) => LocalAudioInstallation.Status(data);
        public Task<JsonObject> Run(string installation, JsonObject request, Action<string> progress, CancellationToken cancellation)
            => inner.Run(installation, request, progress, cancellation);
        public void Dispose() => inner.Dispose();
    }
}
