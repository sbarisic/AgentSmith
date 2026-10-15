using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AgentSmith.Core;

namespace AgentSmith.Tools;

public sealed record AudioReadiness(bool Transcription, bool Classification, string Location, string Message)
{
    public bool Ready(string tool) => tool switch { "audio_transcribe" => Transcription, "audio_classify" => Classification, _ => Transcription && Classification };
}

public static class LocalAudioInstallation
{
    public static string Bundle => Path.Combine(AppContext.BaseDirectory, "LocalAudio");
    private static string Fingerprint(string bundle) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(bundle, "downloads.json"))
        .Concat(File.ReadAllBytes(Path.Combine(bundle, "requirements.lock"))).Concat(File.ReadAllBytes(Path.Combine(bundle, "audio_helper.py"))).ToArray()));
    public static AudioReadiness Status(string dataDirectory) => Status(dataDirectory, Bundle);
    internal static AudioReadiness Status(string dataDirectory, string bundle)
    {
        var root = Path.Combine(dataDirectory, "audio");
        try
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64)
                return new(false, false, root, "Local audio requires Windows x64.");
            var missing = MissingPrerequisite();
            if (missing is not null) return new(false, false, root, missing);
            var name = File.ReadAllText(Path.Combine(root, "current")).Trim();
            if (!Guid.TryParseExact(name, "N", out _)) throw new IOException("Invalid installation pointer.");
            var directory = Path.Combine(root, "installations", name);
            var ready = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "ready.json"))) ?? throw new IOException("Invalid audio readiness record.");
            var update = ready["fingerprint"]?.ToString() != Fingerprint(bundle);
            var runtime = File.Exists(Path.Combine(directory, "env", "Scripts", "python.exe")) && File.Exists(Path.Combine(directory, "helper", "audio_helper.py"));
            var whisper = runtime && new[] { "model.bin", "config.json", "tokenizer.json", "vocabulary.txt" }.All(n => File.Exists(Path.Combine(directory, "models", "whisper", n)));
            var yamnet = runtime && new[] { "saved_model.pb", "assets/yamnet_class_map.csv", "variables/variables.index", "variables/variables.data-00000-of-00001" }.All(n => File.Exists(Path.Combine(directory, "models", "yamnet", n)));
            return new(whisper, yamnet, directory,
                $"Whisper small multilingual / faster-whisper 1.2.1 / CPU INT8: {(whisper ? "ready" : "missing files")}\nYAMNet 1 / TensorFlow CPU 2.20.0 / 521 classes: {(yamnet ? "ready" : "missing files")}\nLocation: {directory}" +
                (whisper && yamnet ? "\nInference is local and offline. Returned text and labels may enter your chat provider's context." : "\nRun smith audio setup.") +
                (update ? "\nA newer bundled helper is available; run smith audio setup. The previous installation remains usable." : ""));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        { return new(false, false, root, "Local audio is unavailable. Run smith audio setup. " + ex.Message); }
    }

    public static async Task Setup(string dataDirectory, Action<string> progress, CancellationToken cancellation)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        if (MissingPrerequisite() is { } missing) throw new UserFacingException(missing);
        try
        {
            await Setup(dataDirectory, progress, cancellation, Bundle, http,
                (exe, args, cwd, env, ct) => LocalAudioProcess.Run(exe, args, cwd, null, ct, environment: env), LocalAudioProcess.Infer);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException)
        { throw new UserFacingException("Local audio setup failed: " + ex.Message + " Previous installation retained. Inspect smith audio status, then retry smith audio setup."); }
    }
    private static string? MissingPrerequisite()
    {
        foreach (var dll in new[] { "vcruntime140.dll", "msvcp140.dll", "msvcp140_1.dll" })
            if (OperatingSystem.IsWindows() && !File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), dll)))
                return "Missing native runtime " + dll + ". Install Microsoft Visual C++ 2015–2022 x64 Redistributable (https://aka.ms/vs/17/release/vc_redist.x64.exe), then run smith audio setup.";
        return null;
    }
    internal static async Task Setup(string dataDirectory, Action<string> progress, CancellationToken cancellation, string bundle, HttpClient http,
        Func<string, string[], string, IReadOnlyDictionary<string, string>, CancellationToken, Task<string>> command,
        Func<string, JsonObject, Action<string>, CancellationToken, Task<JsonObject>> infer)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new UserFacingException("Managed audio setup requires Windows x64.");
        var root = Path.GetFullPath(Path.Combine(dataDirectory, "audio")); Directory.CreateDirectory(root);
        // Cross-process setup lock; inference continues using the previous generation.
        using var setupLock = new FileStream(Path.Combine(root, "setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var status = Status(dataDirectory, bundle);
        if (status.Transcription && status.Classification && JsonNode.Parse(File.ReadAllText(Path.Combine(status.Location, "ready.json")))?["fingerprint"]?.ToString() == Fingerprint(bundle))
        { progress(status.Message); return; }
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(root, "installations", id); Directory.CreateDirectory(directory);
        var downloads = Path.Combine(root, "downloads"); Directory.CreateDirectory(downloads);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(bundle, "downloads.json")))!;
        var files = new Dictionary<string, string>();
        try
        {
            foreach (var item in manifest["downloads"]!.AsArray())
            {
                var name = item!["path"]!.ToString(); var hash = item["sha256"]!.ToString(); var size = item["size"]!.GetValue<long>();
                var cached = Path.Combine(downloads, hash);
                if (!await Matches(cached, hash, size, cancellation))
                {
                    progress("Downloading " + name);
                    var partial = cached + ".partial";
                    using var response = await http.GetAsync(item["url"]!.ToString(), HttpCompletionOption.ResponseHeadersRead, cancellation);
                    response.EnsureSuccessStatusCode();
                    await using (var input = await response.Content.ReadAsStreamAsync(cancellation))
                    await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920]; long written = 0; int count;
                        while ((count = await input.ReadAsync(buffer, cancellation)) > 0)
                        {
                            written += count; if (written > size) throw new IOException("Download exceeds manifest size: " + name);
                            await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                        }
                    }
                    if (!await Matches(partial, hash, size, cancellation)) throw new IOException("Download checksum mismatch: " + name);
                    File.Move(partial, cached, true);
                }
                files[name] = cached;
            }
            progress("Installing private Python 3.11.14 and uv 0.9.5");
            ZipFile.ExtractToDirectory(files["uv.zip"], Path.Combine(directory, "uv"));
            ExtractTar(files["python.tar.gz"], Path.Combine(directory, "runtime"));
            var models = Path.Combine(directory, "models"); Directory.CreateDirectory(Path.Combine(models, "whisper"));
            ExtractTar(files["yamnet.tar.gz"], Path.Combine(models, "yamnet"));
            foreach (var file in files.Where(f => f.Key.StartsWith("whisper/", StringComparison.Ordinal)))
                File.Copy(file.Value, Path.Combine(models, file.Key));
            if (!await Matches(Path.Combine(models, "yamnet", "assets", "yamnet_class_map.csv"), manifest["classMapSha256"]!.ToString(), null, cancellation))
                throw new IOException("YAMNet class map checksum mismatch.");
            var helper = Path.Combine(directory, "helper"); Directory.CreateDirectory(helper);
            foreach (var name in new[] { "audio_helper.py", "requirements.lock", "downloads.json" }) File.Copy(Path.Combine(bundle, name), Path.Combine(helper, name));
            var uv = Directory.GetFiles(Path.Combine(directory, "uv"), "uv.exe", SearchOption.AllDirectories).Single();
            var environment = new Dictionary<string, string> { ["UV_CACHE_DIR"] = Path.Combine(root, "cache"), ["UV_PYTHON_INSTALL_DIR"] = Path.Combine(root, "python"), ["UV_NO_PROGRESS"] = "1" };
            await command(uv, ["--no-config", "venv", "--no-python-downloads", "--python", Path.Combine(directory, "runtime", "python", "python.exe"), Path.Combine(directory, "env")], directory, environment, cancellation);
            progress("Installing hash-locked Windows dependencies");
            await command(uv, ["--no-config", "pip", "sync", Path.Combine(helper, "requirements.lock"), "--python", Path.Combine(directory, "env", "Scripts", "python.exe"), "--require-hashes", "--only-binary", ":all:", "--index-url", "https://pypi.org/simple"], directory, environment, cancellation);
            progress("Running offline self-tests for both engines");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var result = await infer(directory, new() { ["protocol"] = 1, ["operation"] = "selftest", ["models"] = models }, progress, timeout.Token);
            if (result["transcription"]?["status"]?.ToString() != "succeeded" || result["classification"]?["status"]?.ToString() != "succeeded")
                throw new IOException("Audio self-test failed: " + result.ToJsonString()[..Math.Min(1800, result.ToJsonString().Length)]);
            await File.WriteAllTextAsync(Path.Combine(directory, "ready.json"), Json.Serialize(new { fingerprint = Fingerprint(bundle), installedAt = DateTimeOffset.UtcNow }), cancellation);
            // Generation paths never move: venv and SavedModel asset paths remain valid.
            var pointer = Path.Combine(root, "current.new"); await File.WriteAllTextAsync(pointer, id, cancellation);
            File.Move(pointer, Path.Combine(root, "current"), true);
            File.Delete(Path.Combine(root, "last-error.txt"));
            progress(Status(dataDirectory, bundle).Message);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "last-error.txt"), ex.Message, CancellationToken.None);
            // Preserve previous current and resumable verified downloads. Incomplete generation is not advertised.
            throw;
        }
    }

    internal static async Task<bool> Matches(string path, string hash, long? size, CancellationToken cancellation)
    {
        if (!File.Exists(path)) return false;
        await using var file = File.OpenRead(path);
        return (size is null || file.Length == size) && Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }
    private static void ExtractTar(string path, string target)
    {
        Directory.CreateDirectory(target);
        using var file = File.OpenRead(path); using var gzip = new GZipStream(file, CompressionMode.Decompress); using var tar = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
                throw new IOException("Unsupported link or special entry in audio archive.");
            var destination = Path.GetFullPath(Path.Combine(target, entry.Name));
            if (!destination.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe archive path.");
            if (entry.EntryType == TarEntryType.Directory) Directory.CreateDirectory(destination);
            else { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); entry.ExtractToFile(destination, false); }
        }
    }
}
