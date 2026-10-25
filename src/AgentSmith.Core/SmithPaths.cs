namespace AgentSmith.Core;

public static class SmithPaths
{
    public static string DataDirectory(string? directory) => Path.GetFullPath(directory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentSmith"));
}
