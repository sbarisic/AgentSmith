using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace AgentSmith.CodeAnalysis;

// This workspace contains source, options and compiler references only. It never loads
// an MSBuild project, analyzer or generator in the interactive process.
public sealed class SemanticAnalysis : IDisposable
{
    private readonly AdhocWorkspace workspace = new();
    private readonly Dictionary<DocumentId, SourceFile> sources = [];
    public SemanticAnalysis(AnalysisBundle bundle, CancellationToken ct = default)
    {
        if (bundle.Projects.Count > 100 || bundle.Projects.Sum(p => (long)p.Documents.Sum(d => (long)d.Text.Length)) > 10_000_000)
            throw new InvalidDataException("Snapshot exceeds the project/source limit.");
        var references = new Dictionary<string, PortableExecutableReference>(StringComparer.OrdinalIgnoreCase);
        long referenceBytes = 0;
        var ids = bundle.Projects.ToDictionary(p => p.Id, _ => ProjectId.CreateNewId());
        var solution = workspace.CurrentSolution;
        foreach (var p in bundle.Projects)
        {
            ct.ThrowIfCancellationRequested();
            if (!Enum.TryParse<LanguageVersion>(p.LanguageVersion, out var version)) LanguageVersionFacts.TryParse(p.LanguageVersion, out version);
            var options = new CSharpCompilationOptions((OutputKind)p.OutputKind, allowUnsafe: p.Unsafe,
                checkOverflow: p.Overflow, optimizationLevel: (OptimizationLevel)p.Optimization,
                platform: (Platform)p.Platform, nullableContextOptions: (NullableContextOptions)p.Nullable, usings: p.Usings);
            solution = solution.AddProject(ProjectInfo.Create(ids[p.Id], VersionStamp.Create(), p.Name, p.Assembly,
                LanguageNames.CSharp, filePath: p.File, compilationOptions: options,
                parseOptions: new CSharpParseOptions(version, preprocessorSymbols: p.Defines)));
            foreach (var source in p.Documents)
            {
                var id = DocumentId.CreateNewId(ids[p.Id]); sources[id] = source;
                solution = solution.AddDocument(id, Path.GetFileName(source.Path), SourceText.From(source.Text), filePath: source.Path);
            }
            foreach (var reference in p.References)
            {
                ct.ThrowIfCancellationRequested();
                // Capture bytes, not a live file handle that can change underneath a query.
                var key = reference.Path + ":" + reference.Hash;
                if (!references.TryGetValue(key, out var metadata))
                {
                    var length = new FileInfo(reference.Path).Length;
                    referenceBytes += length;
                    if (length > 100 * 1024 * 1024 || referenceBytes > 512 * 1024 * 1024) throw new InvalidDataException("Snapshot compiler references exceed their memory budget.");
                    var bytes = File.ReadAllBytes(reference.Path);
                    if (AnalysisHash.Bytes(bytes) != reference.Hash) throw new InvalidDataException("A metadata reference changed while preparing the snapshot.");
                    references[key] = metadata = MetadataReference.CreateFromImage(bytes, filePath: reference.Path);
                }
                solution = solution.AddMetadataReference(ids[p.Id], metadata.WithProperties(new MetadataReferenceProperties(aliases: reference.Aliases.ToImmutable(), embedInteropTypes: reference.EmbedInteropTypes)));
            }
        }
        foreach (var p in bundle.Projects)
            foreach (var reference in p.ProjectReferences.Where(r => ids.ContainsKey(r.Id)))
                solution = solution.AddProjectReference(ids[p.Id], new ProjectReference(ids[reference.Id], reference.Aliases.ToImmutable(), reference.EmbedInteropTypes));
        if (!workspace.TryApplyChanges(solution)) throw new InvalidDataException("Could not initialize the analysis workspace.");
    }

    public async Task<IReadOnlyList<NavigationMatch>> Query(string path, int line, int column, string operation, CancellationToken ct)
    {
        if (operation is not ("definitions" or "references" or "callers" or "implementations")) throw new ArgumentException("operation must be definitions, references, callers or implementations.");
        var results = new List<NavigationMatch>();
        foreach (var pair in sources.Where(s => s.Value.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            var document = workspace.CurrentSolution.GetDocument(pair.Key)!;
            var text = await document.GetTextAsync(ct);
            if (line < 1 || line > text.Lines.Count || column < 1 || column > text.Lines[line - 1].Span.Length + 1) throw new ArgumentException("Position is outside the source document.");
            var position = text.Lines[line - 1].Start + column - 1;
            var root = await document.GetSyntaxRootAsync(ct);
            var semanticModel = await document.GetSemanticModelAsync(ct);
            var node = root?.FindToken(Math.Min(position, Math.Max(0, text.Length - 1))).Parent;
            var ambiguous = false;
            foreach (var expression in node?.AncestorsAndSelf().Take(4).OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax>() ?? [])
            {
                var info = semanticModel!.GetSymbolInfo(expression, ct);
                if (info.Symbol is not null || info.CandidateSymbols.Length <= 1) continue;
                foreach (var candidate in info.CandidateSymbols)
                    foreach (var location in candidate.Locations) Add(candidate, location, null, document.Project.Name, true);
                ambiguous = true; break;
            }
            if (ambiguous) continue;
            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position, ct);
            if (symbol is null) continue;
            if (operation == "references")
            {
                foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, workspace.CurrentSolution, ct))
                    foreach (var location in reference.Locations)
                    {
                        var model = await location.Document.GetSemanticModelAsync(ct);
                        Add(reference.Definition, location.Location, model?.GetEnclosingSymbol(location.Location.SourceSpan.Start, ct)?.ToDisplayString(), document.Project.Name);
                    }
            }
            else if (operation == "callers")
            {
                foreach (var caller in await SymbolFinder.FindCallersAsync(symbol, workspace.CurrentSolution, ct))
                    foreach (var location in caller.Locations) Add(caller.CallingSymbol, location, caller.CallingSymbol.ToDisplayString(), document.Project.Name);
            }
            else
            {
                var symbols = operation == "implementations"
                    ? await SymbolFinder.FindImplementationsAsync(symbol, workspace.CurrentSolution, cancellationToken: ct)
                    : new[] { await SymbolFinder.FindSourceDefinitionAsync(symbol, workspace.CurrentSolution, ct) ?? symbol };
                foreach (var found in symbols)
                {
                    foreach (var location in found.Locations) Add(found, location, null, document.Project.Name);
                    if (found.Locations.Length == 0) Add(found, Location.None, null, document.Project.Name);
                }
            }
        }
        return results.Distinct().ToArray();

        void Add(ISymbol symbol, Location location, string? enclosing, string variant, bool candidate = false)
        {
            if (results.Count >= 5000) throw new InvalidDataException("Query exceeds 5000 locations; narrow its scope.");
            SourceLocation? source = null;
            if (location.IsInSource && location.SourceTree is { } tree)
            {
                var document = workspace.CurrentSolution.GetDocument(tree);
                if (document is null || !sources.TryGetValue(document.Id, out var file)) return;
                var span = location.GetLineSpan();
                source = new(file.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
                    span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1, file.Hash);
            }
            results.Add(new(symbol.ToDisplayString(), symbol.Kind.ToString(), source, symbol.ContainingAssembly?.Identity.ToString(), enclosing, variant, candidate));
        }
    }
    public void Dispose() => workspace.Dispose();
}
public sealed record NavigationMatch(string Symbol, string Kind, SourceLocation? Location, string? Assembly, string? EnclosingSymbol, string ProjectVariant, bool IsCandidate = false);
internal static class ImmutableExtensions
{
    public static System.Collections.Immutable.ImmutableArray<T> ToImmutable<T>(this IEnumerable<T> values) => System.Collections.Immutable.ImmutableArray.CreateRange(values);
}
