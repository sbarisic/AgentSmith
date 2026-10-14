using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;

namespace AgentSmith.CodeAnalysis;

public sealed record LoadRequest(string Workspace, string Project, string Configuration, string Framework);

public static class ProjectLoader
{
    // Only invoked in the owned helper process, before any MSBuild types are loaded.
    public static Task<AnalysisBundle> Load(LoadRequest request, Func<string, SourceFile> read, CancellationToken ct)
    {
        var instance = MSBuildLocator.QueryVisualStudioInstances(new VisualStudioInstanceQueryOptions
        { DiscoveryTypes = DiscoveryType.DotNetSdk, WorkingDirectory = request.Workspace }).FirstOrDefault()
            ?? throw new InvalidOperationException("No compatible installed SDK was found. Check global.json and install the required SDK explicitly.");
        MSBuildLocator.RegisterInstance(instance);
        return LoadRegistered(request, read, ct);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<AnalysisBundle> LoadRegistered(LoadRequest request, Func<string, SourceFile> read, CancellationToken ct)
    {
        var bundle = new AnalysisBundle();
        var properties = new Dictionary<string, string> { ["Configuration"] = request.Configuration, ["BuildProjectReferences"] = "false",
            ["RestoreDuringBuild"] = "false", ["Restore"] = "false", ["UseSharedCompilation"] = "false",
            ["RunAnalyzers"] = "false", ["RunAnalyzersDuringBuild"] = "false" };
        if (request.Framework.Length > 0) properties["TargetFramework"] = request.Framework;
        using var msbuild = MSBuildWorkspace.Create(properties);
        msbuild.LoadMetadataForReferencedProjects = false;
        var solution = Path.GetExtension(request.Project).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            ? (await msbuild.OpenProjectAsync(request.Project, cancellationToken: ct)).Solution
            : await msbuild.OpenSolutionAsync(request.Project, cancellationToken: ct);
        foreach (var diagnostic in msbuild.Diagnostics.Take(20))
            bundle.Limitations.Add(diagnostic.Kind + ": " + diagnostic.Message[..Math.Min(diagnostic.Message.Length, 500)]);
        long characters = 0;
        if (solution.ProjectIds.Count > 100) throw new InvalidDataException("Navigation supports at most 100 projects per snapshot.");
        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            if (project.Language != LanguageNames.CSharp || project.FilePath is null) { bundle.Limitations.Add("Non-C# project omitted."); continue; }
            try { read(project.FilePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AgentSmith.Core.UserFacingException)
            { bundle.Limitations.Add("An out-of-scope or unreadable project was excluded."); continue; }
            using (var collection = new Microsoft.Build.Evaluation.ProjectCollection(properties))
            {
                var evaluated = collection.LoadProject(project.FilePath);
                var frameworks = evaluated.GetPropertyValue("TargetFrameworks").Split(';', StringSplitOptions.RemoveEmptyEntries);
                if (frameworks.Length > 1 && request.Framework.Length == 0) throw new InvalidDataException("Multiple target frameworks: specify framework explicitly (" + string.Join(", ", frameworks) + ").");
                if (request.Framework.Length > 0 && frameworks.Length > 0 && !frameworks.Contains(request.Framework, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("The selected framework is not supported by every loaded multi-target project.");
                Track(project.FilePath);
                foreach (var import in evaluated.Imports) Track(import.ImportedProject.FullPath);
                var assets = evaluated.GetPropertyValue("ProjectAssetsFile");
                if (assets.Length > 0) Track(assets);
                if (assets.Length == 0 || !File.Exists(assets)) bundle.Limitations.Add($"{project.Name}: package assets missing. Restore explicitly using Execute tools, then reload.");
            }
            var parse = (CSharpParseOptions?)project.ParseOptions ?? CSharpParseOptions.Default;
            var options = (CSharpCompilationOptions?)project.CompilationOptions ?? new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
            var input = new ProjectInput { Id = project.Id.Id.ToString(), Name = project.Name, File = Path.GetRelativePath(request.Workspace, project.FilePath),
                Assembly = project.AssemblyName ?? project.Name, LanguageVersion = parse.LanguageVersion.ToString(), Defines = parse.PreprocessorSymbolNames.ToArray(),
                OutputKind = (int)options.OutputKind, Nullable = (int)options.NullableContextOptions, Unsafe = options.AllowUnsafe, Overflow = options.CheckOverflow,
                Optimization = (int)options.OptimizationLevel, Platform = (int)options.Platform, Usings = options.Usings.ToArray() };
            foreach (var document in project.Documents)
            {
                ct.ThrowIfCancellationRequested();
                if (document.FilePath is null) { bundle.Limitations.Add($"{project.Name}: a document without a path was excluded."); continue; }
                try
                {
                    var source = read(document.FilePath);
                    var loaded = (await document.GetTextAsync(ct)).ToString();
                    if (!string.Equals(source.Text, loaded, StringComparison.Ordinal)) throw new InvalidDataException("Source changed during project preparation.");
                    characters += source.Text.Length;
                    if (characters > 10_000_000 || input.Documents.Count >= 5000) throw new InvalidDataException("Snapshot source exceeds its 10M character/5000 document limit.");
                    input.Documents.Add(source); bundle.Inputs[document.FilePath] = source.Hash;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AgentSmith.Core.UserFacingException)
                { bundle.Limitations.Add($"{project.Name}: a linked, missing, excluded or oversized source document was omitted."); }
            }
            foreach (var reference in project.MetadataReferences.OfType<PortableExecutableReference>())
            {
                if (reference.FilePath is not { } path || !File.Exists(path)) { bundle.Limitations.Add($"{project.Name}: unresolved compiler reference."); continue; }
                if (new FileInfo(path).Length > 100 * 1024 * 1024) throw new InvalidDataException("Compiler reference exceeds 100 MiB.");
                var hash = AnalysisHash.File(path); bundle.Inputs[path] = hash;
                input.References.Add(new(path, hash, reference.Properties.Aliases.ToArray(), reference.Properties.EmbedInteropTypes));
            }
            input.ProjectReferences = project.ProjectReferences.Select(r => new ProjectReferenceInput(r.ProjectId.Id.ToString(), r.Aliases.ToArray(), r.EmbedInteropTypes)).ToList();
            if (project.AnalyzerReferences.Count > 0) bundle.Limitations.Add($"{project.Name}: analyzers and source generators are excluded; generated symbols may be missing.");
            bundle.Projects.Add(input);
        }
        Track(request.Project);
        bundle.Limitations = bundle.Limitations.Distinct().Take(30).ToList();
        return bundle;

        void Track(string path)
        {
            path = Path.GetFullPath(path);
            bundle.Inputs[path] = File.Exists(path) ? AnalysisHash.File(path) : "missing";
        }
    }
}
