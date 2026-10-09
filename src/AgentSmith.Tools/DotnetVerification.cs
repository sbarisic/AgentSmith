using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using AgentSmith.Core;

namespace AgentSmith.Tools;

public static class DotnetVerification
{
    public static PreparedOperation Prepare(ToolExecutionContext ctx, ToolCall call, JsonObject args, ProcessManager processes)
    {
        var project = WorkspaceTools.Resolve(ctx.Workspace, args["project"]!.GetValue<string>());
        if (!File.Exists(project) || Path.GetExtension(project).ToLowerInvariant() is not (".csproj" or ".fsproj" or ".vbproj" or ".sln" or ".slnx")) throw new UserFacingException("Select a workspace project or solution file.");
        ValidateSdk(ctx.Workspace, ProcessManager.ResolveExecutable("dotnet"), call.Name == "dotnet_test");
        if (call.Name == "dotnet_test") ValidateVstest(ctx.Workspace, project);
        var configuration = args["configuration"]!.GetValue<string>(); if (configuration.Length == 0) configuration = "Debug";
        var framework = args["framework"]!.GetValue<string>();
        if (configuration.Length > 80 || framework.Length > 80 || configuration.StartsWith('-') || framework.StartsWith('-')) throw new UserFacingException("Invalid build configuration/framework.");
        var verification = new VerificationRecord { Kind = call.Name == "dotnet_build" ? "build" : "test" };
        verification.Directory = Path.Combine(ctx.ArtifactDirectory, verification.Id);
        var command = new List<string> { verification.Kind == "build" ? "build" : "test", project, "--configuration", configuration, "--nologo", "--verbosity", "minimal", "--disable-build-servers", "-p:UseSharedCompilation=false", "-nodeReuse:false" };
        if (framework.Length > 0) command.AddRange(["--framework", framework]);
        if (verification.Kind == "build")
        {
            var logger = Path.Combine(AppContext.BaseDirectory, "AgentSmith.BuildLogger.dll");
            if (!File.Exists(logger)) throw new UserFacingException("The bundled build diagnostic logger is missing. Republish the complete CLI folder.");
            command.Add("-logger:AgentSmith.BuildLogger.DiagnosticLogger," + logger + ";" + Path.Combine(verification.Directory, "diagnostics.jsonl"));
        }
        else
        {
            command.AddRange(["--logger", "trx;LogFilePrefix=smith-" + verification.Id, "--results-directory", verification.Directory]);
            var filter = args["filter"]!.GetValue<string>(); if (filter.Length > 4000) throw new UserFacingException("Test filter exceeds the limit.");
            if (filter.Length > 0) command.AddRange(["--filter", filter]);
        }
        var processArgs = new JsonObject { ["executable"] = "dotnet", ["arguments"] = new JsonArray(command.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()), ["workingDirectory"] = ".", ["environment"] = new JsonArray(new JsonObject { ["name"] = "MSBUILDDISABLENODEREUSE", ["value"] = "1" }, new JsonObject { ["name"] = "DOTNET_CLI_USE_MSBUILD_SERVER", ["value"] = "0" }), ["timeoutSeconds"] = args["timeoutSeconds"]!.GetValue<int>() };
        var prepared = processes.Prepare(ctx, new ToolCall { Name = "process_start" }, processArgs);
        verification.ProcessId = prepared.OperationId!; ctx.Session.Verifications.Add(verification);
        return prepared;
    }
    private static void ValidateSdk(string workspace, string dotnet, bool test)
    {
        var sdkRoot = Path.Combine(Path.GetDirectoryName(dotnet)!, "sdk");
        if (!Directory.Exists(sdkRoot) || !Directory.EnumerateDirectories(sdkRoot, "10.*").Any(d => File.Exists(Path.Combine(d, "Microsoft.Build.Framework.dll")))) throw new UserFacingException("Structured verification requires an installed .NET 10 SDK.");
        var hasGlobal = false;
        for (var directory = new DirectoryInfo(workspace); directory is not null; directory = directory.Parent)
        {
            var global = Path.Combine(directory.FullName, "global.json"); if (!File.Exists(global)) continue;
            var json = JsonNode.Parse(FileOperations.ReadBytes(global), documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var version = json?["sdk"]?["version"]?.GetValue<string>();
            hasGlobal = version is not null;
            if (version is not null && !version.StartsWith("10.", StringComparison.Ordinal)) throw new UserFacingException("This project selects an unsupported SDK. Use process tools or select .NET 10 explicitly.");
            if (json?["sdk"]?["rollForward"]?.GetValue<string>()?.ToLowerInvariant() is "major" or "latestmajor") throw new UserFacingException("Structured verification requires an SDK selection restricted to .NET 10, without major roll-forward.");
            if (test && json?["test"]?["runner"]?.GetValue<string>() == "Microsoft.Testing.Platform") throw new UserFacingException("Microsoft.Testing.Platform structured execution is unsupported; use process tools.");
            break;
        }
        if (!hasGlobal)
        {
            var installed = Directory.EnumerateDirectories(sdkRoot).Select(d => Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : null).Where(v => v is not null).OrderDescending().FirstOrDefault();
            if (installed?.Major != 10) throw new UserFacingException("The default SDK is not .NET 10. Select it with global.json or use process tools.");
        }
    }
    private static void ValidateVstest(string workspace, string project)
    {
        // Inspect configuration without evaluating MSBuild, which can execute project code.
        var files = new List<string> { project }; var pending = new Stack<string>(); pending.Push(workspace); var visited = 0;
        while (pending.TryPop(out var dir))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
            {
                if (++visited > 10000) throw new UserFacingException("Project configuration scan exceeds its bound; use process tools.");
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                if (Directory.Exists(path)) { if (Path.GetFileName(path) is not (".git" or "bin" or "obj" or "node_modules" or "artifacts")) pending.Push(path); }
                else if (Path.GetExtension(path) is ".csproj" or ".props" or ".targets") files.Add(path);
            }
        }
        foreach (var path in files.Distinct())
        {
            var text = FileOperations.Decode(FileOperations.ReadBytes(path));
            if (text.Contains("Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(text, @"<(TestingPlatformDotnetTestSupport|UseMicrosoftTestingPlatformRunner)>\s*true", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) throw new UserFacingException("Microsoft.Testing.Platform configuration detected; structured tests currently support VSTest only.");
        }
    }
    public static async Task<ToolOutput> Start(ToolExecutionContext ctx, ToolCall call, ProcessManager processes)
    {
        var verification = ctx.Session.Verifications.Single(v => v.ProcessId == call.Prepared!.OperationId);
        ArtifactAccess.Validate(ctx, Path.Combine(verification.Directory, "placeholder")); Directory.CreateDirectory(verification.Directory);
        var output = await processes.ExecuteAsync(ctx, new ToolCall { Name = "process_start", Prepared = call.Prepared });
        var result = JsonNode.Parse(output.Content)!.AsObject(); result["verificationId"] = verification.Id;
        ArtifactAccess.Backfill(ctx); return new(result.ToJsonString());
    }
    public static ToolOutput Result(ToolExecutionContext ctx, string id)
    {
        var verification = ctx.Session.Verifications.SingleOrDefault(v => v.Id == id) ?? throw new UserFacingException("Verification is not registered to this conversation.");
        if (verification.Result is not null) return new(verification.Result);
        var process = ctx.Session.Processes.Single(p => p.Id == verification.ProcessId);
        if (process.State is "Running" or "Prepared") return new(Json.Serialize(new { verificationId = id, state = "pending", processId = process.Id }));
        var diagnostics = new List<JsonObject>(); var tests = new List<object>(); var artifacts = new List<string>();
        var state = "complete"; var reason = ""; var total = 0; var passed = 0; var failed = 0; var skipped = 0;
        try
        {
            ArtifactAccess.Validate(ctx, Path.Combine(verification.Directory, "placeholder"));
            var files = Directory.Exists(verification.Directory) ? Directory.GetFiles(verification.Directory, verification.Kind == "build" ? "diagnostics.jsonl" : "*.trx") : [];
            if (files.Length == 0) { state = "unavailable"; reason = "No structured report was produced."; }
            if (files.Length > 100 || files.Sum(f => new FileInfo(f).Length) > ArtifactAccess.MaxBytes) throw new InvalidDataException("Structured reports exceed the 20 MiB/100-file limit.");
            foreach (var path in files)
            {
                var artifact = ArtifactAccess.Register(ctx, path, verification.Kind == "build" ? "build-diagnostics" : "test-report", process.Id); artifacts.Add(artifact.Id);
                if (verification.Kind == "build")
                {
                    var finished = false;
                    foreach (var line in File.ReadLines(path))
                    {
                        var item = JsonNode.Parse(line)!.AsObject(); var type = item["type"]!.GetValue<string>();
                        if (type is "error" or "warning") diagnostics.Add(item);
                        if (type == "finished") finished = true;
                        if (type == "truncated" || item["truncated"]?.GetValue<bool>() == true) throw new InvalidDataException("Build diagnostic report is truncated.");
                    }
                    if (!finished) throw new InvalidDataException("Build report has no completion event.");
                }
                else
                {
                    using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = ArtifactAccess.MaxBytes });
                    var document = XDocument.Load(reader); XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
                    if (document.Root?.Name != ns + "TestRun") throw new InvalidDataException("Not a supported TRX report.");
                    var counters = document.Descendants(ns + "Counters").SingleOrDefault() ?? throw new InvalidDataException("TRX counters missing.");
                    int Count(string key) => int.TryParse((string?)counters.Attribute(key), out var number) && number >= 0 ? number : throw new InvalidDataException("Invalid TRX counters.");
                    var results = document.Descendants(ns + "UnitTestResult").ToArray();
                    if (results.Length != Count("total") || Count("passed") + (long)Count("failed") > Count("total")) throw new InvalidDataException("TRX counts do not match its test records.");
                    total = checked(total + Count("total")); passed = checked(passed + Count("passed")); failed = checked(failed + Count("failed"));
                    skipped += results.Count(e => (string?)e.Attribute("outcome") == "NotExecuted");
                    foreach (var test in results) tests.Add(new { name = (string?)test.Attribute("testName"), outcome = (string?)test.Attribute("outcome"), duration = (string?)test.Attribute("duration"), message = test.Descendants(ns + "Message").FirstOrDefault()?.Value, stackTrace = test.Descendants(ns + "StackTrace").FirstOrDefault()?.Value });
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or XmlException or System.Text.Json.JsonException or InvalidOperationException or NullReferenceException or UnauthorizedAccessException or OverflowException)
        { state = "incomplete"; reason = "Report parsing failed or exceeded its bounds; inspect the retained artifacts."; }
        if (process.State is "Interrupted" or "Stopped" or "Failed" || !process.TerminationConfirmed) { state = "incomplete"; reason = "Process outcome was interrupted or unconfirmed."; }
        if (state == "complete" && verification.Kind == "test" && total == 0) state = "no_tests";
        var full = Json.Serialize(new { verificationId = id, kind = verification.Kind, state, reason, processId = process.Id, processState = process.State, process.ExitCode, process.DurationMilliseconds, process.TerminationConfirmed, total, passed, failed, skipped, diagnostics, tests, artifacts });
        var summaryPath = ArtifactAccess.Validate(ctx, Path.Combine(verification.Directory, "summary.json")); Directory.CreateDirectory(verification.Directory);
        if (Encoding.UTF8.GetByteCount(full) <= ArtifactAccess.MaxBytes) File.WriteAllText(summaryPath, full, new UTF8Encoding(false));
        else { state = "incomplete"; reason = "Normalized report exceeds the artifact size limit."; File.WriteAllText(summaryPath, Json.Serialize(new { state, reason })); }
        var summary = ArtifactAccess.Register(ctx, summaryPath, "verification", process.Id);
        var compact = new JsonObject { ["verificationId"] = id, ["kind"] = verification.Kind, ["state"] = state, ["reason"] = reason, ["processId"] = process.Id, ["processState"] = process.State, ["exitCode"] = process.ExitCode, ["durationMilliseconds"] = process.DurationMilliseconds, ["terminationConfirmed"] = process.TerminationConfirmed, ["total"] = total, ["passed"] = passed, ["failed"] = failed, ["skipped"] = skipped, ["summaryArtifactId"] = summary.Id, ["sourceArtifactIds"] = new JsonArray(artifacts.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()) };
        var items = new JsonArray();
        foreach (var item in diagnostics.Cast<object>().Concat(tests)) { var node = JsonNode.Parse(Json.Serialize(item)); if (items.ToJsonString().Length + node!.ToJsonString().Length > 12000) break; items.Add(node); }
        compact["items"] = items; compact["moreItems"] = items.Count < diagnostics.Count + tests.Count;
        verification.Result = compact.ToJsonString(); ctx.Save("verification.completed", new { verification.Id, state }); return new(verification.Result);
    }
}
