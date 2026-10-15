using System.Globalization;
using System.Text.Json;
using AgentSmith.Core;

namespace AgentSmith.Cli;

/// <summary>Human-facing presentation only. Machine events and persisted state bypass this renderer.</summary>
public sealed partial class TerminalRenderer(TextWriter output, bool color, int width = 90, TimeProvider? timeProvider = null)
{
    private int width = Math.Clamp(width, 32, 100);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private long? startedAt;
    private bool streaming;
    private MarkdownWriter? markdownStream;
    private string workflow = "Work";
    private string guideStyle = TerminalTheme.Guide("Work");
    public void Resize(int columns) => width = Math.Clamp(columns, 32, 160);
    public bool Verbose { get; set; }
    public bool MarkdownEnabled { get; set; } = true;
    private long inputTokens;
    private long outputTokens;
    private int tools;
    private string? lastQuestion;

    public void Render(string type, object payload)
    {
        var data = JsonSerializer.SerializeToElement(payload, Json.Options);
        if (type == "text.delta") { Stream(Text(data, "text")); return; }
        if (type is "model.requested" or "question.answered") { runningStatus = "Thinking"; return; }
        if (type == "model.completed")
        {
            EndStream(); // Separate responses even when no tool event follows the first answer.
            if (TryJson(Text(data, "usage"), out var usage))
            {
                inputTokens += Number(usage, "input_tokens");
                outputTokens += Number(usage, "output_tokens");
            }
            return;
        }
        EndStream();
        if (type is "conversation" or "conversation.resumed" or "run.started" or "workflow.started" or "workflow.status" or "mode" or "user.submitted" && Text(data, "workflow").Length > 0)
        { workflow = Text(data, "workflow"); guideStyle = TerminalTheme.Guide(workflow); }
        switch (type)
        {
            case "audio.progress":
                runningStatus = Text(data, "message"); if (Verbose) MessageText("› " + runningStatus, "info"); break;
            case "web.search.progress":
                runningStatus = Text(data, "message") == "Web search completed" ? "Thinking" : "Searching web";
                MessageText("› " + Text(data, "message"), "info"); break;
            case "web.search.policy": MessageText(Text(data, "message"), "info"); break;
            case "agent.collected":
                MessageText("› Collected Agent #" + Text(data, "number") + " · " + Text(data, "state"));
                MessageText("  Saved result: /artifact " + Text(data, "artifactId"), "dim"); break;
            case "run.completion.pending":
                MessageText("Completion pending · " + Text(data, "reason"), "status-warning");
                MessageText("Next: " + Text(data, "next")); break;
            case "steering.saved": case "steering.incorporated": case "steering.sent":
                SteeringActivity(data); break;
            case "steering.status":
                if (data.TryGetProperty("items", out var steeringItems) && steeringItems.ValueKind == JsonValueKind.Array)
                    foreach (var item in steeringItems.EnumerateArray()) SteeringActivity(item, listing: true);
                MessageText(Text(data, "message"), "dim"); break;
            case "steering.removed": case "steering.held":
                MessageText(Text(data, "message"), type == "steering.held" ? "status-warning" : "accent"); break;
            case "workflow.report":
                var reportWriter = new MarkdownWriter(output, color, width, MarkdownEnabled, TerminalTheme.Guide(Text(data, "workflow")));
                reportWriter.Append(Text(data, "markdown")); reportWriter.Finish(); break;
            case "workflow.reports":
                Title("Saved workflow reports");
                foreach (var report in data.GetProperty("reports").EnumerateArray()) Wrapped("  " + Text(report, "id") + " · " + Text(report, "workflow") + " · " + Text(report, "state") + " · " + Text(report, "task"), "accent");
                break;
            case "workflow.started": MessageText("Workflow " + Text(data, "workflow") + " · report " + Text(data, "id"), "accent"); break;
            case "workflow.updated": MessageText("Report " + Text(data, "id") + " · revision " + Text(data, "revision"), "accent"); break;
            case "workflow.finished":
                MessageText("Report saved · " + Text(data, "id") + " · artifact " + Text(data, "artifactId"), "accent");
                if (Text(data, "workflow") is { Length: > 0 } finishedWorkflow) MessageText("Workflow: " + finishedWorkflow, "accent");
                Preview(Text(data, "summary"));
                if (data.TryGetProperty("findings", out var findings) && findings.ValueKind == JsonValueKind.Array)
                    foreach (var finding in findings.EnumerateArray()) MessageText(Text(finding, "id") + " · " + Text(finding, "category") + " / " + Text(finding, "severity") + " · " + Text(finding, "title") + " · " + Text(finding, "path") + ":" + Text(finding, "line"), Text(finding, "category") == "defect" ? "warning" : "accent");
                if (data.TryGetProperty("limitations", out var limitations) && limitations.ValueKind == JsonValueKind.Array)
                    foreach (var limitation in limitations.EnumerateArray().Take(5)) MessageText("Limit: " + limitation.GetString(), "warning");
                MessageText("Use /report " + Text(data, "id") + " for the complete saved report.", "dim"); break;
            case "usage":
                Title("Token usage · conversation and workers");
                Wrapped($"  ChatGPT:       {Number(data, "cloudInputTokens"):N0} in / {Number(data, "cloudOutputTokens"):N0} out");
                Wrapped($"  Local models:  {Number(data, "localInputTokens"):N0} in / {Number(data, "localOutputTokens"):N0} out");
                Wrapped($"  Local workers: {Number(data, "localWorkerTokens"):N0} tokens handled locally ({Number(data, "localWorkerInputTokens"):N0} in / {Number(data, "localWorkerOutputTokens"):N0} out)");
                foreach (var row in data.GetProperty("rows").EnumerateArray())
                {
                    Wrapped("  " + Text(row, "role") + (Text(row, "profile").Length > 0 ? " · " + Text(row, "profile") : "") + " · " + Text(row, "provider") + " · " + Text(row, "model"), "accent");
                    Wrapped($"    {Number(row, "inputTokens"):N0} in / {Number(row, "outputTokens"):N0} out · {Number(row, "reportedRequests")} requests with usage");
                    if (Number(row, "requestsWithCacheDetails") > 0) Wrapped($"    Reported cache: {Number(row, "cachedInputTokens"):N0} cached / {Number(row, "knownUncachedInputTokens"):N0} uncached input · {Number(row, "requestsWithCacheDetails")} requests", "dim");
                    if (Text(row, "purpose").Length > 0) Wrapped($"    {Text(row, "purpose")} · {Text(row, "outcome")} · local-slot wait {TerminalDuration.Format(TimeSpan.FromMilliseconds(Number(row, "waitingMilliseconds")))}", "dim");
                    if (Number(row, "compactionInputTokens") + Number(row, "compactionOutputTokens") > 0)
                        Wrapped($"    Includes compaction: {Number(row, "compactionInputTokens"):N0} in / {Number(row, "compactionOutputTokens"):N0} out", "dim");
                }
                Wrapped($"  Usage unavailable: {Number(data, "requestsWithoutUsage")} completed records; {Number(data, "unfinishedRequests")} requests unfinished/failed; {Number(data, "failedCompactions")} failed compactions.", "dim");
                Wrapped("  " + Text(data, "interpretation"), "dim");
                break;
            case "agent.started":
                WorkerStarted(data); break;
            case "agent.finished":
                WorkerStatus(data); break;
            case "plan.display":
                Plan(data.GetProperty("plan")); break;
            case "plan.list": DirectoryPlanList(data); break;
            case "plan.review": Plan(data); break;
            case "plan.review.pending": MessageText(Text(data, "text"), "accent"); MessageText(Text(data, "responses"), "dim"); break;
            case "plan.decision": MessageText("Plan decision: " + Text(data, "decision") + " " + Text(data, "mode"), "accent"); break;
            case "plan.updated":
                PlanProgress(data); break;
            case "artifact.result": if (TryJson(Text(data, "content"), out var artifact)) Artifact(artifact); break;
            case "diagnostics.result": DiagnosticPage(data); break;
            case "tools":
                Title("Registered tools · serial execution");
                foreach (var tool in data.EnumerateArray())
                { Wrapped("  " + Text(tool, "name") + " · " + Text(tool, "capability") + (Flag(tool, "enabled") ? "" : " · disabled"), "accent"); Wrapped("    " + Text(tool, "description")); }
                break;
            case "changes":
                Title("File-tool checkpoints · process side effects are not checkpointed");
                if (data.GetArrayLength() == 0) Line("  No file-tool changes.", "dim");
                foreach (var change in data.EnumerateArray())
                { Wrapped("  " + Text(change, "id") + " · " + Text(change, "kind") + " · " + Text(change, "state"), "accent"); DiffPreview(Text(change, "diff")); }
                break;
            case "processes":
                Title("Processes owned by this conversation");
                foreach (var process in data.EnumerateArray())
                { Wrapped("  " + (Number(process, "displayNumber") > 0 ? "#" + Text(process, "displayNumber") : Text(process, "id")) + " · " + Text(process, "state") + " · exit " + Text(process, "exitCode"), "accent"); Wrapped("    " + Text(process, "executable")); Wrapped("    " + Text(process, "logPath"), "dim"); }
                break;
            case "operation":
                if (TryJson(Text(data, "content"), out var operation)) Operation(operation);
                break;
            case "conversation":
            case "conversation.resumed":
                Header(data);
                if (type == "conversation.resumed" && data.TryGetProperty("history", out var history)) History(history);
                if (type == "conversation.resumed" && data.TryGetProperty("resume", out var resume) && resume.ValueKind == JsonValueKind.Object) ResumeSummary(resume);
                break;
            case "run.started":
                startedAt = clock.GetTimestamp(); inputTokens = outputTokens = 0; tools = 0; lastQuestion = null;
                activeActivities.Clear(); runningStatus = "Thinking";
                MessageTitle("AgentSmith");
                Line("  │ Thinking…", "status-running");
                break;
            case "tool.started":
                tools++;
                StartActivity(data); break;
            case "tool.completed":
                runningStatus = "Thinking";
                if (TryJson(Text(data, "content"), out var result))
                {
                    if (result.ValueKind != JsonValueKind.Object) break;
                    if (ToolFailure(result, data)) { }
                    else if (RoutineResult(data, result)) { }
                    else if (Text(data, "name") is "agent_spawn" or "agent_wait" or "agent_result" or "agent_cancel" or "agent_message") WorkerResult(Text(data, "name"), result);
                    else if (Text(result, "kind") is "audio-analysis" or "workspace-image" or "web-image" or "workspace-map" or "http-response") InspectionResult(result);
                    else if (Text(result, "kind") is "web-search" or "web-page") WebResult(result);
                    else if (Text(data, "name") is "workflow_read" or "workflow_update" or "workflow_finish"
                        || result.TryGetProperty("targetKind", out _) && result.TryGetProperty("current", out _)) WorkflowResult(result);
                    else if (result.TryGetProperty("plans", out _)) DirectoryPlanList(result);
                    else if (result.TryGetProperty("current", out _))
                    { if (Text(data, "name") != "plan_update" || Verbose) Plan(result); }
                    else if (Text(result, "kind") == "diagnostics") DiagnosticPage(result);
                    else if (Text(result, "kind") == "navigation") NavigationResult(result);
                    else if (result.TryGetProperty("verificationId", out _) && result.TryGetProperty("kind", out _)) Verification(result);
                    else if (result.TryGetProperty("artifactId", out _) || result.TryGetProperty("artifacts", out _)) Artifact(result);
                    else if (Text(data, "name") == "git_status") GitStatus(result);
                    else if (result.TryGetProperty("checkpointId", out _) || result.TryGetProperty("processId", out _) || result.TryGetProperty("exitCode", out _)) Operation(result, Text(data, "name") == "git_diff");
                    else if (result.TryGetProperty("error", out var error)) MessageText("  " + error.GetString(), "status-error");
                    else if (result.TryGetProperty("path", out var path))
                        MessageText("  " + path.GetString() + (result.TryGetProperty("totalLines", out var lines) ? $"  ·  {lines} lines" : ""), "dim");
                    else if (result.TryGetProperty("files", out var files)) MessageText($"  {files.GetArrayLength()} files" + (Flag(result, "truncated") ? " · more available" : ""), "dim");
                    else if (result.TryGetProperty("matches", out var matches)) MessageText($"  {matches.GetArrayLength()} matches" + (Flag(result, "limitReached") ? " · limit reached" : ""), "dim");
                }
                activeActivities.Remove(Text(data, "id"));
                if (Verbose && CallTiming(data).Length > 0) MessageText("Call completed · " + CallTiming(data), "activity");
                break;
            case "tool.rejected":
                runningStatus = "Thinking"; activeActivities.Remove(Text(data, "id"));
                if (TryJson(Text(data, "result"), out var rejection) && !FailureBlock(rejection, Text(data, "id"))) MessageText(Text(rejection, "error"), "status-warning");
                break;
            case "run.outcome":
                runningStatus = "Running"; activeActivities.Clear();
                var elapsed = startedAt is { } start ? clock.GetElapsedTime(start) : (TimeSpan?)null;
                startedAt = null;
                var state = Text(data, "state");
                var detail = new List<string>();
                if (elapsed is { } duration) detail.Add($"Executed in {TerminalDuration.Format(duration)}");
                if (tools > 0) detail.Add($"{tools} tool call{(tools == 1 ? "" : "s")}");
                if (inputTokens + outputTokens > 0) detail.Add($"{inputTokens.ToString("N0", CultureInfo.InvariantCulture)} in / {outputTokens.ToString("N0", CultureInfo.InvariantCulture)} out tokens");
                Wrapped("  └─ " + (state switch { "Completed" => "✓ Done", "AwaitingUser" => "Question ready", _ => state }) + (detail.Count > 0 ? "  ·  " + string.Join("  ·  ", detail) : ""), state is "Completed" or "AwaitingUser" ? "dim" : "warning");
                if (!FailureBlock(data) && !string.IsNullOrEmpty(Text(data, "reason"))) Wrapped("  " + Text(data, "reason"), "warning");
                if (Text(data, "workflow").Length > 0) workflow = Text(data, "workflow");
                guideStyle = TerminalTheme.Guide(workflow);

                break;
            case "models":
                Title("Available models");
                if (width >= 72)
                {
                    Line("  " + "MODEL".PadRight(28) + "ID", "dim");
                    foreach (var model in data.EnumerateArray())
                    {
                        var name = Safe(Text(model, "name"));
                        if (name.Length > 26) { Wrapped("  " + name, "bright"); Line("  " + new string(' ', 28) + Safe(Text(model, "id")), "dim"); }
                        else { Paint("  " + name.PadRight(28), "bright"); Line(Text(model, "id"), "accent"); }
                    }
                }
                else foreach (var model in data.EnumerateArray()) { Wrapped("  " + Text(model, "name"), "bright"); Wrapped("    " + Text(model, "id"), "dim"); output.WriteLine(); }
                output.WriteLine(); Wrapped("  Start a chat: smith chat --model <id>", "dim"); output.WriteLine();
                break;
            case "question.heading":
                Message("Your input", Text(data, "text"));
                break;
            case "user.submitted":
                MessageTitle("You");
                if (data.TryGetProperty("content", out var submittedContent) && submittedContent.ValueKind == JsonValueKind.Array)
                    UserContent(submittedContent);
                else MessageText(Text(data, "text"));
                EndPrompt();
                break;
            case "prompt.queued":
                MessageText(Text(data, "message"), "dim");
                if (data.TryGetProperty("content", out var queuedContent) && queuedContent.ValueKind == JsonValueKind.Array)
                    UserContent(queuedContent, imagesOnly: true);
                break;
            case "answer.submitted":
                Message("You", Text(data, "text"));
                break;
            case "question.pending":
                var questionId = Text(data, "id");
                if (lastQuestion == questionId) break;
                lastQuestion = questionId;
                Line("  ├─ Your input", "accent"); MessageText(Text(data, "text"));
                if (data.TryGetProperty("choices", out var choices)) foreach (var choice in choices.EnumerateArray()) MessageText("  • " + choice.GetString());
                MessageText("Type your answer. Nothing is selected automatically.", "dim");
                break;
            case "auth.accounts":
                Title("ChatGPT accounts");
                var accounts = data.GetProperty("accounts");
                if (accounts.GetArrayLength() == 0) Line("  Not signed in. Run smith auth login.", "dim");
                foreach (var account in accounts.EnumerateArray())
                {
                    Wrapped("  " + (Text(account, "id") == Text(data, "activeId") ? "● " : "  ") + Text(account, "email"), "bright");
                    Wrapped("    " + Text(account, "id"), "dim");
                    Line("    " + (Flag(account, "planUsage") ? "ChatGPT plan enabled" : "Sign-in or plan permission required"), "dim");
                }
                output.WriteLine(); break;
            case "auth.signed_in":
                Title("Signed in", "success"); Wrapped("  " + Text(data, "email"));
                Line(Flag(data, "planUsage") ? "  ChatGPT plan enabled · Run smith models to choose a model." : "  Plan permission is required before chatting.", "dim"); break;
            case "auth.selected": Line("  ✓ Account selected: " + Text(data, "id"), "success"); break;
            case "sessions":
                Title("Conversations");
                if (data.GetArrayLength() == 0) Line("  No saved conversations yet.", "dim");
                foreach (var session in data.EnumerateArray())
                {
                    Wrapped("  " + Text(session, "model") + "  ·  " + Text(session, "state"), "bright");
                    Wrapped("    " + Text(session, "workspace"), "dim");
                    Wrapped("    smith resume " + Text(session, "id"), "accent"); output.WriteLine();
                }
                break;
            case "status":
                if (Text(data, "webSearch").Length > 0) Field("Web search", Text(data, "webSearch"));
                if (data.TryGetProperty("pendingSteering", out var pendingSteering)) Field("Steering pending", pendingSteering.ToString());
                if (data.TryGetProperty("workerPolicy", out var workerPolicy)) Field("Worker limits", Text(workerPolicy, "maxConcurrent") + " total / " + Text(workerPolicy, "maxLocalConcurrent") + " local · parent excluded");
                if (data.TryGetProperty("execution", out var limits)) Field("Batch limits", Text(limits, "maxRequests") + " requests / " + Text(limits, "maxMinutes") + " minutes · " + Text(data, "remainingRequests") + " requests left · " + Text(data, "remainingMinutes") + " minutes left");
                if (Text(data, "delegation").Length > 0) Field("Delegation", Text(data, "delegation"));
                Field("Workflow", Text(data, "workflow") + " / " + Text(data, "mode"));
                if (data.TryGetProperty("report", out var activeReport) && activeReport.ValueKind == JsonValueKind.Object) Field("Report", Text(activeReport, "id") + " · " + Text(activeReport, "targetKind") + " " + Text(activeReport, "target"));
                if (Text(data, "provider").Length > 0) Field("Provider", Text(data, "provider") + " · " + Text(data, "model"));
                if (data.TryGetProperty("workers", out var workerStates)) Field("Workers", workerStates.GetArrayLength().ToString());
                Title("Conversation details"); Field("Session", Text(data, "id")); Field("State", Text(data, "state"));
                Field("Reason", Text(data, "reason"));
                if (data.TryGetProperty("completionRequirement", out var requirement) && requirement.ValueKind == JsonValueKind.Object)
                { Field("Completion", Text(requirement, "reason")); Field("Next", Text(requirement, "next")); }
                if (TryJson(Text(data, "context"), out var context)) Context(context);
                break;
            case "run.inspection":
                Title("Run details"); Field("Run", Text(data, "id"));
                var stateValue = data.GetProperty("state");
                Field("State", stateValue.ValueKind == JsonValueKind.Number ? ((RunState)stateValue.GetInt32()).ToString() : stateValue.ToString());
                Field("Steps", Text(data, "steps")); Field("Reason", Text(data, "reason"));
                if (TryJson(Text(data, "contextManifest"), out var manifest)) Context(manifest);
                if (data.TryGetProperty("calls", out var calls)) foreach (var call in calls.EnumerateArray())
                { Wrapped("  › " + Text(call, "name")); Wrapped("    " + Text(call, "result"), "dim"); }
                break;
            case "resume.confirmation":
                Title("Continue saved work", "warning"); Field("State", Text(data, "state")); Field("Reason", Text(data, "reason")); Wrapped("  " + Text(data, "message")); break;
            case "help":
                Title("Chat commands");
                Wrapped("  Example: " + Text(data, "example"), "accent", maxWidth: 100);
                if (data.TryGetProperty("groups", out var groups)) foreach (var group in groups.EnumerateArray())
                {
                    Title(Text(group, "title"));
                    foreach (var command in group.GetProperty("commands").EnumerateArray())
                    {
                        Wrapped("  " + Text(command, "command"), "accent", maxWidth: 100);
                        Wrapped("    " + Text(command, "description"), "normal", maxWidth: 100);
                    }
                }
                output.WriteLine(); Wrapped("  " + Text(data, "note"), "dim", maxWidth: 100);
                Wrapped("  " + Text(data, "topics"), "accent", maxWidth: 100); output.WriteLine(); break;
            case "context.compaction.started": runningStatus = "Compacting context"; MessageText("Compacting older context…", "status-running"); break;
            case "memory.saved": MessageText(Text(data, "message"), "accent"); break;
            case "memory.warning": MessageText(Text(data, "message"), "warning"); break;
            case "context.compaction.retry": MessageText(Text(data, "reason"), "status-warning"); break;
            case "context.compaction.completed": MessageText("Context compacted · " + Text(data, "before") + " → " + Text(data, "after") + " " + (Text(data, "unit").Length > 0 ? Text(data, "unit") : "estimated characters"), "status-success"); break;
            case "context.compaction.failed": MessageText("Compaction paused: " + Text(data, "reason"), "status-warning"); break;
            case "model.waiting": MessageText(Text(data, "message"), "status-running"); break;
            case "run.cancelling":
            case "queue.held": MessageText(Text(data, "message"), "status-warning"); break;
            case "error":
                if (!FailureBlock(data)) { Title("Error", "status-error"); Wrapped("  " + Text(data, "message")); output.WriteLine(); }
                break;
            case "agent.detail": WorkerResult("agent_result", data); break;
            default:
                if (!string.IsNullOrEmpty(Text(data, "message"))) Wrapped("  " + Text(data, "message"));
                break;
        }
    }

    public void Prompt()
    {
        EndStream();
        MessageTitle("You");
        Paint("  │ you › ", "accent");
        output.Flush();
    }

    public void EndPrompt() => Line("  └─", "border");

    private void MessageTitle(string title)
    { Line("  ┌─ " + title, guideStyle); }

    private void MessageText(string text, string style = "normal")
    {
        if (style.StartsWith("status-", StringComparison.Ordinal))
            StatusLine(style switch { "status-running" => "Active", "status-success" => "Done", "status-error" => "Error", "status-warning" => "Notice", _ => "Info" }, text, style);
        else Wrapped(text, style, "  │ ");
    }

    private void Message(string title, string text)
    { MessageTitle(title); MessageText(text); EndPrompt(); }

    private void Header(JsonElement data)
    {
        var inner = width - 4;
        output.WriteLine();
        Line("  ┌" + new string('─', inner) + "┐", "border");
        Box("AgentSmith", "bright", inner);
        var mode = Text(data, "mode");
        var provider = Text(data, "provider"); var local = provider is "LmStudio" or "Local";
        var label = provider == "Local" ? "Local Chat Completions" : local ? "LM Studio" : "ChatGPT plan";
        Box(Text(data, "model") + "  ·  " + label + "  ·  " + (Text(data, "workflow").Length == 0 ? "Work" : Text(data, "workflow")) + " / " + (mode.Length == 0 ? "Inspect" : mode), "accent", inner);
        Box(Text(data, "workspace"), "dim", inner);
        if (data.TryGetProperty("report", out var active) && active.ValueKind == JsonValueKind.Object) Box("Report " + Text(active, "id") + " · " + Text(active, "state") + " · " + Text(active, "targetKind") + " " + Text(active, "target"), "dim", inner);
        Line("  └" + new string('─', inner) + "┘", "border");
        if (mode == "Execute" && Text(data, "workflow") is not ("Review" or "Plan")) Wrapped(Text(data, "workflow") == "Document" ? "  Execute runs project code; process effects are not restricted to documentation files." : "  Processes run as your Windows account; the workspace is not a sandbox.", "dim");
        if (local) Wrapped("  " + Text(data, "endpoint") + " · Loaded context: " + (Text(data, "loadedContextTokens").Length > 0 ? Text(data, "loadedContextTokens") + " tokens" : "not reported"), "dim");
        else { Wrapped("  Remaining usage unavailable", "dim"); Wrapped("  Manage usage: https://chatgpt.com/settings/usage", "dim"); }
        Line("  /help commands   /exit leave   Ctrl+C stop", "dim");
        output.WriteLine();
    }
    private void Box(string value, string style, int inner)
    {
        foreach (var part in Chunks(Safe(value).Replace('\n', ' ').Replace('\t', ' '), inner - 4))
        {
            Paint("  │  ", "border"); Paint(part.PadRight(inner - 4), style); Line("  │", "border");
        }
    }
    private void History(JsonElement history)
    {
        var messages = history.EnumerateArray().Where(i => Text(i, "role") is "user" or "assistant").TakeLast(6).ToArray();
        if (messages.Length == 0) return;
        Line("  Recent messages · saved history", "dim");
        foreach (var message in messages)
        {
            if (!message.TryGetProperty("content", out var content)) continue;
            guideStyle = TerminalTheme.Guide(Text(message, "_workflow"));
            MessageTitle(Text(message, "role") == "user" ? "You" : "AgentSmith");
            var historyWriter = Text(message, "role") == "assistant" ? new MarkdownWriter(output, color, width, MarkdownEnabled, guideStyle) : null;
            void HistoryText(string value) { if (historyWriter is null) MessageText(value); else historyWriter.Append(value); }
            if (content.ValueKind == JsonValueKind.String) HistoryText(content.GetString() ?? "");
            else if (content.ValueKind == JsonValueKind.Array && historyWriter is null) UserContent(content);
            else if (content.ValueKind == JsonValueKind.Array)
                foreach (var block in content.EnumerateArray()) { if (!string.IsNullOrEmpty(Text(block, "text"))) HistoryText(Text(block, "text")); else if (Text(block, "type") == "input_image") HistoryText("[Image " + Text(block, "attachment_id") + "]"); }
            historyWriter?.Finish();
            EndPrompt();
        }
        guideStyle = TerminalTheme.Guide(workflow);
        output.WriteLine(); Line("  ── Resumed ──", "dim"); output.WriteLine();
    }
    private void UserContent(JsonElement content, bool imagesOnly = false)
    {
        var imageNumber = 0;
        foreach (var part in content.EnumerateArray())
        {
            if (Text(part, "type") == "input_image")
            {
                var dimensions = Number(part, "width") > 0 && Number(part, "height") > 0
                    ? $" · {Number(part, "width")}×{Number(part, "height")}" : "";
                var size = Number(part, "bytes") > 0 ? $" · {Number(part, "bytes") / 1024d:0.#} KiB" : "";
                MessageText($"[Image {++imageNumber}{dimensions}{size}]", "accent");
                if (Text(part, "attachment_id").Length > 0) MessageText("  Attachment: " + Text(part, "attachment_id"), "dim");
            }
            else if (!imagesOnly && Text(part, "text").Length > 0) MessageText(Text(part, "text"));
        }
    }
    private void Context(JsonElement data)
    {
        if (Text(data, "unit") == "tokens" && data.TryGetProperty("measurement", out var measure))
        { Field("Context", $"{Number(measure, "tokens"):N0} / {Number(data, "limit"):N0} tokens · {Text(measure, "method")}"); Field("Output reserve", Text(data, "outputReserve")); }
        else Field("Context", $"{Number(data, "characters"):N0} / {Number(data, "limit"):N0} estimated characters");
        if (data.TryGetProperty("capacity", out var capacity)) Field("Capacity", Text(capacity, "contextTokens") + " · " + Text(capacity, "source"));
        if (data.TryGetProperty("categories", out var categories)) Field("Breakdown", categories.ToString());
        if (data.TryGetProperty("trigger", out _)) Field("Compaction", "trigger " + Text(data, "trigger") + " · target " + Text(data, "target"));
        Field("History", $"{Number(data, "historyItems")} items");
        Field("Guidance", Text(data, "instructionSource"));
        if (data.TryGetProperty("memory", out var memory))
        {
            Field("Memory", Text(memory, "enabled") + " · " + Text(memory, "characters") + " estimated characters");
            Field("Memory warning", Text(memory, "warning"));
            if (memory.TryGetProperty("entries", out var entries)) foreach (var entry in entries.EnumerateArray())
                Wrapped("  " + Text(entry, "id") + " · revision " + Text(entry, "revision") + " · " + Text(entry, "reason") + " · " + Text(entry, "characters") + " characters", "dim");
        }
        if (Text(data, "snapshotId").Length > 0) Field("Summary", Text(data, "snapshotId") + " · through item " + Text(data, "through"));
    }
    private void Operation(JsonElement data, bool diff = false)
    {
        if (data.TryGetProperty("checkpointId", out _))
        { MessageText("Checkpoint " + Text(data, "checkpointId") + " · " + Text(data, "state"), "accent"); DiffPreview(Text(data, "diff")); }
        if (data.TryGetProperty("processId", out _))
        {
            ProcessOutput(data);
        }
        else if (data.TryGetProperty("content", out _))
        { if (diff) DiffPreview(Text(data, "content")); else Preview(Text(data, "content")); }
    }
    private void WorkflowResult(JsonElement data)
    {
        MessageText("Report " + Text(data, "id") + " · revision " + Text(data, "revision") + " · " + Text(data, "state"), "accent");
        if (Text(data, "task").Length > 0) Preview(Text(data, "task"));
        if (Text(data, "targetKind").Length > 0) MessageText("Target: " + Text(data, "targetKind") + " " + Text(data, "target"), "dim");
        if (data.TryGetProperty("current", out var current))
        {
            if (current.ValueKind == JsonValueKind.Object)
            {
                Preview(Text(current, "summary"));
                if (Text(current, "coverage").Length > 0) Preview("Coverage: " + Text(current, "coverage"));
                if (current.TryGetProperty("findings", out var findings) && findings.ValueKind == JsonValueKind.Array)
                    MessageText(findings.GetArrayLength() + " saved findings · /report " + Text(data, "id") + " for details", "dim");
            }
            else MessageText("No saved report revision yet.", "dim");
        }
        else if (Text(data, "summary").Length > 0) Preview(Text(data, "summary"));
        if (Text(data, "artifactId").Length > 0) MessageText("Report artifact: " + Text(data, "artifactId"), "dim");
    }
    private void Plan(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) { MessageText("No saved plan.", "dim"); return; }
        if (!data.TryGetProperty("current", out var current) || current.ValueKind != JsonValueKind.Object)
        { MessageText("No saved plan revision.", "dim"); return; }
        MessageText("Plan " + Text(data, "id") + " · revision " + Text(current, "revision") + " · " + Text(data, "state"), "accent");
        if (Text(data, "originWorkspace").Length > 0)
            MessageText("Created in: " + Text(data, "originWorkspace") + (Flag(data, "originInferred") ? " (inferred from saved conversation)" : ""), "dim");
        if (Text(data, "sourceConversationId").Length > 0) MessageText("Source conversation: " + Text(data, "sourceConversationId"), "dim");
        if (Text(data, "notice").Length > 0) MessageText(Text(data, "notice"), "dim");
        MessageText(Text(current, "goal"));
        foreach (var key in new[] { "deliverables", "constraints", "questions" })
            if (current.TryGetProperty(key, out var list) && list.GetArrayLength() > 0) { MessageText(key + ":", "accent"); foreach (var item in list.EnumerateArray()) MessageText("  - " + item.GetString()); }
        foreach (var step in current.GetProperty("steps").EnumerateArray())
        {
            MessageText("[" + Text(step, "status") + "] " + Text(step, "id") + " · " + Text(step, "description"), "accent");
            MessageText("  Verify: " + Text(step, "verification"), "dim");
            if (Text(step, "note").Length > 0) MessageText("  " + Text(step, "note"));
        }
    }
    private void DirectoryPlanList(JsonElement data)
    {
        MessageText("Saved directory plans · " + Text(data, "workspace") + " · page " + Text(data, "page"), "accent");
        var plans = data.GetProperty("plans");
        if (plans.GetArrayLength() == 0) MessageText("No saved plans on this page.", "dim");
        foreach (var plan in plans.EnumerateArray())
        {
            MessageText(Text(plan, "id") + " · " + Text(plan, "state") + " · revision " + Text(plan, "revision"), "accent");
            MessageText("  " + Text(plan, "goal"));
            MessageText("  Conversation: " + Text(plan, "sourceConversationId") + (Flag(plan, "originInferred") ? " · origin inferred" : ""), "dim");
        }
        if (Flag(data, "hasMore")) MessageText("Next page: /plans " + (Number(data, "page") + 1), "dim");
        MessageText("Read: /plan read <id> · Reading does not activate or approve a plan.", "dim");
    }
    private void Artifact(JsonElement data)
    {
        if (data.TryGetProperty("artifacts", out var artifacts)) foreach (var item in artifacts.EnumerateArray()) MessageText(Text(item, "id") + " · " + Text(item, "kind"), "accent");
        else
        {
            MessageText("Artifact " + Text(data, "artifactId") + " · " + Text(data, "state"), "accent");
            if (data.TryGetProperty("text", out var text)) MessageText(text.GetString() ?? "");
            if (data.TryGetProperty("matches", out var matches)) foreach (var match in matches.EnumerateArray()) MessageText("[" + Text(match, "stream") + "] " + Text(match, "excerpt"));
            if (Flag(data, "hasMore")) MessageText("Next page: /artifact " + Text(data, "artifactId") + (Text(data, "view") is { Length: > 0 } audioView ? " audio " + audioView + " " : " read ") + Text(data, "cursor"), "dim");
        }
    }
    private void Verification(JsonElement data)
    {
        var outcome = Text(data, "state") != "complete" ? Text(data, "state") : Text(data, "exitCode") == "0" ? "succeeded" : "failed";
        MessageText(Text(data, "kind") + " " + outcome + $" · {Number(data, "errors")} errors · {Number(data, "warnings")} warnings · exit " + Text(data, "exitCode"), outcome == "succeeded" ? "accent" : "warning");
        if (Text(data, "kind") == "test") MessageText($"Tests: {Number(data, "passed")} passed, {Number(data, "failed")} failed, {Number(data, "skipped")} skipped; {Number(data, "total")} total.");
        DiagnosticItems(data);
        MessageText("Details: /diagnostics " + Text(data, "summaryArtifactId"), "dim");
        if (Text(data, "reason").Length > 0) MessageText(Text(data, "reason"), "warning");
    }
    private void DiagnosticPage(JsonElement data)
    {
        MessageText("Diagnostics · " + Text(data, "state") + " · " + Text(data, "totalMatches") + " matching records", "accent");
        DiagnosticItems(data);
        if (Text(data, "reason").Length > 0) MessageText(Text(data, "reason"), "warning");
        if (Text(data, "cursor").Length > 0) MessageText("Next: /diagnostics " + Text(data, "artifactId") + " " + Text(data, "filter") + " " + (Number(data, "page") + 1), "dim");
    }
    private void DiagnosticItems(JsonElement data)
    {
        if (!data.TryGetProperty("items", out var items)) return;
        foreach (var item in items.EnumerateArray())
        {
            var location = Text(item, "path");
            if (location.Length > 0) location += ":" + Text(item, "line") + ":" + Text(item, "column");
            var severityStyle = Text(item, "severity").ToLowerInvariant() switch
            {
                "error" or "failed" => "error", "warning" or "skipped" => "warning",
                "passed" => "success", _ => "activity"
            };
            MessageText("  " + string.Join(" · ", new[] { Text(item, "id"), Text(item, "severity"), Text(item, "code"), location, Text(item, "test") }.Where(s => s.Length > 0)), severityStyle);
            if (Text(item, "message").Length > 0) MessageText("  " + Text(item, "message"));
            if (Number(item, "occurrences") > 1) MessageText("  Occurrences: " + Number(item, "occurrences"), "dim");
        }
    }
    private void NavigationResult(JsonElement data)
    {
        if (data.TryGetProperty("items", out var items))
        {
            MessageText("C# navigation · " + Text(data, "total") + " matches", "accent");
            foreach (var item in items.EnumerateArray())
            {
                var label = Text(item, "signature") is { Length: > 0 } signature ? signature : Text(item, "symbol");
                var location = item.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object ? Text(loc, "path") + ":" + Text(loc, "line") + ":" + Text(loc, "column") : "metadata";
                MessageText("  " + label + " · " + location, "activity");
            }
            MessageText("Retained results: /artifact " + Text(data, "artifactId"), "dim");
        }
        else MessageText("C# snapshot · " + Text(data, "state") + " · " + Text(data, "project"), "accent");
        if (Text(data, "reason").Length > 0) MessageText(Text(data, "reason"), "warning");
    }
    private void Stream(string text)
    {
        if (!streaming)
        {
            if (startedAt is null) MessageTitle("AgentSmith");
            streaming = true;
            markdownStream = new(output, color, width, MarkdownEnabled, guideStyle);
        }
        markdownStream!.Append(text);
    }
    private void EndStream()
    {
        if (!streaming) return;
        markdownStream!.Finish(); markdownStream = null;
        streaming = false;
    }
    private void Field(string label, string value)
    { if (value.Length > 0) Wrapped("  " + label.PadRight(10) + value); }
    private void Title(string text, string style = "accent") { output.WriteLine(); Line("  " + text, style); output.WriteLine(); }
    private void Wrapped(string text, string style = "normal", string prefix = "", int? maxWidth = null)
    {
        var wrapWidth = Math.Min(width, maxWidth ?? width);
        foreach (var line in Safe(text).Split('\n'))
        {
            var indent = new string(' ', Math.Min(line.TakeWhile(char.IsWhiteSpace).Count(), wrapWidth / 2));
            var remaining = line.TrimStart();
            var available = wrapWidth - 2 - indent.Length - prefix.Length;
            while (remaining.Length > available)
            {
                var split = remaining.LastIndexOf(' ', available, available + 1);
                if (split <= 0) split = available;
                if (char.IsHighSurrogate(remaining[split - 1])) split--;
                Line(prefix + indent + remaining[..split], style);
                remaining = remaining[split..].TrimStart();
            }
            Line(prefix + indent + remaining, style);
        }
    }
    private static IEnumerable<string> Chunks(string value, int length)
    {
        if (value.Length == 0) { yield return ""; yield break; }
        for (var i = 0; i < value.Length;)
        {
            var count = Math.Min(length, value.Length - i);
            if (i + count < value.Length && char.IsHighSurrogate(value[i + count - 1])) count--;
            yield return value.Substring(i, count); i += count;
        }
    }
    private void Line(string value, string style = "normal")
    {
        if (value.StartsWith("  │", StringComparison.Ordinal) || value.StartsWith("  ┌─", StringComparison.Ordinal) || value.StartsWith("  └─", StringComparison.Ordinal) || value.StartsWith("  ├─", StringComparison.Ordinal))
        {
            var length = value[2] == '│' ? 3 : 4;
            Paint(value[..length], guideStyle); Paint(value[length..], style);
        }
        else Paint(value, style);
        output.WriteLine();
    }
    private void Paint(string value, string style)
    {
        var escape = TerminalTheme.Style(style == "border" ? guideStyle : style);
        if (color && escape.Length > 0) output.Write(escape);
        output.Write(Safe(value));
        if (color && escape.Length > 0) output.Write("\u001b[0m");
    }
    public static string Safe(string value) => TerminalControlFilter.Strip(value).Replace("\r", "");
    private static string Text(JsonElement data, string name) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? (v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString()) : "";
    private static long Number(JsonElement data, string name) => data.TryGetProperty(name, out var v) && v.TryGetInt64(out var result) ? result : 0;
    private static bool Flag(JsonElement data, string name) => data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    private static bool TryJson(string text, out JsonElement result)
    {
        try { using var doc = JsonDocument.Parse(text); result = doc.RootElement.Clone(); return result.ValueKind == JsonValueKind.Object; }
        catch (JsonException) { result = default; return false; }
    }
    private static string ToolLabel(string name) => name switch
    {
        "workspace_list" => "Listing files", "file_read" => "Reading file", "text_search" => "Searching workspace", "user_ask" => "Waiting for your input",
        "agent_spawn" => "Spawn worker", "agent_wait" => "Wait for workers", "agent_message" => "Message worker", "agent_result" => "Collect worker result", "agent_cancel" => "Cancel worker", _ => name
    };
}
