using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentSmith.Core;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Xunit;
using static AgentSmith.Tests.RuntimeTests;

namespace AgentSmith.Tests;

/// <summary>Explicit real-model acceptance, never downloads and never calls a chat provider.</summary>
public sealed class LocalAudioAcceptanceTests
{
    public sealed class InstalledAudioFactAttribute : FactAttribute
    {
        public InstalledAudioFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SMITH_AUDIO_ACCEPTANCE_FIXTURES") is null)
                Skip = "Explicit real-model acceptance: set SMITH_AUDIO_ACCEPTANCE_FIXTURES and SMITH_AUDIO_ACCEPTANCE_DATA after managed setup.";
        }
    }
    private sealed class InstalledBackend(string dataDirectory) : ILocalAudioBackend
    {
        public AudioReadiness Status(string _) => LocalAudioInstallation.Status(dataDirectory);
        public Task<JsonObject> Run(string installation, JsonObject request, Action<string> progress, CancellationToken cancellation)
            => LocalAudioProcess.Infer(installation, request, progress, cancellation);
    }
    [InstalledAudioFact]
    public async Task RealModelsThroughToolsAndScriptedChat()
    {
        var fixtures = Path.GetFullPath(Environment.GetEnvironmentVariable("SMITH_AUDIO_ACCEPTANCE_FIXTURES")!);
        var data = Environment.GetEnvironmentVariable("SMITH_AUDIO_ACCEPTANCE_DATA")!;
        Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY")));
        Assert.True(LocalAudioInstallation.Status(data).Ready("audio_listen"));
        using var temporary = new TestDirectory(); var store = new SessionStore(temporary.Path);
        using var tools = new ToolRegistry(store, audio: new AudioTool(new InstalledBackend(data)));
        var measurements = new JsonArray();
        foreach (var (path, operation, start, duration) in new[] {
            ("test01_20s.wav", "audio_listen", 0, 0), ("speech.wav", "audio_listen", 0, 0),
            ("silence.wav", "audio_listen", 0, 0), ("tone.wav", "audio_classify", 0, 0),
            ("mixed.wav", "audio_listen", 0, 0), ("test01.mp3", "audio_transcribe", 10, 10) })
        {
            var session = new Session { Workspace = fixtures, Mode = PermissionMode.Inspect };
            var args = new JsonObject { ["path"] = path, ["startSeconds"] = start, ["durationSeconds"] = duration };
            if (operation == "audio_listen") args["question"] = "Transcribe speech and identify sounds.";
            if (operation == "audio_transcribe") args["language"] = "auto";
            var model = new ScriptedModel([Done(Call("audio", operation, args.ToJsonString()))], [Done(Message("Local evidence received."))]);
            var watch = Stopwatch.StartNew();
            var run = await new AgentRunner(store, model, tools).RunAsync(session, "Analyze " + path, null, _ => { }, default);
            Assert.Equal(RunState.Completed, run.State);
            var artifact = Assert.Single(session.Artifacts); var result = JsonNode.Parse(File.ReadAllText(artifact.Path))!.AsObject();
            if (operation != "audio_classify") Assert.Equal("succeeded", result["transcription"]!["status"]!.ToString());
            if (operation != "audio_transcribe") Assert.Equal("succeeded", result["classification"]!["status"]!.ToString());
            if (path == "silence.wav") Assert.True(result["transcription"]!["noSpeech"]!.GetValue<bool>());
            if (path == "test01_20s.wav")
            {
                var reference = File.ReadAllText(Path.Combine(fixtures, "reference.txt"));
                var transcript = string.Join(' ', result["transcription"]!["segments"]!.AsArray().Select(s => s!["text"]!.ToString()));
                var rate = WordErrorRate(reference, transcript); result["referenceWordErrorRate"] = rate;
                Assert.True(rate <= .1, "Human-reference word error rate exceeded 10%: " + rate);
            }
            result["wallSeconds"] = watch.Elapsed.TotalSeconds;
            var reloaded = store.Get(session.Id)!; var call = run.Calls.Single(c => c.Name == operation);
            var recovered = tools.Recover(new(reloaded, reloaded.Runs.Last(), store, default), call)!;
            Assert.Equal(call.Result, recovered.Content);
            measurements.Add(result);
        }
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(temporary.Path, "audio", "jobs")));
        File.WriteAllText(Path.Combine(fixtures, "..", "real-tool-results.json"), measurements.ToJsonString(new() { WriteIndented = true }));
    }
    private static double WordErrorRate(string reference, string actual)
    {
        static string[] Words(string text) => Regex.Matches(text.ToLowerInvariant(), "[a-z0-9']+").Select(m => m.Value).ToArray();
        var left = Words(reference); var right = Words(actual); var costs = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var diagonal = costs[0]; costs[0] = i;
            for (var j = 1; j <= right.Length; j++)
            { var previous = costs[j]; costs[j] = Math.Min(Math.Min(costs[j] + 1, costs[j - 1] + 1), diagonal + (left[i - 1] == right[j - 1] ? 0 : 1)); diagonal = previous; }
        }
        return (double)costs[^1] / left.Length;
    }
}
