using AgentSmith.Tools;

namespace AgentSmith.Desktop;

internal sealed record TranscriptActions(bool Transcribe, bool Resume, bool RetryFailed, bool RetryUncertain)
{
    internal static TranscriptActions For(RecordingAnalysisJob? job)
    {
        if (job is null) return new(true, false, false, false);
        var states = job.Chunks.SelectMany(c => c.Components.Values).ToArray();
        var failed = states.Contains("Failed");
        var uncertain = states.Any(s => s is "Submitted" or "Uncertain");
        var pending = !job.Prepared && job.State is not ("Completed" or "CompletedWithGaps") || states.Contains("Pending") ||
            job.State is not ("Completed" or "CompletedWithGaps") && job.Stage is not ("Summary" or "Done") && !failed && !uncertain;
        return new(false, pending, failed, uncertain);
    }
}
