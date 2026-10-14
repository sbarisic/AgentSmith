using AgentSmith.Core;
using AgentSmith.Storage;
using AgentSmith.Tools;
using System.Text.Json.Nodes;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class NavigationIntegrationTests
{
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "smith-navigation-" + Guid.NewGuid().ToString("N"));
        public ToolRegistry Tools { get; } = new();
        public ToolExecutionContext Context { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            var store = new SessionStore(Path.Combine(Root, "state"));
            var session = new Session { Workspace = Root }; var run = new Run(); session.Runs.Add(run);
            store.Save(session, new("created", session.Id, run.Id, new { })); Context = new(session, run, store, default);
            File.WriteAllText(Path.Combine(Root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(Root, "C.cs"), "public class C { public int M() => 1; public int N() => M(); }");
            File.WriteAllText(Path.Combine(Root, "unrelated.txt"), "keep this user edit");
        }
        public async Task<JsonNode> Invoke(string name, object args, ToolExecutionContext? context = null)
        {
            var ctx = context ?? Context;
            var call = new ToolCall { Name = name, Arguments = Json.Serialize(args), Id = Guid.NewGuid().ToString("N") };
            call.Prepared = Tools.Prepare(ctx, call); ctx.Save("intent", new { call.Id });
            return JsonNode.Parse((await Tools.ExecuteAsync(ctx, call)).Content)!;
        }
        public async Task<JsonNode> Load(string project = "App.csproj", string framework = "")
        {
            var start = await Invoke("csharp_load", new { project, configuration = "", framework });
            await Invoke("process_wait", new { processId = start["processId"]!.ToString(), cursor = 0, waitMilliseconds = 60000, until = "exit" });
            return await Invoke("csharp_status", new { snapshotId = start["snapshotId"]!.ToString() });
        }
        public void Dispose() { Tools.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); }
    }

    [Fact]
    public async Task OwnedHelperLoadsWithoutRestoreSharesReadOnlyAndRejectsChangedEvidenceAndRestart()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture();
        var status = await f.Load(); var id = status["snapshotId"]!.ToString();
        Assert.True(status["state"]!.ToString() is "Ready" or "Partial", status.ToJsonString());
        Assert.False(File.Exists(Path.Combine(f.Root, "obj", "project.assets.json")));
        var references = await f.Invoke("csharp_query", new { snapshotId = id, path = "C.cs", line = 1, column = 29, operation = "references", cursor = "" });
        Assert.Equal(1, references["total"]!.GetValue<int>());
        var child = new Session { Workspace = f.Root, ParentConversationId = f.Context.Session.Id, Mode = PermissionMode.Inspect }; var childRun = new Run(); child.Runs.Add(childRun);
        var childContext = f.Context with { Session = child, Run = childRun };
        await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("csharp_status", new { snapshotId = id }, childContext));
        await f.Tools.Navigation.ValidateGrants(f.Context, [id]); child.NavigationSnapshotIds = [id];
        var before = Json.Serialize(f.Context.Store.Get(f.Context.Session.Id));
        await f.Invoke("csharp_status", new { snapshotId = id }, childContext);
        Assert.Equal(before, Json.Serialize(f.Context.Store.Get(f.Context.Session.Id)));
        File.AppendAllText(Path.Combine(f.Root, "C.cs"), "\nclass Added {}");
        await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("csharp_query", new { snapshotId = id, path = "C.cs", line = 1, column = 29, operation = "references", cursor = "" }, childContext));
        status = await f.Invoke("csharp_status", new { snapshotId = id }); Assert.Equal("Stale", status["state"]!.ToString());
        Assert.Equal("keep this user edit", File.ReadAllText(Path.Combine(f.Root, "unrelated.txt")));
        using var restarted = new ToolRegistry();
        var result = await restarted.Navigation.Read(f.Context, "csharp_status", new JsonObject { ["snapshotId"] = id });
        Assert.Contains("Unavailable", result.Content); Assert.Contains("restart", result.Content);
    }

    [Fact]
    public async Task MultiTargetNeedsSelectionAndSolutionLoadingTracksConfiguration()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture();
        var project = Path.Combine(f.Root, "App.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks></PropertyGroup></Project>");
        var result = await f.Load(); Assert.Equal("Unavailable", result["state"]!.ToString()); Assert.Contains("framework", result["reason"]!.ToString());
        File.WriteAllText(Path.Combine(f.Root, "App.slnx"), "<Solution><Project Path=\"App.csproj\" /></Solution>");
        result = await f.Load("App.slnx", "net10.0"); Assert.True(result["state"]!.ToString() is "Ready" or "Partial", result.ToJsonString());
        Assert.Equal("net10.0", result["framework"]!.ToString());
        File.WriteAllText(Path.Combine(f.Root, "Directory.Build.props"), "<Project><PropertyGroup><DefineConstants>CHANGED</DefineConstants></PropertyGroup></Project>");
        result = await f.Invoke("csharp_status", new { snapshotId = result["snapshotId"]!.ToString() }); Assert.Equal("Stale", result["state"]!.ToString());
    }

    [Fact]
    public async Task LegacySolutionExcludesOutsideSourceAndQueryReturnsNoOutsideLocations()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); using var outside = new Fixture();
        File.WriteAllText(Path.Combine(outside.Root, "Outside.cs"), "public class OutsideOnly {}");
        var link = System.Security.SecurityElement.Escape(Path.Combine(outside.Root, "Outside.cs"));
        File.WriteAllText(Path.Combine(f.Root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"" + link + "\" Link=\"Outside.cs\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(f.Root, "App.sln"), """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App.csproj", "{A0508CC0-EFF0-40D0-B41C-73D60FBE5D30}"
            EndProject
            Global
                GlobalSection(SolutionConfigurationPlatforms) = preSolution
                    Debug|Any CPU = Debug|Any CPU
                EndGlobalSection
                GlobalSection(ProjectConfigurationPlatforms) = postSolution
                    {A0508CC0-EFF0-40D0-B41C-73D60FBE5D30}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                    {A0508CC0-EFF0-40D0-B41C-73D60FBE5D30}.Debug|Any CPU.Build.0 = Debug|Any CPU
                EndGlobalSection
            EndGlobal
            """);
        var status = await f.Load("App.sln"); Assert.Equal("Partial", status["state"]!.ToString());
        Assert.Contains("omitted", status.ToJsonString());
        await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("csharp_query", new { snapshotId = status["snapshotId"]!.ToString(), path = Path.Combine(outside.Root, "Outside.cs"), line = 1, column = 14, operation = "definitions", cursor = "" }));
        Assert.Equal("public class OutsideOnly {}", File.ReadAllText(Path.Combine(outside.Root, "Outside.cs")));
    }

    [Fact]
    public async Task CapacityDoesNotEvictSnapshotsGrantedToAnActiveWorkerAndLoadingCanBeStopped()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture();
        var first = await f.Load(); var second = await f.Load();
        var ids = new[] { first["snapshotId"]!.ToString(), second["snapshotId"]!.ToString() };
        var child = new Session { Workspace = f.Root, ParentConversationId = f.Context.Session.Id, NavigationSnapshotIds = ids };
        var worker = new WorkerRecord { ParentId = f.Context.Session.Id, ParentRunId = f.Context.Run.Id, ChildId = child.Id, NavigationSnapshotIds = ids };
        ((IWorkerStore)f.Context.Store).CreateWorker(worker, child);
        await Assert.ThrowsAsync<UserFacingException>(() => f.Invoke("csharp_load", new { project = "App.csproj", configuration = "", framework = "" }));
        worker.State = "Completed"; ((IWorkerStore)f.Context.Store).SaveWorker(worker);
        File.WriteAllText(Path.Combine(f.Root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><Target Name=\"Delay\" BeforeTargets=\"Compile\"><Exec Command=\"ping -n 40 127.0.0.1 &amp;gt; NUL\" /></Target></Project>".Replace("&amp;gt;", "&gt;"));
        var start = await f.Invoke("csharp_load", new { project = "App.csproj", configuration = "", framework = "" });
        var stopped = await f.Invoke("process_stop", new { processId = start["processId"]!.ToString() });
        Assert.True(stopped["terminationConfirmed"]!.GetValue<bool>());
        var status = await f.Invoke("csharp_status", new { snapshotId = start["snapshotId"]!.ToString() });
        Assert.Equal("Unavailable", status["state"]!.ToString());
    }

    [Fact]
    public void DiagnosticsRendererKeepsTextEscapedAndErrorsActionable()
    {
        using var writer = new StringWriter(); var renderer = new AgentSmith.Cli.TerminalRenderer(writer, false);
        renderer.Render("diagnostics.result", new { state = "complete", artifactId = "report", totalMatches = 1,
            items = new[] { new { id = "D-one", severity = "error", code = "CS0103", path = "src/Player.cs", line = 42, column = 13, message = "missing\u001b[2Jvalue", occurrences = 1 } } });
        Assert.Contains("src/Player.cs:42:13", writer.ToString()); Assert.Contains("CS0103", writer.ToString()); Assert.DoesNotContain("\u001b", writer.ToString(), StringComparison.Ordinal);
    }
}
