using AgentSmith.Cli;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class AudioCleanupStartupTests
{
    [Fact]
    public void StartupRetiresUnusedGenerationsWithoutDeletingSavedEvidence()
    {
        var data = Path.Combine(Path.GetTempPath(), "smith-cleanup-cli-" + Guid.NewGuid().ToString("N"));
        var active = Guid.NewGuid().ToString("N"); var old = Guid.NewGuid().ToString("N");
        string Generation(string id) => Path.Combine(data, "audio", "installations", id);
        Directory.CreateDirectory(Generation(active)); Directory.CreateDirectory(Generation(old));
        File.WriteAllText(Path.Combine(Generation(active), "ready.json"), "{\"fingerprint\":\"fixture\"}");
        File.WriteAllText(Path.Combine(data, "audio", "current"), active);
        try
        {
            var store = new RecordingAnalysisStore(data);
            var job = new RecordingAnalysisJob { Installation = old, State = "Completed" };
            Directory.CreateDirectory(store.DirectoryFor(job.Id)); store.Save(job);
            File.WriteAllText(store.FileFor(job.Id, "summary.md"), "Saved evidence");
            using (var app = new CliApp(data, true, true))
            {
                Assert.True(Directory.Exists(Generation(active))); Assert.False(Directory.Exists(Generation(old)));
                Assert.Equal("Saved evidence", File.ReadAllText(store.FileFor(job.Id, "summary.md")));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(data, true);
        }
    }
}
