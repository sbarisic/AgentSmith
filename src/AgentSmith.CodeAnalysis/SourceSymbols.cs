using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgentSmith.CodeAnalysis;

public sealed class SourceSymbols
{
    private readonly ConcurrentDictionary<string, (string Hash, SyntaxTree Tree)> cache = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<SymbolMatch> Find(SourceFile source, string query, CancellationToken ct)
    {
        if (cache.Count > 5000) cache.Clear();
        var entry = cache.AddOrUpdate(source.Path, _ => Parse(), (_, old) => old.Hash == source.Hash ? old : Parse());
        (string, SyntaxTree) Parse() => (source.Hash, CSharpSyntaxTree.ParseText(source.Text, path: source.Path, cancellationToken: ct));
        var matches = new List<SymbolMatch>();
        foreach (var node in entry.Tree.GetRoot(ct).DescendantNodes())
        {
            ct.ThrowIfCancellationRequested();
            var name = node switch
            {
                BaseTypeDeclarationSyntax n => n.Identifier.Text,
                DelegateDeclarationSyntax n => n.Identifier.Text,
                MethodDeclarationSyntax n => n.Identifier.Text,
                ConstructorDeclarationSyntax n => n.Identifier.Text,
                PropertyDeclarationSyntax n => n.Identifier.Text,
                EventDeclarationSyntax n => n.Identifier.Text,
                VariableDeclaratorSyntax n when n.Parent?.Parent is BaseFieldDeclarationSyntax => n.Identifier.Text,
                _ => null
            };
            if (name is null) continue;
            var parents = node.Ancestors().Select(n => n switch { BaseNamespaceDeclarationSyntax ns => ns.Name.ToString(), BaseTypeDeclarationSyntax t => t.Identifier.Text, _ => null }).Where(n => n is not null).Reverse();
            var qualified = string.Join(".", parents.Append(name));
            if (!qualified.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            var span = node.GetLocation().GetLineSpan();
            var signature = node switch
            {
                MethodDeclarationSyntax m => $"{m.ReturnType} {qualified}{m.TypeParameterList}{m.ParameterList}",
                ConstructorDeclarationSyntax c => qualified + c.ParameterList,
                PropertyDeclarationSyntax p => $"{p.Type} {qualified}",
                _ => qualified
            };
            matches.Add(new(qualified, node.Kind().ToString(), signature, new(source.Path, span.StartLinePosition.Line + 1,
                span.StartLinePosition.Character + 1, span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1, source.Hash)));
        }
        return matches;
    }
}
