using System.Security.Cryptography;
using System.Text;

namespace AgentSmith.CodeAnalysis;

public sealed record SourceFile(string Path, string Text, string Hash);
public sealed record SourceLocation(string Path, int Line, int Column, int EndLine, int EndColumn, string Hash);
public sealed record SymbolMatch(string Name, string Kind, string Signature, SourceLocation Location, string? Project = null);
public sealed record ReferenceInput(string Path, string Hash, string[] Aliases, bool EmbedInteropTypes);
public sealed class ProjectInput
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    public string Assembly { get; set; } = "";
    public string LanguageVersion { get; set; } = "Default";
    public string[] Defines { get; set; } = [];
    public int OutputKind { get; set; }
    public int Nullable { get; set; }
    public bool Unsafe { get; set; }
    public bool Overflow { get; set; }
    public int Optimization { get; set; }
    public int Platform { get; set; }
    public string[] Usings { get; set; } = [];
    public List<SourceFile> Documents { get; set; } = [];
    public List<ReferenceInput> References { get; set; } = [];
    public List<ProjectReferenceInput> ProjectReferences { get; set; } = [];
}
public sealed record ProjectReferenceInput(string Id, string[] Aliases, bool EmbedInteropTypes);
public sealed class AnalysisBundle
{
    public string Inventory { get; set; } = "";
    public int Version { get; set; } = 1;
    public List<ProjectInput> Projects { get; set; } = [];
    public Dictionary<string, string> Inputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Limitations { get; set; } = [];
    public string? Failure { get; set; }
}
public static class AnalysisHash
{
    public static string Bytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string Text(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static string File(string path)
    { using var stream = System.IO.File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
}
