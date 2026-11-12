using AgentSmith.Core;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

public sealed class AudioExtractionTests
{
    [Fact]
    public void CodingAssistantNoLongerAdvertisesAudioToolsOrSetup()
    {
        using var directory = new TestDirectory();
        using var registry = new ToolRegistry();
        Assert.DoesNotContain(registry.Definitions, tool => tool.Name.StartsWith("audio_", StringComparison.Ordinal));
        Assert.DoesNotContain("smith audio", registry.Instructions(directory.Path));
        Assert.Contains(registry.Available(PermissionMode.Inspect), tool => tool.Name == "file_read");
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "audio")));
    }
}
