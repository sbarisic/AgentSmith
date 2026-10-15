using AgentSmith.Cli;
using AgentSmith.Core;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class AudioCommandTests
{
    [Fact]
    public async Task StatusFromChatNeedsNoProviderOrInference()
    {
        var path = Path.Combine(Path.GetTempPath(), "smith-audio-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            using var app = new CliApp(path, true, true);
            var session = new Session { Workspace = path, Mode = PermissionMode.Inspect };
            app.Store.Save(session, new("created", session.Id, null, new { }));
            await app.HandleCommand("/audio status", session);
            Assert.Empty(session.Runs); Assert.Empty(session.Artifacts);
            Assert.False(Directory.Exists(Path.Combine(path, "audio", "installations")));
        }
        finally
        {
            foreach (var database in Directory.EnumerateFiles(path, "*.db"))
            {
                var settings = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = database };
                if (Path.GetFileName(database) == "sessions.db") settings.DefaultTimeout = 5;
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(settings.ToString());
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            }
            Directory.Delete(path, true);
        }
    }

    [Fact]
    public void LocalProgressAndComponentSummaryAreVisibleWithoutBillingGuidance()
    {
        using var output = new StringWriter(); var renderer = new TerminalRenderer(output, false);
        renderer.Render("audio.progress", new { message = "Local audio · transcribing" });
        renderer.Render("tool.completed", new { id = "a", name = "audio_transcribe", content = Json.Serialize(new {
            kind = "audio-analysis", backend = "local", path = "speech.wav", model = "Whisper small", startSeconds = 10, durationSeconds = 5,
            analysis = "[10–12s] Hello world.\nClassification failed: unavailable", artifactId = "local-audio" }) });
        var text = output.ToString(); Assert.Contains("transcribing", text); Assert.Contains("Hello world", text);
        Assert.Contains("Classification failed", text); Assert.Contains("/artifact local-audio", text); Assert.DoesNotContain("billing", text);
    }
}
