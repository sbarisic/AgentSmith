using AgentSmith.Cli;
using AgentSmith.Core;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class RecordingAnalysisCommandTests
{
    [Fact]
    public async Task OfflineCommandsAttachImmutableEvidenceWithoutProviderOrService()
    {
        var root = Path.Combine(Path.GetTempPath(), "smith-analysis-command-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var app = new CliApp(root, true, true, new StringReader(""));
            var session = new Session { Workspace = root };
            app.Store.Save(session, new("created", session.Id, null, new { }));
            using var analysis = new RecordingAnalysis(root);
            var job = new RecordingAnalysisJob { State = "Completed", Stage = "Done", Options = new(NoSummary: true) };
            Directory.CreateDirectory(analysis.Store.DirectoryFor(job.Id)); analysis.Store.Save(job);
            foreach (var name in new[] { "transcript.jsonl", "speakers.jsonl", "sounds.jsonl" }) File.WriteAllText(analysis.Store.FileFor(job.Id, name), "");
            File.WriteAllText(analysis.Store.FileFor(job.Id, "coverage.json"), "{}");
            Assert.Equal(0, await app.AnalysisCommand(["analysis", "status", job.Id], null, default));
            Assert.Equal(0, await app.AnalysisCommand(["analysis", "read", job.Id, "coverage"], null, default));
            Assert.Equal(0, await app.AnalysisCommand(["analysis", "attach", job.Id], session, default));
            var artifact = Assert.Single(app.Store.Get(session.Id)!.Artifacts);
            Assert.Equal("recording-analysis", artifact.Kind);
            Assert.Equal(0, await app.AnalysisCommand(["analysis", "delete", job.Id], null, default));
            Assert.True(File.Exists(artifact.Path));
            Assert.Throws<UserFacingException>(() => WorkspaceTools.Resolve(root, "../outside.wav"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
