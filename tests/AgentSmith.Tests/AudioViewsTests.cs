using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Storage;
using AgentSmith.Tools;
using Xunit;

namespace AgentSmith.Tests;

public sealed class AudioViewsTests
{
    private static JsonObject Evidence() => JsonNode.Parse("""
        {"path":"clip.wav","durationSeconds":23.9999375,"classification":{"status":"succeeded","classMap":[{"label":"DO NOT DISPLAY DICTIONARY"}],"aggregateScores":[{"label":"Speech","score":0.87},{"label":"Silence","score":0.05},{"label":"Synthesizer","score":0.003}],"windows":[
        {"startSeconds":0,"endSeconds":0.96,"labels":[{"label":"Speech","score":0.2},{"label":"Bell","score":0.6}]},
        {"startSeconds":0.48,"endSeconds":1.44,"labels":[{"label":"Speech","score":0.9}]},
        {"startSeconds":0.96,"endSeconds":1.92,"labels":[{"label":"Bell","score":0.4}]}]}}
        """)!.AsObject();

    [Fact]
    public void ThresholdsPreserveBriefAndOverlappingWindowsWithoutDictionaryNoise()
    {
        var data = Evidence(); var text = AudioViews.Render(data, "summary");
        Assert.Contains("24.0 s", text); Assert.Contains("Silence: 0.050", text);
        Assert.DoesNotContain("Synthesizer", text); Assert.DoesNotContain("DICTIONARY", text);
        var spans = AudioViews.Timeline(data); Assert.Equal(3, spans.Count);
        Assert.Contains(spans, i => i.Label == "Speech" && i.Start == 0 && i.End == 1.44);
        Assert.Equal(2, spans.Count(i => i.Label == "Bell"));
        Assert.Contains("Synthesizer", AudioViews.Render(data, "scores"));
    }

    [Fact]
    public void AlignmentLeavesAmbiguousAndUncoveredWordsExplicit()
    {
        var data = JsonNode.Parse("""
        {"transcription":{"segments":[{"text":"one two three four","words":[
        {"startSeconds":0,"endSeconds":1,"text":"one"},{"startSeconds":1,"endSeconds":2,"text":" two"},
        {"startSeconds":2,"endSeconds":3,"text":" three"},{"startSeconds":3,"endSeconds":4,"text":" four"}]}]},
        "diarization":{"status":"succeeded","turns":[{"startSeconds":0,"endSeconds":2,"speaker":"Speaker 1"},{"startSeconds":1.5,"endSeconds":2.5,"speaker":"Speaker 2"}]}}
        """)!.AsObject();
        AudioReuse.Align(data);
        var rows = data["transcription"]!["speakerSegments"]!.AsArray();
        Assert.Equal("Speaker 1", rows[0]!["speaker"]!.ToString());
        Assert.Equal("Overlapping/ambiguous speakers", rows[1]!["speaker"]!.ToString());
        Assert.Equal("Unknown speaker", rows[2]!["speaker"]!.ToString());
        Assert.Equal("one two three four", data["transcription"]!["segments"]![0]!["text"]!.ToString());
        data["transcription"]!["segments"]![0]!.AsObject().Remove("words"); AudioReuse.Align(data);
        Assert.Null(data["transcription"]!["speakerSegments"]);
    }

    [Fact]
    public void ViewsPaginateAndRequireAnExplicitParentGrant()
    {
        using var directory = new TestDirectory(); var store = new SessionStore(directory.Path);
        var parent = new Session { Workspace = directory.Path, Runs = [new()] }; var ctx = new ToolExecutionContext(parent, parent.Runs[0], store, default);
        Directory.CreateDirectory(ctx.ArtifactDirectory); var path = Path.Combine(ctx.ArtifactDirectory, "audio.json"); File.WriteAllText(path, Evidence().ToJsonString());
        var artifact = ArtifactAccess.Register(ctx, path, "audio-analysis");
        var args = new JsonObject { ["artifactId"] = artifact.Id, ["view"] = "summary", ["cursor"] = 0, ["maxCharacters"] = 20 };
        var result = JsonNode.Parse(ArtifactAccess.Execute(ctx, "audio_read", args).Content)!;
        Assert.True(result["hasMore"]!.GetValue<bool>()); Assert.Equal(20, result["cursor"]!.GetValue<int>());
        var child = new Session { Workspace = directory.Path, ParentConversationId = parent.Id, Runs = [new()] };
        var childCtx = new ToolExecutionContext(child, child.Runs[0], store, default);
        Assert.Throws<UserFacingException>(() => ArtifactAccess.Execute(childCtx, "audio_read", args));
        child.ArtifactGrants.Add(new(parent.Id, artifact.Id));
        Assert.Equal(result.ToJsonString(), JsonNode.Parse(ArtifactAccess.Execute(childCtx, "audio_read", args).Content)!.ToJsonString());
        child.ParentConversationId = "wrong";
        Assert.Throws<UserFacingException>(() => ArtifactAccess.Execute(childCtx, "audio_read", args));
    }

    [Fact]
    public void HistoricalArtifactsDoNotInventStructuredEvidence()
    {
        var data = new JsonObject { ["analysis"] = "Historical cloud answer." };
        Assert.Contains("Historical cloud answer", AudioViews.Render(data, "summary"));
        Assert.Contains("unavailable", AudioViews.Render(data, "speakers"));
        Assert.Contains("unavailable", AudioViews.Render(data, "timeline"));
        Assert.Throws<UserFacingException>(() => AudioViews.Render(data, "unknown"));
    }
}
