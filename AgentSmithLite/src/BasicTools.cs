using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Tools;

namespace AgentSmithLite;

internal sealed class BasicTools(string workspace, Func<string, JsonObject, CancellationToken, Task<bool>> approve)
{
    private readonly string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
    public const int OutputLimit = 16000;
    public static readonly string[] Names = ["list_files", "read_file", "write_file", "run_process", "start_app"];

    public static JsonArray Definitions()
    {
        JsonObject Define(string name, string description, params (string Name, string Type)[] fields)
        {
            var properties = new JsonObject(); var required = new JsonArray();
            foreach (var (field, type) in fields)
            {
                properties[field] = type == "array"
                    ? new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
                    : new JsonObject { ["type"] = type };
                required.Add(field);
            }
            return new JsonObject
            {
                ["type"] = "function", ["name"] = name, ["description"] = description, ["strict"] = true,
                ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false }
            };
        }
        return new JsonArray(
            Define("list_files", "List up to 200 entries in a workspace-relative directory. Use . for workspace root. Does not recurse.", ("path", "string")),
            Define("read_file", "Read up to 16000 characters of a workspace-relative text file.", ("path", "string")),
            Define("write_file", "Create or replace a workspace-relative UTF-8 text file (up to 32000 characters). No undo. Requires approval.", ("path", "string"), ("content", "string")),
            Define("run_process", "Run a console executable with separate arguments, capture bounded output, and wait. Use cmd.exe /c or powershell.exe -NoProfile -Command for scripts. workingDirectory is workspace-relative; timeoutSeconds must be 1..120. Process tree is killed on timeout/cancellation or CLI exit. Runs as the user, not sandboxed. Requires approval.", ("executable", "string"), ("arguments", "array"), ("workingDirectory", "string"), ("timeoutSeconds", "integer")),
            Define("start_app", "Launch a desktop .exe with separate arguments. workingDirectory is workspace-relative. The app stays open after this chat exits. Returns launch status, not proof the app is ready. Requires approval.", ("executable", "string"), ("arguments", "array"), ("workingDirectory", "string")));
    }

    public async Task<string> ExecuteAsync(string name, string arguments, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (arguments.Length > 128000) throw new UserFacingException("Tool arguments exceed the size limit.");
            var args = JsonNode.Parse(arguments)?.AsObject() ?? throw new UserFacingException("Expected an argument object.");
            Validate(name, args);
            // Prepare and validate the actual target before asking for approval.
            var path = name is "read_file" or "write_file" or "list_files" ? Resolve(Text(args, "path")) : Resolve(Text(args, "workingDirectory"));
            string? executable = null;
            string[] argv = [];
            if (name is "run_process" or "start_app")
            {
                if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Working directory does not exist.");
                executable = ResolveExecutable(Text(args, "executable"), path);
                argv = args["arguments"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
                if (argv.Length > 128 || argv.Any(a => a.Contains('\0')) || argv.Sum(a => a.Length) > 16000)
                    throw new UserFacingException("Too many or invalid process arguments.");
                args["executable"] = executable;
            }
            if (name == "run_process" && args["timeoutSeconds"]!.GetValue<int>() is < 1 or > 120)
                throw new UserFacingException("timeoutSeconds must be 1..120.");
            if (name == "write_file" && Text(args, "content").Length > 32000)
                throw new UserFacingException("File content exceeds 32000 characters.");
            if (name is "write_file" or "run_process" or "start_app")
            {
                if (!await approve(name, args, ct)) return Json.Serialize(new { status = "denied", message = "User declined; nothing was executed." });
                ct.ThrowIfCancellationRequested();
                // Recheck directory links after an arbitrarily long approval wait.
                Resolve(Path.GetRelativePath(root, path));
            }
            switch (name)
            {
                case "list_files":
                    var entries = Directory.EnumerateFileSystemEntries(path).Take(201).ToArray();
                    return Json.Serialize(new { entries = entries.Take(200).Select(p => new { name = Path.GetFileName(p), directory = Directory.Exists(p) }), truncated = entries.Length > 200 });
                case "read_file":
                    using (var reader = new StreamReader(path, Encoding.UTF8, true))
                    {
                        var buffer = new char[OutputLimit + 1]; var count = 0;
                        while (count < buffer.Length)
                        {
                            var read = await reader.ReadAsync(buffer.AsMemory(count), ct);
                            if (read == 0) break;
                            count += read;
                        }
                        return Json.Serialize(new { content = new string(buffer, 0, Math.Min(count, OutputLimit)), truncated = count > OutputLimit });
                    }
                case "write_file":
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var temporary = path + ".lite-" + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        await File.WriteAllTextAsync(temporary, Text(args, "content"), new UTF8Encoding(false), ct);
                        File.Move(temporary, path, true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    return Json.Serialize(new { status = "written", path = Path.GetRelativePath(root, path) });
                case "run_process":
                    return await RunAsync(executable!, argv, path, args["timeoutSeconds"]!.GetValue<int>(), ct);
                case "start_app":
                    var start = new ProcessStartInfo(executable!) { UseShellExecute = true, WorkingDirectory = path };
                    foreach (var arg in argv) start.ArgumentList.Add(arg);
                    using (var app = Process.Start(start))
                        return Json.Serialize(new { status = "launch_requested", processId = app?.Id, message = "App is independent of this chat; readiness is not verified." });
                default: throw new UserFacingException("Unknown tool.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or UserFacingException or NotSupportedException)
        {
            return Json.Serialize(new { status = "error", message = ex.Message });
        }
    }

    private static string Text(JsonObject args, string key) => args[key]!.GetValue<string>();
    private static void Validate(string name, JsonObject args)
    {
        var definition = Definitions().OfType<JsonObject>().SingleOrDefault(d => d["name"]!.GetValue<string>() == name)
            ?? throw new UserFacingException("Unknown tool: " + name);
        var fields = definition["parameters"]!["properties"]!.AsObject();
        if (args.Count != fields.Count || args.Any(p => !fields.ContainsKey(p.Key))) throw new UserFacingException("Tool fields do not match its schema.");
        foreach (var (key, schema) in fields)
        {
            var value = args[key];
            var valid = schema!["type"]!.GetValue<string>() switch
            {
                "string" => value is JsonValue s && s.TryGetValue<string>(out _),
                "integer" => value is JsonValue n && n.TryGetValue<int>(out _),
                "array" => value is JsonArray a && a.All(x => x is JsonValue v && v.TryGetValue<string>(out _)),
                _ => false
            };
            if (!valid) throw new UserFacingException("Invalid tool field: " + key);
        }
    }

    internal string Resolve(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new UserFacingException("Use a workspace-relative path.");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException("Path leaves the workspace.");
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UserFacingException("Linked files and directories are not supported by file tools.");
        }
        return full;
    }

    internal static string ResolveExecutable(string name, string cwd)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\0')) throw new UserFacingException("Executable is required.");
        var candidates = Path.IsPathRooted(name) || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar)
            ? new[] { Path.GetFullPath(name, cwd) }
            : new[] { cwd }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                .Select(p => Path.Combine(p.Trim('"'), Path.HasExtension(name) ? name : name + ".exe"));
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is null || !Path.GetExtension(found).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException("Executable not found. Use an installed .exe or its full path; use cmd.exe/powershell.exe explicitly for scripts.");
        return Path.GetFullPath(found);
    }

    private static async Task<string> RunAsync(string exe, string[] args, string cwd, int seconds, CancellationToken ct)
    {
        string? commandLine = null;
        if (Path.GetFileName(exe).Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            // cmd.exe parses a command string, not CRT argv. Quoting /c as an argv item
            // breaks its quote stripping. Keep one explicit script after /c, disable AutoRun.
            var index = Array.FindIndex(args, a => a.Equals("/c", StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index != args.Length - 2 || args.Take(index).Any(a => !a.Equals("/d", StringComparison.OrdinalIgnoreCase) && !a.Equals("/s", StringComparison.OrdinalIgnoreCase)))
                throw new UserFacingException("Use cmd.exe with arguments [/c, SCRIPT] (optional /d or /s before /c).");
            commandLine = WindowsChild.Quote(exe) + " /d /s /c \"" + args[index + 1] + "\"";
        }
        using var child = WindowsChild.Start(exe, args, cwd, new Dictionary<string, string>(), commandLine);
        child.Input.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        var stdout = Capture(child.Output); var stderr = Capture(child.Error);
        var timedOut = false;
        try
        {
            while (!child.TreeExited) await Task.Delay(30, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            child.Stop();
            timedOut = !ct.IsCancellationRequested;
        }
        // Closing the job kills every owned descendant, including inherited pipe holders.
        try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { throw new UserFacingException("Process output did not close after termination; the process job is being closed."); }
        ct.ThrowIfCancellationRequested();
        return Json.Serialize(new { status = timedOut ? "timed_out" : "exited", exitCode = child.Exited ? (int?)child.ExitCode : null,
            stdout = stdout.Result.Text, stderr = stderr.Result.Text, truncated = stdout.Result.Truncated || stderr.Result.Truncated });
    }

    private static async Task<(string Text, bool Truncated)> Capture(StreamReader reader)
    {
        var result = new StringBuilder(); var buffer = new char[4096]; var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory());
            if (count == 0) break;
            var keep = Math.Min(count, OutputLimit - result.Length);
            result.Append(buffer, 0, keep); truncated |= keep < count;
        }
        return (result.ToString(), truncated);
    }
}
