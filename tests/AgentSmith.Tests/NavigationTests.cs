using AgentSmith.CodeAnalysis;
using AgentSmith.Core;
using AgentSmith.Tools;
using System.Text.Json.Nodes;
using Xunit;

namespace AgentSmith.Tests;

public sealed class NavigationTests
{
    [Fact]
    public async Task SyntaxSearchFindsOverloadsPartialTypesAndInvalidatesContentAndCursor()
    {
        using var dir = new TestDirectory(); using var registry = new ToolRegistry(); var ctx = ProcessTests.Context(dir.Path);
        var source = "namespace N; public partial class C<T> { public C() {} public int Field; public int P {get;} public void M(int x) {} public void M(string x) {} }";
        File.WriteAllText(Path.Combine(dir.Path, "C.cs"), source);
        Directory.CreateDirectory(Path.Combine(dir.Path, "obj")); File.WriteAllText(Path.Combine(dir.Path, "obj", "Hidden.cs"), "class Hidden {}");
        var result = await ProcessTests.Invoke(registry, ctx, "csharp_symbols", new { path = ".", query = "", cursor = "" });
        Assert.Contains("N.C.M(int x)", result.ToJsonString()); Assert.Contains("N.C.M(string x)", result.ToJsonString());
        Assert.Contains("N.C.Field", result.ToJsonString()); Assert.DoesNotContain("class Hidden", result.ToJsonString()); Assert.Contains("excluded", result.ToJsonString());
        File.WriteAllText(Path.Combine(dir.Path, "C.cs"), string.Join('\n', Enumerable.Range(0, 100).Select(i => "class Type" + i + " {}")));
        result = await ProcessTests.Invoke(registry, ctx, "csharp_symbols", new { path = "C.cs", query = "", cursor = "" });
        Assert.Equal(100, result["total"]!.GetValue<int>()); var cursor = result["cursor"]!.ToString();
        File.AppendAllText(Path.Combine(dir.Path, "C.cs"), "\nclass Another {}");
        await Assert.ThrowsAsync<UserFacingException>(() => ProcessTests.Invoke(registry, ctx, "csharp_symbols", new { path = "C.cs", query = "", cursor }));
        File.Delete(Path.Combine(dir.Path, "C.cs"));
        result = await ProcessTests.Invoke(registry, ctx, "csharp_symbols", new { path = ".", query = "Type", cursor = "" });
        Assert.Equal(0, result["total"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(WorkflowMode.Plan, PermissionMode.Execute, false)]
    [InlineData(WorkflowMode.Review, PermissionMode.Execute, false)]
    [InlineData(WorkflowMode.Document, PermissionMode.Inspect, false)]
    [InlineData(WorkflowMode.Work, PermissionMode.Execute, true)]
    public void LoadingRequiresExecuteParentButSyntaxIsAvailableEverywhere(WorkflowMode workflow, PermissionMode mode, bool child)
    {
        using var dir = new TestDirectory(); using var registry = new ToolRegistry(); var ctx = ProcessTests.Context(dir.Path);
        ctx.Session.Workflow = workflow; ctx.Session.Mode = mode; ctx.Session.ParentConversationId = child ? "parent" : null;
        Assert.Contains(registry.Available(ctx.Session), t => t.Name == "csharp_symbols");
        Assert.DoesNotContain(registry.Available(ctx.Session), t => t.Name == "csharp_load");
        Assert.Throws<UserFacingException>(() => registry.Prepare(ctx, new() { Name = "csharp_load", Arguments = "{\"project\":\"A.csproj\",\"configuration\":\"\",\"framework\":\"\"}" }));
        Assert.Empty(ctx.Session.Processes);
    }

    [Fact]
    public async Task SemanticQueriesResolveOverloadsInterfacesExtensionsAndCrossProjectCallers()
    {
        var lib = "namespace N; public interface I { void M(int x); } public partial class C : I { public void M(int x) {} public void M(string x) {} } public static class Ext { public static void Go(this C c) { c.M(1); } }";
        var app = "class Use { void Run() { new N.C().M(2); N.Ext.Go(new N.C()); } }";
        var bundle = new AnalysisBundle { Projects = [Project("lib", "Lib.cs", lib), Project("app", "App.cs", app)] };
        bundle.Projects[1].ProjectReferences.Add(new("lib", [], false));
        using var semantic = new SemanticAnalysis(bundle);
        var column = lib.IndexOf("M(int x) {}", StringComparison.Ordinal) + 1;
        var references = await semantic.Query("Lib.cs", 1, column, "references", default);
        Assert.Contains(references, r => r.Location?.Path == "App.cs"); Assert.Contains(references, r => r.EnclosingSymbol?.Contains("Go") == true);
        var callers = await semantic.Query("Lib.cs", 1, column, "callers", default);
        Assert.Contains(callers, r => r.Symbol.Contains("Run"));
        var implementations = await semantic.Query("Lib.cs", 1, lib.IndexOf("I {", StringComparison.Ordinal) + 1, "implementations", default);
        Assert.Contains(implementations, r => r.Symbol == "N.C");
        var definition = await semantic.Query("App.cs", 1, app.IndexOf("M(2)", StringComparison.Ordinal) + 1, "definitions", default);
        Assert.Contains(definition, r => r.Symbol.Contains("M(int)") && r.Location?.Path == "Lib.cs");
    }

    [Fact]
    public async Task AmbiguousOverloadReturnsCandidatesInsteadOfAnArbitraryDefinition()
    {
        const string source = "class C { void M(string x) {} void M(int? x) {} void Go() { M(null); } }";
        using var semantic = new SemanticAnalysis(new() { Projects = [Project("ambiguous", "C.cs", source)] });
        var results = await semantic.Query("C.cs", 1, source.IndexOf("M(null)", StringComparison.Ordinal) + 1, "definitions", default);
        Assert.Equal(2, results.Count); Assert.All(results, r => Assert.True(r.IsCandidate));
    }

    [Fact]
    public async Task PartialGenericDefinitionsReturnBothPartsAndMetadataHasNoInventedSource()
    {
        var first = Project("parts", "First.cs", "public partial class Box<T> { public T Value; public string Text() => Value.ToString(); }");
        first.Documents.Add(new("Second.cs", "public partial class Box<T> { public Box() {} }", AnalysisHash.Text("public partial class Box<T> { public Box() {} }")));
        using var semantic = new SemanticAnalysis(new() { Projects = [first] });
        var definitions = await semantic.Query("First.cs", 1, 23, "definitions", default);
        Assert.Contains(definitions, d => d.Location?.Path == "First.cs"); Assert.Contains(definitions, d => d.Location?.Path == "Second.cs");
        var metadata = await semantic.Query("First.cs", 1, first.Documents[0].Text.IndexOf("ToString", StringComparison.Ordinal) + 1, "definitions", default);
        Assert.Contains(metadata, d => d.Location is null && d.Assembly is not null);
    }

    [Fact]
    public async Task LinkedFileVariantsKeepConditionalSymbolsAndCancellation()
    {
        const string source = "#if FIRST\nclass SelectedA {}\n#else\nclass SelectedB {}\n#endif";
        var first = Project("one", "Shared.cs", source); first.Defines = ["FIRST"];
        var second = Project("two", "Shared.cs", source);
        using var semantic = new SemanticAnalysis(new() { Projects = [first, second] });
        var results = await semantic.Query("Shared.cs", 2, 8, "definitions", default);
        Assert.Contains(results, r => r.ProjectVariant == "one" && r.Symbol == "SelectedA");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => semantic.Query("Shared.cs", 2, 8, "definitions", cancelled.Token));
    }
    private static ProjectInput Project(string name, string path, string source) => new()
    {
        Id = name, Name = name, File = name + ".csproj", Assembly = name, OutputKind = 2,
        Documents = [new(path, source, AnalysisHash.Text(source))],
        References = [new(typeof(object).Assembly.Location, AnalysisHash.File(typeof(object).Assembly.Location), [], false)]
    };
}
