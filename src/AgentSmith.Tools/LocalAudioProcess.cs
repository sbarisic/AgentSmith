using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Core;

namespace AgentSmith.Tools;

/// <summary>Fixed helper transport, shared with explicit installation. Owned Windows job, bounded output.</summary>
internal static class LocalAudioProcess
{
    internal const int OutputLimit = 2 * 1024 * 1024;
    internal static async Task<string> Run(string executable, IReadOnlyList<string> arguments, string directory,
        string? input, CancellationToken cancellation, Action<string>? progress = null, bool protocol = false,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var child = WindowsChild.Start(executable, arguments, directory, environment ?? new Dictionary<string, string>
        {
            ["HF_HUB_OFFLINE"] = "1", ["TRANSFORMERS_OFFLINE"] = "1", ["HF_HUB_DISABLE_TELEMETRY"] = "1",
            ["OPENAI_API_KEY"] = "", ["SMITH_AUDIO_MODEL"] = "", ["PYTHONNOUSERSITE"] = "1"
        });
        using var stop = cancellation.Register(() => { try { child.Stop(); } catch (IOException) { } });
        async Task<string> Read(StreamReader reader, bool events)
        {
            var content = new StringBuilder(); var line = new StringBuilder(); var buffer = new char[4096];
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), cancellation);
                if (count == 0) break;
                if (content.Length + count > OutputLimit) throw new UserFacingException("Local audio helper output exceeded 2 MiB.");
                content.Append(buffer, 0, count);
                if (events)
                    for (var i = 0; i < count; i++)
                    {
                        if (buffer[i] != '\n') { line.Append(buffer[i]); continue; }
                        var value = JsonNode.Parse(line.ToString()); line.Clear();
                        if (value?["protocol"]?.GetValue<int>() != 1) throw new UserFacingException("Invalid local audio protocol.");
                        if (value["type"]?.ToString() == "progress") progress?.Invoke(value["stage"]?.ToString() ?? "processing");
                    }
            }
            return content.ToString();
        }
        // Pipe reads must start on separate threads: WindowsChild owns synchronous native pipe handles.
        var stdout = Task.Run(() => Read(child.Output, protocol), CancellationToken.None);
        var stderr = Task.Run(() => Read(child.Error, false), CancellationToken.None);
        try
        {
            if (input is not null) await child.Input.WriteLineAsync(input.AsMemory(), cancellation);
            child.Input.Close();
            while (!child.Exited)
            {
                cancellation.ThrowIfCancellationRequested();
                if (stdout.IsFaulted) await stdout;
                if (stderr.IsFaulted) await stderr;
                await Task.Delay(30, cancellation);
            }
            var text = await stdout; var error = await stderr;
            cancellation.ThrowIfCancellationRequested();
            if (child.ExitCode != 0)
                throw new UserFacingException("Local audio helper failed (exit " + child.ExitCode + "). " +
                    (protocol ? text[^Math.Min(text.Length, 800)..] : error[^Math.Min(error.Length, 1200)..]) +
                    " If a DLL is missing, install the Microsoft Visual C++ 2015–2022 x64 Redistributable, then run smith audio setup.");
            return text;
        }
        finally
        {
            child.Stop();
            try { await Task.WhenAll(stdout, stderr); } catch { /* Preserve the initiating failure. */ }
        }
    }

    internal static async Task<JsonObject> Infer(string directory, JsonObject request, Action<string> progress, CancellationToken cancellation)
    {
        var text = await Run(Path.Combine(directory, "env", "Scripts", "python.exe"),
            ["-I", "-u", Path.Combine(directory, "helper", "audio_helper.py")], directory, request.ToJsonString(), cancellation, progress, true);
        try
        {
            var messages = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => JsonNode.Parse(s)!).ToArray();
            var results = messages.Where(m => m["type"]?.ToString() == "result").ToArray();
            if (results.Length != 1 || messages.Any(m => m["protocol"]?.GetValue<int>() != 1 || m["type"]?.ToString() is not ("progress" or "result")))
                throw new UserFacingException("Local audio helper returned an invalid or incomplete result.");
            var result = results[0]["result"]?.DeepClone().AsObject() ?? throw new UserFacingException("Local audio helper returned no result.");
            Validate(result, request["operation"]!.ToString());
            return result;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException or NullReferenceException or ArgumentException)
        { throw new UserFacingException("Local audio helper returned malformed evidence; no result was retained."); }
    }

    internal static void Validate(JsonObject result, string operation)
    {
        double Number(JsonNode? node, double min, double max)
        {
            var value = node?.GetValue<double>() ?? double.NaN;
            if (!double.IsFinite(value) || value < min || value > max) throw new UserFacingException("Local audio helper returned an invalid score or time.");
            return value;
        }
        var start = Number(result["startSeconds"], 0, int.MaxValue);
        var duration = Number(result["durationSeconds"], double.Epsilon, 120);
        foreach (var name in new[] { "transcription", "classification" })
        {
            if (operation == "audio_transcribe" && name == "classification" || operation == "audio_classify" && name == "transcription") continue;
            var component = result[name] ?? throw new UserFacingException("Local audio helper omitted a component outcome.");
            if (component["status"]?.ToString() == "failed") continue;
            if (component["status"]?.ToString() != "succeeded") throw new UserFacingException("Invalid audio component status.");
            if (name == "transcription")
            {
                var segments = component["segments"]!.AsArray();
                if (segments.Count > 2000) throw new UserFacingException("Too many transcript segments.");
                foreach (var segment in segments)
                {
                    var from = Number(segment!["startSeconds"], start, start + duration + .1);
                    Number(segment["endSeconds"], from, start + duration + .1);
                    if (segment["text"]?.GetValue<string>() is not { Length: <= 16000 }) throw new UserFacingException("Invalid transcript text.");
                }
            }
            else
            {
                if (component["classMap"]!.AsArray().Count != 521 || component["aggregateScores"]!.AsArray().Count != 521 || component["windows"]!.AsArray().Count > 250)
                    throw new UserFacingException("Invalid YAMNet class map or window count.");
                void Labels(JsonArray labels) { foreach (var label in labels) { Number(label!["score"], 0, 1); Number(label["index"], 0, 520); if (label["label"]?.GetValue<string>() is not { Length: > 0 and <= 200 }) throw new UserFacingException("Invalid YAMNet label."); } }
                Labels(component["aggregateScores"]!.AsArray());
                foreach (var window in component["windows"]!.AsArray())
                {
                    var from = Number(window!["startSeconds"], start, start + duration);
                    Number(window["endSeconds"], from, start + duration);
                    var labels = window["labels"]!.AsArray(); if (labels.Count != 5) throw new UserFacingException("Invalid YAMNet window."); Labels(labels);
                }
            }
        }
    }
}
